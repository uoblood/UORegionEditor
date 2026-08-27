using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace UORegionEditor.Net;

// Client side of the region server connection. All sends go through one ordered queue;
// any send/receive failure flips to disconnected exactly once and raises Disconnected -
// callers can then warn that recent edits may not have reached the server.
// Events fire on background threads - the form marshals them with BeginInvoke.
public class NetClient : IDisposable
{
    TcpClient tcp;
    NetworkStream stream;
    CancellationTokenSource cts;
    Channel<(string type, object data)> sendQueue;
    System.Threading.Timer pingTimer;
    int disconnectedRaised;

    public bool Connected { get; private set; }
    public string User { get; private set; } = "";
    public string Session { get; } = Guid.NewGuid().ToString("N");
    public string HostDisplay { get; private set; } = "";
    public int Access { get; private set; }
    public bool ReadOnly => Access < 1;
    public int MapWidth { get; private set; }    // server's configured map bounds (0 if not sent)
    public int MapHeight { get; private set; }
    public int MaxRects { get; private set; }    // server's per-region box limit

    public event Action<SyncData> Synced;
    public event Action<ChangePut> RegionPut;
    public event Action<ChangeDel> RegionDeleted;
    public event Action<ReorderMsg> Reordered;
    public event Action<DrawPreview> Preview;
    public event Action<List<string>> UsersChanged;
    public event Action<PosMsg> PosChanged;
    public event Action<string> Notice;
    public event Action<string> Disconnected;

    TaskCompletionSource<MulsManifest> manifestTcs;
    TaskCompletionSource<MulsChunk> chunkTcs;

