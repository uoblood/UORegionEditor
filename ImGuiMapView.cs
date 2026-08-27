using System.Numerics;
using ImGuiNET;
using UORegionEditor.Net;

namespace UORegionEditor;

// Map rendering + interactions in two projections:
//  - flat: the radar map, 1 px = 1 tile (regions are rectangles) - best for region layout
//  - iso ("CentrED view"): the real in-game isometric projection rendered from the muls
//    (regions are diamonds, exactly like CentrED shows the world)
// origin/zoom are in the CURRENT projection's pixel units (tile px flat, iso px iso).
public partial class ImGuiApp
{
    double zoom = 0.2;
    double originX, originY;

    enum Mode { Select, Draw, Corners, Lasso, EraseBox, EraseLasso, BrushAdd, BrushErase, Wand, WandErase }
    Mode mode = Mode.Select;
    bool addToSelected = true;
    bool avoidOverlap;                    // tools refuse to paint inside other VISIBLE regions
    TileIndex strokeIndex;                // occupancy snapshot taken when a stroke starts (brush + draw box)
    List<RegionRect> drawPreviewPieces;   // cached carve of the rubber box (recomputed on change)
    string drawPreviewSig = "";
    long strokeIndexSig;                  // layout the strokeIndex was built against

    // occupancy of every VISIBLE region except the one being added to. Hiding a region
    // is how you deliberately draw inside it (same curation trick as the PNG export) -
    // without that escape hatch an umbrella region like Blood Action blocks the world.
    TileIndex BuildOverlapIndex(RegionDef except)
    {
        var idx = new TileIndex(mapW, mapH);
        if (!avoidOverlap) return idx;
        foreach (var r in project.Regions)
        {
            if (r == except || !r.Visible) continue;
            foreach (var rc in r.Rects) idx.Add(rc, r.Name);
        }
        return idx;
    }

    // the region new tiles would join (null = a fresh region gets created)
    RegionDef OverlapExcept() => addToSelected ? selReg : null;

    // cheap fingerprint of every region's geometry + visibility: any edit (mine, a
    // teammate's, an undo) changes it, so a cached wand preview can never outlive the
    // layout it was computed against
    long RegionLayoutSig()
    {
        if (!avoidOverlap) return 0;
        // WHICH region is exempt matters as much as the geometry: switching the
        // selection changes what blocks, so it belongs in the same fingerprint
        long h = project.Regions.Count * 1000003L;
        h = h * 31 + (addToSelected ? selReg?.Uid.GetHashCode() ?? 0 : -1);
        foreach (var r in project.Regions)
        {
            if (!r.Visible) { h = h * 1000003L ^ unchecked((long)0x9E3779B97F4A7C15); continue; }
            foreach (var rc in r.Rects)
                h = (h * 1000003L) ^ (rc.X1 * 73856093L) ^ (rc.Y1 * 19349663L)
                    ^ (rc.X2 * 83492791L) ^ (rc.Y2 * 2971215073L);
        }
        return h;
    }

    bool scrollListToSel;                 // Regions list scrolls to the selection picked on the map
    bool pickTile;

    static readonly (Silk.NET.Input.Key key, Mode m)[] ToolKeys =
    {
        (Silk.NET.Input.Key.F1, Mode.Select), (Silk.NET.Input.Key.F2, Mode.Draw),
        (Silk.NET.Input.Key.F3, Mode.Lasso), (Silk.NET.Input.Key.F4, Mode.BrushAdd),
        (Silk.NET.Input.Key.F5, Mode.Wand), (Silk.NET.Input.Key.F6, Mode.Corners),
        (Silk.NET.Input.Key.F7, Mode.EraseBox), (Silk.NET.Input.Key.F8, Mode.EraseLasso),
        (Silk.NET.Input.Key.F9, Mode.BrushErase), (Silk.NET.Input.Key.F10, Mode.WandErase),
    };

    void SetTool(Mode m)
    {
        mode = m;
        AbortDrag();
        cornerPts.Clear();
        lassoPts.Clear();
    }

    enum Drag { None, Pan, Rubber, Move, Resize, Lasso, Brush, Wand }

    readonly List<(int x, int y)> lassoPts = new();   // traced tile path while lassoing

    // Photoshop-style brush: a circular stamp dragged along the stroke
    readonly HashSet<(int x, int y)> brushMask = new();
    readonly List<(int x, int y)> brushPath = new();   // stroke centers (preview + remote)
    int brushSize = 4;                                  // diameter in tiles, 1..32
    (int x, int y) lastBrushTile;
    List<RegionRect> brushPreview;                      // cached MaskToRects of brushMask

    void StampBrush((int x, int y) c)
    {
        double r = brushSize / 2.0, r2 = r * r;
        int ri = (int)Math.Ceiling(r);
        for (int dy = -ri; dy <= ri; dy++)
            for (int dx = -ri; dx <= ri; dx++)
                if (dx * dx + dy * dy <= r2)
                {
                    int x = c.x + dx, y = c.y + dy;
                    if (x < 0 || y < 0 || x >= mapW || y >= mapH) continue;
                    if (strokeIndex != null && strokeIndex.Occupied(x, y)) continue;   // no-overlap: paint around it
                    brushMask.Add((x, y));
                }
        brushPreview = null;
    }
    // Photoshop-style quick select (magic wand): click flood-fills all connected
    // tiles whose radar color matches the clicked tile, straight into region boxes.
    int wandTolerance = 10;                             // per-channel color diff, 0..64
    int wandMatch;                                      // 0 = land only, 1 = surface (incl. statics)
    const int WandMaxTiles = 120_000;                   // fill cap: beyond this a rect region is unreasonable
    HashSet<(int x, int y)> wandMask;                   // live fill preview under the cursor
    (int x, int y) wandHoverTile = (-1, -1);            // last MAP tile hovered (frozen while over UI)
    (int x, int y) wandDragSeed;                        // press tile while sizing the radius
    int wandRadius;                                     // 0 = unlimited (plain click)
    List<RegionRect> wandRects;                         // cached MaskToRects of wandMask
    bool wandOverflow;
    string wandSig = "";                                // seed+params the cached preview was computed for
    long wandLastCompute;

    MulMapData WandData => artView?.Map ?? sharedMapData;

    // Land type match is exact by definition - the slider must not blur name ids
    int WandEffTolerance => wandMatch == 2 ? 0 : wandTolerance;

    bool WandTypeUnavailable(MulMapData md) => wandMatch == 2 && md?.LandType == null;

    string WandSig((int x, int y) seed) =>
        $"{seed.x},{seed.y}|{WandEffTolerance}|{wandMatch}|{wandRadius}|{minZ}|{maxZ}|{showLand}|{showStatics}|{mode}|{avoidOverlap}|{RegionLayoutSig()}";

    void ClearWandPreview()
    {
        wandRadius = 0;
        wandMask = null;
        wandRects = null;
        wandSig = "";
        wandOverflow = false;
    }

    // recompute the hover preview when the cursor tile or the tool options changed
    // (throttled: an ocean-sized fill hitting the cap costs a few tens of ms)
    void UpdateWandPreview()
    {
        var md = WandData;
        if (md == null || wandHoverTile.x < 0 || WandTypeUnavailable(md)) { ClearWandPreview(); return; }
        string sig = WandSig(wandHoverTile);
        // ocean-scale fills that just hit the cap cost real ms and draw nothing -
        // back the recompute rate off while the area stays hopeless
        long wait = wandOverflow ? 300 : 60;
        if (sig == wandSig || Environment.TickCount64 - wandLastCompute < wait) return;
        wandSig = sig;
        wandMask = WandCompute(md, wandHoverTile, mode == Mode.WandErase, out wandOverflow);
        wandRects = wandOverflow ? null
            : wandMask.Count <= 40_000 ? RegionOps.MaskToRectsCompact(wandMask) : RegionOps.MaskToRects(wandMask);
        wandLastCompute = Environment.TickCount64;
    }

    const int WandHidden = 1 << 24;   // out-of-band "nothing visible here" (WandFill never crosses it)

