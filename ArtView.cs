using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ClassicUO.Assets;
using ClassicUO.Utility;

namespace UORegionEditor;

// CentrED-look detail renderer: real top-down art (texmap/land-art ground + statics with hues),
// adaptive resolution (8/16/32 px per tile by zoom), 64x64-tile chunks with a pixel-budget LRU,
// min/max Z + land/statics/nodraw filters like the CentrED# FilterWindow.
// GPU-free - same ClassicUO loaders the map exporters use.
public class ArtView : IDisposable
{
    public const int ChunkTiles = 64;
    public const int ClassicN = 8;     // fixed resolution used by the classic WinForms UI

    readonly UOFileManager ufm;
    readonly MulMapData map;
    readonly Dictionary<int, (uint[] px, int w, int h)> artCache = new();
    readonly Dictionary<long, (uint[] px, int w, int h)> huedCache = new();
    readonly Dictionary<int, (uint[] px, int w, int h)> texCache = new();
    readonly Dictionary<long, Bitmap> chunks = new();
    readonly LinkedList<long> lru = new();
    long cachedPixels;
    // must stay ABOVE the worst-case visible chunk count (~350 at the 0.05 zoom floor
    // on a big window) or eviction thrashes into an endless render loop
    const long MaxCachedPixels = 128_000_000;   // ~512 MB of chunk bitmaps

    public sbyte MinZ { get; private set; } = -128;
    public sbyte MaxZ { get; private set; } = 127;
    public bool ShowLand { get; private set; } = true;
    public bool ShowStatics { get; private set; } = true;
    public bool ShowNoDraw { get; private set; }

    public int WT => map.WT;
    public int HT => map.HT;

    public static bool HasArt(string dir) =>
        File.Exists(Path.Combine(dir, "artLegacyMUL.uop")) ||
        (File.Exists(Path.Combine(dir, "art.mul")) && File.Exists(Path.Combine(dir, "artidx.mul")));

    // null = pack looks usable, else a human explanation of what's missing
    public static string CheckArtPack(string dir)
    {
        if (!HasArt(dir))
            return "art files missing (artLegacyMUL.uop, or art.mul + artidx.mul)";
        if (File.Exists(Path.Combine(dir, "artLegacyMUL.uop")) && !File.Exists(Path.Combine(dir, "MainMisc.uop")))
            return "MainMisc.uop is missing - the UOP art loader needs it (copy it from the client)";
        if (!File.Exists(Path.Combine(dir, "tiledata.mul"))) return "tiledata.mul missing";
        if (!File.Exists(Path.Combine(dir, "hues.mul"))) return "hues.mul missing";
        bool hasTex = File.Exists(Path.Combine(dir, "texmaps.mul")) && File.Exists(Path.Combine(dir, "texidx.mul"));
        if (!hasTex) return "texmaps.mul + texidx.mul missing";
        return null;
    }

    int filterGen;   // renders started before a filter change must not poison the cache

    public ArtView(string mulsDir, MulMapData sharedMap = null)
    {
        ufm = new UOFileManager(ClientVersion.CV_7090, mulsDir);
        ufm.Arts.Load();
        ufm.TileData.Load();
        ufm.Hues.Load();
        ufm.Texmaps.Load();
        map = sharedMap ?? MulRadar.LoadData(mulsDir);
    }

    public MulMapData Map => map;

    // ---- isometric projection (the real CentrED / in-game look) -----------
    // Land is drawn as textured quads warped to the tile's 4 vertex heights, statics are
    // painter-ordered with a z lift - the same approach the game client itself uses.
    public const int IsoHalf = 22;
    public const int IsoZStep = 4;
    public const int IsoChunkPx = 512;

    public int IsoOffX => (HT - 1) * IsoHalf + IsoHalf;
    public int IsoOffY => 1300;                          // headroom for tall statics on high z
    public int IsoW => (WT + HT) * IsoHalf + 2 * IsoHalf;
    public int IsoH => (WT + HT) * IsoHalf + IsoOffY + 600;

    // grid-vertex projection - MUST match the renderer's vertex formula exactly:
    // (vx-vy)*22 + offX , (vx+vy)*22 - 22 - z*4 + offY   (the -22 half-tile is part of it!)
    public (double px, double py) IsoTileToPx(double x, double y, double z = 0) =>
        ((x - y) * IsoHalf + IsoOffX, (x + y) * IsoHalf - IsoHalf - z * IsoZStep + IsoOffY);

    public (double x, double y) IsoPxToTile(double px, double py)
    {
        double u = px - IsoOffX, v = py - IsoOffY + IsoHalf;
        return ((v / IsoHalf + u / IsoHalf) / 2.0, (v / IsoHalf - u / IsoHalf) / 2.0);
    }

