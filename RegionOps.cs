namespace UORegionEditor;

// Pure tile-set geometry behind the lasso and eraser tools. Everything is inclusive
// game tiles, the same convention as RegionRect. Kept UI-free so the selftest can
// verify the algorithms directly.
// Spatial index of tiles already owned by regions, for the "don't overlap" tools.
// Rects are bucketed into a coarse grid so a point test touches only a handful of
// them (the project has thousands of rects). Each rect carries its region's name so
// a blocked tool can SAY who blocked it instead of silently doing nothing.
public sealed class TileIndex
{
    const int Cell = 64;
    readonly Dictionary<long, List<int>> buckets = new();
    readonly List<(RegionRect rc, string name)> all = new();
    readonly int limX, limY;

    // bounds clamp the bucket loop: imported .scp rects are NOT clamped to the map
    // (Sphere parses them atoi-style), and a 999999-wide rect would explode the grid
    public TileIndex(int mapW = 1 << 16, int mapH = 1 << 16)
    {
        limX = Math.Max(0, mapW - 1);
        limY = Math.Max(0, mapH - 1);
    }

    public bool IsEmpty => all.Count == 0;

    static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    public void Add(RegionRect rc, string name)
    {
        if (rc.X2 < 0 || rc.Y2 < 0 || rc.X1 > limX || rc.Y1 > limY) return;   // fully off-map
        rc = new RegionRect(Math.Max(0, rc.X1), Math.Max(0, rc.Y1),
            Math.Min(limX, rc.X2), Math.Min(limY, rc.Y2));
        int i = all.Count;
        all.Add((rc, name));
        int cx0 = rc.X1 / Cell, cx1 = rc.X2 / Cell;
        int cy0 = rc.Y1 / Cell, cy1 = rc.Y2 / Cell;
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                var k = Key(cx, cy);
                if (!buckets.TryGetValue(k, out var list)) buckets[k] = list = new List<int>();
                list.Add(i);
            }
    }

    // name of the region covering this tile, or null when the tile is free
    public string Blocker(int x, int y)
    {
        if (x < 0 || y < 0) return null;
        if (!buckets.TryGetValue(Key(x / Cell, y / Cell), out var list)) return null;
        foreach (var i in list)
            if (all[i].rc.Contains(x, y)) return all[i].name;
        return null;
    }

    public bool Occupied(int x, int y) => Blocker(x, y) != null;

    // blocker rects overlapping a window (rect algebra instead of rasterizing)
    public IEnumerable<RegionRect> Intersecting(RegionRect w) =>
        all.Where(t => t.rc.X2 >= w.X1 && t.rc.X1 <= w.X2 && t.rc.Y2 >= w.Y1 && t.rc.Y1 <= w.Y2)
           .Select(t => t.rc);
}