    // The wand matches WHAT YOU SEE: all three modes honor the Filter window
    // (Min/Max Z, Land, Objects). Lower Max Z to look inside a mountain and the
    // wand selects the cave floor instead of the invisible roof above it.
    int WandLandColorAt(MulMapData md, int x, int y)
    {
        var (id, z) = md.LandAt(x, y);
        return showLand && z >= minZ && z <= maxZ ? md.RadarRgb(id & 0x3FFF) : WandHidden;
    }

    int WandSurfaceColorAt(MulMapData md, int x, int y)
    {
        var (id, lz) = md.LandAt(x, y);
        bool vis = showLand && lz >= minZ && lz <= maxZ;
        int col = vis ? md.RadarRgb(id & 0x3FFF) : 0;
        sbyte topZ = vis ? lz : sbyte.MinValue;
        if (showStatics)
            foreach (var s in md.StaticsAt(x, y))
            {
                if (s.z < minZ || s.z > maxZ || (vis && s.z < topZ)) continue;
                topZ = s.z;
                col = md.RadarRgb(0x4000 + s.id);
                vis = true;
            }
        return vis ? col : WandHidden;
    }

    // tiledata-NAME id of the visible surface: the topmost in-range FLOOR-LIKE static
    // (cave floors, pavers - StaticGround), else the land. Trees/walls/furniture are
    // not floors, so the fill flows under them like the Land color mode does.
    int WandTypeAt(MulMapData md, int x, int y)
    {
        var (id, lz) = md.LandAt(x, y);
        bool vis = showLand && lz >= minZ && lz <= maxZ;
        int type = vis ? md.LandType[id & 0x3FFF] : 0;
        sbyte topZ = vis ? lz : sbyte.MinValue;
        if (showStatics && md.StaticType != null)
            foreach (var s in md.StaticsAt(x, y))
            {
                if (s.z < minZ || s.z > maxZ || s.id >= md.StaticType.Length) continue;
                if (!md.StaticGround[s.id]) continue;
                if (vis && s.z < topZ) continue;
                topZ = s.z;
                type = md.StaticType[s.id];
                vis = true;
            }
        return vis ? type : WandHidden;
    }

    // the active mode's match value at one tile (seed probes / hidden checks)
    int WandProbe(MulMapData md, (int x, int y) t) => wandMatch switch
    {
        1 => WandSurfaceColorAt(md, t.x, t.y),
        2 => WandTypeAt(md, t.x, t.y),
        _ => WandLandColorAt(md, t.x, t.y),
    };

    HashSet<(int x, int y)> WandCompute(MulMapData md, (int x, int y) seed, bool erase, out bool overflow)
    {
        overflow = false;
        if (WandProbe(md, seed) == WandHidden) return new HashSet<(int x, int y)>();
        Func<int, int, int> colorAt = wandMatch switch
        {
            1 => (x, y) => WandSurfaceColorAt(md, x, y),
            2 => (x, y) => WandTypeAt(md, x, y),
            _ => (x, y) => WandLandColorAt(md, x, y),
        };
        // erasers only ever cut from the selected region - they are never guarded
        if (avoidOverlap && !erase)
        {
            // other regions read as "nothing here", so the fill STOPS at their border
            // instead of flooding through and being trimmed afterwards
            var idx = BuildOverlapIndex(OverlapExcept());
            if (!idx.IsEmpty)
            {
                // a seed inside a blocker selects nothing (WandFill also guards this)
                if (idx.Occupied(seed.x, seed.y)) return new HashSet<(int x, int y)>();
                var inner = colorAt;
                colorAt = (x, y) => idx.Occupied(x, y) ? WandHidden : inner(x, y);
            }
        }
        return RegionOps.WandFill(seed, mapW, mapH, colorAt, WandEffTolerance, WandMaxTiles, out overflow, wandRadius);
    }

    // click: fill from the tile and commit as boxes (or cut them from the selected region)
    void WandCommit((int x, int y) tile, bool erase)
    {
        var md = WandData;
        if (md == null) { status = "Quick select is waiting for the map data to finish loading."; return; }
        if (WandTypeUnavailable(md)) { status = "Land type match needs tiledata.mul in the muls folder (add it to the server pack)."; return; }
        if (avoidOverlap && !erase && BuildOverlapIndex(OverlapExcept()).Blocker(tile.x, tile.y) is { } who)
        {
            status = $"That tile is inside '{who}' - hide it with the eye icon in the Regions list to draw there, or turn off 'Don't overlap other regions'.";
            return;
        }
        if (erase && selReg == null) { status = "Select a region first - the eraser cuts from the selected region."; return; }
        bool ovf = false;
        var mask = WandSig(tile) == wandSig && wandMask != null && !wandOverflow
            ? wandMask
            : WandCompute(md, tile, erase, out ovf);
        if (ovf)
        {
            status = $"Quick select: area too large (over {WandMaxTiles:N0} tiles) - use Lasso or Draw box for ocean-scale areas.";
            return;
        }
        if (mask.Count == 0)
        {
            if (WandProbe(md, tile) == WandHidden)
                status = "Nothing visible at that tile under the current Z filter - nothing to select.";
            return;
        }
        if (erase)
        {
            CommitMaskErase(mask, "quick select erase");
        }
        else
        {
            int boxes = RegionOps.MaskToRectsCompact(mask).Count +
                (addToSelected && selReg != null ? selReg.Rects.Count : 0);
            if (boxes > maxRectsPerRegion)
            {
                status = $"Quick select: {boxes:N0} boxes exceeds the {maxRectsPerRegion:N0}-box region cap - select a smaller area or adjust tolerance.";
                return;
            }
            CommitMaskAdd(mask, "quick select");
        }
        ClearWandPreview();   // the region under the preview changed: recompute next frame
    }

    Drag drag = Drag.None;
    (int x, int y) rubberAnchor, rubberLast;
    (int x, int y)? pendingAnchor;
    readonly List<(int x, int y)> cornerPts = new();
    int resizeHandle = -1;
    RegionRect dragRectStart;
    (int x, int y) moveStartTile;
    string pendingEditDesc;
    bool editFired;
    (int x, int y) mouseTile;
    string detailHint;
    int visMinX, visMaxX, visMinY, visMaxY;   // visible tile bounds, refreshed each frame

    // ---- transforms -------------------------------------------------------

    bool Iso => detailMode && artView != null;

    double rollDeg;                       // CentrED-style middle-drag view rotation (iso only)

    Vector2 Rot(Vector2 s)
    {
        if (!Iso || rollDeg == 0) return s;
        var c = new Vector2(window.Size.X / 2f, window.Size.Y / 2f);
        float a = (float)(rollDeg * Math.PI / 180.0);
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        var d = s - c;
        return c + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
    }

    Vector2 Unrot(Vector2 s)
    {
        if (!Iso || rollDeg == 0) return s;
        var c = new Vector2(window.Size.X / 2f, window.Size.Y / 2f);
        float a = (float)(-rollDeg * Math.PI / 180.0);
        float cos = MathF.Cos(a), sin = MathF.Sin(a);
        var d = s - c;
        return c + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
    }

    // grid-vertex land height, identical to what the iso terrain renderer uses -
    // region shapes anchored with this hug the rendered ground exactly
    int VLandZ(int vx, int vy)
    {
        var md = artView?.Map ?? sharedMapData;
        if (md == null || !Iso) return 0;
        int z = md.LandAt(Math.Clamp(vx, 0, mapW - 1), Math.Clamp(vy, 0, mapH - 1)).z;
        return Math.Clamp(z, -126, 126);
    }

    // tile-grid vertex -> screen, terrain-conforming in iso
    Vector2 T(double tx, double ty)
    {
        if (Iso)
        {
            double z = VLandZ((int)Math.Round(tx), (int)Math.Round(ty));
            var (ipx, ipy) = artView.IsoTileToPx(tx, ty, z);
            return Rot(new Vector2((float)((ipx - originX) * zoom), (float)((ipy - originY) * zoom)));
        }
        return new Vector2((float)((tx - originX) * zoom), (float)((ty - originY) * zoom));
    }

