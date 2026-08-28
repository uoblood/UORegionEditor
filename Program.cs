using System.Text;
using System.Xml.Linq;

namespace UORegionEditor;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            SelfTest(args.FirstOrDefault(a => a != "--selftest"));
            return;
        }
        if (args.Contains("--rendertest"))
        {
            // --rendertest X Y out.png : offline composite of the iso view + region highlights
            // using exactly the app's math, so rendering issues can be inspected as an image.
            var rest = args.Where(a => a != "--rendertest").ToArray();
            RenderTest(int.Parse(rest[0]), int.Parse(rest[1]), rest[2],
                rest.Length > 3 ? int.Parse(rest[3]) : 127);
            return;
        }
        if (args.Contains("--slopescan"))
        {
            // find where the project's regions sit on the most sloped land: those tiles are
            // where a flat highlight quad diverges most from the warped terrain
            SlopeScan();
            return;
        }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--classic"))
        {
            Application.Run(new MainForm());
            return;
        }
        try
        {
            ImGuiApp.RunApp();
        }
        catch (Exception ex)
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UORegionEditor", "crash.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log));
                File.WriteAllText(log, DateTime.Now + Environment.NewLine + ex);
            }
            catch { }
            MessageBox.Show("The ImGui interface crashed:\n\n" + ex.Message +
                "\n\nDetails: " + log + "\nFallback: run with --classic for the old interface.",
                "UORegionEditor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // diagnostic: rank the project's region tiles by how much their land corners differ.
    // A flat highlight quad is drawn at ONE z, so corner spread = pixel error on screen.
    // Local fixtures for the deeper checks. Never hardcode a developer's own folders
    // here - this repo is public. UORE_MULS / UORE_SCP_DIR opt in; without them the
    // checks that need real files simply skip (they are all guarded by File.Exists).
    static string DevMuls()
    {
        var env = Environment.GetEnvironmentVariable("UORE_MULS");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UORegionEditor", "last-project.json");
            if (File.Exists(p)) return Project.Load(p).MulsDir ?? "";
        }
        catch { }
        return "";
    }

    static string DevScp(string name)
    {
        var dir = Environment.GetEnvironmentVariable("UORE_SCP_DIR");
        return string.IsNullOrWhiteSpace(dir) ? "" : Path.Combine(dir, name);
    }

    static void SlopeScan()
    {
        var projPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UORegionEditor", "last-project.json");
        var proj = File.Exists(projPath) ? Project.Load(projPath) : new Project();
        var muls = proj.MulsDir;
        if (string.IsNullOrEmpty(muls) || MulRadar.FindMap(muls) == null)
            muls = DevMuls();
        var md = MulRadar.LoadData(muls);
        var sb = new StringBuilder();
        sb.AppendLine($"muls: {muls}");
        var worst = new List<(int spread, int x, int y, string reg)>();
        foreach (var r in proj.Regions)
        {
            foreach (var rc in r.Rects ?? new List<RegionRect>())
                for (int y = rc.Y1; y <= rc.Y2; y++)
                    for (int x = rc.X1; x <= rc.X2; x++)
                    {
                        int a = md.LandAt(x, y).z, b = md.LandAt(x + 1, y).z;
                        int c = md.LandAt(x + 1, y + 1).z, d = md.LandAt(x, y + 1).z;
                        int spread = Math.Max(Math.Max(a, b), Math.Max(c, d)) - Math.Min(Math.Min(a, b), Math.Min(c, d));
                        if (spread >= 4) worst.Add((spread, x, y, r.DefName));
                    }
        }
        sb.AppendLine($"region tiles on sloped land (corner spread >= 4): {worst.Count}");
        foreach (var w in worst.OrderByDescending(w => w.spread).Take(25))
            sb.AppendLine($"  spread {w.spread,3} z-units ({w.spread * ArtView.IsoZStep,3}px) at {w.x},{w.y}  in {w.reg}");

        File.WriteAllText("slopescan.txt", sb.ToString());
    }

    static void RenderTest(int tx, int ty, string outPng, int maxZ = 127)
    {
        var projPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UORegionEditor", "last-project.json");
        var proj = File.Exists(projPath) ? Project.Load(projPath) : new Project();
        // use the SAME muls the app is currently using, else the fallback pack
        var muls = !string.IsNullOrEmpty(proj.MulsDir) && MulRadar.FindMap(proj.MulsDir) != null
            ? proj.MulsDir
            : DevMuls();
        using var av = new ArtView(muls);
        av.SetFilter(-128, (sbyte)Math.Clamp(maxZ, -128, 127), true, true, false);

        int W = 1600, H = 1200;
        var (cpx, cpy) = av.IsoTileToPx(tx, ty);
        int ox = (int)cpx - W / 2, oy = (int)cpy - H / 2;

        using var canvas = new Bitmap(W, H);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(16, 16, 18));
        int c0x = Math.Max(0, ox / ArtView.IsoChunkPx), c1x = (ox + W) / ArtView.IsoChunkPx;
        int c0y = Math.Max(0, oy / ArtView.IsoChunkPx), c1y = (oy + H) / ArtView.IsoChunkPx;
        for (int cy = c0y; cy <= c1y; cy++)
            for (int cx = c0x; cx <= c1x; cx++)
            {
                var chunk = av.RenderIsoChunk(cx, cy);
                if (chunk != null)
                    g.DrawImage(chunk, cx * ArtView.IsoChunkPx - ox, cy * ArtView.IsoChunkPx - oy);
            }

        // region highlights with the app's exact per-tile geometry
        foreach (var r in proj.Regions)
        {
            if (!r.Visible) continue;
            using var fill = new SolidBrush(Color.FromArgb(85, r.Color));
            using var pen = new Pen(r.Color, 2f);
            foreach (var rc in r.Rects)
            {
                for (int y = rc.Y1; y <= rc.Y2; y++)
                    for (int x = rc.X1; x <= rc.X2; x++)
                    {
                        var q = av.TileHighlightQuad(x, y);
                        var pts = q.Select(p => new PointF((float)(p.x - ox), (float)(p.y - oy))).ToArray();
                        if (pts.All(p => p.X < -50 || p.X > W + 50 || p.Y < -50 || p.Y > H + 50)) continue;
                        g.FillPolygon(fill, pts);
                        if (y == rc.Y1) g.DrawLine(pen, pts[0], pts[1]);
                        if (x == rc.X2) g.DrawLine(pen, pts[1], pts[2]);
                        if (y == rc.Y2) g.DrawLine(pen, pts[2], pts[3]);
                        if (x == rc.X1) g.DrawLine(pen, pts[3], pts[0]);
                    }
            }
        }

        // reference grid around the target tile to eyeball vertex alignment with the terrain
        using var gridPen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f);
        for (int y = ty - 6; y <= ty + 6; y++)
            for (int x = tx - 6; x <= tx + 6; x++)
            {
                var (ax, ay) = av.IsoTileToPx(x, y, av.LandVertexZ(x, y));
                var (bx, by) = av.IsoTileToPx(x + 1, y, av.LandVertexZ(x + 1, y));
                var (dx, dy) = av.IsoTileToPx(x, y + 1, av.LandVertexZ(x, y + 1));
                g.DrawLine(gridPen, (float)(ax - ox), (float)(ay - oy), (float)(bx - ox), (float)(by - oy));
                g.DrawLine(gridPen, (float)(ax - ox), (float)(ay - oy), (float)(dx - ox), (float)(dy - oy));
            }

        canvas.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
        Environment.Exit(0);
    }

    // Headless checks of the export/import/render logic. Writes a report file (WinExe has no console).
    static void SelfTest(string reportPath)
    {
        var sb = new StringBuilder();
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
            if (!ok) fails++;
        }

        try
        {
            // 1. rect normalize
            var rr = new RegionRect(10, 20, 5, 8);
            Check("rect normalize", rr.X1 == 5 && rr.Y1 == 8 && rr.X2 == 10 && rr.Y2 == 20);

            // 2. sphere export with +1 exclusive edges + auto P
            var p = new Project { SphereExclusiveEdge = true };
            var reg = new RegionDef { DefName = "A_TEST", Name = "Test", Events = "r_default", Flags = "REGION_FLAG_SAFE" };
            reg.Rects.Add(new RegionRect(10, 10, 19, 19));
            p.Regions.Add(reg);
            var scp = SphereScp.Export(p, p.Regions);
            Check("sphere RECT +1", scp.Contains("RECT=10,10,20,20,0"), scp.Split('\n').FirstOrDefault(l => l.StartsWith("RECT=")) ?? "no RECT");
            Check("sphere auto P", scp.Contains("P=14,14,0,0"));
            Check("sphere section", scp.Contains("[AREADEF A_TEST]"));

            // 3. round trip
            var back = SphereScp.Import(scp, true).Regions;
            Check("sphere reimport count", back.Count == 1 && back[0].Rects.Count == 1);
            if (back.Count == 1 && back[0].Rects.Count == 1)
            {
                var b = back[0].Rects[0];
                Check("sphere round-trip coords", b.X1 == 10 && b.Y1 == 10 && b.X2 == 19 && b.Y2 == 19, b.ToString());
                Check("sphere round-trip P", back[0].PX == 14 && back[0].PY == 14);
            }

            // 4. sphere leniencies matched to the real server behavior
            var lenient = SphereScp.Import(
                "VERSION=X\n" +
                "[AREADEFF a_typo]\nNAME=Typo Region\nRECT=100,100,111,111,0\n\n" +
                "[AREADEF a_minax]\nEVENTS=r_default,r_grass\nTAG.X=1\nEVENTS=r_faction_area\nRECT=1103,2615,1213,2579.0\n\n" +
                "[COMMENT AREADEF a_disabled]\nNAME=stash\nRECT=1,1,2,2,0\n\n[EOF]\n", true);
            Check("AREADEFF prefix accepted", lenient.Regions.Any(r => r.DefName == "a_typo" && r.Rects.Count == 1),
                $"{lenient.Regions.Count} regions");
            var minax = lenient.Regions.FirstOrDefault(r => r.DefName == "a_minax");
            Check("RECT atoi (2579.0)", minax != null && minax.Rects.Count == 1 && minax.Rects[0].Y1 == 2579 - 1 + 1
                && minax.Rects[0].Y2 == 2614, minax == null ? "missing" : minax.Rects.FirstOrDefault()?.ToString() ?? "no rect");
            Check("EVENTS accumulate", minax != null && minax.Events == "r_default,r_grass,r_faction_area", minax?.Events ?? "");
            Check("COMMENT block preserved", lenient.OtherSections.Any(s => s.Contains("[COMMENT AREADEF a_disabled]") && s.Contains("RECT=1,1,2,2,0")),
                $"{lenient.OtherSections.Count} other sections");
            Check("VERSION preserved", lenient.OtherSections.Any(s => s.Contains("VERSION=X")));
            var p4 = new Project();
            p4.Regions.AddRange(lenient.Regions);
            p4.ExtraSections.AddRange(lenient.OtherSections);
            var reexport = SphereScp.Export(p4, p4.Regions);
            Check("COMMENT re-emitted on export", reexport.Contains("[COMMENT AREADEF a_disabled]"));

            // 4.5 compact export (the server-side regions.scp): functional lines only, no banner
            var compact = SphereScp.ExportCompact(p.Regions);
            Check("compact export", compact.Contains("RECT=10,10,20,20,0") && compact.Contains("[AREADEF A_TEST]")
                && !compact.Contains("Generated by") && !compact.Contains("boxes"), compact.Length + " chars");

            // The exported script lands on someone else's shard, so the banner may only
            // identify the tool - no author names, no other language. Asserted as an
            // invariant (every non-separator banner line names UORegionEditor) rather than
            // by listing forbidden words, which would just put them back in the source.
            var banner = SphereScp.Export(p, p.Regions);
            var bannerLines = banner.Split('\n')
                .TakeWhile(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("//"))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.Any(ch => char.IsLetterOrDigit(ch)))
                .ToList();
            Check("export banner identifies only the tool",
                bannerLines.Count > 0 && bannerLines.All(l => l.Contains("UORegionEditor")),
                string.Join(" | ", bannerLines));

            // 5. comment attribution: a comment after a blank line belongs to the NEXT region
            var attrib = SphereScp.Import(
                "[AREADEF a_one]\nNAME=One\nRECT=1,1,3,3,0\n\n// this belongs to two\n[AREADEF a_two]\nNAME=Two\nRECT=5,5,7,7,0\n", true);
            Check("comment goes to next region", attrib.Regions.Count == 2 &&
                attrib.Regions[1].Comments.Any(c => c.Contains("belongs to two")) &&
                !attrib.Regions[0].Extra.Any(c => c.Contains("belongs to two")));

            // 6. real files, if present. Point UORE_SCP_DIR at a folder of .scp scripts and
            // every one of them gets parsed, re-exported and parsed again - each region's
            // rectangles must come back identical. Catches edge conventions and formatting
            // drift against whatever scripts you actually run, not just the samples above.
            var scpDir = Environment.GetEnvironmentVariable("UORE_SCP_DIR");
            if (!string.IsNullOrWhiteSpace(scpDir) && Directory.Exists(scpDir))
            {
                foreach (var file in Directory.GetFiles(scpDir, "*.scp").OrderBy(f => f))
                {
                    var regions = SphereScp.Import(File.ReadAllText(file), true).Regions;
                    if (regions.Count == 0) continue;
                    var p2 = new Project();
                    p2.Regions.AddRange(regions);
                    var again = SphereScp.Import(SphereScp.Export(p2, p2.Regions), true).Regions;
                    Check($"{Path.GetFileName(file)} round-trip",
                        again.Count == regions.Count &&
                        again.Zip(regions).All(t => t.First.Rects.Count == t.Second.Rects.Count &&
                            t.First.Rects.Zip(t.Second.Rects).All(u =>
                                u.First.X1 == u.Second.X1 && u.First.Y1 == u.Second.Y1 &&
                                u.First.X2 == u.Second.X2 && u.First.Y2 == u.Second.Y2)),
                        $"{regions.Count} regions, {regions.Sum(r => r.Rects.Count)} rects");
                }
            }
            // the stock Sphere area script, if it is in that folder - its real-world quirks
            // (the [AREADEFF] typos, a malformed RECT, repeated EVENTS lines) are the reason
            // the parser is as lenient as it is
            var stock = DevScp("map0_areas.scp");
            if (File.Exists(stock))
            {
                var res = SphereScp.Import(File.ReadAllText(stock), true);
                Check("map0_areas parse incl AREADEFF", res.Regions.Count >= 312,
                    $"{res.Regions.Count} regions, {res.Regions.Sum(r => r.Rects.Count)} rects, {res.OtherSections.Count} other blocks");
                Check("map0_areas AREADEFF regions", res.Regions.Any(r => r.DefName.Equals("a_guard_tower_1", StringComparison.OrdinalIgnoreCase)));
                var mx = res.Regions.FirstOrDefault(r => r.DefName.Equals("a_Minax_Stronghold", StringComparison.OrdinalIgnoreCase));
                Check("map0_areas Minax typo rect kept", mx != null && mx.Rects.Any(t => t.Y1 == 2579 && t.X1 == 1103),
                    mx == null ? "missing" : $"{mx.Rects.Count} rects");
                var minoc = res.Regions.FirstOrDefault(r => r.DefName.Equals("a_townMinoc", StringComparison.OrdinalIgnoreCase));
                Check("map0_areas Minoc dual EVENTS", minoc != null && minoc.Events.Contains("r_default,") && minoc.Events.Split(',').Length >= 7,
                    minoc?.Events ?? "missing");
            }

            // 7. centred snippet
            var xml = CentredXml.ExportSnippet(p.Regions);
            var doc = XElement.Parse(xml);
            var rect = doc.Element("Region")?.Element("Area")?.Element("Rect");
            Check("centred snippet", rect != null &&
                (int)rect.Attribute("x1") == 10 && (int)rect.Attribute("x2") == 19);

            // 8. merge into a temp config: add, replace, and duplicate names must not collapse
            var tmp = Path.Combine(Path.GetTempPath(), $"uore-selftest-{Guid.NewGuid():N}.xml");
            File.WriteAllText(tmp, "<?xml version=\"1.0\"?>\n<CEDConfig Version=\"6\"><Port>2593</Port><Accounts><Account><Name>a</Name></Account></Accounts><Regions/></CEDConfig>");
            CentredXml.MergeIntoConfig(tmp, p.Regions);
            var merged = XDocument.Load(tmp);
            Check("centred merge add", merged.Root.Element("Regions").Elements("Region").Count() == 1);
            Check("centred merge preserves", merged.Root.Element("Accounts") != null && (string)merged.Root.Element("Port") == "2593");
            reg.Rects.Add(new RegionRect(50, 50, 60, 60));
            CentredXml.MergeIntoConfig(tmp, p.Regions);
            merged = XDocument.Load(tmp);
            Check("centred merge replace", merged.Root.Element("Regions").Elements("Region").Count() == 1 &&
                merged.Root.Element("Regions").Element("Region").Element("Area").Elements("Rect").Count() == 2);
            var dupProject = new Project();
            var d1 = new RegionDef { DefName = "A_D1", Name = "Dungeon" };
            d1.Rects.Add(new RegionRect(1, 1, 2, 2));
            var d2 = new RegionDef { DefName = "A_D2", Name = "Dungeon" };
            d2.Rects.Add(new RegionRect(5, 5, 6, 6));
            dupProject.Regions.Add(d1);
            dupProject.Regions.Add(d2);
            CentredXml.MergeIntoConfig(tmp, dupProject.Regions);
            merged = XDocument.Load(tmp);
            Check("centred merge no dup collapse",
                merged.Root.Element("Regions").Elements("Region").Count(e => (string)e.Element("Name") == "Dungeon") == 2,
                $"{merged.Root.Element("Regions").Elements("Region").Count()} regions total");
            foreach (var bak in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(tmp) + ".bak-*")) File.Delete(bak);
            File.Delete(tmp);

            // 8.5 wire serialization must NOT drop non-default-initialized properties (Visible=false)
            {
                var hidden = new RegionDef { DefName = "A_HIDDEN", Visible = false };
                var wireJson = System.Text.Json.JsonSerializer.Serialize(hidden, Net.Wire.Opts);
                Check("wire keeps Visible=false", wireJson.Contains("\"Visible\":false"), wireJson.Length > 200 ? wireJson.Substring(0, 200) : wireJson);
            }

            // 8.6 per-region undo: undoing MY edit must not revert a teammate's concurrent edit
            {
                var up = new Project();
                var mine = new RegionDef { DefName = "A_MINE" };
                mine.Rects.Add(new RegionRect(1, 1, 5, 5));
                var theirs = new RegionDef { DefName = "A_THEIRS" };
                theirs.Rects.Add(new RegionRect(50, 50, 55, 55));
                up.Regions.Add(mine);
                up.Regions.Add(theirs);
                var um = new UndoManager();
                um.Snapshot("move my box", up, new[] { mine });
                mine.Rects[0].X1 = 10; mine.Rects[0].X2 = 15;          // my edit
                theirs.Rects[0].Y1 = 60; theirs.Rects[0].Y2 = 66;      // teammate's edit arrives after my snapshot
                var res = um.Undo(up);
                var mineNow = up.Regions.First(r => r.DefName == "A_MINE");
                var theirsNow = up.Regions.First(r => r.DefName == "A_THEIRS");
                Check("undo restores only my region", res.Count == 1 && mineNow.Rects[0].X1 == 1 && mineNow.Rects[0].X2 == 5);
                Check("undo leaves teammate edit intact", theirsNow.Rects[0].Y1 == 60 && theirsNow.Rects[0].Y2 == 66);
                var res2 = um.Redo(up);
                Check("redo reapplies my edit", res2 != null && up.Regions.First(r => r.DefName == "A_MINE").Rects[0].X1 == 10);
                // create-undo removes; rename-undo reports the pre-restore name
                var created = new RegionDef { DefName = "A_CREATED" };
                um.Snapshot("create", up, new[] { created });
                up.Regions.Add(created);
                um.Undo(up);
                Check("undo of create removes region", !up.Regions.Any(r => r.DefName == "A_CREATED"));
                var ren = up.Regions.First(r => r.DefName == "A_MINE");
                um.Snapshot("rename", up, new[] { ren });
                ren.DefName = "A_RENAMED";
                var res3 = um.Undo(up);
                Check("undo rename reports prev name", res3.Count == 1 && res3[0].DefNameBefore == "A_RENAMED" &&
                    res3[0].Now.DefName == "A_MINE");
            }

            // 8a2. ServUO Regions.xml round-trip (rect width/height <-> inclusive corners)
            {
                var a = new RegionDef { DefName = "A_SRV", Name = "Srv Town", MapPlane = 0, PX = 100, PY = 110, PZ = 5 };
                a.Rects.Add(new RegionRect(50, 60, 70, 80));
                a.Rects.Add(new RegionRect(90, 90, 95, 99));
                a.ServuoType = "TownRegion";
                a.Priority = 75;
                a.Music = "Britain1";
                var xmlPath = Path.Combine(Path.GetTempPath(), $"uore-servuo-{Guid.NewGuid():N}.xml");
                File.WriteAllText(xmlPath, ServuoXml.ExportXml(new[] { a }));
                var srvBack = ServuoXml.Import(xmlPath);
                try { File.Delete(xmlPath); } catch { }
                Check("servuo xml round-trip", srvBack.Count == 1 && srvBack[0].Rects.Count == 2 &&
                    srvBack[0].Rects[0].X2 == 70 && srvBack[0].Rects[0].Y2 == 80 && srvBack[0].Rects[1].Y2 == 99 &&
                    srvBack[0].PX == 100 && srvBack[0].PY == 110 && srvBack[0].PZ == 5 && srvBack[0].Name == "Srv Town");
                Check("servuo type/priority/music round-trip",
                    srvBack.Count == 1 && srvBack[0].ServuoType == "TownRegion" &&
                    srvBack[0].Priority == 75 && srvBack[0].Music == "Britain1",
                    srvBack.Count == 0 ? "no regions" : $"type={srvBack[0].ServuoType} prio={srvBack[0].Priority} music={srvBack[0].Music}");

                // ModernUO json: x2/y2 are EXCLUSIVE (Rectangle2D.Contains tests _end.X > x),
                // so a 50..70 inclusive rect must be written as x2=71 and read back as 70
                var m = new RegionDef { DefName = "A_MUO", Name = "Muo Town", MapPlane = 1, PX = 100, PY = 110, PZ = 5 };
                m.Rects.Add(new RegionRect(50, 60, 70, 80));
                m.ServuoType = "TownRegion"; m.Priority = 75; m.Music = "Britain1";
                var mjson = ModernUoJson.Export(new[] { m });
                var mBack = ModernUoJson.Import(mjson);
                Check("modernuo json exclusive edges",
                    mjson.Contains("\"x2\": 71") && mjson.Contains("\"y2\": 81"),
                    mjson.Split('\n').FirstOrDefault(l => l.Contains("\"x2\""))?.Trim() ?? "no rect");
                Check("modernuo json round-trip",
                    mBack.Count == 1 && mBack[0].Rects.Count == 1 &&
                    mBack[0].Rects[0].X1 == 50 && mBack[0].Rects[0].Y1 == 60 &&
                    mBack[0].Rects[0].X2 == 70 && mBack[0].Rects[0].Y2 == 80 &&
                    mBack[0].Name == "Muo Town" && mBack[0].MapPlane == 1 &&
                    mBack[0].ServuoType == "TownRegion" && mBack[0].Priority == 75 &&
                    mBack[0].Music == "Britain1" && mBack[0].PX == 100 && mBack[0].PZ == 5,
                    mBack.Count == 0 ? "no regions" : $"{mBack[0].Rects[0]} map={mBack[0].MapPlane} type={mBack[0].ServuoType}");

                // an untyped region must export ModernUO's own default class. "Region" is
                // not a ModernUO type and would fail to deserialize; BaseRegion is what
                // its own regions.json uses for 136 of its regions.
                var bare = new RegionDef { DefName = "A_BARE", Name = "Bare" };
                bare.Rects.Add(new RegionRect(1, 1, 2, 2));
                var bareJson = ModernUoJson.Export(new[] { bare });
                Check("modernuo untyped region defaults to BaseRegion",
                    bareJson.Contains("\"$type\": \"BaseRegion\"") && !bareJson.Contains("\"$type\": \"Region\""),
                    bareJson.Split('\n').FirstOrDefault(l => l.Contains("$type"))?.Trim() ?? "no type");

                // ModernUO is a rewrite: its region classes are not ServUO's, and mixing
                // them would produce a file its server refuses to load
                Check("modernuo has its own region class list",
                    ModernUoJson.RegionTypes.Contains("BaseRegion") &&
                    ModernUoJson.RegionTypes.Contains("JailRegion") &&
                    !ModernUoJson.RegionTypes.Contains("MondainRegion") &&
                    ServuoXml.RegionTypes.Contains("MondainRegion"),
                    $"{ModernUoJson.RegionTypes.Length} modernuo / {ServuoXml.RegionTypes.Length} servuo");

                // profiles saved before the shard setting existed have no Target at all,
                // and those are Sphere shards - they must not land on ServUO
                Check("shard target from profile name",
                    ImGuiApp.TargetFromName(null) == 0 && ImGuiApp.TargetFromName("") == 0 &&
                    ImGuiApp.TargetFromName("Sphere") == 0 && ImGuiApp.TargetFromName("servuo") == 1 &&
                    ImGuiApp.TargetFromName("CentrED+") == 2 && ImGuiApp.TargetFromName("whatever") == 0 &&
                    ImGuiApp.TargetFromName("ModernUO") == 3 && ImGuiApp.TargetFromName("modernuo") == 3);
            }

            // 8c. RegionOps.FillGaps: grass gaps inside a wand-selected forest
            {
                // a solid block with two small gaps punched in it
                var forest = RegionOps.Coverage(new[] { new RegionRect(0, 0, 19, 19) });
                forest.Remove((5, 5));
                forest.Remove((6, 5));
                forest.Remove((12, 14));
                int addedTiles = RegionOps.FillGaps(forest, 0);
                Check("enclosed gaps are filled", addedTiles == 3 && forest.Count == 400, $"added {addedTiles}");

                // a bite out of the EDGE reaches the outside, so it is not a hole
                var bitten = RegionOps.Coverage(new[] { new RegionRect(0, 0, 19, 19) });
                bitten.Remove((0, 10)); bitten.Remove((1, 10));
                Check("edge notches are left alone", RegionOps.FillGaps(bitten, 0) == 0);

                // a clearing bigger than the cap stays open; the small gap next to it fills
                var lake = RegionOps.Coverage(new[] { new RegionRect(0, 0, 29, 29) });
                for (int y = 4; y <= 12; y++)
                    for (int x = 4; x <= 12; x++) lake.Remove((x, y));   // 81-tile clearing
                lake.Remove((20, 20));
                int cappedAdd = RegionOps.FillGaps(lake, 16);
                Check("big clearing survives the cap, small gap fills",
                    cappedAdd == 1 && !lake.Contains((8, 8)) && lake.Contains((20, 20)), $"added {cappedAdd}");

                // a gap connected to the outside by a one-tile channel is not enclosed
                var channel = RegionOps.Coverage(new[] { new RegionRect(0, 0, 19, 19) });
                for (int y = 5; y <= 8; y++)
                    for (int x = 5; x <= 8; x++) channel.Remove((x, y));
                for (int x = 0; x <= 4; x++) channel.Remove((x, 6));     // cut out to the west edge
                Check("gap with a channel to the outside is not filled", RegionOps.FillGaps(channel, 0) == 0);

                // the outer shape must never change
                var ring = RegionOps.Coverage(new[] { new RegionRect(0, 0, 9, 9) });
                ring.Remove((4, 4));
                RegionOps.FillGaps(ring, 0);
                Check("outer bounds unchanged by gap filling",
                    ring.Count == 100 && !ring.Contains((-1, -1)) && !ring.Contains((10, 10)));
            }

            // 8b. RegionOps: lasso fill, mask->rect decomposition, box/mask subtraction
            {
                var sq = new List<(int x, int y)>();
                for (int x = 0; x <= 11; x++) sq.Add((x, 0));
                for (int y = 1; y <= 11; y++) sq.Add((11, y));
                for (int x = 10; x >= 0; x--) sq.Add((x, 11));
                for (int y = 10; y >= 1; y--) sq.Add((0, y));
                var fill = RegionOps.LassoFill(sq);
                Check("lasso fills a square", fill.Count == 144);
                var rects = RegionOps.MaskToRects(fill);
                var cov0 = RegionOps.Coverage(rects);
                Check("mask->rects exact, no overlap", cov0.SetEquals(fill) &&
                    cov0.Count == rects.Sum(r => (long)r.W * r.H));
                var afterBox = RegionOps.SubtractBox(rects, new RegionRect(3, 3, 8, 8));
                var cov1 = RegionOps.Coverage(afterBox);
                Check("erase box cuts exact tiles", cov1.Count == 144 - 36 &&
                    !cov1.Contains((5, 5)) && cov1.Contains((2, 2)) &&
                    cov1.Count == afterBox.Sum(r => (long)r.W * r.H));
                var mask = new HashSet<(int x, int y)> { (0, 0), (1, 0), (0, 1) };
                var afterMask = RegionOps.SubtractMask(afterBox, mask);
                var cov2 = RegionOps.Coverage(afterMask);
                Check("erase lasso cuts exact tiles", cov2.Count == cov1.Count - 3 &&
                    !cov2.Contains((0, 0)) && cov2.Contains((1, 1)) &&
                    cov2.Count == afterMask.Sum(r => (long)r.W * r.H));
                // full-map rect minus a tiny mask: must be instant (row-run algorithm,
                // not bbox rasterization) and exact, with a compact result
                var big = new List<RegionRect> { new RegionRect(0, 0, 7167, 4095) };
                var cut3 = new HashSet<(int x, int y)> { (100, 100), (101, 100), (5000, 3000) };
                var swm = System.Diagnostics.Stopwatch.StartNew();
                var res3 = RegionOps.SubtractMask(big, cut3);
                swm.Stop();
                Check("subtract tiny mask from full-map rect", swm.ElapsedMilliseconds < 500 &&
                    res3.Sum(r => (long)r.W * r.H) == 7168L * 4096 - 3 && res3.Count <= 12 &&
                    !res3.Any(r => r.Contains(100, 100)) && !res3.Any(r => r.Contains(101, 100)) &&
                    !res3.Any(r => r.Contains(5000, 3000)) && res3.Any(r => r.Contains(99, 100)),
                    $"{res3.Count} rects in {swm.ElapsedMilliseconds}ms");
            }

            // 8c. RegionOps: magic-wand flood fill (quick select) on a synthetic color grid
            {
                var grid = new int[8, 8];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        grid[x, y] = x < 4 ? 0x0A0A0A : 0x282828;
                grid[5, 5] = 0x0A0A0A;   // left-half color, but not 4-connected to the left half
                int ColorAt(int x, int y) => grid[x, y];
                var wf = RegionOps.WandFill((1, 1), 8, 8, ColorAt, 0, 10_000, out bool ovf);
                Check("wand fills the connected patch", !ovf && wf.Count == 32 && !wf.Contains((5, 5)),
                    $"{wf.Count} tiles");
                var wr = RegionOps.MaskToRects(wf);
                Check("wand mask -> exact rects", RegionOps.Coverage(wr).SetEquals(wf) &&
                    wf.Count == wr.Sum(r => (long)r.W * r.H));
                var wt = RegionOps.WandFill((1, 1), 8, 8, ColorAt, 30, 10_000, out ovf);
                Check("wand tolerance crosses the border", !ovf && wt.Count == 64, $"{wt.Count} tiles");
                RegionOps.WandFill((1, 1), 8, 8, ColorAt, 64, 20, out ovf);
                Check("wand tile cap sets overflow", ovf);
                int GradAt(int x, int y) => (x * 10) << 16;
                var wg = RegionOps.WandFill((0, 0), 8, 1, GradAt, 25, 10_000, out ovf);
                Check("wand tolerance is seed-relative", !ovf && wg.Count == 3, $"{wg.Count} tiles");
                int HiAt(int x, int y) => x < 4 ? 0x0A0A0A : 1 << 24;   // right half "hidden"
                var wh = RegionOps.WandFill((1, 1), 8, 8, HiAt, 64, 10_000, out ovf);
                Check("wand never crosses the hidden flag", !ovf && wh.Count == 32, $"{wh.Count} tiles");
            }

            // 8d. compact decomposition + wand radius cap
            {
                var blob = new HashSet<(int x, int y)>();
                for (int cy = -40; cy <= 40; cy++)
                    for (int cx = -40; cx <= 40; cx++)
                        if (cx * cx + cy * cy <= 1600) blob.Add((100 + cx, 100 + cy));
                var strips = RegionOps.MaskToRects(blob);
                var cblob = RegionOps.MaskToRectsCompact(blob);
                Check("compact rects cover exactly, no overlap", RegionOps.Coverage(cblob).SetEquals(blob) &&
                    blob.Count == cblob.Sum(t => (long)t.W * t.H));
                Check("compact never worse than strips", cblob.Count <= strips.Count,
                    $"{cblob.Count} vs {strips.Count} strips");
                var wrad = RegionOps.WandFill((50, 50), 100, 100, (x, y) => 0x101010, 0, 100_000, out var rovf, 5);
                Check("wand radius cap", !rovf && wrad.Count == 81 && wrad.All(t =>
                    (t.x - 50) * (t.x - 50) + (t.y - 50) * (t.y - 50) <= 25), $"{wrad.Count} tiles");
            }

            // 8e. map image export: silhouette + label rendering on a synthetic radar
            {
                using var fakeRadar = new Bitmap(64, 64);
                using (var fg = Graphics.FromImage(fakeRadar)) fg.Clear(Color.FromArgb(30, 60, 30));
                var visReg = new RegionDef { DefName = "A_VIS", Name = "Visible Zone", ColorArgb = Color.Red.ToArgb() };
                visReg.Rects.Add(new RegionRect(10, 10, 29, 29));
                var hidReg = new RegionDef { DefName = "A_HID", Name = "Hidden Zone", Visible = false, ColorArgb = Color.Blue.ToArgb() };
                hidReg.Rects.Add(new RegionRect(40, 40, 55, 55));
                using var expL = MapExport.Render(fakeRadar, new[] { visReg, hidReg },
                    new RegionRect(0, 0, 63, 63), 2.0, drawLabels: true, labelSize: 9f);
                using var exp = MapExport.Render(fakeRadar, new[] { visReg, hidReg },
                    new RegionRect(0, 0, 63, 63), 2.0, drawLabels: false, labelSize: 9f);
                var inside = exp.GetPixel(40, 40);       // tile 20,20 = inside the visible region
                var hidden = exp.GetPixel(95, 95);       // tile ~47 = inside the HIDDEN region
                var outside = exp.GetPixel(6, 6);        // tile 3 = plain radar
                Check("export image size", expL.Width == 128 && expL.Height == 128 && exp.Width == 128);
                Check("visible region tinted", inside.R > outside.R, $"in={inside} out={outside}");
                Check("hidden region NOT drawn", hidden.R == outside.R && hidden.G == outside.G && hidden.B == outside.B,
                    $"hid={hidden} out={outside}");
                bool oversize = false;
                try { MapExport.Render(fakeRadar, new[] { visReg }, new RegionRect(0, 0, 99999, 99999), 4.0, false, 9f); }
                catch (ArgumentException) { oversize = true; }
                Check("export size guard", oversize);
                // crop clamping: area reaching past the radar bitmap must render, not throw
                using var expC = MapExport.Render(fakeRadar, new[] { visReg },
                    new RegionRect(32, 32, 99, 99), 1.0, drawLabels: false, labelSize: 9f);
                Check("export crop clamp", expC.Width == 68 && expC.Height == 68);
                // contour ring: the region's edge tile is drawn much stronger than the fill
                var edgePx = exp.GetPixel(21, 40);       // tile 10 = left contour column
                Check("export contour ring", edgePx.R > inside.R + 40, $"edge={edgePx} in={inside}");
                // label pass: the centroid carries the dark label plate in the labeled render
                var lbl = expL.GetPixel(40, 40);
                Check("export label plate", lbl.R + lbl.G + lbl.B < inside.R + inside.G + inside.B,
                    $"lbl={lbl}");
            }

            // 8f. TileIndex + the no-overlap carve behind "Don't overlap other regions"
            {
                var idx = new TileIndex();
                Check("empty tile index", idx.IsEmpty && idx.Blocker(5, 5) == null);
                idx.Add(new RegionRect(10, 10, 19, 19), "Blocker A");
                idx.Add(new RegionRect(200, 200, 209, 209), "Blocker B");
                Check("tile index names the blocker", idx.Blocker(15, 15) == "Blocker A" &&
                    idx.Blocker(205, 205) == "Blocker B" && idx.Blocker(9, 10) == null &&
                    idx.Blocker(19, 19) == "Blocker A" && idx.Blocker(20, 20) == null &&
                    !idx.Occupied(-5, -5), $"{idx.Blocker(15, 15)}");
                // rect carve: a drawn box minus the blockers it overlaps, no rasterizing
                var drawnBox = new RegionRect(0, 0, 99, 99);
                var pieces = new List<RegionRect> { drawnBox };
                foreach (var cut in idx.Intersecting(drawnBox)) pieces = RegionOps.SubtractBox(pieces, cut);
                var cov = RegionOps.Coverage(pieces);
                Check("no-overlap carve exact", cov.Count == 100 * 100 - 100 &&
                    !cov.Contains((15, 15)) && cov.Contains((9, 9)) && cov.Contains((20, 20)) &&
                    cov.Count == pieces.Sum(p => (long)p.W * p.H), $"{pieces.Count} pieces");
                Check("carve ignores far blockers", pieces.Count <= 8 &&
                    idx.Intersecting(drawnBox).Count() == 1, $"{pieces.Count} pieces");
                // a full-map box against a blocker must stay rect algebra (instant)
                var swc = System.Diagnostics.Stopwatch.StartNew();
                var big = RegionOps.SubtractBox(new[] { new RegionRect(0, 0, 7167, 4095) }, new RegionRect(10, 10, 19, 19));
                swc.Stop();
                Check("full-map carve is rect algebra", swc.ElapsedMilliseconds < 50 && big.Count == 4 &&
                    big.Sum(p => (long)p.W * p.H) == 7168L * 4096 - 100, $"{swc.ElapsedMilliseconds}ms");
                // wand blocked by occupancy: the fill stops at the blocker's border
                var occ = new TileIndex();
                occ.Add(new RegionRect(5, 0, 5, 9), "Wall");
                var blocked = RegionOps.WandFill((0, 0), 10, 10,
                    (x, y) => occ.Occupied(x, y) ? 1 << 24 : 0x101010, 0, 10_000, out bool bovf);
                // a seed on a hidden/blocked tile must select NOTHING - without the guard
                // the out-of-band value matches every other blocked tile and floods the blob
                var wallIdx = new TileIndex(64, 64);
                wallIdx.Add(new RegionRect(20, 0, 40, 63), "Neighbour");
                Func<int, int, int> guarded = (x, y) => wallIdx.Occupied(x, y) ? 1 << 24 : 0x101010;
                var seededInside = RegionOps.WandFill((30, 30), 64, 64, guarded, 64, 100_000, out var sovf);
                Check("wand seeded inside a blocker selects nothing", !sovf && seededInside.Count == 0,
                    $"{seededInside.Count} tiles");
                var seededOutside = RegionOps.WandFill((5, 5), 64, 64, guarded, 64, 100_000, out sovf);
                Check("wand from a free tile stops at the blocker", !sovf && seededOutside.Count == 20 * 64 &&
                    seededOutside.All(t => t.x < 20), $"{seededOutside.Count} tiles");
                // off-map rects must not explode the bucket grid (imported .scp are unclamped)
                var wild = new TileIndex(64, 64);
                var swi = System.Diagnostics.Stopwatch.StartNew();
                wild.Add(new RegionRect(-500, -500, 999999, 999999), "Wild");
                wild.Add(new RegionRect(70000, 70000, 80000, 80000), "Off map");
                swi.Stop();
                Check("tile index clamps wild rects", swi.ElapsedMilliseconds < 100 &&
                    wild.Blocker(10, 10) == "Wild" && wild.Blocker(63, 63) == "Wild",
                    $"{swi.ElapsedMilliseconds}ms");
                Check("wand stops at another region", !bovf && blocked.Count == 50 &&
                    blocked.All(t => t.x < 5), $"{blocked.Count} tiles");
            }

            // 8g. add-merge: new tiles fuse with the region's own boxes instead of stacking
            {
                // a 10x10 box plus the 10x10 strip right below it = ONE 10x20 box
                var existing = new List<RegionRect> { new RegionRect(0, 0, 9, 9) };
                var addition = new HashSet<(int x, int y)>();
                for (int y = 10; y <= 19; y++)
                    for (int x = 0; x <= 9; x++) addition.Add((x, y));
                var plain = RegionOps.MaskToRectsCompact(addition);
                var union = new HashSet<(int x, int y)>(addition);
                union.UnionWith(RegionOps.Coverage(existing));
                var fused = RegionOps.MaskToRectsCompact(union);
                Check("adjacent add fuses into one box", fused.Count == 1 && plain.Count == 1 &&
                    fused[0].X1 == 0 && fused[0].Y1 == 0 && fused[0].X2 == 9 && fused[0].Y2 == 19,
                    $"{fused.Count} box(es) vs {existing.Count}+{plain.Count} unfused");
                Check("fused covers exactly the union", RegionOps.Coverage(fused).SetEquals(union) &&
                    union.Count == fused.Sum(t => (long)t.W * t.H));
                // tiles the region already owns must not produce any new box at all
                var ownIdx = new TileIndex();
                foreach (var rc in existing) ownIdx.Add(rc, "Self");
                var repaint = new HashSet<(int x, int y)>();
                for (int y = 5; y <= 14; y++)
                    for (int x = 0; x <= 9; x++) repaint.Add((x, y));
                var fresh = repaint.Where(t => !ownIdx.Occupied(t.x, t.y)).ToHashSet();
                Check("repaint drops already-covered tiles", fresh.Count == 50 &&
                    fresh.All(t => t.y >= 10), $"{fresh.Count} of {repaint.Count} tiles new");
            }

            // 9. server + client integration: in-process ServerCore, two clients, put/sync/delete/persist
            {
                var dataDir = Path.Combine(Path.GetTempPath(), $"uore-srv-{Guid.NewGuid():N}");
                Directory.CreateDirectory(dataDir);
                var mulsSrc = Path.Combine(dataDir, "muls");
                Directory.CreateDirectory(mulsSrc);
                var testBytes = new byte[3 * 1024 * 1024 + 12345];
                new Random(42).NextBytes(testBytes);
                File.WriteAllBytes(Path.Combine(mulsSrc, "radarcol.mul"), testBytes);
                var cfg = new Net.ServerConfig
                {
                    Port = 0,   // ephemeral
                    MulsDir = mulsSrc,
                    DataFile = Path.Combine(dataDir, "regions.json"),
                    Accounts =
                    {
                        new Net.ServerAccount { User = "owner", Md5 = Net.NetClient.Md5("test123"), Access = 255 },
                        new Net.ServerAccount { User = "guest", Md5 = Net.NetClient.Md5("guest"), Access = 0 },
                    },
                };
                var server = new Net.ServerCore(cfg, dataDir);
                using var serverCts = new CancellationTokenSource();
                var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
                for (int i = 0; i < 100 && server.Port == 0; i++) Thread.Sleep(20);
                Check("server started", server.Port != 0, $"port {server.Port}");

                using var c1 = new Net.NetClient();
                using var c2 = new Net.NetClient();
                var c2Puts = new List<Net.ChangePut>();
                var c2Dels = new List<Net.ChangeDel>();
                Net.SyncData c1Sync = null, c2Sync = null;
                c1.Synced += s => c1Sync = s;
                c2.Synced += s => c2Sync = s;
                c2.RegionPut += ch => { lock (c2Puts) c2Puts.Add(ch); };
                c2.RegionDeleted += ch => { lock (c2Dels) c2Dels.Add(ch); };

                Check("bad login rejected",
                    c1.ConnectAsync("127.0.0.1", server.Port, "owner", "WRONG").GetAwaiter().GetResult() != null);
                Check("login ok",
                    c1.ConnectAsync("127.0.0.1", server.Port, "owner", "test123").GetAwaiter().GetResult() == null &&
                    c2.ConnectAsync("127.0.0.1", server.Port, "owner", "test123").GetAwaiter().GetResult() == null);
                for (int i = 0; i < 100 && (c1Sync == null || c2Sync == null); i++) Thread.Sleep(20);
                Check("initial sync", c1Sync != null && c2Sync != null && c1Sync.Regions.Count == 0);

                var netReg = new RegionDef { DefName = "A_NET_TEST", Name = "Net Test", Events = "r_default" };
                netReg.Rects.Add(new RegionRect(100, 100, 120, 120));
                c1.PushRegion(netReg);
                for (int i = 0; i < 150; i++) { lock (c2Puts) if (c2Puts.Count > 0) break; Thread.Sleep(20); }
                lock (c2Puts)
                    Check("put broadcast to other client", c2Puts.Count == 1 &&
                        c2Puts[0].Region.DefName == "A_NET_TEST" && c2Puts[0].Region.Rects.Count == 1 &&
                        c2Puts[0].By == "owner");
                c1.PushDelete("A_NET_TEST");
                for (int i = 0; i < 150; i++) { lock (c2Dels) if (c2Dels.Count > 0) break; Thread.Sleep(20); }
                lock (c2Dels) Check("delete broadcast", c2Dels.Count == 1 && c2Dels[0].DefName == "A_NET_TEST");

                // muls pack distribution: manifest + chunked download + hash verify
                var mulsCache = Path.Combine(dataDir, "cache");
                var mulsErr = c1.SyncMulsAsync(mulsCache, null).GetAwaiter().GetResult();
                var got = Path.Combine(mulsCache, "radarcol.mul");
                Check("muls sync downloads", mulsErr == null && File.Exists(got) &&
                    File.ReadAllBytes(got).AsSpan().SequenceEqual(testBytes), mulsErr ?? "ok");
                Check("muls sync idempotent", c1.SyncMulsAsync(mulsCache, null).GetAwaiter().GetResult() == null);

                // viewer account: put refused with a note, nothing stored
                using var viewer = new Net.NetClient();
                string viewerNote = null;
                viewer.Notice += t => viewerNote = t;
                Check("viewer login", viewer.ConnectAsync("127.0.0.1", server.Port, "guest", "guest").GetAwaiter().GetResult() == null
                    && viewer.ReadOnly);
                var sneak = new RegionDef { DefName = "A_SNEAK" };
                sneak.Rects.Add(new RegionRect(1, 1, 2, 2));
                viewer.PushRegion(sneak);
                for (int i = 0; i < 100 && viewerNote == null; i++) Thread.Sleep(20);
                Check("viewer put refused with note", viewerNote != null && viewerNote.Contains("read-only"));

                // client log forwarding: PushLog lands in the server's clients.log tail
                c1.PushLog("error", "SELFTEST_LOG_MARKER");
                string tail = "";
                for (int i = 0; i < 150; i++)
                {
                    tail = server.TailClientLog(20);
                    if (tail.Contains("SELFTEST_LOG_MARKER")) break;
                    Thread.Sleep(20);
                }
                Check("client log reaches server", tail.Contains("SELFTEST_LOG_MARKER") && tail.Contains("owner"));

                c1.PushRegion(netReg);
                // drag-reorder sync: list order round-trips through the server
                var reg2 = new RegionDef { DefName = "A_NET_TEST2", Name = "Net Test 2" };
                reg2.Rects.Add(new RegionRect(10, 10, 12, 12));
                c1.PushRegion(reg2);
                Net.ReorderMsg gotReorder = null;
                c2.Reordered += rm => gotReorder = rm;
                Thread.Sleep(250);
                c1.PushReorder(new List<string> { "A_NET_TEST2", "A_NET_TEST" });
                for (int i = 0; i < 150 && gotReorder == null; i++) Thread.Sleep(20);
                Check("reorder broadcast", gotReorder != null && gotReorder.Order.Count == 2 &&
                    gotReorder.Order[0] == "A_NET_TEST2" && gotReorder.By == "owner");
                // live draw preview: relayed to the other client with author stamped
                Net.DrawPreview gotPrev = null;
                c2.Preview += pv => gotPrev = pv;
                c1.PushPreview(new Net.DrawPreview { Kind = 1, X1 = 5, Y1 = 6, X2 = 7, Y2 = 8 });
                for (int i = 0; i < 150 && gotPrev == null; i++) Thread.Sleep(20);
                Check("draw preview relayed", gotPrev != null && gotPrev.Kind == 1 &&
                    gotPrev.X2 == 7 && gotPrev.By == "owner" && gotPrev.BySession != "");
                // presence relay: c1 shares its view position; c2 learns it, author stamped
                Net.PosMsg gotPos = null;
                c2.PosChanged += pm => gotPos = pm;
                c1.PushPos(123, 456);
                for (int i = 0; i < 150 && gotPos == null; i++) Thread.Sleep(20);
                Check("presence pos relayed", gotPos != null && gotPos.X == 123 && gotPos.Y == 456 &&
                    gotPos.By == "owner" && gotPos.BySession != "");
                // security: a region with CR/LF injection must be neutralized server-side
                var evil = new RegionDef { DefName = "A_EVIL", Name = "X\r\nRECT=0,0,7168,4096,0", Events = "r_default\n[AREADEF A_OWNED]" };
                evil.Rects.Add(new RegionRect(200, 200, 210, 210));
                evil.Extra.Add("[FUNCTION f_backdoor]");
                c1.PushRegion(evil);
                // security/correctness: an out-of-map rect must clamp to the configured size
                // (cfg defaults to 7168x4096 -> valid inclusive coords 0..7167 x 0..4095)
                var oob = new RegionDef { DefName = "A_OOB", Name = "Out Of Bounds" };
                oob.Rects.Add(new RegionRect(-50, -50, 999999, 999999));
                c1.PushRegion(oob);
                Thread.Sleep(300);   // let the server persist
                serverCts.Cancel();
                try { serverTask.GetAwaiter().GetResult(); } catch { }
                var persisted = File.Exists(cfg.DataFile)
                    ? System.Text.Json.JsonSerializer.Deserialize<Project>(File.ReadAllText(cfg.DataFile))
                    : null;
                Check("server persisted regions", persisted != null &&
                    persisted.Regions.Any(r => r.DefName == "A_NET_TEST" && r.Events == "r_default"));
                Check("server persisted the new order", persisted != null &&
                    persisted.Regions.Count > 0 && persisted.Regions[0].DefName == "A_NET_TEST2");
                var evilStored = persisted?.Regions.FirstOrDefault(r => r.DefName == "A_EVIL");
                Check("injection neutralized server-side", evilStored != null &&
                    !evilStored.Name.Contains('\n') && !evilStored.Name.Contains('\r') &&
                    !evilStored.Events.Contains('\n') && !evilStored.Events.Contains('\r'));
                // the generated .scp must not carry an injected SECTION line (a line starting
                // with '[') - the neutralized content may survive harmlessly inside a value
                var evilScp = File.Exists(Path.Combine(dataDir, "regions.scp")) ? File.ReadAllText(Path.Combine(dataDir, "regions.scp")) : "";
                var scpLines = evilScp.Split('\n').Select(l => l.Trim()).ToList();
                Check("scp export has no injected section line",
                    !scpLines.Any(l => l.StartsWith("[AREADEF A_OWNED")) &&
                    !scpLines.Any(l => l.StartsWith("[FUNCTION")) &&
                    !evilScp.Contains("f_backdoor"));   // the [FUNCTION ...] Extra line was dropped entirely
                var oobStored = persisted?.Regions.FirstOrDefault(r => r.DefName == "A_OOB");
                Check("out-of-map rect clamped to configured size", oobStored != null && oobStored.Rects.Count == 1 &&
                    oobStored.Rects[0].X1 == 0 && oobStored.Rects[0].Y1 == 0 &&
                    oobStored.Rects[0].X2 == 7167 && oobStored.Rects[0].Y2 == 4095);
                try { Directory.Delete(dataDir, true); } catch { }
            }

            // 10. radar render from the real muls (slowest part; validates UOP reading end to end)
            var muls = DevMuls();
            if (Directory.Exists(muls) && MulRadar.FindMap(muls) != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var bmp = MulRadar.LoadOrRender(muls, s => sb.AppendLine("      " + s));
                sw.Stop();
                Check("radar render", bmp.Width == 7168 && bmp.Height == 4096, $"{bmp.Width}x{bmp.Height} in {sw.Elapsed.TotalSeconds:F1}s");
                var c = bmp.GetPixel(3584, 2048);
                Check("radar non-empty", c.R + c.G + c.B > 0, $"center pixel {c}");
                // the wand's per-tile surface color must reproduce the rendered radar exactly
                var wandMd = MulRadar.LoadData(muls);
                var wrnd = new Random(7);
                int mism = 0, landMism = 0;
                for (int i = 0; i < 500; i++)
                {
                    int x = wrnd.Next(bmp.Width), y = wrnd.Next(bmp.Height);
                    var pc = bmp.GetPixel(x, y);
                    int scol = wandMd.SurfaceColorAt(x, y);
                    if (pc.R != ((scol >> 16) & 0xFF) || pc.G != ((scol >> 8) & 0xFF) || pc.B != (scol & 0xFF)) mism++;
                    int lz = wandMd.LandAt(x, y).z;
                    if (!wandMd.StaticsAt(x, y).Any(s => s.z >= lz) && wandMd.LandColorAt(x, y) != scol) landMism++;
                }
                Check("wand surface color == radar pixels", mism == 0, $"{mism}/500 mismatch");
                Check("wand land color matches when no statics", landMism == 0, $"{landMism}/500 mismatch");
                Check("tiledata land names load", wandMd.LandNames != null &&
                    wandMd.LandNames.Length == 0x4000 &&
                    wandMd.LandNames.Count(n => !string.IsNullOrEmpty(n)) > 1000,
                    wandMd.LandNames == null ? "no tiledata" : $"{wandMd.LandNames.Count(n => n.Length > 0)} named");
                if (wandMd.LandNames != null)
                {
                    var caveIds = Enumerable.Range(0, 0x4000).Where(i => wandMd.LandNames[i] == "cave").ToList();
                    Check("cave floors form one distinct land type", caveIds.Count > 0 &&
                        caveIds.All(i => wandMd.LandType[i] == wandMd.LandType[caveIds[0]]) &&
                        wandMd.LandType[caveIds[0]] != wandMd.LandType[0x0003],
                        $"{caveIds.Count} cave land ids");
                    var caveStatics = wandMd.StaticNames == null ? new List<int>() :
                        Enumerable.Range(0, wandMd.StaticNames.Length).Where(i => wandMd.StaticNames[i] == "cave").ToList();
                    Check("cave floor statics share the land cave type", caveStatics.Count > 0 &&
                        caveStatics.All(i => wandMd.StaticType[i] == wandMd.LandType[caveIds[0]]) &&
                        caveStatics.All(i => wandMd.StaticGround[i]),
                        $"{caveStatics.Count} cave statics");
                }
            }
            else
            {
                sb.AppendLine("SKIP  radar render (artmuls not found)");
            }

            // 11. detail view render from the real muls (art + texmaps + statics + hues)
            if (Directory.Exists(muls) && ArtView.HasArt(muls))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var av = new ArtView(muls);
                var chunk = av.RenderChunk(6500 / ArtView.ChunkTiles, 3320 / ArtView.ChunkTiles, 8);   // the mall
                sw.Stop();
                bool nonEmpty = false;
                for (int y = 100; y < 400 && !nonEmpty; y += 7)
                    for (int x = 100; x < 400; x += 7)
                    {
                        var c = chunk.GetPixel(x, y);
                        if (c.R + c.G + c.B > 30) { nonEmpty = true; break; }
                    }
                Check("detail chunk render", chunk.Width == 512 && nonEmpty, $"{sw.Elapsed.TotalSeconds:F1}s incl. art load");
                av.SetFilter(-128, 10, true, true, false);
                var filtered = av.RenderChunk(6500 / ArtView.ChunkTiles, 3320 / ArtView.ChunkTiles, 8);
                Check("detail z-filter rerender", filtered != null && filtered.Width == 512);

                // isometric chunk over the mall (the CentrED-view projection)
                av.SetFilter(-128, 127, true, true, false);
                var (ipx, ipy) = av.IsoTileToPx(6525, 3316);
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var isoChunk = av.RenderIsoChunk((int)(ipx / ArtView.IsoChunkPx), (int)(ipy / ArtView.IsoChunkPx));
                sw2.Stop();
                bool isoNonEmpty = false;
                for (int y = 50; y < 460 && !isoNonEmpty; y += 13)
                    for (int x = 50; x < 460; x += 13)
                    {
                        var c = isoChunk.GetPixel(x, y);
                        if (c.R + c.G + c.B > 30) { isoNonEmpty = true; break; }
                    }
                Check("iso chunk render", isoChunk != null && isoChunk.Width == 512 && isoNonEmpty,
                    $"{sw2.Elapsed.TotalSeconds:F2}s");
                // round-trip of the iso coordinate transform
                var (bx, by) = av.IsoPxToTile(ipx, ipy);
                Check("iso transform round-trip", Math.Abs(bx - 6525) < 0.01 && Math.Abs(by - 3316) < 0.01,
                    $"{bx:F2},{by:F2}");
                // zoomed-out LOD chunk
                var swL = System.Diagnostics.Stopwatch.StartNew();
                var lodChunk = av.RenderIsoChunk((int)(ipx / (ArtView.IsoChunkPx * 4)), (int)(ipy / (ArtView.IsoChunkPx * 4)), 2);
                swL.Stop();
                Check("iso LOD2 overview chunk", lodChunk != null && lodChunk.Width == 512, $"{swL.Elapsed.TotalSeconds:F2}s");
                // parallel rasterization: the app runs several chunk workers against one
                // ArtView; hammer fresh chunks from many threads and require no crash and
                // a valid bitmap from each (sprite caches are behind spriteGate)
                int baseCx = (int)(ipx / ArtView.IsoChunkPx), baseCy = (int)(ipy / ArtView.IsoChunkPx);
                var swP = System.Diagnostics.Stopwatch.StartNew();
                var parOk = new bool[12];
                System.Threading.Tasks.Parallel.For(0, 12, i =>
                {
                    var b = av.RenderIsoChunk(baseCx + 1 + i % 4, baseCy + 1 + i / 4);
                    parOk[i] = b is { Width: 512 };
                });
                swP.Stop();
                Check("parallel chunk render", parOk.All(x => x), $"12 chunks {swP.Elapsed.TotalSeconds:F2}s");
            }
            else
            {
                sb.AppendLine("SKIP  detail render (art files not found)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("FAIL  unhandled: " + ex);
            fails++;
        }

        sb.AppendLine(fails == 0 ? "ALL PASS" : $"{fails} FAILURES");
        File.WriteAllText(string.IsNullOrEmpty(reportPath) ? "selftest-report.txt" : reportPath, sb.ToString());
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