    public static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // a manifest file name is safe only if it is a plain single-segment file name -
    // no directory separators, no drive/colon, no "..", nothing but the bare name
    static bool IsSafeMulName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) return false;
        if (name != Path.GetFileName(name)) return false;                  // strips any path part
        if (name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;  // belt and braces
        return true;
    }

    // returns null on success, else an error message
    public async Task<string> ConnectAsync(string host, int port, string user, string password)
    {
        try
        {
            Dispose();
            disconnectedRaised = 0;
            cts = new CancellationTokenSource();
            tcp = new TcpClient { NoDelay = true };
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await tcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
            }
            stream = tcp.GetStream();
            await Wire.WriteMsgAsync(stream, "login", new LoginReq { User = user, Md5 = Md5(password), Session = Session }, cts.Token).ConfigureAwait(false);
            var res = await Wire.ReadMsgAsync(stream, cts.Token).ConfigureAwait(false);
            if (res.T != "loginRes") return "unexpected server reply";
            var login = res.Get<LoginRes>();
            if (!login.Ok) return login.Reason;
            User = user;
            Access = login.Access;
            MapWidth = login.MapWidth;
            MapHeight = login.MapHeight;
            MaxRects = login.MaxRects;
            HostDisplay = $"{host}:{port}";
            sendQueue = Channel.CreateBounded<(string, object)>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });
            Connected = true;
            _ = Task.Run(ReadLoopAsync);
            _ = Task.Run(SendLoopAsync);
            pingTimer = new System.Threading.Timer(_ => Enqueue("ping", new { }), null, 20000, 20000);
            return null;
        }
        catch (Exception ex)
        {
            Dispose();
            return ex.Message;
        }
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (Connected && !cts.Token.IsCancellationRequested)
            {
                // server->client frames can be a full region sync or a ~1.4MB muls chunk;
                // 64MB: a big project full of quick-select regions can push the login
                // sync well past 16MB, and the server is ours (not hostile-by-default)
                var msg = await Wire.ReadMsgAsync(stream, cts.Token, 64_000_000).ConfigureAwait(false);
                switch (msg.T)
                {
                    case "sync": Synced?.Invoke(msg.Get<SyncData>()); break;
                    case "regionPut": RegionPut?.Invoke(msg.Get<ChangePut>()); break;
                    case "regionDel": RegionDeleted?.Invoke(msg.Get<ChangeDel>()); break;
                    case "reorder": Reordered?.Invoke(msg.Get<ReorderMsg>()); break;
                    case "drawPrev": Preview?.Invoke(msg.Get<DrawPreview>()); break;
                    case "users": UsersChanged?.Invoke(msg.Get<UserList>().Users); break;
                    case "pos": PosChanged?.Invoke(msg.Get<PosMsg>()); break;
                    case "note": Notice?.Invoke(msg.Get<NoteMsg>().Text); break;
                    case "mulsManifest": manifestTcs?.TrySetResult(msg.Get<MulsManifest>()); break;
                    case "mulsChunk": chunkTcs?.TrySetResult(msg.Get<MulsChunk>()); break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex is EndOfStreamException ? "server closed the connection" : ex.Message);
        }
    }

    async Task SendLoopAsync()
    {
        try
        {
            await foreach (var (type, data) in sendQueue.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                await Wire.WriteMsgAsync(stream, type, data, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fail("send failed: " + ex.Message);
        }
    }

    void Fail(string reason)
    {
        if (!Connected) return;
        Connected = false;
        if (Interlocked.Exchange(ref disconnectedRaised, 1) == 0)
            Disconnected?.Invoke(reason);
    }

    void Enqueue(string type, object data)
    {
        if (!Connected) return;
        if (sendQueue?.Writer.TryWrite((type, data)) != true)
            Fail("send queue overflow");
    }

    public void PushRegion(RegionDef r, string prevDefName = "") => Enqueue("putRegion", new PutRegion { Region = r, PrevDefName = prevDefName });
    public void PushDelete(string defName) => Enqueue("delRegion", new DelRegion { DefName = defName });
    public void RequestPull() => Enqueue("pull", new { });
    public void PushLog(string level, string text) => Enqueue("clientLog", new ClientLog { Level = level, Text = text });
    public void PushReorder(List<string> order) => Enqueue("reorder", new ReorderMsg { Order = order });
    public void PushPreview(DrawPreview p) => Enqueue("drawPrev", p);
    public void PushPos(int x, int y) => Enqueue("pos", new PosMsg { X = x, Y = y });

    public async Task<MulsManifest> GetMulsManifestAsync(TimeSpan timeout)
    {
        manifestTcs = new TaskCompletionSource<MulsManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue("mulsManifest", new { });
        var done = await Task.WhenAny(manifestTcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == manifestTcs.Task ? manifestTcs.Task.Result : null;
    }

    async Task<MulsChunk> GetMulsChunkAsync(string name, long offset, TimeSpan timeout)
    {
        chunkTcs = new TaskCompletionSource<MulsChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue("mulsGet", new MulsGet { Name = name, Offset = offset });
        // linked to cts so a disposed/reconnected client aborts NOW instead of parking on a file handle
        var token = cts?.Token ?? CancellationToken.None;
        try
        {
            var done = await Task.WhenAny(chunkTcs.Task, Task.Delay(timeout, token)).ConfigureAwait(false);
            if (done != chunkTcs.Task) return null;
        }
        catch (OperationCanceledException) { return null; }
        var chunk = chunkTcs.Task.Result;
        // never trust a stray reply: it must answer THIS request
        if (chunk.Error == "" && (!chunk.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || chunk.Offset != offset))
            return new MulsChunk { Name = name, Error = "out-of-order chunk reply" };
        return chunk;
    }

    // Downloads the server's muls pack into cacheDir (only missing/changed files).
    // Returns null on success (or nothing-to-do), else an error text. Never throws.
    // frac reports overall download progress 0..1 (not called during the hash pre-check).
    public async Task<string> SyncMulsAsync(string cacheDir, Action<string> progress, Action<double> frac = null)
    {
        try
        {
            var manifest = await GetMulsManifestAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (manifest == null) return "server did not answer the muls request";
            if (manifest.Files.Count == 0) return "server has no muls pack";
            // SECURITY: the server controls every file name; a malicious server could send
            // "..\..\Startup\x.bat" or an absolute path and we would write outside the cache
            // (arbitrary file write = RCE). Only plain, single-segment names are allowed, and
            // total size is bounded so a rogue manifest cannot fill the disk.
            const long MaxTotalMuls = 4L * 1024 * 1024 * 1024;   // 4 GB whole pack ceiling
            long declared = 0;
            foreach (var f in manifest.Files)
            {
                if (!IsSafeMulName(f.Name)) return $"server sent an unsafe file name: '{f.Name}'";
                if (f.Size < 0 || f.Size > MaxTotalMuls) return $"server sent an implausible file size for '{f.Name}'";
                declared += f.Size;
            }
            if (declared > MaxTotalMuls) return "server muls pack is implausibly large";
            Directory.CreateDirectory(cacheDir);
            foreach (var stale in Directory.GetFiles(cacheDir, "*.part-*"))
                try { File.Delete(stale); } catch { }

            using var sha = System.Security.Cryptography.SHA256.Create();
            var toGet = new List<MulsFileInfo>();
            foreach (var f in manifest.Files)
            {
                var local = Path.Combine(cacheDir, f.Name);
                if (File.Exists(local) && new FileInfo(local).Length == f.Size)
                {
                    using var fs = File.OpenRead(local);
                    if (Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant() == f.Sha256) continue;
                }
                toGet.Add(f);
            }
            if (toGet.Count == 0)
            {
                progress?.Invoke("Muls up to date with the server.");
                return null;
            }

            long totalBytes = toGet.Sum(f => f.Size), doneBytes = 0;
            frac?.Invoke(0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var f in toGet)
            {
                var tmp = Path.Combine(cacheDir, $"{f.Name}.part-{Guid.NewGuid():N}");
                try
                {
                    await using (var outFs = File.Create(tmp))
                    {
                        long offset = 0;
                        while (offset < f.Size)
                        {
                            var chunk = await GetMulsChunkAsync(f.Name, offset, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                            if (chunk == null) return $"timeout downloading {f.Name}";
                            if (!string.IsNullOrEmpty(chunk.Error)) return $"{f.Name}: {chunk.Error}";
                            var data = Convert.FromBase64String(chunk.DataB64);
                            if (chunk.Gz)
                            {
                                // SECURITY: cap decompressed output so a decompression bomb can't OOM us -
                                // one chunk can never legitimately exceed the file's remaining bytes
                                long room = f.Size - offset;
                                using var un = new MemoryStream();
                                using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress))
                                {
                                    var tmpBuf = new byte[81920];
                                    int n;
                                    while ((n = await gz.ReadAsync(tmpBuf).ConfigureAwait(false)) > 0)
                                    {
                                        un.Write(tmpBuf, 0, n);
                                        if (un.Length > room + 1) return $"{f.Name}: chunk decompresses larger than declared";
                                    }
                                }
                                data = un.ToArray();   // offsets/progress track UNCOMPRESSED bytes
                            }
                            // SECURITY: a zero-length or oversized chunk would loop forever / overrun the file
                            if (data.Length == 0) return $"{f.Name}: server sent an empty chunk";
                            if (offset + data.Length > f.Size) return $"{f.Name}: chunk overruns the declared size";
                            await outFs.WriteAsync(data).ConfigureAwait(false);
                            offset += data.Length;
                            doneBytes += data.Length;
                            frac?.Invoke(totalBytes == 0 ? 1 : (double)doneBytes / totalBytes);
                            if (sw.ElapsedMilliseconds > 400)
                            {
                                sw.Restart();
                                progress?.Invoke($"Downloading muls: {f.Name}  {doneBytes / 1048576}/{totalBytes / 1048576} MB");
                            }
                        }
                    }
                    using (var check = File.OpenRead(tmp))
                    {
                        if (Convert.ToHexString(sha.ComputeHash(check)).ToLowerInvariant() != f.Sha256)
                            return $"{f.Name}: hash mismatch after download";
                    }
                    File.Move(tmp, Path.Combine(cacheDir, f.Name), overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            progress?.Invoke($"Muls synced: {toGet.Count} file(s), {totalBytes / 1048576} MB.");
            return null;
        }
        catch (Exception ex)
        {
            return "muls sync failed: " + ex.Message;
        }
    }

    public void Dispose()
    {
        Connected = false;
        try { pingTimer?.Dispose(); } catch { }
        try { sendQueue?.Writer.TryComplete(); } catch { }
        try { cts?.Cancel(); } catch { }
        try { stream?.Dispose(); } catch { }
        try { tcp?.Close(); } catch { }
        pingTimer = null; stream = null; tcp = null; sendQueue = null;
    }
}

public class NoteMsg { public string Text { get; set; } = ""; }