    public void SetFilter(sbyte minZ, sbyte maxZ, bool land, bool statics, bool noDraw)
    {
        if (minZ == MinZ && maxZ == MaxZ && land == ShowLand && statics == ShowStatics && noDraw == ShowNoDraw) return;
        MinZ = minZ; MaxZ = maxZ; ShowLand = land; ShowStatics = statics; ShowNoDraw = noDraw;
        Interlocked.Increment(ref filterGen);
        lock (chunks)
        {
            foreach (var b in chunks.Values) b.Dispose();
            chunks.Clear();
            lru.Clear();
            cachedPixels = 0;
        }
    }

    static long Key(int cx, int cy, int n) => ((long)n << 44) | ((long)cx << 22) | (uint)cy;

    public Bitmap TryGetCached(int cx, int cy, int n)
    {
        long key = Key(cx, cy, n);
        lock (chunks)
        {
            if (chunks.TryGetValue(key, out var b))
            {
                lru.Remove(key);
                lru.AddFirst(key);
                return b;
            }
        }
        return null;
    }

    // renders (blocking) and caches; call from a background thread
    public Bitmap RenderChunk(int cx, int cy, int n)
    {
        var cached = TryGetCached(cx, cy, n);
        if (cached != null) return cached;
        int gen = filterGen;
        var bmp = Render(cx, cy, n);
        long key = Key(cx, cy, n);
        lock (chunks)
        {
            if (gen != filterGen) { bmp.Dispose(); return null; }   // filter changed mid-render
            if (chunks.TryGetValue(key, out var race)) { bmp.Dispose(); return race; }
            chunks[key] = bmp;
            lru.AddFirst(key);
            cachedPixels += (long)bmp.Width * bmp.Height;
            while (cachedPixels > MaxCachedPixels && lru.Count > 1)
            {
                var old = lru.Last.Value;
                lru.RemoveLast();
                cachedPixels -= (long)chunks[old].Width * chunks[old].Height;
                chunks[old].Dispose();
                chunks.Remove(old);
            }
        }
        return bmp;
    }

    // exact CentrED# NoDraw semantics (MapManager.CanDrawStatic / CanDrawLand)
    bool HideNoDrawStatic(ushort id)
    {
        if (ShowNoDraw) return false;
        if (id >= ufm.TileData.StaticData.Length) return true;
        switch (id)
        {
            case 0x0001:
            case 0x21BC:
            case 0x63D3:
                return true;
            case 0x9E4C:
            case 0x9E64:
            case 0x9E65:
            case 0x9E7D:
            {
                ref var d = ref ufm.TileData.StaticData[id];
                return d.IsBackground || d.IsSurface;
            }
            case >= 0x2198 and <= 0x21A4:
                return true;
            default:
                return false;
        }
    }

    bool HideNoDrawLand(ushort id) => id <= 2 && !ShowNoDraw;

    // same-tile draw priority, mirroring the client (and our exporters):
    // backgrounds sink below their z, anything with height rises above
    int DrawPrio(ushort sid, int z)
    {
        if (sid < ufm.TileData.StaticData.Length)
        {
            ref var d = ref ufm.TileData.StaticData[sid];
            if (d.IsBackground) z--;
            if (d.Height != 0) z++;
        }
        return z;
    }

