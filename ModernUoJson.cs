using System.Text;
using System.Text.Json;

namespace UORegionEditor;

// ModernUO Distribution/Data/regions.json:
//   [
//     {
//       "$type": "GuardedRegion",
//       "Map": "Felucca",
//       "Name": "Britain",
//       "Priority": 50,
//       "Area": [{"x1": 1416, "y1": 1498, "x2": 1784, "y2": 1808}],
//       "GoLocation": {"x": 1495, "y": 1629, "z": 10},
//       "Music": "Britain1"
//     }
//   ]
//
// ⚠ x2/y2 are EXCLUSIVE, same as Sphere's RECT and unlike CentrED/ServUO xml. Verified
// against ModernUO's Rectangle2D.Contains, which tests `_end.X > x` - so a rect ending at
// x2 does NOT include column x2. Internally we are inclusive, hence the +1 on export.
public static class ModernUoJson
{
    static readonly string[] Facets = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur" };

    // ModernUO is a rewrite, so its region classes are NOT ServUO's - taken from the
    // $type values in ModernUO's own Distribution/Data/regions.json, most used first.
    // Note BaseRegion (the default, and the most common), and that Jail/GreenAcres are
    // JailRegion/GreenAcresRegion here. ServUO-only classes like MondainRegion or
    // BlackthornDungeon do not exist in ModernUO and would fail to deserialize.
    public const string DefaultType = "BaseRegion";

    public static readonly string[] RegionTypes =
    {
        "BaseRegion", "TownRegion", "DungeonRegion", "NoHousingRegion",
        "GuardedRegion", "JailRegion", "GreenAcresRegion",
    };

    public static string Export(IEnumerable<RegionDef> regions)
    {
        var opts = new JsonWriterOptions { Indented = true };
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, opts))
        {
            w.WriteStartArray();
            foreach (var r in regions.Where(r => r.Rects.Count > 0))
            {
                w.WriteStartObject();
                w.WriteString("$type", string.IsNullOrWhiteSpace(r.ServuoType) ? DefaultType : r.ServuoType.Trim());
                w.WriteString("Map", Facets[Math.Clamp(r.MapPlane, 0, Facets.Length - 1)]);
                w.WriteString("Name", r.Name ?? "");
                w.WriteNumber("Priority", Math.Clamp(r.Priority, 0, 32767));
                w.WriteStartArray("Area");
                foreach (var rc in r.Rects)
                {
                    w.WriteStartObject();
                    w.WriteNumber("x1", rc.X1);
                    w.WriteNumber("y1", rc.Y1);
                    w.WriteNumber("x2", rc.X2 + 1);   // exclusive
                    w.WriteNumber("y2", rc.Y2 + 1);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                var (px, py) = r.EffectiveP();
                w.WriteStartObject("GoLocation");
                w.WriteNumber("x", px);
                w.WriteNumber("y", py);
                w.WriteNumber("z", r.PZ);
                w.WriteEndObject();
                if (!string.IsNullOrWhiteSpace(r.Music)) w.WriteString("Music", r.Music.Trim());
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static List<RegionDef> Import(string json)
    {
        var res = new List<RegionDef>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("expected a JSON array of regions");

        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var name = Str(el, "Name") ?? "unnamed";
            var r = new RegionDef
            {
                Name = name,
                DefName = MakeDefName(name, res),
                ServuoType = Str(el, "$type") ?? "",
                Music = Str(el, "Music") ?? "",
                Priority = el.TryGetProperty("Priority", out var pe) && pe.TryGetInt32(out var pv) ? pv : 50,
                MapPlane = Math.Max(0, Array.FindIndex(Facets,
                    f => f.Equals(Str(el, "Map") ?? "", StringComparison.OrdinalIgnoreCase))),
            };
            if (el.TryGetProperty("Area", out var area) && area.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in area.EnumerateArray())
                {
                    int x1 = Int(a, "x1"), y1 = Int(a, "y1"), x2 = Int(a, "x2"), y2 = Int(a, "y2");
                    // exclusive -> inclusive; guard against a degenerate/zero-size rect
                    r.Rects.Add(new RegionRect(x1, y1, Math.Max(x1, x2 - 1), Math.Max(y1, y2 - 1)));
                }
            }
            if (el.TryGetProperty("GoLocation", out var go) && go.ValueKind == JsonValueKind.Object)
            {
                r.PX = Int(go, "x"); r.PY = Int(go, "y"); r.PZ = Int(go, "z");
            }
            if (r.Rects.Count > 0) res.Add(r);
        }
        return res;
    }

    static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 0;

    static string MakeDefName(string name, List<RegionDef> existing)
    {
        var dn = "A_" + new string(name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var final = dn;
        for (int n = 2; existing.Any(r => r.DefName.Equals(final, StringComparison.OrdinalIgnoreCase)); n++)
            final = dn + "_" + n;
        return final;
    }

}
