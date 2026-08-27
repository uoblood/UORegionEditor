using System.Drawing.Drawing2D;

namespace UORegionEditor;

public enum CanvasMode { Select, Draw, Corners }

// Map view: 1 map pixel = 1 game tile. Zoom/pan, draw new rects (drag or click-click),
// 4-corner point mode (bounding box), select/move/resize existing rects.
public class MapCanvas : Control
{
    public Project Project = new();
    public RegionDef SelectedRegion;
    public RegionRect SelectedRect;
    public bool AddToSelected = true;
    public bool PickTileMode;                      // next left click reports a tile (for setting P)
    public bool DetailMode;                        // CentrED-look layer on top of the radar map
    public Func<int, int, Bitmap> DetailChunkProvider;   // (chunkX, chunkY) -> 512px bitmap or null (renders async)

    public event Action SelectionChanged;          // selected region/rect changed by canvas
    public event Action RegionsChanged;            // geometry edited (move/resize/delete)
    public event Action<string, string> EditStarting;  // (description, coalesceKey) - fired BEFORE a mutation
    public event Action<string> Status;
    public event Action<RegionRect> RectDrawn;     // a new rect was completed
    public event Action<Point> TilePicked;

    Bitmap map;
    readonly List<Bitmap> mips = new();            // [0]=full, then /2 each level
    float zoom = 0.2f;
    PointF origin;                                 // map coordinate at control (0,0)

    CanvasMode mode = CanvasMode.Select;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public CanvasMode Mode
    {
        get => mode;
        set { mode = value; cornerPts.Clear(); pendingAnchor = null; PickTileMode = false; Invalidate(); PushStatus(); }
    }

    enum Drag { None, Pan, Rubber, Move, Resize }
    Drag drag = Drag.None;
    string pendingEditDesc;    // EditStarting fires on the FIRST real change, not on the click
    bool editFired;
    Point dragStartScreen;
    PointF dragStartOrigin;
    Point rubberAnchor, rubberLast;
    Point? pendingAnchor;                          // click-click first corner (Draw mode)
    Point lastMouseTile;
    int resizeHandle = -1;                         // 0..7: corners 0-3 (NW,NE,SE,SW), edges 4-7 (N,E,S,W)
    RegionRect dragRectStart;
    Point moveStartTile;
    readonly List<Point> cornerPts = new();

    // smooth WASD panning: velocity applied on a ~60fps timer while keys are held (CentrED feel)
    readonly HashSet<Keys> panKeys = new();
    readonly System.Windows.Forms.Timer panTimer;
    long lastPanTick;

