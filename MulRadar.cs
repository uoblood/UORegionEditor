using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UORegionEditor;

// Raw map/statics access for the detail (CentrED-look) renderer.
public class MulMapData
{
    public byte[] Map;        // concatenated map0 blocks (196 bytes each)
    public byte[] Staidx;
    public byte[] Statics;
    public ushort[] Radarcol;
    public int BW, BH;        // blocks

    public int WT => BW * 8;
    public int HT => BH * 8;

    public (ushort id, sbyte z) LandAt(int x, int y)
    {
        long bi = (long)(x / 8) * BH + y / 8;
        int off = (int)(bi * 196 + 4) + (y % 8 * 8 + x % 8) * 3;
        if (off + 3 > Map.Length) return (0, 0);
        return ((ushort)(Map[off] | (Map[off + 1] << 8)), (sbyte)Map[off + 2]);
    }

    // Radar color (0xRRGGBB) of the land tile alone - what the radar map would show
    // with no statics. Same lookup + 555->888 expansion as the MulRadar full render.
    public int LandColorAt(int x, int y)
    {
        var (id, _) = LandAt(x, y);
        return Rgb888(Col(id & 0x3FFF));
    }

    // Effective radar pixel (0xRRGGBB): the land color, overridden by the topmost
    // static with z >= land z - identical rules to the MulRadar full-map render,
    // including file-order tie-breaks and the 0x4000 static color offset.
    public int SurfaceColorAt(int x, int y)
    {
        var (id, lz) = LandAt(x, y);
        sbyte topZ = lz;
        ushort col = Col(id & 0x3FFF);
        foreach (var s in StaticsAt(x, y))
            if (s.z >= topZ)
            {
                topZ = s.z;
                col = Col(0x4000 + s.id);
            }
        return Rgb888(col);
    }

    ushort Col(int i) => Radarcol != null && i >= 0 && i < Radarcol.Length ? Radarcol[i] : (ushort)0;

    static int Rgb888(ushort c16)
    {
        int r = (c16 >> 10) & 0x1F; r = (r << 3) | (r >> 2);
        int g = (c16 >> 5) & 0x1F;  g = (g << 3) | (g >> 2);
        int b = c16 & 0x1F;         b = (b << 3) | (b >> 2);
        return (r << 16) | (g << 8) | b;
    }

    // land tiledata names -> dense type ids ("Land type" wand match: every land id
    // named "cave" is one type, all "grass" shades another, etc.)
    public string[] LandNames;    // 0x4000 lowercase names; null = no tiledata.mul
    public int[] LandType;        // land id -> dense index over distinct names
    public string[] StaticNames;  // static id -> lowercase tiledata name (shares the land name space)
    public int[] StaticType;      // static id -> dense index (same dictionary as land: "cave" == "cave")
    public bool[] StaticGround;   // static id -> floor-like (Surface|Bridge flags - something you stand ON)

    // dense land-type id at a tile (equal <=> same tiledata NAME as the seed)
    public int LandTypeAt(int x, int y) => LandType == null ? 0 : LandType[LandAt(x, y).id & 0x3FFF];

    // radar palette color (0xRRGGBB) for a radarcol index (land i, statics 0x4000+i)
    public int RadarRgb(int i) => Rgb888(Col(i));

    // 512 blocks x (4-byte header + 32 entries); entry = flags(4 old / 8 HS) + texID(2)
    // + name(20). Format by size like everyone does: HS tiledata.mul >= 3,188,736 bytes.
    // (Layout cross-checked against ClassicUO TileDataLoader.)
    public void LoadTiledata(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            bool hs = b.Length >= 3_188_736;
            int entry = hs ? 30 : 26, block = 4 + 32 * entry, nameOff = hs ? 10 : 6;
            var names = new string[0x4000];
            var type = new int[0x4000];
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < 0x4000; i++)
            {
                int off = i / 32 * block + 4 + i % 32 * entry + nameOff;
                string n = "";
                if (off + 20 <= b.Length)
                {
                    int len = 0;
                    while (len < 20 && b[off + len] != 0) len++;
                    n = Encoding.ASCII.GetString(b, off, len).Trim().ToLowerInvariant();
                }
                names[i] = n;
                if (n.Length == 0) n = "\0" + i;   // unnamed ids must not pool into one type
                if (!ids.TryGetValue(n, out var t)) ids[n] = t = ids.Count;
                type[i] = t;
            }
            LandNames = names;
            LandType = type;

