using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace UORegionEditor.Net;

// The region server: accounts + login, shared region storage, live broadcast to all clients.
// Modeled on cedserver's role but with the FULL region model (sphere events/flags/P included).
//
// Send architecture: every mutation is committed AND its broadcast frame enqueued to every
// client's bounded FIFO queue inside one lock - so all clients see changes in commit order,
// a stalled client can only wedge itself (its writer task times out and kicks it), and the
// initial sync snapshot is serialized in the same lock as the client registration.
public class ServerAccount
{
    public string User { get; set; } = "";
    public string Md5 { get; set; } = "";       // MD5 hex of the password, cedserver style
    public int Access { get; set; } = 1;        // 0=viewer, 1=editor, 255=admin
}

public class ServerConfig
{
    public int Port { get; set; } = 2599;
    public string DataFile { get; set; } = "regions.json";
    public string MulsDir { get; set; } = "muls";   // optional: files here are distributed to clients
    // operator-chosen map size (like CentrED's first-run prompt). Region rects are clamped
    // to it, and it is sent to clients so they show the same bounds. Default = UO map0 (ML).
    public int MapWidth { get; set; } = Limits.DefaultMapWidth;
    public int MapHeight { get; set; } = Limits.DefaultMapHeight;
    public List<ServerAccount> Accounts { get; set; } = new();
}

