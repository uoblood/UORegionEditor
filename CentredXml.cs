using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UORegionEditor;

// CentrED cedserver.xml regions (CentrED+ and CentrED# share this format):
//   <Regions>
//     <Region><Name>Town Square</Name>
//       <Area><Rect x1="1376" y1="1604" x2="1400" y2="1643"/></Area>
//     </Region>
//   </Regions>
// Edges are INCLUSIVE (RectU16.Contains: x >= X1 && x <= X2). Attributes lowercase.
public static class CentredXml
{
    public static XElement BuildRegionsElement(IEnumerable<RegionDef> regions)
    {
        var root = new XElement("Regions");
        foreach (var r in regions)
        {
            var area = new XElement("Area");
            foreach (var rc in r.Rects)
                area.Add(new XElement("Rect",
                    new XAttribute("x1", rc.X1), new XAttribute("y1", rc.Y1),
                    new XAttribute("x2", rc.X2), new XAttribute("y2", rc.Y2)));
            root.Add(new XElement("Region", new XElement("Name", r.Name), area));
        }
        return root;
    }

    public static string ExportSnippet(IEnumerable<RegionDef> regions)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
            BuildRegionsElement(regions).WriteTo(w);
        return sb.ToString();
    }

    // Replaces same-named regions, appends new ones. Creates a timestamped backup first.
    // The CentrED server must be STOPPED while merging (it rewrites its config on shutdown).
    public static string MergeIntoConfig(string cedserverPath, IEnumerable<RegionDef> regions)
    {
        var doc = XDocument.Load(cedserverPath);
        var root = doc.Root ?? throw new InvalidDataException("cedserver.xml has no root element");
        var regionsEl = root.Element("Regions");
        if (regionsEl == null)
        {
            regionsEl = new XElement("Regions");
            root.Add(regionsEl);
        }
        // Only elements that existed BEFORE this merge are candidates for replacement, and each
        // can be consumed once - so incoming regions that share a Name never overwrite each other
        // (the real stock file has 21 regions all named "Dungeon").
        var preExisting = regionsEl.Elements("Region").ToList();
        var consumed = new HashSet<XElement>();
        int replaced = 0, added = 0;
        foreach (var r in regions)
        {
            var newEl = BuildRegionsElement(new[] { r }).Elements().First();
            var existing = preExisting.FirstOrDefault(e => !consumed.Contains(e) &&
                string.Equals((string)e.Element("Name"), r.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.ReplaceWith(newEl); consumed.Add(existing); replaced++; }
            else { regionsEl.Add(newEl); added++; }
        }
        var dupNames = regions.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = cedserverPath + ".bak-" + stamp;
        for (int n = 2; File.Exists(backup); n++)
            backup = cedserverPath + ".bak-" + stamp + "-" + n;
        File.Copy(cedserverPath, backup);
        doc.Save(cedserverPath);
        var msg = $"{added} added, {replaced} replaced. Backup: {Path.GetFileName(backup)}";
        if (dupNames.Count > 0)
            msg += $"\n\nWARNING: {dupNames.Count} name(s) are used by more than one region ({string.Join(", ", dupNames.Take(5))}...). " +
                   "CentrED account permissions refer to regions by NAME - consider giving them unique names.";
        return msg;
    }

    public static List<RegionDef> ImportFromConfig(string cedserverPath, int colorSeed = 0)
    {
        var doc = XDocument.Load(cedserverPath);
        var result = new List<RegionDef>();
        var regionsEl = doc.Root?.Element("Regions");
        if (regionsEl == null) return result;
        int i = colorSeed;
        foreach (var e in regionsEl.Elements("Region"))
        {
            var name = (string)e.Element("Name") ?? "region";
            var rd = new RegionDef
            {
                Name = name,
                DefName = "A_" + new string(name.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()),
                Kind = "AREADEF",
            };
            rd.Color = Palette.Next(i++);
            var area = e.Element("Area");
            if (area != null)
                foreach (var rc in area.Elements("Rect"))
                    rd.Rects.Add(new RegionRect(
                        (int?)rc.Attribute("x1") ?? 0, (int?)rc.Attribute("y1") ?? 0,
                        (int?)rc.Attribute("x2") ?? 0, (int?)rc.Attribute("y2") ?? 0));
            result.Add(rd);
        }
        return result;
    }
}