            // static section follows the 512 land blocks: entry = flags(4/8) + weight(1)
            // + layer(1) + count(4) + animId(2) + hue(2) + light(2) + height(1) + name(20)
            int sEntry = hs ? 41 : 37, sBlock = 4 + 32 * sEntry, sNameOff = sEntry - 20;
            int sBase = 512 * block;
            int sCount = Math.Min(0x10000, (b.Length - sBase) / sBlock * 32);
            if (sCount > 0)
            {
                var sNames = new string[sCount];
                var sType = new int[sCount];
                var sGround = new bool[sCount];
                for (int i = 0; i < sCount; i++)
                {
                    int off = sBase + i / 32 * sBlock + 4 + i % 32 * sEntry;
                    uint fl = BitConverter.ToUInt32(b, off);
                    int no = off + sNameOff;
                    string n = "";
                    if (no + 20 <= b.Length)
                    {
                        int len = 0;
                        while (len < 20 && b[no + len] != 0) len++;
                        n = Encoding.ASCII.GetString(b, no, len).Trim().ToLowerInvariant();
                    }
                    sNames[i] = n;
                    if (n.Length == 0) n = "0001s" + i;   // unnamed: unique, never equal to land's
                    if (!ids.TryGetValue(n, out var t)) ids[n] = t = ids.Count;
                    sType[i] = t;
                    sGround[i] = (fl & 0x600) != 0;   // Surface(0x200)|Bridge(0x400): walkable floor
                }
                StaticNames = sNames;
                StaticType = sType;
                StaticGround = sGround;
            }
        }
        catch { /* wand falls back to color matching */ }
    }

    public IEnumerable<(ushort id, sbyte z, ushort hue)> StaticsAt(int x, int y)
    {
        if (Staidx == null) yield break;
        long bi = (long)(x / 8) * BH + y / 8;
        long ii = bi * 12;
        if (ii + 12 > Staidx.Length) yield break;
        uint lookup = BitConverter.ToUInt32(Staidx, (int)ii);
        int len = BitConverter.ToInt32(Staidx, (int)ii + 4);
        if (lookup == 0xFFFFFFFF || len <= 0 || lookup + (long)len > Statics.Length) yield break;
        int cx = x % 8, cy = y % 8;
        int n = len / 7;
        for (int k = 0; k < n; k++)
        {
            int o = (int)lookup + k * 7;
            if (Statics[o + 2] != cx || Statics[o + 3] != cy) continue;
            yield return ((ushort)(Statics[o] | (Statics[o + 1] << 8)), (sbyte)Statics[o + 4],
                (ushort)(Statics[o + 5] | (Statics[o + 6] << 8)));
        }
    }
}