public static class RegionOps
{
    // Freehand lasso -> filled tile set: the traced tile path plus every tile whose
    // center lies inside the closed polygon (even-odd rule on tile centers).
    public static HashSet<(int x, int y)> LassoFill(IReadOnlyList<(int x, int y)> path)
    {
        var set = new HashSet<(int x, int y)>(path);
        if (path.Count < 3) return set;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var p in path)
        {
            minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
            minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
        }
        // refuse absurd fills instead of freezing (a lasso around half the facet)
        if ((long)(maxX - minX + 1) * (maxY - minY + 1) > 1_000_000) return set;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (set.Contains((x, y))) continue;
                bool inside = false;
                double px = x + 0.5, py = y + 0.5;
                for (int i = 0, j = path.Count - 1; i < path.Count; j = i++)
                {
                    double xi = path[i].x + 0.5, yi = path[i].y + 0.5;
                    double xj = path[j].x + 0.5, yj = path[j].y + 0.5;
                    if (yi > py != yj > py && px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                        inside = !inside;
                }
                if (inside) set.Add((x, y));
            }
        return set;
    }

    // Tile mask -> compact rect list: horizontal runs per row, runs that repeat on
    // consecutive rows extend the same rect downward. Deterministic, no overlaps.
    public static List<RegionRect> MaskToRects(IReadOnlyCollection<(int x, int y)> tiles)
    {
        var res = new List<RegionRect>();
        if (tiles.Count == 0) return res;
        var byRow = new SortedDictionary<int, List<(int s, int e)>>();
        foreach (var g in tiles.GroupBy(t => t.y))
        {
            var xs = g.Select(t => t.x).Distinct().OrderBy(v => v).ToList();
            var runs = new List<(int s, int e)>();
            int s = xs[0], e = xs[0];
            for (int i = 1; i < xs.Count; i++)
            {
                if (xs[i] == e + 1) e = xs[i];
                else { runs.Add((s, e)); s = e = xs[i]; }
            }
            runs.Add((s, e));
            byRow[g.Key] = runs;
        }
        var active = new Dictionary<(int s, int e), RegionRect>();
        int prevRow = int.MinValue;
        foreach (var (row, runs) in byRow)
        {
            var next = new Dictionary<(int s, int e), RegionRect>();
            foreach (var run in runs)
            {
                if (row == prevRow + 1 && active.TryGetValue(run, out var r))
                {
                    r.Y2 = row;             // same span directly below: grow the rect
                    next[run] = r;
                }
                else
                {
                    var nr = new RegionRect(run.s, row, run.e, row);
                    res.Add(nr);
                    next[run] = nr;
                }
            }
            active = next;
            prevRow = row;
        }
        return res;
    }

    // Column-run variant of MaskToRects (transpose in, transpose back) - tall shapes
    // decompose far better vertically than by rows.
    static List<RegionRect> MaskToRectsColumns(IReadOnlyCollection<(int x, int y)> tiles)
    {
        var sw = MaskToRects(tiles.Select(t => (t.y, t.x)).ToList());
        return sw.Select(r => new RegionRect(r.Y1, r.X1, r.Y2, r.X2)).ToList();
    }

    // Compact decomposition: greedily carve the largest all-inside rectangles (big
    // organic fills become a few large boxes), strip-decompose the crumbs, and return
    // whichever of {greedy, row strips, column strips} is smallest - by construction
    // never worse than MaskToRects. Exact cover, no overlaps.
    public static List<RegionRect> MaskToRectsCompact(IReadOnlyCollection<(int x, int y)> tiles)
    {
        var rows = MaskToRects(tiles);
        if (tiles.Count < 64) return rows;
        var cols = MaskToRectsColumns(tiles);
        var best = cols.Count < rows.Count ? cols : rows;
        if (tiles.Count > 200_000) return best;   // greedy pass too costly there
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var t in tiles)
        {
            minX = Math.Min(minX, t.x); maxX = Math.Max(maxX, t.x);
            minY = Math.Min(minY, t.y); maxY = Math.Max(maxY, t.y);
        }
        int W = maxX - minX + 1, H = maxY - minY + 1;
        if ((long)W * H > 4_000_000) return best;
        var grid = new bool[W * H];
        foreach (var t in tiles) grid[(t.y - minY) * W + (t.x - minX)] = true;
        var res = new List<RegionRect>();
        var heights = new int[W];
        var stack = new int[W + 2];
        for (int carve = 0; carve < 96; carve++)
        {
            // largest all-true rectangle: histogram sweep with a monotonic stack
            int bestA = 0, bx1 = 0, by1 = 0, bx2 = -1, by2 = -1;
            Array.Clear(heights, 0, W);
            for (int y = 0; y < H; y++)
            {
                int row = y * W;
                for (int x = 0; x < W; x++) heights[x] = grid[row + x] ? heights[x] + 1 : 0;
                int top = 0;
                stack[0] = -1;
                for (int x = 0; x <= W; x++)
                {
                    int h = x < W ? heights[x] : 0;
                    while (top > 0 && heights[stack[top]] >= h)
                    {
                        int hh = heights[stack[top--]];
                        int area = hh * (x - stack[top] - 1);
                        if (area > bestA)
                        {
                            bestA = area;
                            bx1 = stack[top] + 1; bx2 = x - 1;
                            by1 = y - hh + 1; by2 = y;
                        }
                    }
                    stack[++top] = x;
                }
            }
            if (bestA < 24) break;   // crumbs: strips handle the leftovers better
            res.Add(new RegionRect(bx1 + minX, by1 + minY, bx2 + minX, by2 + minY));
            for (int y = by1; y <= by2; y++)
            {
                int row = y * W;
                for (int x = bx1; x <= bx2; x++) grid[row + x] = false;
            }
        }
        var rest = new List<(int x, int y)>();
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
                if (grid[row + x]) rest.Add((x + minX, y + minY));
        }
        res.AddRange(MaskToRects(rest));
        return res.Count < best.Count ? res : best;
    }

    // Rect list minus one box: intersecting rects split into up to 4 remainder bands.
    public static List<RegionRect> SubtractBox(IEnumerable<RegionRect> rects, RegionRect cut)
    {
        var res = new List<RegionRect>();
        foreach (var r in rects)
        {
            if (r.X2 < cut.X1 || r.X1 > cut.X2 || r.Y2 < cut.Y1 || r.Y1 > cut.Y2)
            {
                res.Add(r);
                continue;
            }
            if (r.Y1 < cut.Y1) res.Add(new RegionRect(r.X1, r.Y1, r.X2, cut.Y1 - 1));
            if (r.Y2 > cut.Y2) res.Add(new RegionRect(r.X1, cut.Y2 + 1, r.X2, r.Y2));
            int midY1 = Math.Max(r.Y1, cut.Y1), midY2 = Math.Min(r.Y2, cut.Y2);
            if (r.X1 < cut.X1) res.Add(new RegionRect(r.X1, midY1, cut.X1 - 1, midY2));
            if (r.X2 > cut.X2) res.Add(new RegionRect(cut.X2 + 1, midY1, r.X2, midY2));
        }
        return res;
    }

    // Rect list minus an arbitrary tile mask (lasso / quick-select eraser). Row-run
    // based: each rect is split band by band against the cut tiles bucketed per row,
    // so the cost is O(rect height + cut size) - never the rect's AREA. (The old
    // bbox-window rasterization iterated ~29M tiles for a full-map rect vs a thin
    // map-spanning mask: a one-click multi-second freeze.)
    public static List<RegionRect> SubtractMask(IEnumerable<RegionRect> rects, HashSet<(int x, int y)> cut)
    {
        var list = rects.ToList();
        if (cut.Count == 0) return list;
        var byRow = new Dictionary<int, List<int>>();
        foreach (var t in cut)
        {
            if (!byRow.TryGetValue(t.y, out var row)) byRow[t.y] = row = new List<int>();
            row.Add(t.x);
        }
        foreach (var row in byRow.Values) row.Sort();

        var res = new List<RegionRect>();
        foreach (var r in list)
        {
            // runs per row (the full width when the row is uncut), merged downward
            // whenever the same span repeats - same shape MaskToRects produces
            var active = new Dictionary<(int s, int e), RegionRect>();
            var runs = new List<(int s, int e)>();
            for (int y = r.Y1; y <= r.Y2; y++)
            {
                runs.Clear();
                if (byRow.TryGetValue(y, out var xs))
                {
                    int i = xs.BinarySearch(r.X1);
                    if (i < 0) i = ~i;
                    int s = r.X1;
                    for (; i < xs.Count && xs[i] <= r.X2; i++)
                    {
                        if (xs[i] > s) runs.Add((s, xs[i] - 1));
                        s = xs[i] + 1;
                    }
                    if (s <= r.X2) runs.Add((s, r.X2));
                }
                else
                {
                    runs.Add((r.X1, r.X2));
                }
                var next = new Dictionary<(int s, int e), RegionRect>();
                foreach (var run in runs)
                {
                    if (active.TryGetValue(run, out var grow))
                    {
                        grow.Y2 = y;
                        next[run] = grow;
                    }
                    else
                    {
                        var nr = new RegionRect(run.s, y, run.e, y);
                        res.Add(nr);
                        next[run] = nr;
                    }
                }
                active = next;
            }
        }
        return res;
    }

    // Magic-wand fill (Photoshop quick select): BFS from the seed over 4-connected
    // tiles whose color stays within tolerance of the SEED tile's color (per-channel
    // max diff on 0xRRGGBB ints from colorAt - Photoshop semantics, not chained
    // neighbor-to-neighbor). Hitting maxTiles sets overflow and aborts: a partial BFS
    // frontier is an arbitrary shape, so callers must refuse to commit it.
    public static HashSet<(int x, int y)> WandFill((int x, int y) seed, int mapW, int mapH,
        Func<int, int, int> colorAt, int tolerance, int maxTiles, out bool overflow, int maxRadius = 0)
    {
        overflow = false;
        var res = new HashSet<(int x, int y)>();
        if (seed.x < 0 || seed.y < 0 || seed.x >= mapW || seed.y >= mapH || maxTiles < 1) return res;
        int sc = colorAt(seed.x, seed.y);
        // a seed that is itself hidden or blocked selects NOTHING: without this the
        // out-of-band value would match every other hidden/blocked tile and the fill
        // would flood the whole invisible (or foreign-region) blob
        if ((sc & ~0xFFFFFF) != 0) return res;
        int sr = (sc >> 16) & 0xFF, sg = (sc >> 8) & 0xFF, sb = sc & 0xFF;
        var seen = new HashSet<(int x, int y)> { seed };   // enqueued or rejected: test each tile once
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue(seed);
        res.Add(seed);
        bool capped = false;
        bool Visit(int nx, int ny)   // true = tile cap hit
        {
            if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH || !seen.Add((nx, ny))) return false;
            if (maxRadius > 0)
            {
                long rdx = nx - seed.x, rdy = ny - seed.y;
                if (rdx * rdx + rdy * rdy > (long)maxRadius * maxRadius) return false;
            }
            int c = colorAt(nx, ny);
            if (((c ^ sc) & ~0xFFFFFF) != 0) return false;   // out-of-band flag bits (e.g. hidden) must match exactly
            int d = Math.Max(Math.Abs(((c >> 16) & 0xFF) - sr),
                    Math.Max(Math.Abs(((c >> 8) & 0xFF) - sg), Math.Abs((c & 0xFF) - sb)));
            if (d > tolerance) return false;
            if (res.Count >= maxTiles) return true;
            res.Add((nx, ny));
            queue.Enqueue((nx, ny));
            return false;
        }
        while (queue.Count > 0 && !capped)
        {
            var (x, y) = queue.Dequeue();
            capped = Visit(x - 1, y) || Visit(x + 1, y) || Visit(x, y - 1) || Visit(x, y + 1);
        }
        overflow = capped;
        return res;
    }

    // A wand fill matches by colour, so anything that does not match stays out - grass
    // clearings inside a forest come back as holes and punch through the region. This
    // fills the gaps that are fully ENCLOSED by the selection, leaving the outer shape
    // exactly as it was.
    //
    // How: flood the outside through non-selected tiles, starting from a one-tile ring
    // around the mask's bounding box. Anything the flood never reaches is enclosed.
    // maxHole caps each gap so a real lake or clearing is not swallowed (<=0 = no cap).
    // Returns tiles added, or -1 if the bounding box was too large to scan.
    public static int FillGaps(HashSet<(int x, int y)> mask, int maxHole, long areaBudget = 8_000_000)
    {
        if (mask == null || mask.Count == 0) return 0;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (var (x, y) in mask)
        {
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        x0--; y0--; x1++; y1++;                       // ring of empty tiles the flood starts from
        long w = (long)x1 - x0 + 1, h = (long)y1 - y0 + 1;
        if (w * h > areaBudget) return -1;

        // flood the outside
        var outside = new HashSet<(int x, int y)>();
        var q = new Queue<(int x, int y)>();
        void Seed(int x, int y)
        {
            if (x < x0 || y < y0 || x > x1 || y > y1) return;
            if (mask.Contains((x, y)) || !outside.Add((x, y))) return;
            q.Enqueue((x, y));
        }
        for (int x = x0; x <= x1; x++) { Seed(x, y0); Seed(x, y1); }
        for (int y = y0; y <= y1; y++) { Seed(x0, y); Seed(x1, y); }
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            Seed(x - 1, y); Seed(x + 1, y); Seed(x, y - 1); Seed(x, y + 1);
        }

        // whatever the flood never reached is an enclosed gap; take them one component at
        // a time so a single oversized clearing does not disqualify the small ones
        int added = 0;
        var done = new HashSet<(int x, int y)>();
        var comp = new List<(int x, int y)>();
        var cq = new Queue<(int x, int y)>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var start = (x, y);
                if (mask.Contains(start) || outside.Contains(start) || !done.Add(start)) continue;
                comp.Clear();
                cq.Clear();
                cq.Enqueue(start);
                comp.Add(start);
                while (cq.Count > 0)
                {
                    var (cx, cy) = cq.Dequeue();
                    foreach (var (nx, ny) in new[] { (cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1) })
                    {
                        if (nx < x0 || ny < y0 || nx > x1 || ny > y1) continue;
                        var n = (nx, ny);
                        if (mask.Contains(n) || outside.Contains(n) || !done.Add(n)) continue;
                        comp.Add(n);
                        cq.Enqueue(n);
                    }
                }
                if (maxHole > 0 && comp.Count > maxHole) continue;
                foreach (var t in comp) if (mask.Add(t)) added++;
            }
        return added;
    }

    // All tiles covered by a rect list (selftest helper / coverage checks).
    public static HashSet<(int x, int y)> Coverage(IEnumerable<RegionRect> rects)
    {
        var set = new HashSet<(int x, int y)>();
        foreach (var r in rects)
            for (int y = r.Y1; y <= r.Y2; y++)
                for (int x = r.X1; x <= r.X2; x++)
                    set.Add((x, y));
        return set;
    }

    // Straight tile line (Bresenham) - fills the gaps when the lasso cursor jumps
    // several tiles in one frame.
    public static IEnumerable<(int x, int y)> Line((int x, int y) a, (int x, int y) b)
    {
        int dx = Math.Abs(b.x - a.x), sx = a.x < b.x ? 1 : -1;
        int dy = -Math.Abs(b.y - a.y), sy = a.y < b.y ? 1 : -1;
        int err = dx + dy, x = a.x, y = a.y;
        while (true)
        {
            yield return (x, y);
            if (x == b.x && y == b.y) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }
}