    Bitmap Render(int cx, int cy, int n)
    {
        int dim = ChunkTiles * n;
        var px = new uint[dim * dim];
        int x0 = cx * ChunkTiles, y0 = cy * ChunkTiles;
        var stmp = new List<(ushort id, sbyte z, ushort hue)>(8);

        for (int ty = 0; ty < ChunkTiles; ty++)
        {
            int gy = y0 + ty;
            if (gy >= HT) break;
            for (int tx = 0; tx < ChunkTiles; tx++)
            {
                int gx = x0 + tx;
                if (gx >= WT) break;
                int baseX = tx * n, baseY = ty * n;

                if (ShowLand && !HideNoDrawLand(map.LandAt(gx, gy).id))
                {
                    var (lid, _) = map.LandAt(gx, gy);
                    var (tp, tw, th) = Tex(lid);
                    if (tw > 0)
                    {
                        for (int yy = 0; yy < n; yy++)
                        {
                            int row = (baseY + yy) * dim + baseX;
                            int sy = yy * th / n;
                            for (int xx = 0; xx < n; xx++)
                            {
                                uint s = tp[sy * tw + xx * tw / n];   // R low byte -> BGRA
                                px[row + xx] = 0xFF000000u | ((s & 0xFF) << 16) | (s & 0xFF00u) | ((s >> 16) & 0xFFu);
                            }
                        }
                    }
                    else
                    {
                        // no texmap: scale the 44x44 land art into the cell (skips its transparent corners)
                        var (lp, lw, lh) = Art(lid & 0x3FFF);
                        if (lw > 0)
                        {
                            FillCellFromArt(px, dim, lp, lw, lh, baseX, baseY, n);
                        }
                        else
                        {
                            uint c = Rgb(lid < map.Radarcol.Length ? map.Radarcol[lid & 0x3FFF] : (ushort)0);
                            for (int yy = 0; yy < n; yy++)
                            {
                                int row = (baseY + yy) * dim + baseX;
                                for (int xx = 0; xx < n; xx++) px[row + xx] = c;
                            }
                        }
                    }
                }

            }
        }

        // statics pass with OVERSCAN: tiles below/right of the chunk hold tall or wide sprites
        // that reach back into it (max art 579px tall / 264px wide at 44px per tile)
        if (ShowStatics)
        {
            const int overY = 15, overX = 4;
            for (int ty = -3; ty < ChunkTiles + overY; ty++)
            {
                int gy = y0 + ty;
                if (gy < 0 || gy >= HT) continue;
                for (int tx = -overX; tx < ChunkTiles + overX; tx++)
                {
                    int gx = x0 + tx;
                    if (gx < 0 || gx >= WT) continue;
                    int baseX = tx * n, baseY = ty * n;
                    stmp.Clear();
                    foreach (var st in map.StaticsAt(gx, gy))
                    {
                        if (st.z < MinZ || st.z > MaxZ) continue;
                        if (HideNoDrawStatic(st.id)) continue;
                        stmp.Add(st);
                    }
                    if (stmp.Count == 0) continue;
                    if (stmp.Count > 1)
                        stmp.Sort((a, b) =>
                        {
                            int pa = DrawPrio(a.id, a.z), pb = DrawPrio(b.id, b.z);
                            return pa != pb ? pa - pb : a.id.CompareTo(b.id);
                        });
                    foreach (var st in stmp)
                    {
                        var (sp, sw, sh) = HuedArt(st.id, st.hue);
                        if (sw == 0) continue;
                        float scale = (float)n / 44f;
                        int dw = Math.Max(1, (int)(sw * scale));
                        int dh = Math.Max(1, (int)(sh * scale));
                        int dx = baseX + n / 2 - dw / 2;
                        int dy = baseY + n - dh - (int)(st.z * 4 * scale);
                        Stamp(px, dim, sp, sw, sh, dx, dy, dw, dh);
                    }
                }
            }
        }

        var bmp = new Bitmap(dim, dim, PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, dim, dim), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            var ints = new int[px.Length];
            Buffer.BlockCopy(px, 0, ints, 0, px.Length * 4);
            if (bd.Stride == dim * 4) Marshal.Copy(ints, 0, bd.Scan0, ints.Length);
            else for (int y = 0; y < dim; y++) Marshal.Copy(ints, y * dim, IntPtr.Add(bd.Scan0, y * bd.Stride), dim);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    // iso chunk render. lod 0: chunk covers 512 iso px (native). lod 2: covers 2048 iso px,
    // rendered native then box-downsampled 4x - the zoomed-out overview.
    public Bitmap RenderIsoChunk(int cx, int cy, int lod = 0)
    {
        int marker = lod == 0 ? 1 : 2;               // n=1/2 mark the iso projection LODs
        var cached = TryGetCached(cx, cy, marker);
        if (cached != null) return cached;
        int gen = filterGen;
        var bmp = lod == 0 ? RenderIso(cx, cy) : RenderIsoLod2(cx, cy);
        long key = Key(cx, cy, marker);
        lock (chunks)
        {
            if (gen != filterGen) { bmp.Dispose(); return null; }
            if (chunks.TryGetValue(key, out var race)) { bmp.Dispose(); return race; }
            chunks[key] = bmp;
            lru.AddFirst(key);
            cachedPixels += (long)bmp.Width * bmp.Height;
            while (cachedPixels > MaxCachedPixels && lru.Count > 1)
            {
                var old = lru.Last.Value;
                lru.RemoveLast();
                cachedPixels -= (long)chunks[old].Width * chunks[old].Height;
                chunks[old].Dispose();
                chunks.Remove(old);
            }
        }
        return bmp;
    }

    // measured world constants from the exporter: max sprite 579px tall, real z in -126..126
    const int IsoZLo = -126, IsoZHi = 126, IsoMaxSpriteH = 580, IsoMaxSpriteW = 264;
    const int IsoUpReach = IsoZHi * IsoZStep + IsoMaxSpriteH - IsoHalf;
    const int IsoLandDown = IsoHalf + (-IsoZLo) * IsoZStep;

    static int Zc(int z) => z < IsoZLo ? IsoZLo : (z > IsoZHi ? IsoZHi : z);

    int LandZ(int x, int y)
    {
        if (x < 0) x = 0; else if (x >= WT) x = WT - 1;
        if (y < 0) y = 0; else if (y >= HT) y = HT - 1;
        return Zc(map.LandAt(x, y).z);
    }

    public sbyte LandZAt(int x, int y) => map.LandAt(Math.Clamp(x, 0, WT - 1), Math.Clamp(y, 0, HT - 1)).z;

    public int LandVertexZ(int x, int y) => LandZ(x, y);

    // the base z of the topmost VISIBLE thing on a tile under the current filter -
    // the plane the user's eye reads as "the tile" (roof, or floor once MaxZ cuts the roof)
    // is this tile's land drawn under the current filter? (CentrED CanDrawLand semantics)
    bool LandVisible(int x, int y)
    {
        var (id, z) = map.LandAt(x, y);
        return ShowLand && !HideNoDrawLand(id) && z >= MinZ && z <= MaxZ;
    }

    // the z the status bar reports - the SAME surface the highlight sits on, so the
    // number and the marker can never disagree
    public int TopVisibleZ(int x, int y)
    {
        x = Math.Clamp(x, 0, WT - 1);
        y = Math.Clamp(y, 0, HT - 1);
        int landZ = Zc(map.LandAt(x, y).z);
        int best = LandVisible(x, y) ? landZ : int.MinValue;
        if (ShowStatics)
            foreach (var st in map.StaticsAt(x, y))
            {
                if (st.z < MinZ || st.z > MaxZ) continue;
                if (HideNoDrawStatic(st.id)) continue;
                int z = Zc(st.z);
                if (z > best) best = z;
            }
        // nothing visible at all: still report the land height so the readout has a value
        return best == int.MinValue ? landZ : best;
    }

    // the static the Z readout points at: the topmost visible one on the tile, or null
    // when bare land is on top (same filtering as TopVisibleZ so they always agree)
    public (ushort id, ushort hue, sbyte z)? TopVisibleStaticAt(int x, int y)
    {
        if (!ShowStatics) return null;
        x = Math.Clamp(x, 0, WT - 1);
        y = Math.Clamp(y, 0, HT - 1);
        // with the land filtered away, any in-range static counts as "on top"
        int bestZ = LandVisible(x, y) ? Zc(map.LandAt(x, y).z) : int.MinValue;
        (ushort id, ushort hue, sbyte z)? best = null;
        foreach (var st in map.StaticsAt(x, y))
        {
            if (st.z < MinZ || st.z > MaxZ) continue;
            if (HideNoDrawStatic(st.id)) continue;
            if (Zc(st.z) >= bestZ)
            {
                bestZ = Zc(st.z);
                best = (st.id, st.hue, st.z);
            }
        }
        return best;
    }

    // ClassicUO-style pixel pick: which static's opaque sprite pixel sits under the
    // iso-world point? Walks the painter order in reverse (front to back), so it returns
    // exactly the item the eye sees at that pixel - not whatever stands on the ground tile.
    public (int tx, int ty, ushort id, ushort hue, sbyte z)? PickStaticAt(double wx, double wy)
    {
        if (!ShowStatics) return null;
        var stmp = new List<(ushort id, sbyte z, ushort hue)>(8);
        int offX = IsoOffX, offY = IsoOffY;
        int ix = (int)Math.Floor(wx), iy = (int)Math.Floor(wy);
        int sLo = Math.Max(0, (int)Math.Floor((wy - IsoLandDown - offY) / (double)IsoHalf));
        int sHi = Math.Min(WT - 1 + HT - 1, (int)Math.Ceiling((wy + IsoUpReach - offY) / (double)IsoHalf));
        for (int S = sHi; S >= sLo; S--)                       // front to back
        {
            double lo = (S + (wx - IsoMaxSpriteW / 2.0 - offX) / IsoHalf) / 2.0;
            double hi = (S + (wx + IsoMaxSpriteW / 2.0 - offX) / IsoHalf) / 2.0;
            int x0 = Math.Max(Math.Max(0, S - (HT - 1)), (int)Math.Floor(lo) - 1);
            int x1 = Math.Min(Math.Min(WT - 1, S), (int)Math.Ceiling(hi) + 1);
            for (int x = x1; x >= x0; x--)                     // reverse of the draw order
            {
                int y = S - x;
                int bx = (x - y) * IsoHalf + offX;
                int by = (x + y) * IsoHalf + offY;
                stmp.Clear();
                foreach (var st in map.StaticsAt(x, y))
                {
                    if (st.z < MinZ || st.z > MaxZ) continue;
                    if (HideNoDrawStatic(st.id)) continue;
                    stmp.Add(st);
                }
                if (stmp.Count == 0) continue;
                if (stmp.Count > 1)
                    stmp.Sort((a, b) =>
                    {
                        int pa = DrawPrio(a.id, a.z), pb = DrawPrio(b.id, b.z);
                        return pa != pb ? pa - pb : a.id.CompareTo(b.id);
                    });
                for (int i = stmp.Count - 1; i >= 0; i--)      // tile's topmost first
                {
                    var st = stmp[i];
                    var (sp, sw, sh) = HuedArt(st.id, st.hue);
                    if (sw == 0) continue;
                    int rx = ix - (bx - sw / 2), ry = iy - (by - Zc(st.z) * IsoZStep - sh + IsoHalf);
                    if (rx < 0 || ry < 0 || rx >= sw || ry >= sh) continue;
                    if ((sp[ry * sw + rx] >> 24) != 0) return (x, y, st.id, st.hue, st.z);
                }
            }
        }
        return null;
    }

    // where the renderer blitted this static, in iso-world px (mirrors RenderIsoInto's
    // IsoBlit placement exactly, int math included, so the overlay is pixel-perfect)
    public (int x, int y, int w, int h)? StaticSpriteRect(int tx, int ty, ushort id, ushort hue, sbyte z)
    {
        var (_, sw, sh) = HuedArt(id, hue);
        if (sw == 0 || sh == 0) return null;
        int bx = (tx - ty) * IsoHalf + IsoOffX;
        int by = (tx + ty) * IsoHalf + IsoOffY;
        return (bx - sw / 2, by - Zc(z) * IsoZStep - sh + IsoHalf, sw, sh);
    }

    // hovered-item overlay: the hued sprite as an ARGB bitmap (transparent background,
    // unlike the opaque chunk bitmaps)
    public Bitmap StaticSpriteBitmap(ushort id, ushort hue)
    {
        var (p, w, h) = HuedArt(id, hue);
        if (w == 0 || h == 0) return null;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ints = new int[p.Length];
            Buffer.BlockCopy(p, 0, ints, 0, p.Length * 4);
            if (bd.Stride == w * 4) Marshal.Copy(ints, 0, bd.Scan0, ints.Length);
            else for (int y = 0; y < h; y++) Marshal.Copy(ints, y * w, IntPtr.Add(bd.Scan0, y * bd.Stride), w);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    // the highlight quad of one region tile in ISO PIXEL space - the single source of truth
    // used by both the live view and the offline render test, so they can never disagree.
    // One flat diamond anchored at the tile's TOP VISIBLE z: the highlight always lies on
    // the surface the user is actually looking at (terrain, roof, or filtered-down floor).
    public (double x, double y)[] TileHighlightQuad(int x, int y)
    {
        // If a STATIC is the top visible thing the surface really is flat -> one z for all
        // four corners. If bare LAND is on top, the renderer warps the tile to its four
        // corner heights (DrawIsoLand), so the highlight must warp the same way.
        var top = TopVisibleStaticAt(x, y);
        if (top is { } st && (!LandVisible(x, y) || Zc(st.z) >= LandZ(x, y)))
        {
            int fz = Zc(st.z);
            return new[]
            {
                IsoTileToPx(x, y, fz),
                IsoTileToPx(x + 1, y, fz),
                IsoTileToPx(x + 1, y + 1, fz),
                IsoTileToPx(x, y + 1, fz),
            };
        }
        if (!LandVisible(x, y))
        {
            int fz = TopVisibleZ(x, y);
            return new[]
            {
                IsoTileToPx(x, y, fz),
                IsoTileToPx(x + 1, y, fz),
                IsoTileToPx(x + 1, y + 1, fz),
                IsoTileToPx(x, y + 1, fz),
            };
        }
        return new[]
        {
            IsoTileToPx(x, y, LandZ(x, y)),
            IsoTileToPx(x + 1, y, LandZ(x + 1, y)),
            IsoTileToPx(x + 1, y + 1, LandZ(x + 1, y + 1)),
            IsoTileToPx(x, y + 1, LandZ(x, y + 1)),
        };
    }

    // the "walking surface" height of a tile: the land, or the top of the highest surface
    // static (building floor, bridge, platform) within the current z filter - region
    // highlights anchored here sit on floors instead of the terrain buried beneath them
    public int SurfaceZ(int x, int y)
    {
        x = Math.Clamp(x, 0, WT - 1);
        y = Math.Clamp(y, 0, HT - 1);
        int best = Zc(map.LandAt(x, y).z);
        foreach (var st in map.StaticsAt(x, y))
        {
            if (st.z < MinZ || st.z > MaxZ) continue;
            if (HideNoDrawStatic(st.id)) continue;
            if (st.id >= ufm.TileData.StaticData.Length) continue;
            ref var d = ref ufm.TileData.StaticData[st.id];
            // floors/pavers/rugs are Surface+Background; benches and tables are Surface only -
            // furniture must NOT lift the highlight off the floor
            if (!d.IsSurface || !d.IsBackground) continue;
            int top = Zc(st.z + d.Height);
            if (top > best) best = top;
        }
        return best;
    }

    Bitmap RenderIso(int cx, int cy)
    {
        int dim = IsoChunkPx;
        var px = new uint[dim * dim];
        RenderIsoInto(px, dim, cx * dim, cy * dim);
        return ToBitmap(px, dim);
    }

    Bitmap RenderIsoLod2(int cx, int cy)
    {
        int big = IsoChunkPx * 4;
        var full = new uint[big * big];
        RenderIsoInto(full, big, cx * big, cy * big);
        int dim = IsoChunkPx;
        var px = new uint[dim * dim];
        for (int y = 0; y < dim; y++)
        {
            int rowD = y * dim;
            for (int x = 0; x < dim; x++)
            {
                long r = 0, g = 0, b = 0;
                int baseY = y * 4, baseX = x * 4;
                for (int yy = 0; yy < 4; yy++)
                {
                    int row = (baseY + yy) * big + baseX;
                    for (int xx = 0; xx < 4; xx++)
                    {
                        uint s = full[row + xx];
                        r += (s >> 16) & 0xFF; g += (s >> 8) & 0xFF; b += s & 0xFF;
                    }
                }
                px[rowD + x] = 0xFF000000u | (uint)((r / 16) << 16) | (uint)((g / 16) << 8) | (uint)(b / 16);
            }
        }
        return ToBitmap(px, dim);
    }

    Bitmap ToBitmap(uint[] px, int dim)
    {
        var bmp = new Bitmap(dim, dim, PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, dim, dim), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            var ints = new int[px.Length];
            Buffer.BlockCopy(px, 0, ints, 0, px.Length * 4);
            if (bd.Stride == dim * 4) Marshal.Copy(ints, 0, bd.Scan0, ints.Length);
            else for (int y = 0; y < dim; y++) Marshal.Copy(ints, y * dim, IntPtr.Add(bd.Scan0, y * bd.Stride), dim);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    void RenderIsoInto(uint[] px, int dim, int CX0, int CY0)
    {
        int CY1 = CY0 + dim, CX1 = CX0 + dim;
        var stmp = new List<(ushort id, sbyte z, ushort hue)>(8);
        int offX = IsoOffX, offY = IsoOffY;

        int sLo = Math.Max(0, (int)Math.Floor((CY0 - IsoLandDown - offY) / (double)IsoHalf));
        int sHi = Math.Min(WT - 1 + HT - 1, (int)Math.Ceiling((CY1 + IsoUpReach - offY) / (double)IsoHalf));

        for (int S = sLo; S <= sHi; S++)
        {
            double lo = (S + (CX0 - IsoMaxSpriteW / 2.0 - offX) / IsoHalf) / 2.0;
            double hi = (S + (CX1 + IsoMaxSpriteW / 2.0 - offX) / IsoHalf) / 2.0;
            int x0 = Math.Max(Math.Max(0, S - (HT - 1)), (int)Math.Floor(lo) - 1);
            int x1 = Math.Min(Math.Min(WT - 1, S), (int)Math.Ceiling(hi) + 1);
            for (int x = x0; x <= x1; x++)
            {
                int y = S - x;
                int bx = (x - y) * IsoHalf + offX - CX0;
                int by = (x + y) * IsoHalf + offY - CY0;

                // land honors Min/Max Z exactly like CentrED's CanDrawLand -> WithinZRange:
                // drop Max Z and the mountains melt away, not just the statics
                var (landId, landZ) = map.LandAt(x, y);
                if (ShowLand && !HideNoDrawLand(landId) && landZ >= MinZ && landZ <= MaxZ)
                    DrawIsoLand(px, dim, x, y, CX0, CY0);

                if (!ShowStatics) continue;
                stmp.Clear();
                foreach (var st in map.StaticsAt(x, y))
                {
                    if (st.z < MinZ || st.z > MaxZ) continue;
                    if (HideNoDrawStatic(st.id)) continue;
                    stmp.Add(st);
                }
                if (stmp.Count > 1)
                    stmp.Sort((a, b) =>
                    {
                        int pa = DrawPrio(a.id, a.z), pb = DrawPrio(b.id, b.z);
                        return pa != pb ? pa - pb : a.id.CompareTo(b.id);
                    });
                foreach (var st in stmp)
                {
                    var (sp, sw, sh) = HuedArt(st.id, st.hue);
                    if (sw == 0) continue;
                    IsoBlit(px, dim, sp, sw, sh, bx - sw / 2, by - Zc(st.z) * IsoZStep - sh + IsoHalf);
                }
            }
        }
    }

    // land as a textured quad warped onto the tile's 4 vertex heights (seamless slopes);
    // texture-less land (cave black): flat art when flat, solid avg-color quad when sloped
    void DrawIsoLand(uint[] dst, int dim, int x, int y, int CX0, int CY0)
    {
        var (lid, _) = map.LandAt(x, y);
        var (tp, tw, th) = Tex(lid);
        int hA = LandZ(x, y), hB = LandZ(x + 1, y), hC = LandZ(x + 1, y + 1), hD = LandZ(x, y + 1);
        float ox = IsoOffX - CX0, oy = IsoOffY - CY0 - IsoHalf;
        float Ax = (x - y) * IsoHalf + ox, Ay = (x + y) * IsoHalf + oy - hA * IsoZStep;
        float Bx = (x + 1 - y) * IsoHalf + ox, By = (x + 1 + y) * IsoHalf + oy - hB * IsoZStep;
        float Cx = (x - y) * IsoHalf + ox, Cy = (x + y + 2) * IsoHalf + oy - hC * IsoZStep;
        float Dx = (x - y - 1) * IsoHalf + ox, Dy = (x + y + 1) * IsoHalf + oy - hD * IsoZStep;
        if (tw == 0)
        {
            var (lp, lw, lh) = Art(lid & 0x3FFF);
            bool flat = hA == hB && hB == hC && hC == hD;
            if (flat && lw > 0)
            {
                IsoBlit(dst, dim, lp, lw, lh,
                    (x - y) * IsoHalf + IsoOffX - CX0 - IsoHalf,
                    (x + y) * IsoHalf + IsoOffY - CY0 - IsoHalf - hA * IsoZStep);
                return;
            }
            var fill = new[] { LandFillColor(lid) };
            TriTex(dst, dim, Ax, Ay, 0, 0, Bx, By, 0, 0, Cx, Cy, 0, 0, fill, 1, 1);
            TriTex(dst, dim, Ax, Ay, 0, 0, Cx, Cy, 0, 0, Dx, Dy, 0, 0, fill, 1, 1);
            return;
        }
        TriTex(dst, dim, Ax, Ay, 0, 0, Bx, By, tw, 0, Cx, Cy, tw, th, tp, tw, th);
        TriTex(dst, dim, Ax, Ay, 0, 0, Cx, Cy, tw, th, Dx, Dy, 0, th, tp, tw, th);
    }

    readonly Dictionary<int, uint> landFillCache = new();

    uint LandFillColor(int landId)
    {
        lock (spriteGate)
        {
            if (landFillCache.TryGetValue(landId, out var f)) return f;
            var (lp, lw, _) = Art(landId & 0x3FFF);
            long r = 0, g = 0, b = 0, cnt = 0;
            if (lw > 0)
                foreach (var p in lp)
                    if ((p >> 24) != 0) { r += p & 0xFF; g += (p >> 8) & 0xFF; b += (p >> 16) & 0xFF; cnt++; }
            f = cnt > 0 ? 0xFF000000u | (uint)(r / cnt) | (uint)((g / cnt) << 8) | (uint)((b / cnt) << 16) : 0xFF000000u;
            landFillCache[landId] = f;
            return f;
        }
    }

    // opaque textured triangle, affine UV (ported from the exporter); source pixels R-low
    static void TriTex(uint[] dst, int dim,
        float x0, float y0, float u0, float v0, float x1, float y1, float u1, float v1,
        float x2, float y2, float u2, float v2, uint[] tex, int tw, int th)
    {
        int minX = (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2)));
        int maxX = (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2)));
        int minY = (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2)));
        int maxY = (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2)));
        if (minX < 0) minX = 0;
        if (minY < 0) minY = 0;
        if (maxX >= dim) maxX = dim - 1;
        if (maxY >= dim) maxY = dim - 1;
        float den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
        if (MathF.Abs(den) < 1e-5f) return;
        float invd = 1f / den;
        for (int py = minY; py <= maxY; py++)
        {
            int rd = py * dim;
            for (int pxx = minX; pxx <= maxX; pxx++)
            {
                float fx = pxx + 0.5f, fy = py + 0.5f;
                float w0 = ((y1 - y2) * (fx - x2) + (x2 - x1) * (fy - y2)) * invd;
                float w1 = ((y2 - y0) * (fx - x2) + (x0 - x2) * (fy - y2)) * invd;
                float w2 = 1f - w0 - w1;
                if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f) continue;
                int tu = (int)(w0 * u0 + w1 * u1 + w2 * u2);
                if (tu < 0) tu = 0; else if (tu >= tw) tu = tw - 1;
                int tv = (int)(w0 * v0 + w1 * v1 + w2 * v2);
                if (tv < 0) tv = 0; else if (tv >= th) tv = th - 1;
                uint s = tex[tv * tw + tu];
                if ((s >> 24) == 0) continue;
                dst[rd + pxx] = 0xFF000000u | ((s & 0xFF) << 16) | (s & 0xFF00u) | ((s >> 16) & 0xFFu);
            }
        }
    }

    // 1:1 sprite blit with alpha skip (source R-low -> BGRA)
    static void IsoBlit(uint[] dst, int dim, uint[] src, int sw, int sh, int dx, int dy)
    {
        for (int yy = 0; yy < sh; yy++)
        {
            int py = dy + yy;
            if (py < 0 || py >= dim) continue;
            int rowD = py * dim, rowS = yy * sw;
            for (int xx = 0; xx < sw; xx++)
            {
                int pxc = dx + xx;
                if (pxc < 0 || pxc >= dim) continue;
                uint s = src[rowS + xx];
                if ((s >> 24) == 0) continue;
                dst[rowD + pxc] = 0xFF000000u | ((s & 0xFF) << 16) | (s & 0xFF00u) | ((s >> 16) & 0xFFu);
            }
        }
    }

    // fill an n x n ground cell from a land art tile, sampling only opaque pixels
    // (the 44x44 diamond leaves transparent corners; sample toward the center for those)
    static void FillCellFromArt(uint[] dst, int dim, uint[] src, int sw, int sh, int baseX, int baseY, int n)
    {
        for (int yy = 0; yy < n; yy++)
        {
            int row = (baseY + yy) * dim + baseX;
            int sy = yy * sh / n;
            for (int xx = 0; xx < n; xx++)
            {
                int sx = xx * sw / n;
                uint s = src[sy * sw + sx];
                if ((s >> 24) == 0)
                {
                    // fall back to the tile center pixel (diamond corner)
                    s = src[(sh / 2) * sw + sw / 2];
                    if ((s >> 24) == 0) continue;
                }
                dst[row + xx] = 0xFF000000u | ((s & 0xFF) << 16) | (s & 0xFF00u) | ((s >> 16) & 0xFFu);
            }
        }
    }

    static void Stamp(uint[] dst, int dim, uint[] src, int sw, int sh, int dx, int dy, int dw, int dh)
    {
        for (int yy = 0; yy < dh; yy++)
        {
            int py = dy + yy;
            if (py < 0 || py >= dim) continue;
            int sy = yy * sh / dh;
            int rowD = py * dim, rowS = sy * sw;
            for (int xx = 0; xx < dw; xx++)
            {
                int pxc = dx + xx;
                if (pxc < 0 || pxc >= dim) continue;
                uint s = src[rowS + xx * sw / dw];
                if ((s >> 24) == 0) continue;
                dst[rowD + pxc] = 0xFF000000u | ((s & 0xFF) << 16) | (s & 0xFF00u) | ((s >> 16) & 0xFFu);
            }
        }
    }

    static uint Rgb(ushort c16)
    {
        int r = (c16 >> 10) & 0x1F; r = (r << 3) | (r >> 2);
        int g = (c16 >> 5) & 0x1F; g = (g << 3) | (g >> 2);
        int b = c16 & 0x1F; b = (b << 3) | (b >> 2);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    // sprite lookups are shared by the parallel chunk workers: the gate covers both the
    // cache dictionaries and the (not proven thread-safe) ClassicUO loader reads; the
    // returned pixel arrays are immutable, so blitting from them needs no lock
    readonly object spriteGate = new();

    (uint[] px, int w, int h) Art(int id)
    {
        lock (spriteGate)
        {
            if (artCache.TryGetValue(id, out var c)) return c;
            var a = ufm.Arts.GetArt((uint)id);
            var r = (a.Pixels.ToArray(), a.Width, a.Height);
            if (artCache.Count > 6000) artCache.Clear();
            artCache[id] = r;
            return r;
        }
    }

    (uint[] px, int w, int h) Tex(int landId)
    {
        lock (spriteGate)
        {
            if (texCache.TryGetValue(landId, out var c)) return c;
            (uint[] px, int w, int h) r = (Array.Empty<uint>(), 0, 0);
            ushort texId = landId >= 0 && landId < ufm.TileData.LandData.Length ? ufm.TileData.LandData[landId].TexID : (ushort)0;
            if (texId > 0)
            {
                var t = ufm.Texmaps.GetTexmap(texId);
                if (t.Width > 0) r = (t.Pixels.ToArray(), t.Width, t.Height);
            }
            texCache[landId] = r;
            return r;
        }
    }

    // same client-exact hueing as the exporters: red-channel ramp index, PartialHue = gray only
    (uint[] px, int w, int h) HuedArt(ushort sid, ushort hue)
    {
        if (hue == 0 || hue >= ufm.Hues.HuesCount) return Art(sid + 0x4000);
        long key = ((long)sid << 16) | hue;
        lock (spriteGate)
        {
        if (huedCache.TryGetValue(key, out var c)) return c;
        var r = Art(sid + 0x4000);
        if (r.w > 0)
        {
            int hi = hue - 1;
            var table = ufm.Hues.HuesRange[hi >> 3].Entries[hi & 7].ColorTable;
            bool partial = sid < ufm.TileData.StaticData.Length && ufm.TileData.StaticData[sid].IsPartialHue;
            var np = new uint[r.px.Length];
            for (int i = 0; i < np.Length; i++)
            {
                uint p = r.px[i], a = p >> 24;
                if (a == 0) continue;
                uint red = p & 0xFF;
                if (partial && (red != ((p >> 8) & 0xFF) || red != ((p >> 16) & 0xFF))) { np[i] = p; continue; }
                np[i] = (a << 24) | (HuesHelper.Color16To32(table[(int)(red >> 3)]) & 0x00FFFFFF);
            }
            r = (np, r.w, r.h);
        }
        if (huedCache.Count > 4000) huedCache.Clear();
        huedCache[key] = r;
        return r;
        }
    }

    public void Dispose()
    {
        lock (chunks)
        {
            foreach (var b in chunks.Values) b.Dispose();
            chunks.Clear();
            lru.Clear();
            cachedPixels = 0;
        }
        // release the memory-mapped muls files, or a later muls re-sync cannot replace them
        try { (ufm as IDisposable)?.Dispose(); } catch { }
    }
}