    public MapCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Cross;
        panTimer = new System.Windows.Forms.Timer { Interval = 15 };
        panTimer.Tick += (_, _) => PanTick();
    }

    void PanTick()
    {
        long now = Environment.TickCount64;
        float dt = Math.Clamp((now - lastPanTick) / 1000f, 0f, 0.1f);
        lastPanTick = now;
        int dx = (panKeys.Contains(Keys.D) ? 1 : 0) - (panKeys.Contains(Keys.A) ? 1 : 0);
        int dy = (panKeys.Contains(Keys.S) ? 1 : 0) - (panKeys.Contains(Keys.W) ? 1 : 0);
        if (dx == 0 && dy == 0) { panTimer.Stop(); return; }
        // viewport-fractions per second so speed feels identical at every zoom
        float speed = (ModifierKeys & Keys.Shift) != 0 ? 2.6f : 1.0f;
        origin = new PointF(
            origin.X + dx * Width * speed * dt / zoom,
            origin.Y + dy * Height * speed * dt / zoom);
        Invalidate();
        PushStatus();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        panKeys.Remove(e.KeyCode);
        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        panKeys.Clear();
        panTimer.Stop();
        base.OnLostFocus(e);
    }

    public void SetMap(Bitmap bmp)
    {
        foreach (var m in mips) m.Dispose();
        mips.Clear();
        map = bmp;
        if (map != null)
        {
            mips.Add(map);
            var cur = map;
            while (cur.Width > 1024 && cur.Height > 1024)
            {
                var half = new Bitmap(cur.Width / 2, cur.Height / 2);
                using (var g = Graphics.FromImage(half))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(cur, new Rectangle(0, 0, half.Width, half.Height));
                }
                mips.Add(half);
                cur = half;
            }
            ZoomFit();
        }
        Invalidate();
    }

    public int MapW => map?.Width ?? 7168;
    public int MapH => map?.Height ?? 4096;

    // ---- transforms -------------------------------------------------------

    public PointF MapToScreen(float mx, float my) => new((mx - origin.X) * zoom, (my - origin.Y) * zoom);
    public PointF ScreenToMap(float sx, float sy) => new(sx / zoom + origin.X, sy / zoom + origin.Y);

    Point TileAt(Point s)
    {
        var m = ScreenToMap(s.X, s.Y);
        int x = Math.Clamp((int)Math.Floor(m.X), 0, MapW - 1);
        int y = Math.Clamp((int)Math.Floor(m.Y), 0, MapH - 1);
        return new Point(x, y);
    }

    RectangleF RectToScreen(RegionRect r)
    {
        var a = MapToScreen(r.X1, r.Y1);
        var b = MapToScreen(r.X2 + 1, r.Y2 + 1);   // inclusive tile -> outer pixel edge
        return new RectangleF(a.X, a.Y, b.X - a.X, b.Y - a.Y);
    }

    public void ZoomFit()
    {
        if (Width <= 0 || Height <= 0) return;
        zoom = Math.Min((float)Width / MapW, (float)Height / MapH);
        if (zoom <= 0) zoom = 0.1f;
        origin = new PointF((MapW - Width / zoom) / 2f, (MapH - Height / zoom) / 2f);
        Invalidate();
    }

    public void CenterOn(int x, int y, float newZoom = -1)
    {
        if (newZoom > 0) zoom = newZoom;
        origin = new PointF(x + 0.5f - Width / (2f * zoom), y + 0.5f - Height / (2f * zoom));
        Invalidate();
    }

    public void ZoomToRegion(RegionDef r)
    {
        if (r == null || r.Rects.Count == 0) return;
        int x1 = r.Rects.Min(t => t.X1), y1 = r.Rects.Min(t => t.Y1);
        int x2 = r.Rects.Max(t => t.X2), y2 = r.Rects.Max(t => t.Y2);
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        zoom = Math.Clamp(Math.Min((float)Width / (w * 2f), (float)Height / (h * 2f)), 0.05f, 16f);
        CenterOn((x1 + x2) / 2, (y1 + y2) / 2);
    }

    // ---- painting ---------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(16, 16, 16));
        if (map == null)
        {
            TextRenderer.DrawText(g, "No map loaded - pick a muls folder (Map > Muls Folder...)",
                Font, ClientRectangle, Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        // pick the mip level whose scale best matches the zoom (avoids huge downscales per paint)
        int level = 0;
        float levelScale = 1f;
        while (level + 1 < mips.Count && zoom < levelScale / 2f) { level++; levelScale /= 2f; }
        var src = mips[level];

        float vx0 = Math.Max(0, origin.X), vy0 = Math.Max(0, origin.Y);
        float vx1 = Math.Min(MapW, origin.X + Width / zoom);
        float vy1 = Math.Min(MapH, origin.Y + Height / zoom);
        if (vx1 > vx0 && vy1 > vy0)
        {
            var srcRect = new RectangleF(vx0 * levelScale, vy0 * levelScale,
                                         (vx1 - vx0) * levelScale, (vy1 - vy0) * levelScale);
            var a = MapToScreen(vx0, vy0);
            var destRect = new RectangleF(a.X, a.Y, (vx1 - vx0) * zoom, (vy1 - vy0) * zoom);
            g.InterpolationMode = zoom >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, destRect, srcRect, GraphicsUnit.Pixel);
        }

        // CentrED-look detail layer (8 px/tile chunks) once zoomed in enough to matter
        if (DetailMode && DetailChunkProvider != null && zoom >= 1.5f)
        {
            int c0x = Math.Max(0, (int)Math.Floor(origin.X / ArtView.ChunkTiles));
            int c1x = (int)Math.Floor(Math.Min(MapW - 1, origin.X + Width / zoom) / ArtView.ChunkTiles);
            int c0y = Math.Max(0, (int)Math.Floor(origin.Y / ArtView.ChunkTiles));
            int c1y = (int)Math.Floor(Math.Min(MapH - 1, origin.Y + Height / zoom) / ArtView.ChunkTiles);
            g.InterpolationMode = zoom >= 8f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            for (int cyi = c0y; cyi <= c1y; cyi++)
                for (int cxi = c0x; cxi <= c1x; cxi++)
                {
                    var bmp = DetailChunkProvider(cxi, cyi);
                    if (bmp == null) continue;   // renders in the background; radar shows meanwhile
                    var a = MapToScreen(cxi * ArtView.ChunkTiles, cyi * ArtView.ChunkTiles);
                    var b = MapToScreen((cxi + 1) * ArtView.ChunkTiles, (cyi + 1) * ArtView.ChunkTiles);
                    g.DrawImage(bmp, new RectangleF(a.X, a.Y, b.X - a.X, b.Y - a.Y),
                        new RectangleF(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);
                }
        }

        // tile grid at deep zoom
        if (zoom >= 8f)
        {
            using var gridPen = new Pen(Color.FromArgb(38, 255, 255, 255));
            int gx0 = (int)Math.Floor(Math.Max(0, origin.X));
            int gx1 = (int)Math.Ceiling(Math.Min(MapW, origin.X + Width / zoom));
            int gy0 = (int)Math.Floor(Math.Max(0, origin.Y));
            int gy1 = (int)Math.Ceiling(Math.Min(MapH, origin.Y + Height / zoom));
            for (int x = gx0; x <= gx1; x++)
            {
                var p = MapToScreen(x, gy0); var q = MapToScreen(x, gy1);
                g.DrawLine(gridPen, p.X, p.Y, q.X, q.Y);
            }
            for (int y = gy0; y <= gy1; y++)
            {
                var p = MapToScreen(gx0, y); var q = MapToScreen(gx1, y);
                g.DrawLine(gridPen, p.X, p.Y, q.X, q.Y);
            }
        }

        // regions
        foreach (var r in Project.Regions)
        {
            if (!r.Visible) continue;
            bool selReg = r == SelectedRegion;
            using var fill = new SolidBrush(Color.FromArgb(selReg ? 90 : 60, r.Color));
            using var pen = new Pen(r.Color, selReg ? 2.5f : 1.2f);
            foreach (var rc in r.Rects)
            {
                var sr = RectToScreen(rc);
                if (sr.Right < 0 || sr.Bottom < 0 || sr.Left > Width || sr.Top > Height) continue;
                g.FillRectangle(fill, sr);
                g.DrawRectangle(pen, sr.X, sr.Y, sr.Width, sr.Height);
            }
            if (r.Rects.Count > 0 && zoom >= 0.12f)
            {
                var first = r.Rects[0];
                var lp = MapToScreen(first.X1, first.Y1);
                var label = r.Name;
                var sz = TextRenderer.MeasureText(label, Font);
                var bg = new Rectangle((int)lp.X, (int)lp.Y - sz.Height - 2, sz.Width, sz.Height);
                using var lb = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                g.FillRectangle(lb, bg);
                TextRenderer.DrawText(g, label, Font, bg, r.Color, TextFormatFlags.NoPadding);
            }
            // P marker
            if (selReg && r.Rects.Count > 0 && zoom >= 0.5f)
            {
                var (px, py) = r.EffectiveP();
                var c = MapToScreen(px + 0.5f, py + 0.5f);
                using var pp = new Pen(Color.White, 2f);
                g.DrawLine(pp, c.X - 6, c.Y, c.X + 6, c.Y);
                g.DrawLine(pp, c.X, c.Y - 6, c.X, c.Y + 6);
                TextRenderer.DrawText(g, "P", Font, new Point((int)c.X + 5, (int)c.Y + 3), Color.White);
            }
        }

        // selected rect: dashed outline + handles
        if (SelectedRect != null && SelectedRegion != null && mode == CanvasMode.Select)
        {
            var sr = RectToScreen(SelectedRect);
            using var dash = new Pen(Color.White, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(dash, sr.X, sr.Y, sr.Width, sr.Height);
            using var hb = new SolidBrush(Color.White);
            foreach (var h in HandlePoints(sr))
                g.FillRectangle(hb, h.X - 3, h.Y - 3, 7, 7);
        }

        // rubber band (draw mode drag or click-click preview)
        if (drag == Drag.Rubber || pendingAnchor != null)
        {
            var a = pendingAnchor ?? rubberAnchor;
            var rr = new RegionRect(a.X, a.Y, rubberLast.X, rubberLast.Y);
            var sr = RectToScreen(rr);
            using var dash = new Pen(Color.Yellow, 1.5f) { DashStyle = DashStyle.Dash };
            using var fill = new SolidBrush(Color.FromArgb(50, Color.Yellow));
            g.FillRectangle(fill, sr);
            g.DrawRectangle(dash, sr.X, sr.Y, sr.Width, sr.Height);
            TextRenderer.DrawText(g, $"{rr.W} x {rr.H}", Font,
                new Point((int)sr.Right + 4, (int)sr.Bottom + 4), Color.Yellow);
        }

        // corner points mode
        if (cornerPts.Count > 0)
        {
            using var cp = new Pen(Color.Orange, 2f);
            foreach (var pt in cornerPts)
            {
                var c = MapToScreen(pt.X + 0.5f, pt.Y + 0.5f);
                g.DrawEllipse(cp, c.X - 4, c.Y - 4, 8, 8);
            }
            var bx1 = cornerPts.Min(p => p.X); var by1 = cornerPts.Min(p => p.Y);
            var bx2 = cornerPts.Max(p => p.X); var by2 = cornerPts.Max(p => p.Y);
            var srB = RectToScreen(new RegionRect(bx1, by1, bx2, by2));
            using var dash = new Pen(Color.Orange, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(dash, srB.X, srB.Y, srB.Width, srB.Height);
        }
    }

    static PointF[] HandlePoints(RectangleF r) => new[]
    {
        new PointF(r.Left, r.Top), new PointF(r.Right, r.Top),
        new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom),
        new PointF((r.Left + r.Right) / 2, r.Top), new PointF(r.Right, (r.Top + r.Bottom) / 2),
        new PointF((r.Left + r.Right) / 2, r.Bottom), new PointF(r.Left, (r.Top + r.Bottom) / 2),
    };

    // ---- input ------------------------------------------------------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var before = ScreenToMap(e.X, e.Y);
        float factor = e.Delta > 0 ? 1.4f : 1f / 1.4f;
        zoom = Math.Clamp(zoom * factor, 0.03f, 24f);
        origin = new PointF(before.X - e.X / zoom, before.Y - e.Y / zoom);
        Invalidate();
        PushStatus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var tile = TileAt(e.Location);

        if (e.Button == MouseButtons.Middle)
        {
            drag = Drag.Pan; dragStartScreen = e.Location; dragStartOrigin = origin;
            Cursor = Cursors.SizeAll;
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            if (mode == CanvasMode.Corners && cornerPts.Count >= 2) { CommitCorners(); return; }
            cornerPts.Clear(); pendingAnchor = null; drag = Drag.None; PickTileMode = false;
            Invalidate();
            PushStatus();
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        if (PickTileMode)
        {
            PickTileMode = false;
            TilePicked?.Invoke(tile);
            return;
        }

        switch (mode)
        {
            case CanvasMode.Draw:
                if (pendingAnchor != null)
                {
                    // second click of click-click
                    var rr = new RegionRect(pendingAnchor.Value.X, pendingAnchor.Value.Y, tile.X, tile.Y);
                    pendingAnchor = null;
                    RectDrawn?.Invoke(rr);
                    Invalidate();
                }
                else
                {
                    drag = Drag.Rubber;
                    rubberAnchor = rubberLast = tile;
                    dragStartScreen = e.Location;
                }
                break;

            case CanvasMode.Corners:
                cornerPts.Add(tile);
                Invalidate();
                PushStatus();
                break;

            case CanvasMode.Select:
                // handles of the selected rect first
                if (SelectedRect != null)
                {
                    var sr = RectToScreen(SelectedRect);
                    var hp = HandlePoints(sr);
                    for (int i = 0; i < hp.Length; i++)
                    {
                        if (Math.Abs(e.X - hp[i].X) <= 6 && Math.Abs(e.Y - hp[i].Y) <= 6)
                        {
                            drag = Drag.Resize; resizeHandle = i;
                            pendingEditDesc = $"resize box in {SelectedRegion?.DefName}";
                            editFired = false;
                            dragRectStart = SelectedRect.Clone();
                            return;
                        }
                    }
                }
                // topmost rect under cursor (later regions draw on top)
                for (int ri = Project.Regions.Count - 1; ri >= 0; ri--)
                {
                    var reg = Project.Regions[ri];
                    if (!reg.Visible) continue;
                    for (int k = reg.Rects.Count - 1; k >= 0; k--)
                    {
                        if (reg.Rects[k].Contains(tile.X, tile.Y))
                        {
                            SelectedRegion = reg; SelectedRect = reg.Rects[k];
                            SelectionChanged?.Invoke();
                            drag = Drag.Move;
                            pendingEditDesc = $"move box in {reg.DefName}";
                            editFired = false;
                            dragRectStart = SelectedRect.Clone();
                            moveStartTile = tile;
                            Invalidate();
                            return;
                        }
                    }
                }
                // empty space -> pan
                drag = Drag.Pan; dragStartScreen = e.Location; dragStartOrigin = origin;
                Cursor = Cursors.SizeAll;
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var tile = TileAt(e.Location);
        lastMouseTile = tile;

        switch (drag)
        {
            case Drag.Pan:
                origin = new PointF(
                    dragStartOrigin.X - (e.X - dragStartScreen.X) / zoom,
                    dragStartOrigin.Y - (e.Y - dragStartScreen.Y) / zoom);
                Invalidate();
                break;
            case Drag.Rubber:
                rubberLast = tile;
                Invalidate();
                break;
            case Drag.Move:
            {
                if (SelectedRect == null) { drag = Drag.None; break; }
                int dx = tile.X - moveStartTile.X, dy = tile.Y - moveStartTile.Y;
                dx = SafeClamp(dx, -dragRectStart.X1, MapW - 1 - dragRectStart.X2);
                dy = SafeClamp(dy, -dragRectStart.Y1, MapH - 1 - dragRectStart.Y2);
                if (dx == 0 && dy == 0 && !editFired) break;   // plain click: no edit yet
                FireEditOnce();
                SelectedRect.X1 = dragRectStart.X1 + dx; SelectedRect.X2 = dragRectStart.X2 + dx;
                SelectedRect.Y1 = dragRectStart.Y1 + dy; SelectedRect.Y2 = dragRectStart.Y2 + dy;
                Invalidate();
                break;
            }
            case Drag.Resize:
            {
                if (SelectedRect == null) { drag = Drag.None; break; }
                FireEditOnce();
                var r = dragRectStart.Clone();
                bool west = resizeHandle is 0 or 3 or 7;
                bool east = resizeHandle is 1 or 2 or 5;
                bool north = resizeHandle is 0 or 1 or 4;
                bool south = resizeHandle is 2 or 3 or 6;
                if (west) r.X1 = tile.X;
                if (east) r.X2 = tile.X;
                if (north) r.Y1 = tile.Y;
                if (south) r.Y2 = tile.Y;
                r.Normalize();
                SelectedRect.X1 = r.X1; SelectedRect.X2 = r.X2;
                SelectedRect.Y1 = r.Y1; SelectedRect.Y2 = r.Y2;
                Invalidate();
                break;
            }
            default:
                if (pendingAnchor != null) { rubberLast = tile; Invalidate(); }
                break;
        }
        PushStatus();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        switch (drag)
        {
            case Drag.Pan:
                Cursor = Cursors.Cross;
                break;
            case Drag.Rubber:
            {
                var moved = Math.Abs(e.X - dragStartScreen.X) + Math.Abs(e.Y - dragStartScreen.Y);
                if (moved < 4)
                {
                    // treat as first click of click-click
                    pendingAnchor = rubberAnchor;
                    rubberLast = rubberAnchor;
                }
                else
                {
                    var rr = new RegionRect(rubberAnchor.X, rubberAnchor.Y, rubberLast.X, rubberLast.Y);
                    RectDrawn?.Invoke(rr);
                }
                Invalidate();
                break;
            }
            case Drag.Move:
            case Drag.Resize:
                if (editFired) RegionsChanged?.Invoke();   // a plain click without movement changed nothing
                break;
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
        EditStarting?.Invoke(pendingEditDesc, null);
    }

    public bool IsDragging => drag is Drag.Move or Drag.Resize or Drag.Rubber;

    // called when the region list is replaced under us (server sync, undo): never keep
    // mutating an object that may no longer be in the project
    public void AbortDrag()
    {
        drag = Drag.None;
        resizeHandle = -1;
        pendingEditDesc = null;
        editFired = false;
        pendingAnchor = null;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (mode == CanvasMode.Corners && cornerPts.Count >= 2) CommitCorners();
    }

    void CommitCorners()
    {
        var bx1 = cornerPts.Min(p => p.X); var by1 = cornerPts.Min(p => p.Y);
        var bx2 = cornerPts.Max(p => p.X); var by2 = cornerPts.Max(p => p.Y);
        cornerPts.Clear();
        RectDrawn?.Invoke(new RegionRect(bx1, by1, bx2, by2));
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.Shift ? 8 : 1;
        switch (e.KeyCode)
        {
            case Keys.Escape:
                if ((drag == Drag.Move || drag == Drag.Resize) && SelectedRect != null && dragRectStart != null)
                {
                    // cancel = restore the rect to where the drag started
                    SelectedRect.X1 = dragRectStart.X1; SelectedRect.Y1 = dragRectStart.Y1;
                    SelectedRect.X2 = dragRectStart.X2; SelectedRect.Y2 = dragRectStart.Y2;
                    RegionsChanged?.Invoke();
                }
                cornerPts.Clear(); pendingAnchor = null; drag = Drag.None; resizeHandle = -1; PickTileMode = false;
                Invalidate();
                PushStatus();
                break;
            case Keys.Enter:
                if (mode == CanvasMode.Corners && cornerPts.Count >= 2) CommitCorners();
                break;
            case Keys.Delete:
                if (SelectedRegion != null && SelectedRect != null)
                {
                    EditStarting?.Invoke($"delete box in {SelectedRegion.DefName}", null);
                    SelectedRegion.Rects.Remove(SelectedRect);
                    SelectedRect = null;
                    drag = Drag.None; resizeHandle = -1;   // never continue a drag on a deleted rect
                    RegionsChanged?.Invoke();
                    Invalidate();
                }
                break;
            case Keys.Up: Nudge(0, -step); break;
            case Keys.Down: Nudge(0, step); break;
            case Keys.Left: Nudge(-step, 0); break;
            case Keys.Right: Nudge(step, 0); break;
            // WASD pans the view, CentrED style: held keys feed the smooth pan timer
            case Keys.W:
            case Keys.A:
            case Keys.S:
            case Keys.D:
                panKeys.Add(e.KeyCode);
                if (!panTimer.Enabled) { lastPanTick = Environment.TickCount64; panTimer.Start(); }
                break;
        }
        base.OnKeyDown(e);
    }

    void Nudge(int dx, int dy)
    {
        if (SelectedRect == null) return;
        EditStarting?.Invoke($"nudge box in {SelectedRegion?.DefName}", "nudge|" + SelectedRegion?.DefName);
        dx = SafeClamp(dx, -SelectedRect.X1, MapW - 1 - SelectedRect.X2);
        dy = SafeClamp(dy, -SelectedRect.Y1, MapH - 1 - SelectedRect.Y2);
        SelectedRect.X1 += dx; SelectedRect.X2 += dx;
        SelectedRect.Y1 += dy; SelectedRect.Y2 += dy;
        RegionsChanged?.Invoke();
        Invalidate();
    }

    // Math.Clamp throws when min > max (a rect bigger than the map, e.g. the whole-facet RECT);
    // in that case the rect cannot move on that axis.
    static int SafeClamp(int v, int min, int max) => min > max ? 0 : Math.Clamp(v, min, max);

    void PushStatus()
    {
        string extra = mode switch
        {
            CanvasMode.Draw => pendingAnchor != null
                ? $"  |  first corner {pendingAnchor.Value.X},{pendingAnchor.Value.Y} - click the opposite corner (Esc cancels)"
                : "  |  drag a box, or click two opposite corners",
            CanvasMode.Corners => $"  |  {cornerPts.Count} corner(s) - Enter/right-click/double-click finishes, Esc cancels",
            _ => "  |  click a box to select, drag to move, handles resize; empty drag / WASD pans; Del removes",
        };
        if (PickTileMode) extra = "  |  PICK P: click the map to place the P point (Esc cancels)";
        Status?.Invoke($"X={lastMouseTile.X}  Y={lastMouseTile.Y}  zoom {zoom:0.##}x{extra}");
    }
}
