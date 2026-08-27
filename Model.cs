using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UORegionEditor;

// Shared limits. Sphere itself has no per-region rect cap (std::vector, RAM-bound) and
// ~1,048,575 regions per type (20-bit resource index); these are OUR sane guards, well
// under Sphere's ceiling. Single source of truth for server enforcement + the UI readout.
public static class Limits
{
    public const int MaxRectsPerRegion = 8192;
    public const int MaxRegions = 50000;
    public const int DefaultMapWidth = 7168;    // UO map0 (ML); operator-configurable on the server
    public const int DefaultMapHeight = 4096;
}

// One axis-aligned box of a region. Coordinates are INCLUSIVE game tiles on both edges.
// (Sphere RECT right/bottom edges are exclusive -> the exporter adds +1; CentrED is inclusive.)
public class RegionRect
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }

    public RegionRect() { }

    public RegionRect(int x1, int y1, int x2, int y2)
    {
        X1 = Math.Min(x1, x2); Y1 = Math.Min(y1, y2);
        X2 = Math.Max(x1, x2); Y2 = Math.Max(y1, y2);
    }

    [JsonIgnore] public int W => X2 - X1 + 1;
    [JsonIgnore] public int H => Y2 - Y1 + 1;

    public bool Contains(int x, int y) => x >= X1 && x <= X2 && y >= Y1 && y <= Y2;

    public void Normalize()
    {
        if (X2 < X1) (X1, X2) = (X2, X1);
        if (Y2 < Y1) (Y1, Y2) = (Y2, Y1);
    }

    public RegionRect Clone() => new() { X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2 };

    public override string ToString() => $"{X1},{Y1} - {X2},{Y2}  ({W}x{H})";
}

public class RegionDef
{
    public Guid Uid { get; set; } = Guid.NewGuid();          // stable identity for undo/sync (defnames can change)
    public string DefName { get; set; } = "A_NEW_REGION";
    public string Name { get; set; } = "New Region";
    public string Kind { get; set; } = "AREADEF";           // AREADEF or ROOMDEF
    public string Events { get; set; } = "";
    public string Flags { get; set; } = "";
    public string Group { get; set; } = "";
    public int PX { get; set; } = -1;                        // -1,-1 = auto (center of first rect)
    public int PY { get; set; } = -1;
    public int PZ { get; set; } = 0;
    public int MapPlane { get; set; } = 0;                   // 5th RECT arg / 4th P arg (map0 shard: 0)
    public List<string> Extra { get; set; } = new();         // preserved lines (TAG.* etc.)
    public List<string> Comments { get; set; } = new();      // comment lines above the section
    // ServUO Data/Regions.xml extras. Ignored by the Sphere and CentrED exports, the way
    // EVENTS/FLAGS are ignored by ServUO - each server gets what it actually understands.
    public string ServuoType { get; set; } = "";     // TownRegion, GuardedRegion, DungeonRegion...
    public int Priority { get; set; } = 50;
    public string Music { get; set; } = "";          // <music name="Britain1"/>
    public List<RegionRect> Rects { get; set; } = new();
    public int ColorArgb { get; set; }
    public bool Visible { get; set; } = true;

    [JsonIgnore]
    public Color Color
    {
        get => ColorArgb == 0 ? Color.FromArgb(255, 80, 80) : Color.FromArgb(ColorArgb);
        set => ColorArgb = value.ToArgb();
    }

    public (int x, int y) EffectiveP()
    {
        if (PX >= 0 && PY >= 0) return (PX, PY);
        if (Rects.Count > 0)
        {
            var r = Rects[0];
            return ((r.X1 + r.X2) / 2, (r.Y1 + r.Y2) / 2);
        }
        return (0, 0);
    }

    public bool ContainsTile(int x, int y) => Rects.Any(r => r.Contains(x, y));
}

public class Project
{
    public string MulsDir { get; set; } = "";
    public bool SphereExclusiveEdge { get; set; } = true;    // RECT right/bottom edges exclusive -> +1
    public string DefaultEvents { get; set; } = "r_default,r_default_rock,r_default_water,r_default_tree,r_default_grass";
    public string DefaultFlags { get; set; } = "";
    public List<RegionDef> Regions { get; set; } = new();
    public List<string> ExtraSections { get; set; } = new(); // verbatim non-region blocks from imports ([COMMENT ...], VERSION=..., etc.)

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }

    public static Project Load(string path)
    {
        var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(path)) ?? new Project();
        EnsureUids(p.Regions);
        return p;
    }

    // older files (and foreign senders) may carry Guid.Empty - every region needs a unique Uid
    public static void EnsureUids(IEnumerable<RegionDef> regions)
    {
        var seen = new HashSet<Guid>();
        foreach (var r in regions)
        {
            if (r.Uid == Guid.Empty || !seen.Add(r.Uid))
            {
                r.Uid = Guid.NewGuid();
                seen.Add(r.Uid);
            }
        }
    }

    // Reorder the list to match a defname sequence (used by the drag-to-reorder sync:
    // list order = script order = what overrides what in Sphere on overlap). Stable:
    // names missing from the sequence keep their relative order after the known ones.
    public static void ApplyOrder(List<RegionDef> list, List<string> order)
    {
        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++) pos.TryAdd(order[i], i);
        var indexed = list.Select((r, i) => (r, i)).ToList();
        indexed.Sort((a, b) =>
        {
            bool ha = pos.TryGetValue(a.r.DefName, out var pa), hb = pos.TryGetValue(b.r.DefName, out var pb);
            if (ha && hb) return pa - pb;
            if (ha != hb) return ha ? -1 : 1;
            return a.i - b.i;
        });
        list.Clear();
        list.AddRange(indexed.Select(t => t.r));
    }
}

public static class Palette
{
    static readonly Color[] Colors =
    {
        Color.FromArgb(255, 90, 90),   Color.FromArgb(90, 165, 255),  Color.FromArgb(95, 220, 95),
        Color.FromArgb(255, 200, 70),  Color.FromArgb(205, 115, 255), Color.FromArgb(85, 220, 220),
        Color.FromArgb(255, 135, 195), Color.FromArgb(165, 215, 70),  Color.FromArgb(255, 145, 65),
        Color.FromArgb(135, 145, 255), Color.FromArgb(125, 205, 165), Color.FromArgb(235, 95, 125),
    };

    public static Color Next(int i) => Colors[((i % Colors.Length) + Colors.Length) % Colors.Length];
}
