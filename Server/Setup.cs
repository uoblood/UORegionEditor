using System.Text;
using System.Text.Json;
using UORegionEditor;
using UORegionEditor.Net;
using System.Security.Cryptography;

namespace UORegionEditor.ServerApp;

// First-run setup and the muls readiness check.
//
// CentrED asks for the map file and its size the first time it starts; we used to drop a
// default server.json and leave the operator guessing which files to copy. Kept terse: the
// wizard is five questions, startup prints ONE line, and the full per-file table appears
// only for 'check' (and once during setup).
public static class Setup
{
    // file (or its alternatives) -> what it unlocks. Required = no map without it.
    public static readonly (string[] names, bool required, string purpose)[] MulsFiles =
    {
        (new[] { "map0LegacyMUL.uop", "map0.uop", "map0.mul" }, true, "terrain"),
        (new[] { "radarcol.mul" }, true, "map colours"),
        (new[] { "staidx0.mul" }, true, "statics index"),
        (new[] { "statics0.mul" }, true, "buildings, trees, roads"),
        (new[] { "tiledata.mul" }, false, "quick select: Tile type"),
        (new[] { "artLegacyMUL.uop", "art.mul" }, false, "CentrED view"),
        (new[] { "MainMisc.uop" }, false, "CentrED view: UOP art"),
        (new[] { "hues.mul" }, false, "CentrED view: colours"),
        (new[] { "texmaps.mul" }, false, "CentrED view: textures"),
        (new[] { "texidx.mul" }, false, "CentrED view: texture index"),
    };

    public static string Resolve(string mulsDir, string baseDir) =>
        string.IsNullOrWhiteSpace(mulsDir) ? "" :
        Path.IsPathRooted(mulsDir) ? mulsDir : Path.Combine(baseDir, mulsDir);

    static string[] Missing(string dir, bool required)
    {
        if (!Directory.Exists(dir))
            return MulsFiles.Where(f => f.required == required).Select(f => f.names[0]).ToArray();
        var present = Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MulsFiles.Where(f => f.required == required && !f.names.Any(present.Contains))
            .Select(f => f.names[0]).ToArray();
    }

    // one line for the startup banner
    public static string Summary(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return "muls: none - clients use their own";
        var files = Directory.GetFiles(dir);
        long mb = files.Sum(f => new FileInfo(f).Length) / 1048576;
        var req = Missing(dir, true);
        var opt = Missing(dir, false);
        string state = req.Length > 0 ? "MISSING " + string.Join(", ", req)
            : opt.Length > 0 ? $"{opt.Length} optional missing"
            : "complete";
        string hint = req.Length + opt.Length > 0 ? "  ('check' for detail)" : "";
        return $"muls: {files.Length} files, {mb} MB - {state}{hint}";
    }