public class ServerCore
{
    class Client
    {
        public TcpClient Tcp;
        public NetworkStream Stream;
        public string User = "?";
        public string Session = "";
        public int Access;
        public DateTime LastRecv = DateTime.UtcNow;   // any message counts (clients ping every 20s)
        public readonly Channel<byte[]> SendQueue = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });
    }

    const int MaxConnections = 1024;   // CentrED-parity total-socket cap
    int liveConnections;               // Interlocked - includes unauthenticated sockets
    const int MaxExtraLines = 256;

    readonly object sync = new();
    readonly object saveLock = new();
    readonly List<Client> clients = new();
    readonly ServerConfig config;
    readonly string dataPath;
    List<RegionDef> regions = new();
    long serial;
    int changesSinceBackup;
    TcpListener listener;
    string mulsDir;                                     // resolved path, null = no pack
    MulsManifest mulsManifest = new();
    public int Port { get; private set; }
    public Action<string> Log = _ => { };

    public ServerCore(ServerConfig cfg, string baseDir)
    {
        config = cfg;
        dataPath = Path.IsPathRooted(cfg.DataFile) ? cfg.DataFile : Path.Combine(baseDir, cfg.DataFile);
    }

    void IndexMuls()
    {
        mulsDir = null;
        mulsManifest = new MulsManifest();
        if (string.IsNullOrWhiteSpace(config.MulsDir)) return;
        var dir = Path.IsPathRooted(config.MulsDir)
            ? config.MulsDir
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataPath)), config.MulsDir);
        if (!Directory.Exists(dir))
        {
            Log($"muls pack: folder not found ({dir}) - clients will use their own local muls");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var f in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(f);
            using var fs = File.OpenRead(f);
            mulsManifest.Files.Add(new MulsFileInfo
            {
                Name = fi.Name,
                Size = fi.Length,
                Sha256 = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant(),
            });
        }
        mulsDir = dir;
        Log($"muls pack: {mulsManifest.Files.Count} files, {mulsManifest.Files.Sum(f => f.Size) / 1048576} MB, indexed in {sw.Elapsed.TotalSeconds:F1}s");
    }

    void LoadData()
    {
        if (File.Exists(dataPath))
        {
            var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(dataPath));
            regions = p?.Regions ?? new List<RegionDef>();
            Log($"loaded {regions.Count} regions from {dataPath}");
        }
        else
        {
            Log($"no data file yet ({dataPath}) - starting empty");
        }
    }

    // Serialize under the region lock, write to disk OUTSIDE it (slow disks must not stall edits).
    // Also keeps ready-to-use sphere/centred exports next to the data file.
    void SaveDataLocked()
    {
        string json = JsonSerializer.Serialize(new Project { Regions = regions }, new JsonSerializerOptions { WriteIndented = true });
        // exports are best-effort: one malformed region must never abort JSON persistence
        // (the source of truth) or disconnect the editor who sent it
        string scp = null, xml = null, servuo = null;
        try { scp = SphereScp.ExportCompact(regions); } catch (Exception ex) { Log($"scp export skipped: {ex.Message}"); }
        try { xml = CentredXml.ExportSnippet(regions); } catch (Exception ex) { Log($"centred xml export skipped: {ex.Message}"); }
        try { servuo = ServuoXml.ExportXml(regions); } catch (Exception ex) { Log($"servuo export skipped: {ex.Message}"); }
        bool doBackup = ++changesSinceBackup >= 25;
        if (doBackup) changesSinceBackup = 0;
        Task.Run(() =>
        {
            lock (saveLock)
            {
                try
                {
                    var tmp = dataPath + ".tmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataPath)));
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, dataPath, overwrite: true);
                    var dir = Path.GetDirectoryName(Path.GetFullPath(dataPath));
                    if (scp != null) File.WriteAllText(Path.Combine(dir, "regions.scp"), scp, System.Text.Encoding.UTF8);
                    if (xml != null) File.WriteAllText(Path.Combine(dir, "regions.centred.xml"), xml);
                    if (servuo != null) File.WriteAllText(Path.Combine(dir, "regions.servuo.xml"), servuo);
                    if (doBackup)
                    {
                        File.Copy(dataPath, dataPath + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
                        foreach (var old in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(dataPath)),
                                     Path.GetFileName(dataPath) + ".bak-*").OrderByDescending(f => f).Skip(20))
                            File.Delete(old);
                    }
                }
                catch (Exception ex)
                {
                    Log($"!!! SAVE FAILED ({ex.Message}) - REGIONS ARE NOT PERSISTED, fix the disk/permissions !!!");
                }
            }
        });
    }

    static byte[] Frame(string type, object data)
    {
        var env = new Envelope { T = type, D = JsonSerializer.SerializeToElement(data ?? new { }, Wire.Opts) };
        var payload = JsonSerializer.SerializeToUtf8Bytes(env, Wire.Opts);
        var frame = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    // must be called inside lock(sync)
    void EnqueueAllLocked(byte[] frame)
    {
        foreach (var c in clients)
        {
            if (!c.SendQueue.Writer.TryWrite(frame))
            {
                Log($"'{c.User}' send queue overflow - kicking");
                try { c.Tcp.Close(); } catch { }
            }
        }
    }

    static bool EnqueueTo(Client c, byte[] frame)
    {
        if (c.SendQueue.Writer.TryWrite(frame)) return true;
        try { c.Tcp.Close(); } catch { }
        return false;
    }

    async Task WriterLoopAsync(Client c, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in c.SendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await c.Stream.WriteAsync(frame, writeTimeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // dead/stalled client: close the socket so its read loop cleans up
            try { c.Tcp.Close(); } catch { }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            LoadData();
        }
        catch (Exception ex)
        {
            Log($"FATAL: cannot read {dataPath}: {ex.Message}");
            Log("Fix or delete the data file, then start again.");
            return;
        }
        try
        {
            IndexMuls();
        }
        catch (Exception ex)
        {
            Log($"muls pack indexing failed ({ex.Message}) - continuing without it");
            mulsDir = null;
            mulsManifest = new MulsManifest();
        }
        try
        {
            listener = new TcpListener(IPAddress.Any, config.Port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            Log($"FATAL: cannot listen on port {config.Port}: {ex.Message}");
            Log("Is another instance (or another service) already using the port?");
            return;
        }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Log($"listening on port {Port}  ({config.Accounts.Count} accounts)");
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        // ghost sweep: a hard-killed client (crash, power loss) never sends a FIN, and a
        // blocked read would keep it in the list forever - inflating the online count.
        // 2 minutes of silence = dead, same as CentrED (our clients ping every 20s).
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch { return; }
                List<Client> dead;
                lock (sync)
                    dead = clients.Where(c => (DateTime.UtcNow - c.LastRecv).TotalSeconds > 120).ToList();
                foreach (var c in dead)
                {
                    Log($"'{c.User}' timed out (no traffic for 2 minutes) - dropping");
                    try { c.Stream?.Dispose(); } catch { }
                    try { c.Tcp?.Close(); } catch { }
                }
            }
        }, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) when (!ct.IsCancellationRequested)
                {
                    // e.g. a queued connection reset before accept (port scanners) - never fatal
                    Log($"accept error: {ex.SocketErrorCode}");
                    continue;
                }
                // cap total live sockets (incl. unauthenticated) - CentrED does the same at 1024;
                // stops a flood of half-open connections from exhausting the process
                if (Interlocked.Increment(ref liveConnections) > MaxConnections)
                {
                    Interlocked.Decrement(ref liveConnections);
                    Log($"connection from {tcp.Client.RemoteEndPoint} refused: at {MaxConnections}-connection cap");
                    try { tcp.Close(); } catch { }
                    continue;
                }
                // log the socket itself, not just successful logins: a client that never
                // authenticates (wrong build, blocked port, port scan) is otherwise invisible
                try { Log($"connection from {tcp.Client.RemoteEndPoint}"); } catch { }
                _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
            }
        }
        finally
        {
            string json;
            lock (sync) json = JsonSerializer.Serialize(new Project { Regions = regions }, new JsonSerializerOptions { WriteIndented = true });
            lock (saveLock)
            {
                try
                {
                    File.WriteAllText(dataPath + ".tmp", json);
                    File.Move(dataPath + ".tmp", dataPath, overwrite: true);
                }
                catch (Exception ex) { Log("final save failed: " + ex.Message); }
            }
            Log("server stopped");
        }
    }

    async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var client = new Client { Tcp = tcp };
        var remote = "?";
        Task writerTask = null;
        try
        {
            remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
            tcp.NoDelay = true;
            client.Stream = tcp.GetStream();

            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            loginTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            // pre-auth frame is tiny (a login envelope): an unauthenticated peer can't
            // make us allocate more than 8 KB from a forged length header
            var hello = await Wire.ReadMsgAsync(client.Stream, loginTimeout.Token, 8192).ConfigureAwait(false);
            if (hello.T != "login") return;
            var req = hello.Get<LoginReq>();
            var acct = config.Accounts.FirstOrDefault(a =>
                a.User.Equals(req.User, StringComparison.OrdinalIgnoreCase) &&
                a.Md5.Equals(req.Md5, StringComparison.OrdinalIgnoreCase));
            if (acct == null)
            {
                await client.Stream.WriteAsync(Frame("loginRes", new LoginRes { Ok = false, Reason = "wrong user or password" }), ct).ConfigureAwait(false);
                Log($"{remote}: login FAILED for '{req.User}'");
                return;
            }
            client.User = acct.User;
            client.Session = req.Session ?? "";
            client.Access = acct.Access;
            await client.Stream.WriteAsync(Frame("loginRes", new LoginRes
            {
                Ok = true, Access = acct.Access,
                MapWidth = config.MapWidth, MapHeight = config.MapHeight, MaxRects = Limits.MaxRectsPerRegion,
            }), ct).ConfigureAwait(false);

            writerTask = Task.Run(() => WriterLoopAsync(client, ct), CancellationToken.None);
            lock (sync)
            {
                clients.Add(client);
                // snapshot + registration atomically: nothing can be committed between this
                // sync frame and the broadcasts that follow it in the queue
                EnqueueTo(client, Frame("sync", new SyncData { Regions = regions, Serial = serial }));
                EnqueueAllLocked(Frame("users", new UserList { Users = clients.Select(c => c.User).ToList() }));
            }
            Log($"{remote}: '{client.User}' logged in (access {client.Access}); {ClientCount} online");

            while (!ct.IsCancellationRequested)
            {
                var msg = await Wire.ReadMsgAsync(client.Stream, ct).ConfigureAwait(false);
                client.LastRecv = DateTime.UtcNow;
                switch (msg.T)
                {
                    case "ping":
                        lock (sync) EnqueueTo(client, Frame("pong", new { }));
                        break;
                    case "pull":
                        lock (sync) EnqueueTo(client, Frame("sync", new SyncData { Regions = regions, Serial = serial }));
                        break;
                    case "clientLog":
                    {
                        if (client.Access < 1) break;   // only real accounts write to the log
                        var cl = msg.Get<ClientLog>();
                        // sanitize BOTH fields: CR/LF would let a client forge log lines, and
                        // the level goes to the operator's terminal
                        var level = OneLine(cl.Level, 8);
                        if (level is not ("info" or "warn" or "error")) level = "info";
                        var text = OneLine(cl.Text, 2000);
                        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {client.User}@{remote} [{level}] {text}";
                        try
                        {
                            lock (saveLock)
                            {
                                var lp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataPath)), "clients.log");
                                // bounded rotation so a chatty/hostile client can't fill the disk
                                if (File.Exists(lp) && new FileInfo(lp).Length > 5_000_000)
                                    File.Move(lp, lp + ".old", overwrite: true);
                                File.AppendAllText(lp, line + Environment.NewLine);
                            }
                        }
                        catch { }
                        if (level is "warn" or "error") Log($"CLIENT {client.User}: [{level}] {text}");
                        break;
                    }
                    case "mulsManifest":
                        lock (sync) EnqueueTo(client, Frame("mulsManifest", mulsManifest));
                        break;
                    case "mulsGet":
                    {
                        var get = msg.Get<MulsGet>();
                        var chunk = ReadMulsChunk(get);
                        lock (sync) EnqueueTo(client, Frame("mulsChunk", chunk));
                        break;
                    }
                    case "putRegion":
                    {
                        var put = msg.Get<PutRegion>();
                        if (put.Region == null || string.IsNullOrWhiteSpace(put.Region.DefName)) break;
                        if (client.Access < 1)
                        {
                            lock (sync) EnqueueTo(client, Frame("note", new { Text = "read-only account: your change was NOT saved" }));
                            break;
                        }
                        put.Region.Rects ??= new List<RegionRect>();
                        // validate + neutralize the region BEFORE it touches shared state or the
                        // .scp/.xml files (strips CR/LF injection, caps sizes) - reject if abusive
                        var vErr = SanitizeIncomingRegion(put.Region);
                        if (vErr != null)
                        {
                            lock (sync) EnqueueTo(client, Frame("note", new { Text = "change rejected: " + vErr }));
                            Log($"'{client.User}' putRegion rejected: {vErr}");
                            break;
                        }
                        lock (sync)
                        {
                            bool isNew = !regions.Any(r => r.DefName.Equals(put.Region.DefName, StringComparison.OrdinalIgnoreCase));
                            if (isNew && regions.Count >= Limits.MaxRegions)
                            {
                                EnqueueTo(client, Frame("note", new { Text = $"change rejected: server is at the {Limits.MaxRegions}-region limit" }));
                                break;
                            }
                            if (!string.IsNullOrEmpty(put.PrevDefName))
                                regions.RemoveAll(r => r.DefName.Equals(put.PrevDefName, StringComparison.OrdinalIgnoreCase));
                            int i = regions.FindIndex(r => r.DefName.Equals(put.Region.DefName, StringComparison.OrdinalIgnoreCase));
                            if (i >= 0) regions[i] = put.Region;
                            else regions.Add(put.Region);
                            serial++;
                            EnqueueAllLocked(Frame("regionPut", new ChangePut
                            {
                                Region = put.Region, PrevDefName = put.PrevDefName,
                                By = client.User, BySession = client.Session, Serial = serial,
                            }));
                            SaveDataLocked();
                        }
                        Log($"'{client.User}' put {put.Region.DefName} ({put.Region.Rects.Count} boxes)");
                        break;
                    }
                    case "drawPrev":
                    {
                        // stateless relay of a teammate's in-progress stroke
                        if (client.Access < 1) break;
                        var dp = msg.Get<DrawPreview>();
                        if (dp.Path is { Count: > 4000 }) dp.Path.RemoveRange(4000, dp.Path.Count - 4000);
                        dp.By = client.User;
                        dp.BySession = client.Session;
                        lock (sync) EnqueueAllLocked(Frame("drawPrev", dp));
                        break;
                    }
                    case "pos":
                    {
                        // stateless presence relay: everyone learns where this user is looking
                        // (viewers too - it carries no edit rights, just a view-center tile)
                        var pm = msg.Get<PosMsg>();
                        pm.By = client.User;
                        pm.BySession = client.Session;
                        lock (sync) EnqueueAllLocked(Frame("pos", pm));
                        break;
                    }
                    case "reorder":
                    {
                        var ro = msg.Get<ReorderMsg>();
                        if (ro.Order == null || ro.Order.Count == 0) break;
                        if (client.Access < 1)
                        {
                            lock (sync) EnqueueTo(client, Frame("note", new { Text = "read-only account: your change was NOT saved" }));
                            break;
                        }
                        lock (sync)
                        {
                            Project.ApplyOrder(regions, ro.Order);
                            serial++;
                            EnqueueAllLocked(Frame("reorder", new ReorderMsg
                            {
                                Order = regions.Select(r => r.DefName).ToList(),
                                By = client.User, BySession = client.Session,
                            }));
                            SaveDataLocked();
                        }
                        Log($"'{client.User}' reordered the region list ({ro.Order.Count} names)");
                        break;
                    }
                    case "delRegion":
                    {
                        var del = msg.Get<DelRegion>();
                        if (client.Access < 1)
                        {
                            lock (sync) EnqueueTo(client, Frame("note", new { Text = "read-only account: your change was NOT saved" }));
                            break;
                        }
                        bool removed;
                        lock (sync)
                        {
                            removed = regions.RemoveAll(r => r.DefName.Equals(del.DefName, StringComparison.OrdinalIgnoreCase)) > 0;
                            if (removed)
                            {
                                serial++;
                                EnqueueAllLocked(Frame("regionDel", new ChangeDel
                                {
                                    DefName = del.DefName, By = client.User, BySession = client.Session, Serial = serial,
                                }));
                                SaveDataLocked();
                            }
                        }
                        if (removed) Log($"'{client.User}' deleted {del.DefName}");
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidDataException)
        {
            // normal disconnect or garbage frame from this client - never fatal for the server
        }
        catch (Exception ex)
        {
            Log($"{remote} '{client.User}': error {ex.Message}");
        }
        finally
        {
            bool wasIn;
            lock (sync)
            {
                wasIn = clients.Remove(client);
                if (wasIn)
                {
                    EnqueueAllLocked(Frame("users", new UserList { Users = clients.Select(c => c.User).ToList() }));
                    // their in-progress stroke dies with them
                    EnqueueAllLocked(Frame("drawPrev", new DrawPreview { Kind = 0, By = client.User, BySession = client.Session }));
                }
            }
            client.SendQueue.Writer.TryComplete();
            if (!wasIn && client.User == "?")   // stays "?" until a login succeeds
                Log($"{remote}: disconnected without logging in");
            try { tcp.Close(); } catch { }
            if (writerTask != null) { try { await writerTask.ConfigureAwait(false); } catch { } }
            Interlocked.Decrement(ref liveConnections);
            if (wasIn) Log($"'{client.User}' disconnected; {ClientCount} online");
        }
    }

    public int ClientCount { get { lock (sync) return clients.Count; } }

    // ---- console account management (adduser/deluser/passwd/users) --------

    public string ConfigPath;    // set by the host so account changes persist immediately

    static string Md5(string s) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    void SaveConfig()
    {
        if (ConfigPath == null) return;
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    // view / set the operator's map size in TILES (CentrED asks this at first-run setup;
    // it can't be assumed from the map file since UOP and MUL differ). map0 = 7168 x 4096.
    public string MapSizeInfo() =>
        $"map size {config.MapWidth} x {config.MapHeight} tiles  (region rects clamp to it; reconnect clients to pick up a change)";

    public string SetMapSize(int w, int h)
    {
        if (w is < 8 or > 65535 || h is < 8 or > 65535) return "map size must be 8..65535 tiles on each axis";
        lock (sync) { config.MapWidth = w; config.MapHeight = h; SaveConfig(); }
        return $"map size set to {w} x {h} tiles - out-of-bounds rects clamp on their next edit; reconnect clients to update their bounds";
    }

    public string AddUser(string name, string password, int access)
    {
        lock (sync)
        {
            if (config.Accounts.Any(a => a.User.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"account '{name}' already exists (use passwd to change the password)";
            config.Accounts.Add(new ServerAccount { User = name, Md5 = Md5(password), Access = access });
            SaveConfig();
            return $"account '{name}' added (access {access})";
        }
    }

    public string DelUser(string name)
    {
        lock (sync)
        {
            int n = config.Accounts.RemoveAll(a => a.User.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return $"no account '{name}'";
            SaveConfig();
            foreach (var c in clients.Where(c => c.User.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { c.Tcp.Close(); } catch { }
            }
            return $"account '{name}' removed (and kicked if online)";
        }
    }

    public string SetAccess(string name, int access)
    {
        lock (sync)
        {
            var a = config.Accounts.FirstOrDefault(x => x.User.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (a == null) return $"no account '{name}'";
            a.Access = access;
            SaveConfig();
            // kick so the next login picks up the new access level
            foreach (var c in clients.Where(c => c.User.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { c.Tcp.Close(); } catch { }
            }
            return $"access of '{name}' set to {access} (kicked if online - they reconnect with the new level)";
        }
    }

    // 255 root (first-sync push-local choice) / 2 admin / 1 editor / 0 viewer.
    // admin==editor over the wire today; the tier is headroom for admin-only commands.
    static string AccessName(int a) => a >= 255 ? "root" : a >= 2 ? "admin" : a >= 1 ? "editor" : "viewer";

    // collapse CR/LF and C0/C1 control chars to spaces and cap length - used on every
    // string that gets written into regions.scp / regions.centred.xml / clients.log, so a
    // crafted region field can't inject new script lines or break the exporters
    static string OneLine(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new System.Text.StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max) break;
            sb.Append(ch < 0x20 || (ch >= 0x7F && ch <= 0x9F) ? ' ' : ch);
        }
        return sb.ToString();
    }

    // returns an error string if the region is structurally abusive, else null and the
    // region's string fields are neutralized in place. The .scp writer treats these as
    // single-line values, so stripping CR/LF here closes the injection vector.
    // Rect coordinates are clamped to the configured map size, exactly as Sphere does.
    string SanitizeIncomingRegion(RegionDef r)
    {
        if (r.Rects.Count > Limits.MaxRectsPerRegion) return $"too many boxes ({r.Rects.Count}, limit {Limits.MaxRectsPerRegion})";
        int mx = Math.Max(1, config.MapWidth) - 1, my = Math.Max(1, config.MapHeight) - 1;
        foreach (var rc in r.Rects)
        {
            rc.X1 = Math.Clamp(rc.X1, 0, mx); rc.X2 = Math.Clamp(rc.X2, 0, mx);
            rc.Y1 = Math.Clamp(rc.Y1, 0, my); rc.Y2 = Math.Clamp(rc.Y2, 0, my);
            rc.Normalize();
        }
        r.DefName = OneLine(r.DefName, 64);
        r.Name = OneLine(r.Name, 128);
        r.Events = OneLine(r.Events, 2048);
        r.Flags = OneLine(r.Flags, 256);
        r.Group = OneLine(r.Group, 128);
        r.Kind = r.Kind == null ? null : OneLine(r.Kind, 32);
        r.ServuoType = r.ServuoType == null ? null : OneLine(r.ServuoType, 64);
        r.Music = r.Music == null ? null : OneLine(r.Music, 64);
        r.Priority = Math.Clamp(r.Priority, 0, 32767);
        r.Extra ??= new List<string>();
        if (r.Extra.Count > MaxExtraLines) return $"too many extra lines ({r.Extra.Count})";
        // a network-pushed Extra line that opens a section ("[FUNCTION ...]", "[AREADEF ...]")
        // would inject a whole new script block into regions.scp - drop those; legit
        // per-region extras are single TAG.* / comment lines, never section headers
        r.Extra = r.Extra
            .Select(x => OneLine(x, 512))
            .Where(x => !x.TrimStart().StartsWith("["))
            .ToList();
        return null;
    }

    public string TailClientLog(int lines)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataPath)), "clients.log");
        if (!File.Exists(path)) return "  (no client logs yet)";
        try
        {
            var all = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)).Select(l => "  " + l));
        }
        catch (Exception ex) { return "  (log read failed: " + ex.Message + ")"; }
    }

    public string ExportNow()
    {
        lock (sync) SaveDataLocked();
        var dir = Path.GetDirectoryName(Path.GetFullPath(dataPath));
        return $"writing regions.scp, regions.centred.xml and regions.servuo.xml in {dir} ({regions.Count} regions)";
    }

    public string SetPassword(string name, string password)
    {
        lock (sync)
        {
            var a = config.Accounts.FirstOrDefault(x => x.User.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (a == null) return $"no account '{name}'";
            a.Md5 = Md5(password);
            SaveConfig();
            return $"password changed for '{name}'";
        }
    }

    public string ListUsers()
    {
        lock (sync)
        {
            var online = clients.Select(c => c.User).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return string.Join(Environment.NewLine, config.Accounts.Select(a =>
                $"  {a.User,-20} access {a.Access,3} ({AccessName(a.Access),-6}) {(online.Contains(a.User) ? "ONLINE" : "")}"));
        }
    }

    const int MulsChunkSize = 1024 * 1024;

    MulsChunk ReadMulsChunk(MulsGet get)
    {
        try
        {
            // strict: only plain names from the indexed manifest, never paths
            var entry = mulsDir == null ? null
                : mulsManifest.Files.FirstOrDefault(f => f.Name.Equals(get.Name, StringComparison.OrdinalIgnoreCase));
            if (entry == null || get.Name.IndexOfAny(new[] { '/', '\\' }) >= 0 || get.Name.Contains(".."))
                return new MulsChunk { Name = get.Name, Error = "unknown file" };
            var path = Path.Combine(mulsDir, entry.Name);
            using var fs = File.OpenRead(path);
            if (get.Offset < 0 || get.Offset >= fs.Length)
                return new MulsChunk { Name = get.Name, Error = "bad offset" };
            fs.Seek(get.Offset, SeekOrigin.Begin);
            var buf = new byte[Math.Min(MulsChunkSize, fs.Length - get.Offset)];
            fs.ReadExactly(buf);
            // gzip when it actually shrinks (map muls compress well; already-packed UOPs may not)
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(buf);
            bool useGz = ms.Length < buf.Length * 9 / 10;
            return new MulsChunk
            {
                Name = entry.Name, Offset = get.Offset, Total = fs.Length,
                DataB64 = Convert.ToBase64String(useGz ? ms.ToArray() : buf), Gz = useGz,
            };
        }
        catch (Exception ex)
        {
            return new MulsChunk { Name = get.Name, Error = ex.Message };
        }
    }
}