    (int x, int y) TileAt(Vector2 s)
    {
        s = Unrot(s);
        double ux = s.X / zoom + originX, uy = s.Y / zoom + originY;
        double mx, my;
        if (Iso)
        {
            (mx, my) = artView.IsoPxToTile(ux, uy);
            // iterate the land-height refinement to a fixed point: on tall hills a single
            // pass can still be tiles off (which also made the Z readout look wrong)
            for (int i = 0; i < 4; i++)
            {
                int z = VLandZ((int)Math.Round(mx), (int)Math.Round(my));
                var (nx, ny) = artView.IsoPxToTile(ux, uy + z * ArtView.IsoZStep);
                bool stable = (int)Math.Floor(nx) == (int)Math.Floor(mx) && (int)Math.Floor(ny) == (int)Math.Floor(my);
                (mx, my) = (nx, ny);
                if (stable) break;
            }
        }
        else
        {
            (mx, my) = (ux, uy);
        }
        return (Math.Clamp((int)Math.Floor(mx), 0, mapW - 1), Math.Clamp((int)Math.Floor(my), 0, mapH - 1));
    }

    void CenterOn(int x, int y, double newZoom = -1)
    {
        if (newZoom > 0) zoom = newZoom;
        double ux = x + 0.5, uy = y + 0.5;
        if (Iso) (ux, uy) = artView.IsoTileToPx(x + 0.5, y + 0.5);
        originX = ux - window.Size.X / (2.0 * zoom);
        originY = uy - window.Size.Y / (2.0 * zoom);
    }

    void ZoomFit()
    {
        if (Iso)
        {
            // the whole facet cannot fit in iso (chunk budget) - go to the selected
            // region, else just zoom out wide around whatever is on screen now
            if (selReg is { Rects.Count: > 0 }) { ZoomToRegion(selReg); return; }
            var c = TileAt(new Vector2(window.Size.X / 2f, window.Size.Y / 2f));
            CenterOn(c.x, c.y, 0.08);
            return;
        }
        var size = window.Size;
        zoom = Math.Min((double)size.X / mapW, (double)size.Y / mapH);
        if (zoom <= 0) zoom = 0.1;
        originX = (mapW - size.X / zoom) / 2.0;
        originY = (mapH - size.Y / zoom) / 2.0;
    }

    void ZoomToRegion(RegionDef r)
    {
        if (r == null || r.Rects.Count == 0) return;
        int x1 = r.Rects.Min(t => t.X1), y1 = r.Rects.Min(t => t.Y1);
        int x2 = r.Rects.Max(t => t.X2), y2 = r.Rects.Max(t => t.Y2);
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        zoom = Iso
            ? Math.Clamp(Math.Min(window.Size.X / ((w + h) * 22.0 * 1.6), window.Size.Y / ((w + h) * 22.0 * 1.6)), 0.05, 4.0)
            : Math.Clamp(Math.Min(window.Size.X / (w * 2.0), window.Size.Y / (h * 2.0)), 0.05, 16.0);
        CenterOn((x1 + x2) / 2, (y1 + y2) / 2);
    }

    // keep looking at the same spot when flipping between flat and iso
    void SwitchProjection(bool toIso)
    {
        var center = TileAt(new Vector2(window.Size.X / 2f, window.Size.Y / 2f));
        double newZoom = toIso ? Math.Clamp(zoom / 22.0 * 8.0, 0.05, 4.0) : Math.Clamp(zoom * 22.0 / 8.0, 0.03, 24.0);
        detailMode = toIso;
        if (toIso && artView == null) return;   // transforms fall back to flat until art loads
        zoom = newZoom;
        CenterOn(center.x, center.y);
    }

    void AbortDrag()
    {
        drag = Drag.None;
        resizeHandle = -1;
        pendingEditDesc = null;
        editFired = false;
        pendingAnchor = null;
        lassoPts.Clear();
        brushMask.Clear();
        brushPath.Clear();
        brushPreview = null;
        strokeIndex = null;
        drawPreviewPieces = null;
        drawPreviewSig = "";
        ClearWandPreview();
        exportPicking = false;   // any abort (tool switch, right-click, Esc) also cancels area picking
    }

    static uint Col(Color c, byte alpha) => (uint)((alpha << 24) | (c.B << 16) | (c.G << 8) | c.R);

    // ---- drawing ----------------------------------------------------------

    void RectCorners(RegionRect rc, out Vector2 p1, out Vector2 p2, out Vector2 p3, out Vector2 p4)
    {
        p1 = T(rc.X1, rc.Y1);
        p2 = T(rc.X2 + 1, rc.Y1);
        p3 = T(rc.X2 + 1, rc.Y2 + 1);
        p4 = T(rc.X1, rc.Y2 + 1);
    }

    // iso screen position of a tile-grid vertex at an explicit height
    Vector2 Tz(double tx, double ty, double z)
    {
        var (ipx, ipy) = artView.IsoTileToPx(tx, ty, z);
        return Rot(new Vector2((float)((ipx - originX) * zoom), (float)((ipy - originY) * zoom)));
    }

    void DrawRegionShape(ImDrawListPtr dl, RegionRect rc, uint fill, uint line, float thickness)
    {
        if (Iso && DrawRegionShapeIsoTiles(dl, rc, fill, line, thickness)) return;
        RectCorners(rc, out var p1, out var p2, out var p3, out var p4);
        if (Iso)
        {
            dl.AddQuadFilled(p1, p2, p3, p4, fill);
            dl.AddQuad(p1, p2, p3, p4, line, thickness);
        }
        else
        {
            dl.AddRectFilled(p1, p3, fill);
            dl.AddRect(p1, p3, line, 0, ImDrawFlags.None, thickness);
        }
    }