    // the full table - 'check' and first-run only
    public static bool Report(string dir, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            log($"  muls folder not found: {dir}");
            return false;
        }
        var present = Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ok = true;
        var optMissing = new List<string>();
        foreach (var (names, required, purpose) in MulsFiles)
        {
            string found = names.FirstOrDefault(n => present.Contains(n));
            if (found != null)
                log($"  [x] {found,-22} {new FileInfo(Path.Combine(dir, found)).Length / 1048576,5} MB  {purpose}");
            else if (required)
            {
                ok = false;
                log($"  [ ] {names[0],-22}  MISSING  {purpose}");
            }
            else optMissing.Add(names[0]);
        }
        if (optMissing.Count > 0) log($"  [-] optional: {string.Join(", ", optMissing)}");
        if (!ok) log("  ! clients cannot use this pack until those are added");
        return ok;
    }

    // Map dimensions from the map file: 196 bytes per 8x8 block. UOP entry lengths are
    // summed from the archive index (nothing is decompressed). (0,0) = unknown.
    public static (int w, int h, string how) DetectMapSize(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return (0, 0, "");
        string file = new[] { "map0LegacyMUL.uop", "map0.uop", "map0.mul" }
            .Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        if (file == null) return (0, 0, "");
        long bytes;
        try
        {
            bytes = file.EndsWith(".mul", StringComparison.OrdinalIgnoreCase)
                ? new FileInfo(file).Length
                : UopPayloadSize(file);
        }
        catch { return (0, 0, ""); }
        if (bytes <= 0) return (0, 0, "");
        long blocks = bytes / 196;
        foreach (var (bw, bh) in new[] { (896, 512), (768, 512), (640, 512), (288, 200), (320, 256), (181, 181), (128, 128), (160, 512) })
            if (blocks >= (long)bw * bh && blocks <= (long)bw * bh + 8)
                return (bw * 8, bh * 8, Path.GetFileName(file));
        if (blocks % 896 == 0)
            return (896 * 8, (int)(blocks / 896) * 8, Path.GetFileName(file) + ", custom");
        return (0, 0, $"{Path.GetFileName(file)}: {blocks:N0} blocks, unrecognised");
    }

    static long UopPayloadSize(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (br.ReadInt32() != 0x50594D) return 0;      // "MYP"
        br.ReadInt64();                                 // version + signature
        long next = br.ReadInt64();
        br.ReadInt32();                                 // block capacity
        int count = br.ReadInt32();
        if (count <= 0 || count > 1_000_000) return 0;
        long total = 0;
        int guard = 0;
        fs.Seek(next, SeekOrigin.Begin);
        do
        {
            int files = br.ReadInt32();
            if (files < 0 || files > 1_000_000) return 0;
            next = br.ReadInt64();
            for (int i = 0; i < files; i++)
            {
                long offset = br.ReadInt64();
                br.ReadInt32();                        // header length
                br.ReadInt32();                        // compressed length
                int decompressed = br.ReadInt32();
                br.ReadInt64();                        // hash
                br.ReadUInt32();                       // adler
                br.ReadInt16();                        // flag
                if (offset != 0) total += decompressed;
            }
        } while (fs.Seek(next, SeekOrigin.Begin) != 0 && ++guard < 100_000);
        return total;
    }

    static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    static string Ask(string prompt, string def)
    {
        Console.Write($"{prompt} [{def}]: ");
        string line = null;
        try { line = Console.ReadLine(); } catch { }
        // no console input (service, redirected stdin) -> default, never block
        if (line == null) { Console.WriteLine(def); return def; }
        line = line.Trim();
        return line.Length == 0 ? def : line;
    }

    static (int w, int h) ParseSize(string s, int dw, int dh)
    {
        var p = s.Split(new[] { ' ', 'x', 'X', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 2 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h) && w > 0 && h > 0
            ? (w, h) : (dw, dh);
    }

    public static ServerConfig FirstRun(string cfgPath, string baseDir)
    {
        var cfg = new ServerConfig();
        Console.WriteLine();
        Console.WriteLine("=== UORegionServer setup ===   (Enter = value in brackets)");

        cfg.Port = int.TryParse(Ask("Port", "2599"), out int port) && port > 0 && port < 65536 ? port : 2599;

        // the server hands these files to every client, so the whole team renders one map
        cfg.MulsDir = Ask("Muls folder", "muls");
        string dir = Resolve(cfg.MulsDir, baseDir);
        if (!Directory.Exists(dir))
            try { Directory.CreateDirectory(dir); } catch { }
        Console.WriteLine($"  {dir}");
        Report(dir, Console.WriteLine);

        var (dw, dh, how) = DetectMapSize(dir);
        if (dw > 0) Console.WriteLine($"  detected from {how}");
        string sizeDef = dw > 0 ? $"{dw} {dh}" : $"{Limits.DefaultMapWidth} {Limits.DefaultMapHeight}";
        (cfg.MapWidth, cfg.MapHeight) = ParseSize(Ask("Map size in tiles", sizeDef),
            dw > 0 ? dw : Limits.DefaultMapWidth, dw > 0 ? dh : Limits.DefaultMapHeight);

        string user = Ask("Owner account", "owner");
        string pass = Ask("Password", "owner");
        cfg.Accounts.Add(new ServerAccount { User = user, Md5 = Md5Hex(pass), Access = 255 });
        cfg.DataFile = "regions.json";
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"wrote {Path.GetFileName(cfgPath)}: port {cfg.Port}, map {cfg.MapWidth}x{cfg.MapHeight}, owner '{user}'"
            + (pass == "owner" ? "   (change it: passwd " + user + " <new>)" : ""));
        Console.WriteLine();
        return cfg;
    }
}
