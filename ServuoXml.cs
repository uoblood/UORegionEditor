using System.Xml.Linq;

namespace UORegionEditor;

// ServUO / RunUO Data\Regions.xml:
//   <ServerRegions><Facet name="Felucca">
//     <region type="TownRegion" priority="50" name="Britain">
//       <rect x="1416" y="1498" width="368" height="310" />
//       <go x="1495" y="1629" z="10" />
//       <music name="Britain1" />
//     </region>
//   </Facet></ServerRegions>
// rect is x/y + WIDTH/HEIGHT (count, not inclusive corner) -> inclusive X2 = x+width-1.
// Facet name maps to our MapPlane by classic facet index.
//
// `type` is the region CLASS ServUO instantiates - without it the server falls back to a
// plain region, so it matters as much as EVENTS does on the Sphere side.
public static class ServuoXml
{
    static readonly string[] Facets = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur" };

    // types present in ServUO's own Data/Regions.xml, most useful first
    public static readonly string[] RegionTypes =
    {
        "TownRegion", "GuardedRegion", "DungeonRegion", "NoHousingRegion", "MondainRegion",
        "NoTravelSpellsAllowed", "Jail", "GreenAcres", "SeaMarketRegion", "NewMaginciaRegion",
        "BlackthornDungeon", "BlackthornCastle", "ApprenticeRegion", "TwistedWealdDesert",
        "VerLorRegCity", "CrystalField", "IcyRiver", "AcidRiver", "PoisonedTree",
    };

    public static List<RegionDef> Import(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException("empty xml");
        var res = new List<RegionDef>();
        foreach (var facet in root.Elements("Facet"))
        {
            int plane = Array.FindIndex(Facets,
                f => f.Equals((string)facet.Attribute("name") ?? "", StringComparison.OrdinalIgnoreCase));
            if (plane < 0) plane = 0;
            foreach (var reg in facet.Descendants("region"))
                res.Add(ReadRegion(reg, plane, res));
        }
        return res;
    }

    static RegionDef ReadRegion(XElement reg, int plane, List<RegionDef> existing)
    {
        var name = (string)reg.Attribute("name") ?? "unnamed";
        var r = new RegionDef
        {
            Name = name,
            MapPlane = plane,
            DefName = MakeDefName(name, existing),
            ServuoType = (string)reg.Attribute("type") ?? "",
            Priority = (int?)reg.Attribute("priority") ?? 50,
            Music = (string)reg.Element("music")?.Attribute("name") ?? "",
        };
        foreach (var rc in reg.Elements("rect"))
        {
            int x = (int?)rc.Attribute("x") ?? 0, y = (int?)rc.Attribute("y") ?? 0;
            int w = (int?)rc.Attribute("width") ?? 0, h = (int?)rc.Attribute("height") ?? 0;
            if (w > 0 && h > 0) { r.Rects.Add(new RegionRect(x, y, x + w - 1, y + h - 1)); continue; }
            // the corner form also appears in the wild
            int x1 = (int?)rc.Attribute("x1") ?? int.MinValue, y1 = (int?)rc.Attribute("y1") ?? int.MinValue;
            int x2 = (int?)rc.Attribute("x2") ?? int.MinValue, y2 = (int?)rc.Attribute("y2") ?? int.MinValue;
            if (x1 != int.MinValue && y1 != int.MinValue && x2 != int.MinValue && y2 != int.MinValue)
                r.Rects.Add(new RegionRect(x1, y1, x2, y2));
        }
        var go = reg.Element("go");
        if (go != null)
        {
            r.PX = (int?)go.Attribute("x") ?? -1;
            r.PY = (int?)go.Attribute("y") ?? -1;
            r.PZ = (int?)go.Attribute("z") ?? 0;
        }
        return r;
    }

    static string MakeDefName(string name, List<RegionDef> existing)
    {
        var dn = "A_" + new string(name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var final = dn;
        for (int n = 2; existing.Any(r => r.DefName.Equals(final, StringComparison.OrdinalIgnoreCase)); n++)
            final = dn + "_" + n;
        return final;
    }

    public static string ExportXml(IEnumerable<RegionDef> regions)
    {
        var root = new XElement("ServerRegions");
        foreach (var g in regions.Where(r => r.Rects.Count > 0)
                     .GroupBy(r => Math.Clamp(r.MapPlane, 0, Facets.Length - 1)).OrderBy(g => g.Key))
        {
            var facet = new XElement("Facet", new XAttribute("name", Facets[g.Key]));
            foreach (var r in g)
            {
                var reg = new XElement("region",
                    new XAttribute("priority", r.Priority));
                if (!string.IsNullOrWhiteSpace(r.ServuoType))
                    reg.SetAttributeValue("type", r.ServuoType.Trim());
                reg.SetAttributeValue("name", r.Name);
                foreach (var rc in r.Rects)
                    reg.Add(new XElement("rect",
                        new XAttribute("x", rc.X1), new XAttribute("y", rc.Y1),
                        new XAttribute("width", rc.X2 - rc.X1 + 1), new XAttribute("height", rc.Y2 - rc.Y1 + 1)));
                var (px, py) = r.EffectiveP();
                reg.Add(new XElement("go",
                    new XAttribute("x", px), new XAttribute("y", py), new XAttribute("z", r.PZ)));
                if (!string.IsNullOrWhiteSpace(r.Music))
                    reg.Add(new XElement("music", new XAttribute("name", r.Music.Trim())));
                facet.Add(reg);
            }
            root.Add(facet);
        }
        return new XDocument(
            new XComment(" generated by UO Region Editor - merge into ServUO Data/Regions.xml "),
            root).ToString();
    }
}
