using System.Text.Json;
using System.Text.Json.Serialization;

namespace UORegionEditor.Net;

// Wire format: 4-byte little-endian length + UTF8 JSON envelope {"T":"type","D":{...}}.
// Own protocol (both ends are ours) - JSON keeps it debuggable and easily extended.
public class Envelope
{
    public string T { get; set; } = "";
    public JsonElement D { get; set; }
}

public class LoginReq { public string User { get; set; } = ""; public string Md5 { get; set; } = ""; public string Session { get; set; } = ""; public int Ver { get; set; } = 1; }
public class LoginRes
{
    public bool Ok { get; set; }
    public string Reason { get; set; } = "";
    public int Access { get; set; }
    // the server's operator-chosen map bounds + the per-region box limit, so the client
    // shows the same numbers and clamps to the same size (0 = client keeps its own)
    public int MapWidth { get; set; }
    public int MapHeight { get; set; }
    public int MaxRects { get; set; }
}
public class SyncData { public List<RegionDef> Regions { get; set; } = new(); public long Serial { get; set; } }
public class PutRegion { public RegionDef Region { get; set; } public string PrevDefName { get; set; } = ""; }
public class DelRegion { public string DefName { get; set; } = ""; }
public class ChangePut { public RegionDef Region { get; set; } public string PrevDefName { get; set; } = ""; public string By { get; set; } = ""; public string BySession { get; set; } = ""; public long Serial { get; set; } }
public class ChangeDel { public string DefName { get; set; } = ""; public string By { get; set; } = ""; public string BySession { get; set; } = ""; public long Serial { get; set; } }
public class UserList { public List<string> Users { get; set; } = new(); }
// list order = script order = Sphere override precedence; client sends Order only,
// the server broadcast fills By/BySession for echo suppression
public class ReorderMsg { public List<string> Order { get; set; } = new(); public string By { get; set; } = ""; public string BySession { get; set; } = ""; }

// live draw preview: the stroke a teammate is currently dragging (box or lasso).
// Pure relay - the server stores nothing; receivers expire stale ones by time.
public class DrawPreview
{
    public int Kind { get; set; }              // 0 = cleared, 1 = box, 2 = lasso
    public bool Erase { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public List<int> Path { get; set; } = new();   // lasso: flattened x,y pairs (thinned)
    public string By { get; set; } = "";           // filled by the server
    public string BySession { get; set; } = "";
}
// presence: where each user is looking right now (view-center tile). Stateless relay
// like DrawPreview - the server stamps By/BySession; receivers keep the latest per session.
public class PosMsg { public int X { get; set; } public int Y { get; set; } public string By { get; set; } = ""; public string BySession { get; set; } = ""; }

public class ClientLog { public string Level { get; set; } = "info"; public string Text { get; set; } = ""; }

// ---- muls distribution: the server hosts a muls pack so every client renders the same map ----
public class MulsFileInfo { public string Name { get; set; } = ""; public long Size { get; set; } public string Sha256 { get; set; } = ""; }
public class MulsManifest { public List<MulsFileInfo> Files { get; set; } = new(); }
public class MulsGet { public string Name { get; set; } = ""; public long Offset { get; set; } }
public class MulsChunk
{
    public string Name { get; set; } = "";
    public long Offset { get; set; }
    public long Total { get; set; }
    public string DataB64 { get; set; } = "";   // up to 1 MiB per chunk (before compression)
    public bool Gz { get; set; }                // DataB64 is gzip-compressed (CentrED-style: only when it pays)
    public string Error { get; set; } = "";
}

public static class Wire
{
    // NO WhenWritingDefault here: it would drop properties whose value equals default(T)
    // (e.g. Visible=false) and the receiver's non-default initializer would silently win.
    public static readonly JsonSerializerOptions Opts = new();

    public static async Task WriteMsgAsync(Stream s, string type, object data, CancellationToken ct)
    {
        var env = new Envelope { T = type, D = JsonSerializer.SerializeToElement(data ?? new { }, Opts) };
        var payload = JsonSerializer.SerializeToUtf8Bytes(env, Opts);
        var frame = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    // maxLen bounds the buffer we allocate from a peer-supplied length. Kept small for
    // the pre-auth/edit direction so 4 header bytes can't force a huge allocation
    // (CentrED bounds this implicitly with fixed 64KB ring buffers; we cap explicitly).
    public const int DefaultMaxFrame = 2_000_000;

    public static async Task<Envelope> ReadMsgAsync(Stream s, CancellationToken ct, int maxLen = DefaultMaxFrame)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(s, lenBuf, ct).ConfigureAwait(false);
        int len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > maxLen) throw new InvalidDataException($"bad frame length {len}");
        var buf = new byte[len];
        await ReadExactAsync(s, buf, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Envelope>(buf) ?? throw new InvalidDataException("null envelope");
    }

    public static T Get<T>(this Envelope e) => e.D.Deserialize<T>() ?? throw new InvalidDataException($"bad {typeof(T).Name}");

    static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
    }
}
