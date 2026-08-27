using System.Text;
using System.Text.Json;
using UORegionEditor;
using UORegionEditor.Net;

// UORegionEditor.Server - shared region storage for the team, cedserver-style accounts.
//   UORegionEditor.Server.exe [server.json]
// First run writes a default server.json next to the exe. Put your muls files into the
// "muls" folder next to it and every connecting client downloads/updates them automatically.
// Console commands: adduser, deluser, passwd, users, help, quit.

var baseDir = AppContext.BaseDirectory;
var cfgPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(baseDir, "server.json");
var cfgDir = Path.GetDirectoryName(Path.GetFullPath(cfgPath));   // muls paths resolve against the CONFIG, like ServerCore does

if (!File.Exists(cfgPath))
{
    UORegionEditor.ServerApp.Setup.FirstRun(cfgPath, cfgDir);
}
ServerConfig cfg;
try
{
    cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(cfgPath)) ?? new ServerConfig();
}
catch (Exception ex)
{
    Console.WriteLine($"FATAL: cannot parse {cfgPath}: {ex.Message}");
    Console.WriteLine("Fix the JSON (or delete the file to regenerate a default) and start again.");
    return;
}
var server = new ServerCore(cfg, Path.GetDirectoryName(cfgPath))
{
    Log = s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}"),
    ConfigPath = cfgPath,
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ver = typeof(ServerCore).Assembly.GetName().Version;
try { Console.Title = $"UORegionServer {ver?.ToString(3)} - port {cfg.Port}"; } catch { }
Console.WriteLine($"UORegionServer {ver?.ToString(3)} on port {cfg.Port} - type 'help' for commands, Ctrl+C or 'quit' to stop");
// one line at startup; the full table lives behind the check command
Console.WriteLine($"map {cfg.MapWidth}x{cfg.MapHeight} tiles   {UORegionEditor.ServerApp.Setup.Summary(UORegionEditor.ServerApp.Setup.Resolve(cfg.MulsDir, cfgDir))}");
var serverTask = server.RunAsync(cts.Token);

while (!cts.IsCancellationRequested && !serverTask.IsCompleted)
{
    string line;
    try { line = Console.ReadLine(); }
    catch { line = null; }
    if (line == null) break;   // stdin closed (service mode) - just keep serving
    var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (p.Length == 0) continue;
    try
    {
        switch (p[0].ToLowerInvariant())
        {
            case "check":
                Console.WriteLine($"muls: {UORegionEditor.ServerApp.Setup.Resolve(cfg.MulsDir, cfgDir)}");
                UORegionEditor.ServerApp.Setup.Report(UORegionEditor.ServerApp.Setup.Resolve(cfg.MulsDir, cfgDir), Console.WriteLine);
                {
                    var det = UORegionEditor.ServerApp.Setup.DetectMapSize(UORegionEditor.ServerApp.Setup.Resolve(cfg.MulsDir, cfgDir));
                    if (det.w > 0 && (det.w != cfg.MapWidth || det.h != cfg.MapHeight))
                        Console.WriteLine($"  ! config says {cfg.MapWidth} x {cfg.MapHeight} but the map file looks like {det.w} x {det.h} - fix with: mapsize {det.w} {det.h}");
                }
                break;

            case "help":
                Console.WriteLine("  adduser <name> <password> [root|admin|editor|viewer]   (default editor)");
                Console.WriteLine("  deluser <name>");
                Console.WriteLine("  passwd <name> <newpassword>");
                Console.WriteLine("  setaccess <name> <root|admin|editor|viewer>");
                Console.WriteLine("      root   = full control incl. the first-sync push-local choice");
                Console.WriteLine("      admin/editor = edit regions; always adopt the server copy on login");
                Console.WriteLine("      viewer = read-only");
                Console.WriteLine("  export      (write regions.scp + regions.centred.xml + regions.servuo.xml now)");
                Console.WriteLine("  mapsize [w h]   (show or set the map size region rects clamp to; default 7168 4096)");
                Console.WriteLine("  check       (re-check the muls folder and report what is missing)");
                Console.WriteLine("  logs [n]    (show the last n client log lines, default 20)");
                Console.WriteLine("  users");
                Console.WriteLine("  about       (version + credits)");
                Console.WriteLine("  quit");
                break;
            case "mapsize" when p.Length >= 3 && int.TryParse(p[1], out var mw) && int.TryParse(p[2], out var mh):
                Console.WriteLine("  " + server.SetMapSize(mw, mh));
                break;
            case "mapsize" when p.Length >= 2:
                Console.WriteLine("  usage: mapsize <width> <height>   (or just 'mapsize' to show current)");
                break;
            case "mapsize":
                Console.WriteLine("  " + server.MapSizeInfo());
                break;
            case "about" or "info" or "version" or "credits":
                Console.WriteLine($"  UORegionServer v{ver?.ToString(3)} - multi-user region server for UO Region Editor");
                Console.WriteLine("  regions for Sphere, CentrED and ServUO - by chemist");
                Console.WriteLine("  special thanks:");
                Console.WriteLine("    Kaczy and all CentrED# contributors");
                Console.WriteLine("    Andreas Schneider for the original CentrED");
                Console.WriteLine("    andreakarasho and all ClassicUO contributors");
                Console.WriteLine("    Voxpire and all ServUO contributors");
                Console.WriteLine("    the Sphere team and all Source-X contributors");
                Console.WriteLine("    False");
                break;
            case "adduser" when p.Length < 3:
                Console.WriteLine("  usage: adduser <name> <password> [root|admin|editor|viewer]   (no spaces in passwords)");
                break;
            case "adduser":
            {
                int access = p.Length >= 4 ? ParseAccess(p[3]) : 1;
                Console.WriteLine("  " + server.AddUser(p[1], p[2], access));
                break;
            }
            case "deluser" when p.Length < 2:
                Console.WriteLine("  usage: deluser <name>");
                break;
            case "deluser":
                Console.WriteLine("  " + server.DelUser(p[1]));
                break;
            case "setaccess" when p.Length >= 3:
                Console.WriteLine("  " + server.SetAccess(p[1], ParseAccess(p[2])));
                break;
            case "setaccess":
                Console.WriteLine("  usage: setaccess <name> <root|admin|editor|viewer>");
                break;
            case "logs":
            {
                int n = p.Length >= 2 && int.TryParse(p[1], out var ln) ? ln : 20;
                Console.WriteLine(server.TailClientLog(n));
                break;
            }
            case "export":
                Console.WriteLine("  " + server.ExportNow());
                break;
            case "passwd" when p.Length < 3:
                Console.WriteLine("  usage: passwd <name> <newpassword>   (no spaces in passwords)");
                break;
            case "passwd":
                Console.WriteLine("  " + server.SetPassword(p[1], p[2]));
                break;
            case "users":
                Console.WriteLine(server.ListUsers());
                break;
            case "quit" or "exit" or "stop":
                cts.Cancel();
                break;
            default:
                Console.WriteLine("  unknown command - type 'help'");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("  command failed: " + ex.Message);
    }
}

try { await serverTask; } catch (OperationCanceledException) { }

// 255 root / 2 admin / 1 editor / 0 viewer; bare numbers also accepted
static int ParseAccess(string s) => s.ToLowerInvariant() switch
{
    "root" or "255" => 255,
    "admin" or "2" => 2,
    "viewer" or "0" => 0,
    _ => int.TryParse(s, out var n) ? Math.Clamp(n, 0, 255) : 1,
};