    // CentrED-style per-tile highlight: each tile of the region is tinted as its own small
    // quad sitting on the WALKING SURFACE (building floor or terrain), so highlights sit on
    // floors instead of wobbling along the land buried under buildings.
    bool DrawRegionShapeIsoTiles(ImDrawListPtr dl, RegionRect rc, uint fill, uint line, float thickness)
    {
        if (zoom < 0.30 || renderQuality <= 2) return false;   // too far out / lowest quality: cheap quad is fine
        int x0 = Math.Max(rc.X1, visMinX), x1 = Math.Min(rc.X2, visMaxX);
        int y0 = Math.Max(rc.Y1, visMinY), y1 = Math.Min(rc.Y2, visMaxY);
        if (x0 > x1 || y0 > y1) return true;           // fully off screen: draw nothing
        long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
        if (area > 2000L * renderQuality) return false;   // huge region on screen: fall back (quality scales the budget)

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var q = artView.TileHighlightQuad(x, y);
                Vector2 P(int i) => Rot(new Vector2((float)((q[i].x - originX) * zoom), (float)((q[i].y - originY) * zoom)));
                Vector2 q1 = P(0), q2 = P(1), q3 = P(2), q4 = P(3);
                dl.AddQuadFilled(q1, q2, q3, q4, fill);
                // border segments only on the region's outer edges
                if (y == rc.Y1) dl.AddLine(q1, q2, line, thickness);
                if (x == rc.X2) dl.AddLine(q2, q3, line, thickness);
                if (y == rc.Y2) dl.AddLine(q3, q4, line, thickness);
                if (x == rc.X1) dl.AddLine(q4, q1, line, thickness);
            }
        }
        return true;
    }

    bool ShapeOffScreen(Vector2 p1, Vector2 p3)
    {
        float minX = Math.Min(p1.X, p3.X), maxX = Math.Max(p1.X, p3.X);
        float minY = Math.Min(p1.Y, p3.Y), maxY = Math.Max(p1.Y, p3.Y);
        if (Iso) { minX -= 4000; maxX += 4000; }   // diamonds extend sideways past the two corners
        return maxX < 0 || maxY < 0 || minX > window.Size.X || minY > window.Size.Y;
    }

    // Preview a box the way it will actually be COMMITTED: carved around other regions
    // while the no-overlap guard is on (cached, since the carve is rect algebra per frame).
    void DrawBoxPreview(ImDrawListPtr dl, RegionRect rr, uint fill, uint line, float thickness)
    {
        if (avoidOverlap && !exportPicking && strokeIndex is { IsEmpty: false })
        {
            string sig = $"{rr.X1},{rr.Y1},{rr.X2},{rr.Y2}";
            if (sig != drawPreviewSig)
            {
                drawPreviewSig = sig;
                var pieces = new List<RegionRect> { rr };
                foreach (var cut in strokeIndex.Intersecting(rr))
                {
                    if (pieces.Count == 0 || pieces.Count > 1200) break;   // per-frame budget
                    pieces = RegionOps.SubtractBox(pieces, cut);
                }
                drawPreviewPieces = pieces;
            }
            foreach (var piece in drawPreviewPieces) DrawRegionShape(dl, piece, fill, line, thickness);
            return;
        }
        DrawRegionShape(dl, rr, fill, line, thickness);
    }

    void DrawMap()
    {
        var dl = ImGui.GetBackgroundDrawList();
        detailHint = null;

        // visible tile bounds (used to clip per-tile region highlights)
        var tA = TileAt(new Vector2(0, 0));
        var tB = TileAt(new Vector2(window.Size.X, 0));
        var tC = TileAt(new Vector2(window.Size.X, window.Size.Y));
        var tD = TileAt(new Vector2(0, window.Size.Y));
        visMinX = Math.Min(Math.Min(tA.x, tB.x), Math.Min(tC.x, tD.x)) - 2;
        visMaxX = Math.Max(Math.Max(tA.x, tB.x), Math.Max(tC.x, tD.x)) + 2;
        visMinY = Math.Min(Math.Min(tA.y, tB.y), Math.Min(tC.y, tD.y)) - 2;
        visMaxY = Math.Max(Math.Max(tA.y, tB.y), Math.Max(tC.y, tD.y)) + 2;

        if (Iso)
        {
            DrawIsoWorld(dl);
        }
        else
        {
            if (mapTex != 0)
            {
                var a = T(0, 0);
                var b = T(mapW, mapH);
                dl.AddImage((nint)mapTex, a, b);
            }
            if (detailMode && artView == null)
                detailHint = "loading art for the CentrED view...";
        }

        // tile grid at deep zoom
        double tileScreen = Iso ? 44 * zoom : zoom;
        if (tileScreen >= 12)
        {
            uint gcol = 0x26FFFFFF;
            var c = TileAt(new Vector2(window.Size.X / 2f, window.Size.Y / 2f));
            int range = (int)(Math.Max(window.Size.X, window.Size.Y) / tileScreen) + 2;
            int gx0 = Math.Max(0, c.x - range), gx1 = Math.Min(mapW, c.x + range);
            int gy0 = Math.Max(0, c.y - range), gy1 = Math.Min(mapH, c.y + range);
            for (int x = gx0; x <= gx1; x++) dl.AddLine(T(x, gy0), T(x, gy1), gcol);
            for (int y = gy0; y <= gy1; y++) dl.AddLine(T(gx0, y), T(gx1, y), gcol);
        }

        // regions
        foreach (var r in project.Regions)
        {
            if (!r.Visible) continue;
            bool sel = r == selReg;
            uint fill = Col(r.Color, (byte)(sel ? 90 : 55));
            uint line = Col(r.Color, 255);
            foreach (var rc in r.Rects)
            {
                RectCorners(rc, out var p1, out _, out var p3, out _);
                if (ShapeOffScreen(p1, p3)) continue;
                DrawRegionShape(dl, rc, fill, line, sel ? 2.5f : 1.2f);
            }
            if (r.Rects.Count > 0 && (Iso || zoom >= 0.12))
            {
                var first = r.Rects[0];
                var lp = T(first.X1, first.Y1);
                if (!(lp.X < -300 || lp.Y < -60 || lp.X > window.Size.X + 60 || lp.Y > window.Size.Y + 60))
                {
                    var sz = ImGui.CalcTextSize(r.Name);
                    var bgA = lp with { Y = lp.Y - sz.Y - 3 };
                    dl.AddRectFilled(bgA, bgA + sz + new Vector2(4, 2), 0xAA000000);
                    dl.AddText(bgA + new Vector2(2, 1), line, r.Name);
                }
            }
            if (sel && r.Rects.Count > 0 && (Iso || zoom >= 0.5))
            {
                var (px, py) = r.EffectiveP();
                var c = T(px + 0.5, py + 0.5);
                dl.AddLine(c - new Vector2(6, 0), c + new Vector2(6, 0), 0xFFFFFFFF, 2f);
                dl.AddLine(c - new Vector2(0, 6), c + new Vector2(0, 6), 0xFFFFFFFF, 2f);
                dl.AddText(c + new Vector2(6, 4), 0xFFFFFFFF, "P");
            }
        }

        // selected rect handles
        if (selRect != null && selReg != null && mode == Mode.Select)
        {
            RectCorners(selRect, out var p1, out var p2, out var p3, out var p4);
            if (Iso) dl.AddQuad(p1, p2, p3, p4, 0xFFFFFFFF, 1.5f);
            else dl.AddRect(p1, p3, 0xFFFFFFFF, 0, ImDrawFlags.None, 1.5f);
            foreach (var h in Handles(p1, p2, p3, p4))
                dl.AddRectFilled(h - new Vector2(3, 3), h + new Vector2(4, 4), 0xFFFFFFFF);
        }

        // rubber band / click-click preview (red when erasing)
        if (drag == Drag.Rubber || pendingAnchor != null)
        {
            bool erasing = mode == Mode.EraseBox;
            uint pf = erasing ? 0x320000FFu : 0x3200FFFFu, pl = erasing ? 0xFF4040FFu : 0xFF00FFFFu;
            var an = pendingAnchor ?? rubberAnchor;
            var rr = new RegionRect(an.x, an.y, rubberLast.x, rubberLast.y);
            // no-overlap: preview exactly what will be kept, carved around other regions
            if (mode == Mode.Draw) DrawBoxPreview(dl, rr, pf, pl, 1.5f);
            else DrawRegionShape(dl, rr, pf, pl, 1.5f);
            var tip = T(rr.X2 + 1, rr.Y2 + 1);
            dl.AddText(tip + new Vector2(5, 5), pl, $"{rr.W} x {rr.H}");
        }

        SendDrawPreview();
        SendPresence();
        DrawRemotePreviews(dl);

        // brush: painted area preview (red when erasing) + cursor ring sized to the brush
        if (drag == Drag.Brush && brushMask.Count > 0)
        {
            brushPreview ??= RegionOps.MaskToRects(brushMask);
            bool berase = mode == Mode.BrushErase;
            uint bff = berase ? 0x320000FFu : 0x3200FFFFu, bll = berase ? 0xFF4040FFu : 0xFF00FFFFu;
            foreach (var rc in brushPreview)
                DrawRegionShape(dl, rc, bff, bll, 1f);
        }
        if (mode is Mode.BrushAdd or Mode.BrushErase && !ImGui.GetIO().WantCaptureMouse)
        {
            // tile-space circle -> screen ellipse (half height in iso), rotation-aware
            uint bc = mode == Mode.BrushErase ? 0xFF4040FFu : 0xFF00FFFFu;
            var cen = new Vector2(mouseTile.x + 0.5f, mouseTile.y + 0.5f);
            float r = brushSize / 2f;
            for (int i = 0; i < 24; i++)
            {
                double a0 = Math.PI * 2 * i / 24, a1 = Math.PI * 2 * (i + 1) / 24;
                dl.AddLine(
                    T(cen.X + r * Math.Cos(a0), cen.Y + r * Math.Sin(a0)),
                    T(cen.X + r * Math.Cos(a1), cen.Y + r * Math.Sin(a1)), bc, 1.5f);
            }
        }

        // quick select: live fill preview under the cursor (red when erasing). The
        // preview stays up while the mouse is over the Tools panel, so the Tolerance
        // slider can be tuned against a fixed spot and the fill updates live.
        if (mode is Mode.Wand or Mode.WandErase && drag is Drag.None or Drag.Wand)
        {
            if (drag == Drag.Wand) wandHoverTile = wandDragSeed;                 // sizing: seed stays put
            else if (!ImGui.GetIO().WantCaptureMouse) wandHoverTile = mouseTile;
            UpdateWandPreview();
            int wandBoxes = wandRects == null ? 0
                : wandRects.Count + (addToSelected && mode == Mode.Wand && selReg != null ? selReg.Rects.Count : 0);
            if (wandRects != null && wandBoxes <= maxRectsPerRegion)
            {
                bool werase = mode == Mode.WandErase;
                uint wf = werase ? 0x320000FFu : 0x3200FFFFu, wl = werase ? 0xFF4040FFu : 0xFF00FFFFu;
                bool outlines = wandRects.Count <= 1500;   // fill-only when huge: draw-call budget
                // iso per-tile highlighting of thousands of tiny rects is a per-frame
                // cost - past a few hundred rects drop to plain diamond quads
                bool cheap = Iso && wandRects.Count > 400;
                foreach (var rc in wandRects)
                {
                    RectCorners(rc, out var p1, out var p2, out var p3, out var p4);
                    if (ShapeOffScreen(p1, p3)) continue;
                    if (cheap) { dl.AddQuadFilled(p1, p2, p3, p4, wf); continue; }
                    DrawRegionShape(dl, rc, wf, outlines ? wl : wf, 1f);
                }
            }
        }

        // radius ring while sizing the quick select (drag from the press tile)
        if (drag == Drag.Wand && wandRadius > 0)
        {
            uint rcRing = mode == Mode.WandErase ? 0xFF4040FFu : 0xFF00FFFFu;
            var wCen = new Vector2(wandDragSeed.x + 0.5f, wandDragSeed.y + 0.5f);
            for (int i = 0; i < 32; i++)
            {
                double a0 = Math.PI * 2 * i / 32, a1 = Math.PI * 2 * (i + 1) / 32;
                dl.AddLine(
                    T(wCen.X + wandRadius * Math.Cos(a0), wCen.Y + wandRadius * Math.Sin(a0)),
                    T(wCen.X + wandRadius * Math.Cos(a1), wCen.Y + wandRadius * Math.Sin(a1)), rcRing, 1.5f);
            }
        }

        // lasso stroke preview (red when erasing), with a faint closing edge
        if (drag == Drag.Lasso && lassoPts.Count > 0)
        {
            uint lc = mode == Mode.EraseLasso ? 0xFF4040FF : 0xFF00FFFF;
            for (int li = 1; li < lassoPts.Count; li++)
                dl.AddLine(T(lassoPts[li - 1].x + 0.5, lassoPts[li - 1].y + 0.5),
                           T(lassoPts[li].x + 0.5, lassoPts[li].y + 0.5), lc, 2f);
            if (lassoPts.Count > 2)
                dl.AddLine(T(lassoPts[^1].x + 0.5, lassoPts[^1].y + 0.5),
                           T(lassoPts[0].x + 0.5, lassoPts[0].y + 0.5), (lc & 0x00FFFFFFu) | 0x70000000u, 1.5f);
        }

        // corner points
        if (cornerPts.Count > 0)
        {
            foreach (var pt in cornerPts)
                dl.AddCircle(T(pt.x + 0.5, pt.y + 0.5), 4.5f, 0xFF00A5FF, 12, 2f);
            var bx1 = cornerPts.Min(p => p.x); var by1 = cornerPts.Min(p => p.y);
            var bx2 = cornerPts.Max(p => p.x); var by2 = cornerPts.Max(p => p.y);
            DrawBoxPreview(dl, new RegionRect(bx1, by1, bx2, by2), 0x1500A5FF, 0xFF00A5FF, 1.5f);
        }

        // hover marker modes:
        //   1 = tile diamond on the Z-readout surface
        //   2 = ClassicUO item glow: pixel-picks the sprite under the cursor (the item
        //       you SEE, not whatever stands on the ground tile) and glows only it;
        //       bare land shows nothing at all in this mode
        //   3 = both: the glow plus the diamond (on the picked item's own tile when
        //       there is one, else on the hovered ground tile)
        hoveredItem = null;
        if (hoverMode != 0 && !ImGui.GetIO().WantCaptureMouse)
        {
            bool wantItem = hoverMode is 2 or 3;
            bool wantTile = hoverMode is 1 or 3;
            if (wantItem && Iso)
            {
                var mp = Unrot(ImGui.GetMousePos());
                hoveredItem = artView.PickStaticAt(mp.X / zoom + originX, mp.Y / zoom + originY);
                if (hoveredItem is { } hi &&
                    artView.StaticSpriteRect(hi.tx, hi.ty, hi.id, hi.hue, hi.z) is { } r &&
                    GetStaticSpriteTexture(hi.id, hi.hue) is var tex && tex != 0)
                {
                    Vector2 C(double wx, double wy) => Rot(new Vector2((float)((wx - originX) * zoom), (float)((wy - originY) * zoom)));
                    var a = C(r.x, r.y);
                    var b = C(r.x + r.w, r.y);
                    var c = C(r.x + r.w, r.y + r.h);
                    var d = C(r.x, r.y + r.h);
                    // the sprite redrawn over itself with a tinted multiply = the item glows
                    dl.AddImageQuad((nint)tex, a, b, c, d,
                        new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), UCol(hoverColor));
                }
            }
            if (wantTile && Iso)
            {
                var (dx, dy) = hoveredItem is { } hj ? (hj.tx, hj.ty) : mouseTile;
                var q = artView.TileHighlightQuad(dx, dy);
                Vector2 HP(int i) => Rot(new Vector2((float)((q[i].x - originX) * zoom), (float)((q[i].y - originY) * zoom)));
                Vector2 h1 = HP(0), h2 = HP(1), h3 = HP(2), h4 = HP(3);
                dl.AddQuadFilled(h1, h2, h3, h4, UCol(hoverColor with { W = 0.16f }));
                dl.AddQuad(h1, h2, h3, h4, UCol(hoverColor with { W = 1f }), 2f);
            }
            else if (wantTile && zoom >= 2)   // flat map: only once a tile is a few pixels big
            {
                var h1 = T(mouseTile.x, mouseTile.y);
                var h3 = T(mouseTile.x + 1, mouseTile.y + 1);
                dl.AddRectFilled(h1, h3, UCol(hoverColor with { W = 0.16f }));
                dl.AddRect(h1, h3, UCol(hoverColor with { W = 1f }), 0, ImDrawFlags.None, 2f);
            }
        }
    }

    // the item the glow marks this frame (mode 2); the status bar reports ITS x/y/z
    (int tx, int ty, ushort id, ushort hue, sbyte z)? hoveredItem;

    // share our view-center tile with teammates (throttled; heartbeat even when idle
    // so a fresh joiner learns everyone's spot within a few seconds)
    void SendPresence()
    {
        if (net is not { Connected: true }) return;
        long now = Environment.TickCount64;
        if (now - lastPosSent < 1500) return;
        var c = TileAt(new Vector2(window.Size.X / 2f, window.Size.Y / 2f));
        if (c == lastPosTile && now - lastPosSent < 5000) return;
        lastPosSent = now;
        lastPosTile = c;
        net.PushPos(c.x, c.y);
    }

    // latest known position for a user name (multiple sessions: freshest wins)
    (int x, int y)? LatestPosFor(string user)
    {
        (int x, int y)? best = null;
        long bestAt = -1;
        foreach (var v in remotePos.Values)
            if (v.by == user && v.at > bestAt) { bestAt = v.at; best = (v.x, v.y); }
        return best;
    }

    // broadcast our in-progress stroke (throttled ~10Hz, only on change) so teammates
    // watch the box/lasso grow live; a Kind=0 clear is sent the moment it ends
    void SendDrawPreview()
    {
        if (net is not { Connected: true, ReadOnly: false }) return;
        DrawPreview p = null;
        if (exportPicking)
        {
            // picking a PNG area is not drawing - never broadcast it as a stroke
        }
        else if (drag == Drag.Rubber || pendingAnchor != null)
        {
            var an = pendingAnchor ?? rubberAnchor;
            p = new DrawPreview
            {
                Kind = 1, Erase = mode == Mode.EraseBox,
                X1 = Math.Min(an.x, rubberLast.x), Y1 = Math.Min(an.y, rubberLast.y),
                X2 = Math.Max(an.x, rubberLast.x), Y2 = Math.Max(an.y, rubberLast.y),
            };
        }
        else if (drag == Drag.Lasso && lassoPts.Count > 1)
        {
            p = new DrawPreview { Kind = 2, Erase = mode == Mode.EraseLasso };
            int step = Math.Max(1, lassoPts.Count / 600);   // thin long strokes on the wire
            for (int i = 0; i < lassoPts.Count; i += step)
            {
                p.Path.Add(lassoPts[i].x);
                p.Path.Add(lassoPts[i].y);
            }
        }
        else if (drag is Drag.Move or Drag.Resize && selRect != null)
        {
            // teammates watch the box being moved/resized live, in its current shape
            p = new DrawPreview
            {
                Kind = 1,
                X1 = Math.Min(selRect.X1, selRect.X2), Y1 = Math.Min(selRect.Y1, selRect.Y2),
                X2 = Math.Max(selRect.X1, selRect.X2), Y2 = Math.Max(selRect.Y1, selRect.Y2),
            };
        }
        else if (drag == Drag.Brush && brushPath.Count > 1)
        {
            // teammates see the stroke centerline while the brush is painting
            p = new DrawPreview { Kind = 2, Erase = mode == Mode.BrushErase };
            int step = Math.Max(1, brushPath.Count / 600);
            for (int i = 0; i < brushPath.Count; i += step)
            {
                p.Path.Add(brushPath[i].x);
                p.Path.Add(brushPath[i].y);
            }
        }
        long now = Environment.TickCount64;
        if (p == null)
        {
            if (prevActive)
            {
                net.PushPreview(new DrawPreview { Kind = 0 });
                prevActive = false;
                lastPrevSig = "";
            }
            return;
        }
        string sig = $"{p.Kind}|{p.Erase}|{p.X1},{p.Y1},{p.X2},{p.Y2}|{p.Path.Count}|{(p.Path.Count >= 2 ? p.Path[^2] + "," + p.Path[^1] : "")}";
        if (sig == lastPrevSig || now - lastPrevSent < 100) return;
        lastPrevSent = now;
        lastPrevSig = sig;
        prevActive = true;
        net.PushPreview(p);
    }

    // teammates' live strokes: their box/lasso in a distinct style with their name tag
    void DrawRemotePreviews(ImDrawListPtr dl)
    {
        if (remoteDraws.Count == 0) return;
        long now = Environment.TickCount64;
        List<string> stale = null;
        foreach (var (sess, (p, at)) in remoteDraws)
        {
            if (now - at > 5000) { (stale ??= new List<string>()).Add(sess); continue; }
            uint line = p.Erase ? 0xFF5050FFu : 0xFFFFB050u;   // red = erasing, azure = drawing
            uint fill = (line & 0x00FFFFFFu) | 0x20000000u;
            Vector2 tag;
            if (p.Kind == 1)
            {
                var rr = new RegionRect(p.X1, p.Y1, p.X2, p.Y2);
                DrawRegionShape(dl, rr, fill, line, 1.5f);
                tag = T(p.X1, p.Y1);
            }
            else if (p.Kind == 2 && p.Path.Count >= 4)
            {
                for (int i = 3; i < p.Path.Count; i += 2)
                    dl.AddLine(T(p.Path[i - 3] + 0.5, p.Path[i - 2] + 0.5),
                               T(p.Path[i - 1] + 0.5, p.Path[i] + 0.5), line, 2f);
                tag = T(p.Path[0] + 0.5, p.Path[1] + 0.5);
            }
            else continue;
            var sz = ImGui.CalcTextSize(p.By);
            var bg = tag with { Y = tag.Y - sz.Y - 3 };
            dl.AddRectFilled(bg, bg + sz + new Vector2(4, 2), 0xAA000000);
            dl.AddText(bg + new Vector2(2, 1), line, p.By);
        }
        if (stale != null)
            foreach (var s in stale)
                remoteDraws.Remove(s);
    }

    void DrawIsoWorld(ImDrawListPtr dl)
    {
        // native chunks up close, 4x-downsampled overview chunks when zoomed out.
        // lower render quality raises the switch point (overview chunks cover 16x the
        // area per rasterization); quality <=2 stays on the overview at EVERY zoom.
        double lodZoom = 0.45 + (10 - renderQuality) * 0.15;
        int lod = zoom >= lodZoom && renderQuality > 2 ? 0 : 2;
        // visible world range from the (possibly rotated) screen corners
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var sc in new[]
        {
            new Vector2(0, 0), new Vector2(window.Size.X, 0),
            new Vector2(window.Size.X, window.Size.Y), new Vector2(0, window.Size.Y),
        })
        {
            var s = Unrot(sc);
            double u = s.X / zoom + originX, v = s.Y / zoom + originY;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        // only flag the overview when the quality slider forced it - blur from plain
        // zooming out is self-explanatory, no need to narrate it
        if (lod != 0 && renderQuality < 10 && (zoom >= 0.45 || renderQuality <= 2))
            detailHint = $"overview quality (render quality {renderQuality}/10 - raise it in View > Options)";
        if (rollDeg != 0) detailHint = (detailHint == null ? "" : detailHint + "  |  ") + $"view rotated {rollDeg:0}° (double middle-click resets)";

        // hand the worker the current view: it renders nearest-to-center first and
        // drops queued chunks that scrolled away or belong to the wrong LOD
        UpdateChunkPriority(lod, minU, maxU, minV, maxV);

        // already-baked chunks of the OTHER lod fill in under the wanted layer while it
        // bakes, so panning/zooming/quality changes never leave black holes. Both
        // directions matter: dropping the quality slider flips a hot lod0 view to lod2
        // with nothing baked yet - the old sharp chunks must keep covering the screen.
        // (the >400-visible guard inside DrawIsoLayer keeps the lod0 peek off at far zoom)
        DrawIsoLayer(dl, lod == 0 ? 2 : 0, minU, maxU, minV, maxV, peekOnly: true);
        DrawIsoLayer(dl, lod, minU, maxU, minV, maxV, peekOnly: false);
    }

    void DrawIsoLayer(ImDrawListPtr dl, int lod, double minU, double maxU, double minV, double maxV, bool peekOnly)
    {
        int span = ArtView.IsoChunkPx << (lod == 0 ? 0 : 2);   // iso px covered per chunk
        int c0x = Math.Max(0, (int)Math.Floor(minU / span));
        int c1x = Math.Min(artView.IsoW / span, (int)Math.Floor(maxU / span));
        int c0y = Math.Max(0, (int)Math.Floor(minV / span));
        int c1y = Math.Min(artView.IsoH / span, (int)Math.Floor(maxV / span));
        long visible = (long)(c1x - c0x + 1) * (c1y - c0y + 1);
        if (visible > 400 && !peekOnly)
        {
            // safety net (the zoom floor keeps normal use well below this)
            detailHint = "zoom in for the CentrED view (or use the flat map for overview)";
            return;
        }
        if (visible > 400) return;
        for (int cy = c0y; cy <= c1y; cy++)
            for (int cx = c0x; cx <= c1x; cx++)
            {
                uint tex = GetIsoChunkTexture(cx, cy, lod, peekOnly);
                if (tex == 0) continue;
                var a = Rot(new Vector2((float)((cx * (long)span - originX) * zoom),
                                        (float)((cy * (long)span - originY) * zoom)));
                var b = Rot(new Vector2((float)(((cx + 1) * (long)span - originX) * zoom),
                                        (float)((cy * (long)span - originY) * zoom)));
                var c = Rot(new Vector2((float)(((cx + 1) * (long)span - originX) * zoom),
                                        (float)(((cy + 1) * (long)span - originY) * zoom)));
                var d = Rot(new Vector2((float)((cx * (long)span - originX) * zoom),
                                        (float)(((cy + 1) * (long)span - originY) * zoom)));
                dl.AddImageQuad((nint)tex, a, b, c, d,
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
            }
    }

    static Vector2[] Handles(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4) => new[]
    {
        p1, p2, p3, p4,
        (p1 + p2) / 2, (p2 + p3) / 2, (p3 + p4) / 2, (p4 + p1) / 2,
    };

    // ---- input ------------------------------------------------------------

    void HandleMapInput()
    {
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();
        mouseTile = TileAt(mouse);

        if (!io.WantCaptureKeyboard)
        {
            float dx = (KeyHeld(Silk.NET.Input.Key.D) ? 1 : 0) - (KeyHeld(Silk.NET.Input.Key.A) ? 1 : 0);
            float dy = (KeyHeld(Silk.NET.Input.Key.S) ? 1 : 0) - (KeyHeld(Silk.NET.Input.Key.W) ? 1 : 0);
            if (dx != 0 || dy != 0)
            {
                float speed = ShiftHeld ? 2.6f : 1.0f;
                var (wx, wy) = ScreenDeltaToWorld(dx * window.Size.X * speed * io.DeltaTime,
                                                  dy * window.Size.Y * speed * io.DeltaTime);
                originX += wx / zoom;
                originY += wy / zoom;
            }

            if (KeyHit(Silk.NET.Input.Key.Escape))
            {
                bool busy = drag != Drag.None || cornerPts.Count > 0 || pickTile || exportPicking || pendingAnchor != null;
                if ((drag == Drag.Move || drag == Drag.Resize) && selRect != null && dragRectStart != null && editFired)
                {
                    selRect.X1 = dragRectStart.X1; selRect.Y1 = dragRectStart.Y1;
                    selRect.X2 = dragRectStart.X2; selRect.Y2 = dragRectStart.Y2;
                    MarkDirty(selReg);
                }
                cornerPts.Clear();
                AbortDrag();
                pickTile = false;
                exportPicking = false;
                rollDeg = 0;                    // Esc also straightens the rotated view
                if (!busy) { selRect = null; selReg = null; }   // idle Esc drops the selection
            }
            if (KeyHit(Silk.NET.Input.Key.Enter) && mode == Mode.Corners && cornerPts.Count >= 2) CommitCorners();
            if (KeyHit(Silk.NET.Input.Key.Delete) && selReg != null && selRect != null)
            {
                SnapshotFor($"delete box in {selReg.DefName}", selReg);
                selReg.Rects.Remove(selRect);
                selRect = selReg.Rects.Count > 0 ? selReg.Rects[^1] : null;
                AbortDrag();
                MarkDirty(selReg);
            }
            int step = ShiftHeld ? 8 : 1;
            if (KeyHit(Silk.NET.Input.Key.Up)) Nudge(0, -step);
            if (KeyHit(Silk.NET.Input.Key.Down)) Nudge(0, step);
            if (KeyHit(Silk.NET.Input.Key.Left)) Nudge(-step, 0);
            if (KeyHit(Silk.NET.Input.Key.Right)) Nudge(step, 0);
            if (CtrlHeld && KeyHit(Silk.NET.Input.Key.Z) && !ShiftHeld && drag == Drag.None) DoUndo();
            if ((CtrlHeld && KeyHit(Silk.NET.Input.Key.Y)) ||
                (CtrlHeld && ShiftHeld && KeyHit(Silk.NET.Input.Key.Z)))
                if (drag == Drag.None) DoRedo();
            // CentrED-style function-key tool switching (same order as the Tools panel)
            foreach (var (key, m) in ToolKeys)
                if (KeyHit(key)) SetTool(m);
            // P = Pick P for the selected region (same as the Properties button)
            if (KeyHit(Silk.NET.Input.Key.P) && selReg != null && drag == Drag.None)
            {
                pickTile = true;
                status = "Click on the map to place P (Esc cancels).";
            }
        }

        if (io.WantCaptureMouse)
        {
            if (drag != Drag.None && !ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseDown(ImGuiMouseButton.Middle))
                EndLeftDrag();
            return;
        }

        if (io.MouseWheel != 0)
        {
            var um = Unrot(mouse);
            double ux = um.X / zoom + originX, uy = um.Y / zoom + originY;
            // iso floor 0.05: below that the visible overview-chunk count outruns every
            // cache (the old 0.02 floor caused eviction thrash = black screen)
            double zMin = Iso ? 0.05 : 0.03, zMax = Iso ? 4.0 : 24.0;
            zoom = Math.Clamp(zoom * Math.Pow(1.4, io.MouseWheel), zMin, zMax);
            originX = ux - um.X / zoom;
            originY = uy - um.Y / zoom;
        }

        // middle mouse: CentrED-style view rotation in iso (double-click resets), pan on the flat map
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var d = io.MouseDelta;
            if (Iso)
            {
                rollDeg = (rollDeg + d.X * 0.4) % 360.0;
            }
            else
            {
                originX -= d.X / zoom;
                originY -= d.Y / zoom;
            }
        }
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Middle)) rollDeg = 0;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            if (mode == Mode.Corners && cornerPts.Count >= 2) CommitCorners();
            else { cornerPts.Clear(); AbortDrag(); pickTile = false; }
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            OnLeftDown(mouse);

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && drag != Drag.None)
            OnLeftDragUpdate(mouse, io);

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            EndLeftDrag();

        if (mode == Mode.Corners && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && cornerPts.Count >= 2)
            CommitCorners();
    }

    void OnLeftDown(Vector2 mouse)
    {
        var tile = TileAt(mouse);
        if (exportPicking)
        {
            // the export dialog sent us: this drag picks the PNG area
            pendingAnchor = null;   // stale two-click Draw anchor must not warp the preview
            drag = Drag.Rubber;
            rubberAnchor = rubberLast = tile;
            return;
        }
        if (pickTile)
        {
            pickTile = false;
            if (selReg != null)
            {
                SnapshotFor($"set P of {selReg.DefName}", selReg);
                selReg.PX = tile.x;
                selReg.PY = tile.y;
                if (artView != null) selReg.PZ = artView.LandZAt(tile.x, tile.y);
                else if (sharedMapData != null) selReg.PZ = sharedMapData.LandAt(tile.x, tile.y).z;
                MarkDirty(selReg);
                status = $"P set to {tile.x},{tile.y} z{selReg.PZ}";
            }
            return;
        }
        switch (mode)
        {
            case Mode.Draw:
                // snapshot occupancy per box so the live preview can show the carve; a
                // layout change between the two corner clicks rebuilds it
                if (avoidOverlap && (strokeIndex == null || strokeIndexSig != RegionLayoutSig()))
                {
                    strokeIndex = BuildOverlapIndex(OverlapExcept());
                    strokeIndexSig = RegionLayoutSig();
                    drawPreviewSig = "";
                }
                if (pendingAnchor != null)
                {
                    var rr = new RegionRect(pendingAnchor.Value.x, pendingAnchor.Value.y, tile.x, tile.y);
                    pendingAnchor = null;
                    CommitDrawnRect(rr);
                    strokeIndex = null;
                }
                else
                {
                    drag = Drag.Rubber;
                    rubberAnchor = rubberLast = tile;
                }
                break;
            case Mode.Corners:
                if (avoidOverlap && (strokeIndex == null || strokeIndexSig != RegionLayoutSig()))
                {
                    strokeIndex = BuildOverlapIndex(OverlapExcept());
                    strokeIndexSig = RegionLayoutSig();
                    drawPreviewSig = "";
                }
                cornerPts.Add(tile);
                break;
            case Mode.EraseBox:
                drag = Drag.Rubber;
                rubberAnchor = rubberLast = tile;
                break;
            case Mode.Lasso:
            case Mode.EraseLasso:
                drag = Drag.Lasso;
                lassoPts.Clear();
                lassoPts.Add(tile);
                break;
            case Mode.BrushAdd:
            case Mode.BrushErase:
                drag = Drag.Brush;
                // snapshot occupancy once per stroke (add only - erasing never overlaps)
                strokeIndex = mode == Mode.BrushAdd && avoidOverlap ? BuildOverlapIndex(OverlapExcept()) : null;
                strokeIndexSig = RegionLayoutSig();
                brushMask.Clear();
                brushPath.Clear();
                brushPath.Add(tile);
                StampBrush(tile);
                lastBrushTile = tile;
                break;
            case Mode.Wand:
            case Mode.WandErase:
                // press starts radius sizing; a plain click (no drag) selects unlimited
                drag = Drag.Wand;
                wandDragSeed = tile;
                wandRadius = 0;
                break;
            case Mode.Select:
                if (selRect != null)
                {
                    RectCorners(selRect, out var p1, out var p2, out var p3, out var p4);
                    var hs = Handles(p1, p2, p3, p4);
                    for (int i = 0; i < hs.Length; i++)
                    {
                        if (Math.Abs(mouse.X - hs[i].X) <= 6 && Math.Abs(mouse.Y - hs[i].Y) <= 6)
                        {
                            drag = Drag.Resize;
                            resizeHandle = i;
                            pendingEditDesc = $"resize box in {selReg?.DefName}";
                            editFired = false;
                            dragRectStart = selRect.Clone();
                            return;
                        }
                    }
                }
                for (int ri = project.Regions.Count - 1; ri >= 0; ri--)
                {
                    var reg = project.Regions[ri];
                    if (!reg.Visible) continue;
                    for (int k = reg.Rects.Count - 1; k >= 0; k--)
                    {
                        if (reg.Rects[k].Contains(tile.x, tile.y))
                        {
                            selReg = reg;
                            selRect = reg.Rects[k];
                            scrollListToSel = true;
                            drag = Drag.Move;
                            pendingEditDesc = $"move box in {reg.DefName}";
                            editFired = false;
                            dragRectStart = selRect.Clone();
                            moveStartTile = tile;
                            return;
                        }
                    }
                }
                drag = Drag.Pan;
                break;
        }
    }

    void OnLeftDragUpdate(Vector2 mouse, ImGuiIOPtr io)
    {
        var tile = TileAt(mouse);
        switch (drag)
        {
            case Drag.Pan:
            {
                var (wx, wy) = ScreenDeltaToWorld(io.MouseDelta.X, io.MouseDelta.Y);
                originX -= wx / zoom;
                originY -= wy / zoom;
                break;
            }
            case Drag.Rubber:
                rubberLast = tile;
                break;
            case Drag.Wand:
            {
                double wdx = tile.x - wandDragSeed.x, wdy = tile.y - wandDragSeed.y;
                wandRadius = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(wdx * wdx + wdy * wdy)));
                break;
            }
            case Drag.Lasso:
                // connect with a tile line so fast strokes leave no gaps in the outline
                if (lassoPts.Count == 0) lassoPts.Add(tile);
                else if (lassoPts[^1] != tile)
                    foreach (var p in RegionOps.Line(lassoPts[^1], tile).Skip(1))
                        lassoPts.Add(p);
                break;
            case Drag.Brush:
                if (tile != lastBrushTile)
                {
                    foreach (var p in RegionOps.Line(lastBrushTile, tile).Skip(1))
                    {
                        StampBrush(p);
                        brushPath.Add(p);
                    }
                    lastBrushTile = tile;
                }
                break;
            case Drag.Move:
            {
                if (selRect == null) { drag = Drag.None; break; }
                int dx = SafeClamp(tile.x - moveStartTile.x, -dragRectStart.X1, mapW - 1 - dragRectStart.X2);
                int dy = SafeClamp(tile.y - moveStartTile.y, -dragRectStart.Y1, mapH - 1 - dragRectStart.Y2);
                if (dx == 0 && dy == 0 && !editFired) break;
                FireEditOnce();
                selRect.X1 = dragRectStart.X1 + dx; selRect.X2 = dragRectStart.X2 + dx;
                selRect.Y1 = dragRectStart.Y1 + dy; selRect.Y2 = dragRectStart.Y2 + dy;
                break;
            }
            case Drag.Resize:
            {
                if (selRect == null) { drag = Drag.None; break; }
                FireEditOnce();
                var r = dragRectStart.Clone();
                // handles are ordered p1..p4 = tile corners (x1,y1) (x2,y1) (x2,y2) (x1,y2) + edge mids
                bool west = resizeHandle is 0 or 3 or 7;
                bool east = resizeHandle is 1 or 2 or 5;
                bool north = resizeHandle is 0 or 1 or 4;
                bool south = resizeHandle is 2 or 3 or 6;
                if (west) r.X1 = tile.x;
                if (east) r.X2 = tile.x;
                if (north) r.Y1 = tile.y;
                if (south) r.Y2 = tile.y;
                r.Normalize();
                selRect.X1 = r.X1; selRect.X2 = r.X2;
                selRect.Y1 = r.Y1; selRect.Y2 = r.Y2;
                break;
            }
        }
    }

    void EndLeftDrag()
    {
        if (exportPicking && drag == Drag.Rubber)
        {
            exportArea = new RegionRect(rubberAnchor.x, rubberAnchor.y, rubberLast.x, rubberLast.y);
            exportPicking = false;
            drag = Drag.None;
            exportDialogOpen = true;   // hand the picked area back to the dialog
            return;
        }
        switch (drag)
        {
            case Drag.Rubber:
            {
                var dragPx = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                if (mode == Mode.EraseBox)
                {
                    // eraser is drag-only: a stray click must never eat a box
                    if (Math.Abs(dragPx.X) + Math.Abs(dragPx.Y) >= 4)
                        CommitEraseRect(new RegionRect(rubberAnchor.x, rubberAnchor.y, rubberLast.x, rubberLast.y));
                }
                else if (Math.Abs(dragPx.X) + Math.Abs(dragPx.Y) < 4)
                {
                    pendingAnchor = rubberAnchor;
                    rubberLast = rubberAnchor;
                }
                else
                {
                    CommitDrawnRect(new RegionRect(rubberAnchor.x, rubberAnchor.y, rubberLast.x, rubberLast.y));
                    strokeIndex = null;   // next box re-snapshots (this one may have created a region)
                }
                break;
            }
            case Drag.Lasso:
                if (lassoPts.Count >= 3)
                {
                    if (mode == Mode.EraseLasso) CommitLassoErase(lassoPts);
                    else CommitLassoAdd(lassoPts);
                }
                lassoPts.Clear();
                break;
            case Drag.Brush:
                if (brushMask.Count > 0)
                {
                    if (mode == Mode.BrushErase) CommitMaskErase(brushMask, "brush erase");
                    else CommitMaskAdd(brushMask, "brush");
                }
                else if (mode == Mode.BrushAdd && strokeIndex is { IsEmpty: false })
                {
                    // every stamped tile was blocked - say so instead of a silent no-op
                    status = strokeIndex.Blocker(lastBrushTile.x, lastBrushTile.y) is { } bw
                        ? $"brush: nothing added - that area is inside '{bw}'."
                        : "brush: nothing added - that area is inside another region.";
                }
                brushMask.Clear();
                brushPath.Clear();
                brushPreview = null;
                strokeIndex = null;
                break;
            case Drag.Move:
            case Drag.Resize:
                if (editFired) MarkDirty(selReg);
                break;
            case Drag.Wand:
            {
                var wandPx = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                if (Math.Abs(wandPx.X) + Math.Abs(wandPx.Y) < 4) wandRadius = 0;   // plain click = whole area
                WandCommit(wandDragSeed, mode == Mode.WandErase);
                wandRadius = 0;
                break;
            }
        }
        drag = Drag.None;
        resizeHandle = -1;
        pendingEditDesc = null;
        editFired = false;
    }

    void FireEditOnce()
    {
        if (editFired || pendingEditDesc == null) return;
        editFired = true;
        SnapshotFor(pendingEditDesc, selReg);
    }

    void CommitCorners()
    {
        var bx1 = cornerPts.Min(p => p.x); var by1 = cornerPts.Min(p => p.y);
        var bx2 = cornerPts.Max(p => p.x); var by2 = cornerPts.Max(p => p.y);
        cornerPts.Clear();
        CommitDrawnRect(new RegionRect(bx1, by1, bx2, by2));
    }

    void Nudge(int dx, int dy)
    {
        if (selRect == null) return;
        SnapshotFor($"nudge box in {selReg?.DefName}", selReg, "nudge|" + selReg?.Uid);
        dx = SafeClamp(dx, -selRect.X1, mapW - 1 - selRect.X2);
        dy = SafeClamp(dy, -selRect.Y1, mapH - 1 - selRect.Y2);
        selRect.X1 += dx; selRect.X2 += dx;
        selRect.Y1 += dy; selRect.Y2 += dy;
        MarkDirty(selReg);
    }

    static int SafeClamp(int v, int min, int max) => min > max ? 0 : Math.Clamp(v, min, max);

    // a screen-space movement expressed in (unrotated) world-pixel space
    (double wx, double wy) ScreenDeltaToWorld(double sx, double sy)
    {
        if (!Iso || rollDeg == 0) return (sx, sy);
        double a = -rollDeg * Math.PI / 180.0;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        return (sx * cos - sy * sin, sx * sin + sy * cos);
    }
}