// Renders the flat radar-color world map (1 pixel = 1 tile) straight from the muls:
// map0LegacyMUL.uop / map0.mul + staidx0/statics0 + radarcol.mul. Cached as PNG per muls version.
public static class MulRadar
{
    public static string FindMap(string dir)
    {
        foreach (var n in new[] { "map0LegacyMUL.uop", "map0.uop", "map0.mul" })
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static Bitmap LoadOrRender(string mulsDir, Action<string> progress)
    {
        string mapPath = FindMap(mulsDir)
            ?? throw new FileNotFoundException($"map0LegacyMUL.uop / map0.mul not found in {mulsDir}");
        string staidxPath = Path.Combine(mulsDir, "staidx0.mul");
        string staticsPath = Path.Combine(mulsDir, "statics0.mul");
        string radarcolPath = Path.Combine(mulsDir, "radarcol.mul");
        if (!File.Exists(radarcolPath))
            throw new FileNotFoundException($"radarcol.mul not found in {mulsDir}");
        bool hasStatics = File.Exists(staidxPath) && File.Exists(staticsPath);

        string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UORegionEditor");
        Directory.CreateDirectory(cacheDir);
        string key;
        using (var md5 = MD5.Create())
        {
            var fi = new FileInfo(mapPath);
            var fr = new FileInfo(radarcolPath);
            var s = $"{mapPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{fr.Length}|{fr.LastWriteTimeUtc.Ticks}";
            if (hasStatics)
            {
                var f2 = new FileInfo(staticsPath);
                s += $"|{f2.Length}|{f2.LastWriteTimeUtc.Ticks}";
            }
            key = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }
        string cachePng = Path.Combine(cacheDir, $"radar-{key}.png");
        if (File.Exists(cachePng))
        {
            progress?.Invoke("Loading cached map image...");
            try
            {
                using var tmp = new Bitmap(cachePng);
                return new Bitmap(tmp); // copy so the file handle is released
            }
            catch
            {
                // truncated/corrupt cache (e.g. crash mid-save): drop it and re-render
                try { File.Delete(cachePng); } catch { }
            }
        }

        progress?.Invoke("Reading map files...");
        byte[] mapData = mapPath.EndsWith(".mul", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(mapPath)
            : ReadUopConcat(mapPath);

        long blocks = mapData.Length / 196;
        int bw = 896, bh = 512;
        if (blocks != (long)bw * bh)
        {
            foreach (var (w, h) in new[] { (896, 512), (768, 512), (128, 128), (288, 200), (320, 256), (181, 181), (160, 512) })
                if (blocks >= (long)w * h && blocks <= (long)w * h + 8) { bw = w; bh = h; break; }
        }

        var rb = File.ReadAllBytes(radarcolPath);
        var rcol = new ushort[rb.Length / 2];
        Buffer.BlockCopy(rb, 0, rcol, 0, rcol.Length * 2);

        byte[] idx = hasStatics ? File.ReadAllBytes(staidxPath) : null;
        byte[] st = hasStatics ? File.ReadAllBytes(staticsPath) : null;

        int W = bw * 8, H = bh * 8;
        progress?.Invoke($"Rendering {W}x{H} radar map...");
        var px = new int[W * H];

        Parallel.For(0, bw, bx =>
        {
            var topZ = new sbyte[64];
            var topColor = new ushort[64];
            for (int by = 0; by < bh; by++)
            {
                long bi = (long)bx * bh + by;
                int off = (int)(bi * 196 + 4);
                if (off + 192 > mapData.Length) continue;
                for (int c = 0; c < 64; c++)
                {
                    int o = off + c * 3;
                    ushort id = (ushort)(mapData[o] | (mapData[o + 1] << 8));
                    topZ[c] = (sbyte)mapData[o + 2];
                    topColor[c] = SafeCol(rcol, id & 0x3FFF);
                }
                if (idx != null)
                {
                    long ii = bi * 12;
                    if (ii + 12 <= idx.Length)
                    {
                        uint lookup = BitConverter.ToUInt32(idx, (int)ii);
                        int len = BitConverter.ToInt32(idx, (int)ii + 4);
                        if (lookup != 0xFFFFFFFF && len > 0 && lookup + (long)len <= st.Length)
                        {
                            int n = len / 7;
                            for (int k = 0; k < n; k++)
                            {
                                int o = (int)lookup + k * 7;
                                ushort sid = (ushort)(st[o] | (st[o + 1] << 8));
                                int cx = st[o + 2], cy = st[o + 3];
                                sbyte z = (sbyte)st[o + 4];
                                if (cx > 7 || cy > 7) continue;
                                int c = cy * 8 + cx;
                                if (z >= topZ[c])
                                {
                                    topZ[c] = z;
                                    topColor[c] = SafeCol(rcol, 0x4000 + sid);
                                }
                            }
                        }
                    }
                }
                int baseX = bx * 8, baseY = by * 8;
                for (int cy = 0; cy < 8; cy++)
                {
                    int row = (baseY + cy) * W + baseX;
                    for (int cx = 0; cx < 8; cx++)
                    {
                        ushort c16 = topColor[cy * 8 + cx];
                        int r = (c16 >> 10) & 0x1F; r = (r << 3) | (r >> 2);
                        int g = (c16 >> 5) & 0x1F;  g = (g << 3) | (g >> 2);
                        int b = c16 & 0x1F;         b = (b << 3) | (b >> 2);
                        px[row + cx] = unchecked((255 << 24) | (r << 16) | (g << 8) | b);
                    }
                }
            }
        });

        var bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            if (bd.Stride == W * 4)
            {
                Marshal.Copy(px, 0, bd.Scan0, px.Length);
            }
            else
            {
                for (int y = 0; y < H; y++)
                    Marshal.Copy(px, y * W, IntPtr.Add(bd.Scan0, y * bd.Stride), W);
            }
        }
        finally { bmp.UnlockBits(bd); }

        progress?.Invoke("Caching map image...");
        try
        {
            // atomic: write to a temp name, then move into place (a killed save must not leave a broken cache)
            var tmpPng = cachePng + ".tmp";
            bmp.Save(tmpPng, ImageFormat.Png);
            File.Move(tmpPng, cachePng, overwrite: true);
        }
        catch { /* cache is best-effort */ }
        return bmp;
    }

    static ushort SafeCol(ushort[] rcol, int i) => i >= 0 && i < rcol.Length ? rcol[i] : (ushort)0;

    // Load the raw map data (for the detail renderer). ~150 MB for the 896x512 facet.
    public static MulMapData LoadData(string mulsDir)
    {
        string mapPath = FindMap(mulsDir)
            ?? throw new FileNotFoundException($"map0LegacyMUL.uop / map0.mul not found in {mulsDir}");
        var d = new MulMapData
        {
            Map = mapPath.EndsWith(".mul", StringComparison.OrdinalIgnoreCase)
                ? File.ReadAllBytes(mapPath)
                : ReadUopConcat(mapPath),
        };
        long blocks = d.Map.Length / 196;
        d.BW = 896; d.BH = 512;
        if (blocks != (long)d.BW * d.BH)
        {
            foreach (var (w, h) in new[] { (896, 512), (768, 512), (128, 128), (288, 200), (320, 256), (181, 181), (160, 512) })
                if (blocks >= (long)w * h && blocks <= (long)w * h + 8) { d.BW = w; d.BH = h; break; }
        }
        string staidxPath = Path.Combine(mulsDir, "staidx0.mul");
        string staticsPath = Path.Combine(mulsDir, "statics0.mul");
        if (File.Exists(staidxPath) && File.Exists(staticsPath))
        {
            d.Staidx = File.ReadAllBytes(staidxPath);
            d.Statics = File.ReadAllBytes(staticsPath);
        }
        string radarcolPath = Path.Combine(mulsDir, "radarcol.mul");
        if (File.Exists(radarcolPath))
        {
            var rb = File.ReadAllBytes(radarcolPath);
            d.Radarcol = new ushort[rb.Length / 2];
            Buffer.BlockCopy(rb, 0, d.Radarcol, 0, d.Radarcol.Length * 2);
        }
        else
        {
            d.Radarcol = new ushort[0x8000];
        }
        string tiledataPath = Path.Combine(mulsDir, "tiledata.mul");
        if (File.Exists(tiledataPath)) d.LoadTiledata(tiledataPath);
        return d;
    }

    // Concatenate the UOP archive entries "build/<name>/NNNNNNNN.dat" in index order -> plain map0.mul bytes.
    static byte[] ReadUopConcat(string path)
    {
        string pattern = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (br.ReadInt32() != 0x50594D) throw new InvalidDataException("Bad UOP file (magic mismatch).");
        br.ReadInt64();                    // version + signature
        long nextBlock = br.ReadInt64();
        br.ReadInt32();                    // block capacity
        int count = br.ReadInt32();
        if (count <= 0 || count > 1_000_000) throw new InvalidDataException($"Implausible UOP entry count {count}.");

        var hashes = new Dictionary<ulong, int>(count);
        for (int i = 0; i < count; i++)
            hashes.TryAdd(UopHash($"build/{pattern}/{i:D8}.dat"), i);

        var offsets = new long[count];
        var lengths = new int[count];
        fs.Seek(nextBlock, SeekOrigin.Begin);
        int guard = 0;
        do
        {
            int filesCount = br.ReadInt32();
            if (filesCount < 0 || filesCount > 1_000_000) throw new InvalidDataException("Implausible UOP block count.");
            nextBlock = br.ReadInt64();
            for (int i = 0; i < filesCount; i++)
            {
                long offset = br.ReadInt64();
                int headerLength = br.ReadInt32();
                br.ReadInt32();            // compressedLength
                int decompressedLength = br.ReadInt32();
                ulong hash = br.ReadUInt64();
                br.ReadUInt32();           // adler
                short flag = br.ReadInt16();
                if (offset == 0) continue;
                if (flag == 1) throw new NotSupportedException("Compressed UOP map entries are not supported.");
                if (!hashes.TryGetValue(hash, out int idxE))
                    throw new InvalidDataException($"UOP entry hash 0x{hash:X16} does not match the expected naming scheme.");
                offsets[idxE] = offset + headerLength;
                lengths[idxE] = decompressedLength;
            }
        } while (fs.Seek(nextBlock, SeekOrigin.Begin) != 0 && ++guard < 1_000_000);

        long total = 0;
        foreach (var l in lengths) total += l;
        if (total <= 0 || total > int.MaxValue) throw new InvalidDataException("UOP map data size out of range.");
        var data = new byte[total];
        long pos = 0;
        for (int i = 0; i < count; i++)
        {
            if (lengths[i] == 0) continue;
            fs.Seek(offsets[i], SeekOrigin.Begin);
            fs.ReadExactly(data, (int)pos, lengths[i]);
            pos += lengths[i];
        }
        return data;
    }

    // Bob Jenkins lookup3 variant used by UOP archives (same algorithm as CentrED Uop.HashFileName).
    static ulong UopHash(string s)
    {
        uint eax, ecx, edx, ebx, esi, edi;
        eax = ecx = edx = 0;
        ebx = edi = esi = (uint)s.Length + 0xDEADBEEF;
        int i = 0;
        for (i = 0; i + 12 < s.Length; i += 12)
        {
            edi = (uint)((s[i + 7] << 24) | (s[i + 6] << 16) | (s[i + 5] << 8) | s[i + 4]) + edi;
            esi = (uint)((s[i + 11] << 24) | (s[i + 10] << 16) | (s[i + 9] << 8) | s[i + 8]) + esi;
            edx = (uint)((s[i + 3] << 24) | (s[i + 2] << 16) | (s[i + 1] << 8) | s[i]) - esi;
            edx = (edx + ebx) ^ (esi >> 28) ^ (esi << 4);
            esi += edi;
            edi = (edi - edx) ^ (edx >> 26) ^ (edx << 6);
            edx += esi;
            esi = (esi - edi) ^ (edi >> 24) ^ (edi << 8);
            edi += edx;
            ebx = (edx - esi) ^ (esi >> 16) ^ (esi << 16);
            esi += edi;
            edi = (edi - ebx) ^ (ebx >> 13) ^ (ebx << 19);
            ebx += esi;
            esi = (esi - edi) ^ (edi >> 28) ^ (edi << 4);
            edi += ebx;
        }
        if (s.Length - i > 0)
        {
            switch (s.Length - i)
            {
                case 12: esi += (uint)s[i + 11] << 24; goto case 11;
                case 11: esi += (uint)s[i + 10] << 16; goto case 10;
                case 10: esi += (uint)s[i + 9] << 8; goto case 9;
                case 9: esi += (uint)s[i + 8]; goto case 8;
                case 8: edi += (uint)s[i + 7] << 24; goto case 7;
                case 7: edi += (uint)s[i + 6] << 16; goto case 6;
                case 6: edi += (uint)s[i + 5] << 8; goto case 5;
                case 5: edi += (uint)s[i + 4]; goto case 4;
                case 4: ebx += (uint)s[i + 3] << 24; goto case 3;
                case 3: ebx += (uint)s[i + 2] << 16; goto case 2;
                case 2: ebx += (uint)s[i + 1] << 8; goto case 1;
                case 1: ebx += (uint)s[i]; break;
            }
            esi = (esi ^ edi) - ((edi >> 18) ^ (edi << 14));
            ecx = (esi ^ ebx) - ((esi >> 21) ^ (esi << 11));
            edi = (edi ^ ecx) - ((ecx >> 7) ^ (ecx << 25));
            esi = (esi ^ edi) - ((edi >> 16) ^ (edi << 16));
            edx = (esi ^ ecx) - ((esi >> 28) ^ (esi << 4));
            edi = (edi ^ edx) - ((edx >> 18) ^ (edx << 14));
            eax = (esi ^ edi) - ((edi >> 8) ^ (edi << 24));
            return ((ulong)edi << 32) | eax;
        }
        return ((ulong)esi << 32) | eax;
    }
}
