using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using UORegionEditor.Net;
using GLPixelFormat = Silk.NET.OpenGL.PixelFormat;
using SDPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace UORegionEditor;

// ImGui interface (CentrED#-style): Silk.NET window + OpenGL, the map as GPU textures drawn
// through ImGui draw lists, floating tool windows. All model/net/undo logic is shared with
// the classic WinForms UI (run with --classic).
public partial class ImGuiApp
{
    IWindow window;
    GL gl;
    IInputContext input;
    ImGuiController controller;
    readonly ConcurrentQueue<Action> mainQueue = new();

    Project project = new();
    string projectPath;
    readonly UndoManager undoMgr = new();
    NetClient net;
    bool awaitingFirstSync;
    readonly List<object> pendingIncoming = new();
    readonly HashSet<RegionDef> dirtyPush = new();
    readonly Dictionary<RegionDef, string> pendingRenames = new();
    float pushAccum;

    // CentrED-style state machine: no map until the user picks a mode
    enum AppMode { Startup, Offline, Online }
    AppMode appMode = AppMode.Startup;
    bool Gated => appMode == AppMode.Startup || (appMode == AppMode.Online && net is not { Connected: true });

    RegionDef selReg;
    RegionRect selRect;
    uint mapTex;
    int mapW = 7168, mapH = 4096;
    string mapLabel = "no muls";
    uint mapLabelCol = 0xFF909090;
    bool mapFitPending;
    bool mapLoading;                    // radar/map data load in flight
    bool mulsSyncing;                   // server muls download in flight (blocks the map behind a progress bar)
    int mulsSyncGen;
    string mulsProgress = "";
    float mulsFrac = -1;                // <0 = indeterminate (hash pre-check)
    bool autoDef = true;
    bool defnameActive;

    // transient status line: every assignment restamps the clock and the status bar
    // hides it after a few seconds (CentrED's bar carries no lingering action chatter)
    string statusText = "";
    long statusSince;
    string status
    {
        get => statusText;
        set { statusText = value; statusSince = Environment.TickCount64; }
    }
    bool StatusFresh => statusText.Length > 0 && Environment.TickCount64 - statusSince < 8000;
    string onlineText = "offline";
    uint onlineColU = 0xFF909090;

    // File > Export map image... (player-map PNG: silhouettes + labels)
    bool exportDialogOpen;
    int exportScope;                       // 0 whole map, 1 current view, 2 picked area
    int exportScale = 2;                   // px per tile
    bool exportLabels = true;
    int exportLabelSize = 11;
    RegionRect exportArea;                 // picked area (inclusive tiles)
    bool exportPicking;                    // next left-drag on the map picks the area
    bool exporting;
    string loadedMulsDir = "";
    ArtView artView;
    Task artLoadTask;
    bool detailMode;
    MulMapData sharedMapData;          // land z / statics lookup for the status bar and P picking
    int minZ = -128, maxZ = 127;
    bool showLand = true, showStatics = true, showNoDraw;
    readonly Dictionary<long, uint> chunkTex = new();
    readonly LinkedList<long> chunkLru = new();
    readonly HashSet<long> chunkQueued = new();
    readonly List<(int cx, int cy, int lod)> chunkWork = new();
    readonly object chunkGate = new();
    int chunkWorkersRunning;
    // CentrED# renders single-threaded; parallel rasterization is our edge. Half the
    // cores, capped: the render loop and the GC need the rest.
    static readonly int ChunkWorkerLimit = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    // keyboard tracked straight from Silk input (the ImGui backend key mapping is not reliable
    // for shortcuts): keysDown = held, keysPressed = went down since last frame
    readonly HashSet<Silk.NET.Input.Key> keysDown = new();
    readonly HashSet<Silk.NET.Input.Key> keysPressed = new();
    bool KeyHeld(Silk.NET.Input.Key k) { lock (keysDown) return keysDown.Contains(k); }
    bool KeyHit(Silk.NET.Input.Key k) { lock (keysDown) return keysPressed.Contains(k); }
    bool CtrlHeld => KeyHeld(Silk.NET.Input.Key.ControlLeft) || KeyHeld(Silk.NET.Input.Key.ControlRight);
    bool ShiftHeld => KeyHeld(Silk.NET.Input.Key.ShiftLeft) || KeyHeld(Silk.NET.Input.Key.ShiftRight);

    bool showHistory;
    bool showControls;
    bool showOptions;
    bool showAbout;
    static string AppVersion => typeof(ImGuiApp).Assembly.GetName().Version?.ToString(3) ?? "?";
    bool showMinimap = true;
    // 10 = full detail (the default look). Lower trades CentrED-view sharpness and
    // highlight detail for speed - for teammates on weaker machines.
    int renderQuality = 10;
    int hoverMode = 2;   // hovered-tile marker: 0 off, 1 tile diamond, 2 item glow (ClassicUO-style)
    // map bounds + box limit; adopted from the server on connect, else our own defaults.
    // used for the Properties box-count readout and clamping imported/edited coordinates.
    int boundW = Limits.DefaultMapWidth, boundH = Limits.DefaultMapHeight;
    int maxRectsPerRegion = Limits.MaxRectsPerRegion;
    const uint DefaultHoverColor = 0x5FE400FF;   // #FF00E45F from the picker (magenta, 37% alpha; ABGR)
    const uint OldDefaultHoverColor = 0xC000D7FF;   // pre-v0.5.8 gold - migrated to the new default
    Vector4 hoverColor = ColU(DefaultHoverColor);
    bool reorderDirty;   // region list order changed locally, not yet pushed
    Guid dragRegionUid;  // region being drag-reordered in the Regions list

    // teammates' live draw strokes, keyed by session; expired after 5s without update
    readonly Dictionary<string, (DrawPreview p, long at)> remoteDraws = new();
    // presence: last known view-center per session (By = user name), fed by the pos relay
    readonly Dictionary<string, (string by, int x, int y, long at)> remotePos = new();
    List<string> onlineUsers = new();
    long lastPosSent;
    (int x, int y) lastPosTile;
    long lastPrevSent;
    string lastPrevSig = "";
    bool prevActive;     // we told the server we're drawing; must send a clear when done

    // ready-made marker colors (ABGR): the gold is the original default many liked
    static readonly (string name, uint col)[] HoverPresets =
    {
        ("Gold", 0xC000D7FF), ("Magenta", 0x5FE400FF), ("Cyan", 0x90FFE400), ("Lime", 0x9040FF40),
        ("Red", 0x904040FF), ("Orange", 0x90008CFF), ("Blue", 0x90FF8040), ("Slate", 0xECA7847B),
    };

    static Vector4 ColU(uint c) => new(
        (c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 24) & 0xFF) / 255f);

    static uint UCol(Vector4 v) =>
        ((uint)(Math.Clamp(v.W, 0, 1) * 255) << 24) | ((uint)(Math.Clamp(v.Z, 0, 1) * 255) << 16) |
        ((uint)(Math.Clamp(v.Y, 0, 1) * 255) << 8) | (uint)(Math.Clamp(v.X, 0, 1) * 255);
    float miniZoom = 1f;
    float miniCX = 3584, miniCY = 2048;
    string regionFilter = "";
    bool connectOpenRequest;
    NetSettings netSettings = new();
    ConnectProfile pendingProfileSave;
    string cProfile = "", cHost = "127.0.0.1", cUser = "", cPass = "";
    string cMulsCache = "";       // per-profile muls download override; empty = default
    string activeMulsCache = "";  // captured at connect time, used by StartMulsSync
    int cPort = 2599;

    // Which server the shard runs. Purely a VIEW setting: it decides which script
    // fields the Properties panel shows, so a Sphere shard is not asked for a ServUO
    // region class and back. Every field is still stored, synced and exported whatever
    // is selected here - switching target hides fields, it never clears them.
    // plain "CentrED": CentrED+ and CentrED# both read the same cedserver.xml regions
    static readonly string[] TargetNames = { "Sphere", "ServUO", "CentrED" };
    static readonly string[] TargetInfo =
    {
        "AREADEF/ROOMDEF with events, flags and groups",
        "Regions.xml with a region class, priority and music",
        "cedserver.xml regions - name and boxes only",
    };
    int scriptTarget;             // index into TargetNames; 0 = Sphere
    int cTarget;                  // the same, being edited in the connect dialog
    bool TargetSphere => scriptTarget == 0;
    bool TargetServuo => scriptTarget == 1;

    static string TargetName(int i) => TargetNames[Math.Clamp(i, 0, TargetNames.Length - 1)];

    // tolerant on purpose: profiles written before this setting existed have no Target
    // at all, and those shards are Sphere shards
    internal static int TargetFromName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("ServUO", StringComparison.OrdinalIgnoreCase)) return 1;
        if (s.StartsWith("CentrED", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UORegionEditor");
    static string LastProjectPath => Path.Combine(AppDir, "last-project.json");
    static string LayoutPath => Path.Combine(AppDir, "layout.ini");
    IntPtr iniPathPtr;        // native string handed to ImGui - must stay alive
    bool pendingLayoutReset;
    int layoutResetFrames;    // >0: window defaults applied with Cond.Always (undocks too)

    public static void RunApp()
    {
        // Silk.NET finds its windowing/input backend by scanning assemblies, which is
        // fragile in a single-file build. Registering GLFW by hand is the supported fix
        // and costs nothing in a normal build.
        // (The single-file crash "GlfwPlatform - not applicable" was actually caused by
        // EnableCompressionInSingleFile - never turn that on, it breaks native loading.)
        try
        {
            Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
        }
        catch { }

        var app = new ImGuiApp();
        var opts = WindowOptions.Default;
        opts.Size = new Silk.NET.Maths.Vector2D<int>(1500, 900);
        opts.Title = $"UO Region Editor  v{AppVersion}";
        opts.VSync = true;
        app.window = Window.Create(opts);
        app.window.Load += app.OnLoad;
        app.window.Render += app.OnRender;
        app.window.Closing += app.OnClosing;
        app.window.Run();
    }

    // GLFW hands the OS its own default icon unless we give it one: decode the embedded
    // PNGs (32 + 128) into RGBA and hand both to the window, so the taskbar and Alt+Tab
    // show the region editor's mark. Cosmetic - never let it break startup.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Windows draws a LIGHT title bar by default, which looks wrong bolted onto a dark
    // ImGui app (CentrED opens dark). 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Win10 2004+,
    // 19 on the earlier builds - try the modern one, fall back.
    void ApplyDarkTitleBar()
    {
        try
        {
            var w32 = window?.Native?.Win32;
            if (w32 == null) return;
            IntPtr hwnd = w32.Value.Hwnd;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch (Exception ex) { ClientLog("warn", "dark title bar: " + ex.Message); }
    }

    // ImGui carries no clipboard of its own - without these callbacks Ctrl+C / Ctrl+V
    // do nothing in ANY text field (muls paths, defnames, connect settings).
    static IntPtr clipboardBuf;

    static readonly (Silk.NET.Input.Key key, ImGuiKey im)[] ShortcutKeys =
    {
        (Silk.NET.Input.Key.A, ImGuiKey.A), (Silk.NET.Input.Key.C, ImGuiKey.C),
        (Silk.NET.Input.Key.V, ImGuiKey.V), (Silk.NET.Input.Key.X, ImGuiKey.X),
        (Silk.NET.Input.Key.Y, ImGuiKey.Y), (Silk.NET.Input.Key.Z, ImGuiKey.Z),
    };

    void FeedShortcutKeys()
    {
        var io = ImGui.GetIO();
        bool ctrl = CtrlHeld;
        io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
        io.AddKeyEvent(ImGuiKey.ModShift, ShiftHeld);
        io.AddKeyEvent(ImGuiKey.ModAlt, KeyHeld(Silk.NET.Input.Key.AltLeft) || KeyHeld(Silk.NET.Input.Key.AltRight));
        // only while Ctrl is down, so normal typing of these letters is untouched
        if (!ctrl) return;
        foreach (var (key, im) in ShortcutKeys) io.AddKeyEvent(im, KeyHeld(key));
    }

    unsafe void SetupClipboard()
    {
        try
        {
            var io = ImGui.GetIO();
            io.NativePtr->GetClipboardTextFn = (IntPtr)(delegate* unmanaged<void*, byte*>)&ClipboardGet;
            io.NativePtr->SetClipboardTextFn = (IntPtr)(delegate* unmanaged<void*, byte*, void>)&ClipboardSet;
        }
        catch (Exception ex) { ClientLog("warn", "clipboard: " + ex.Message); }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static unsafe byte* ClipboardGet(void* _)
    {
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch { }
        var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        if (clipboardBuf != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(clipboardBuf);
        clipboardBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, clipboardBuf, bytes.Length);
        return (byte*)clipboardBuf;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static unsafe void ClipboardSet(void* _, byte* text)
    {
        try
        {
            string s = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)text) ?? "";
            if (s.Length > 0) Clipboard.SetText(s);
        }
        catch { }
    }

    void SetWindowIcon()
    {
        try
        {
            var imgs = new List<Silk.NET.Core.RawImage>();
            foreach (var name in new[] { "UORegionEditor.icon128.png", "UORegionEditor.icon32.png" })
            {
                using var s = typeof(ImGuiApp).Assembly.GetManifestResourceStream(name);
                if (s == null) continue;
                using var bmp = new Bitmap(s);
                int w = bmp.Width, h = bmp.Height;
                var bytes = new byte[w * h * 4];
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, SDPixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* p = (byte*)bd.Scan0;
                        int i = 0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int o = y * bd.Stride + x * 4;        // BGRA in memory
                                bytes[i++] = p[o + 2];
                                bytes[i++] = p[o + 1];
                                bytes[i++] = p[o];
                                bytes[i++] = p[o + 3];
                            }
                    }
                }
                finally { bmp.UnlockBits(bd); }
                imgs.Add(new Silk.NET.Core.RawImage(w, h, new Memory<byte>(bytes)));
            }
            if (imgs.Count > 0)
                window.SetWindowIcon(new ReadOnlySpan<Silk.NET.Core.RawImage>(imgs.ToArray()));
        }
        catch (Exception ex) { ClientLog("warn", "window icon: " + ex.Message); }
    }

    void OnLoad()
    {
        gl = GL.GetApi(window);
        SetWindowIcon();
        ApplyDarkTitleBar();
        input = window.CreateInput();
        controller = new ImGuiController(gl, window, input);
        SetupClipboard();
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        // pin the window layout to AppData: by default ImGui writes imgui.ini into the
        // LAUNCH directory, so the layout silently "reset" depending on how the app was
        // started (shortcut vs console vs different working dirs)
        try
        {
            Directory.CreateDirectory(AppDir);
            if (!File.Exists(LayoutPath) && File.Exists("imgui.ini"))
                File.Copy("imgui.ini", LayoutPath);   // adopt a stray old layout once
        }
        catch { }
        unsafe
        {
            iniPathPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(LayoutPath);
            ImGui.GetIO().NativePtr->IniFilename = (byte*)iniPathPtr;
        }
        if (File.Exists(LayoutPath)) ImGui.LoadIniSettingsFromDisk(LayoutPath);

        window.FramebufferResize += s => gl.Viewport(s);   // without this, resizing skews everything

        foreach (var kb in input.Keyboards)
        {
            kb.KeyDown += (_, key, _) => { lock (keysDown) { keysDown.Add(key); keysPressed.Add(key); } };
            kb.KeyUp += (_, key, _) => { lock (keysDown) keysDown.Remove(key); };
        }

        try
        {
            if (File.Exists(LastProjectPath)) { project = Project.Load(LastProjectPath); projectPath = LastProjectPath; }
        }
        catch { project = new Project(); }

        if (string.IsNullOrEmpty(project.MulsDir) || MulRadar.FindMap(project.MulsDir) == null)
        {
            // a "muls" folder beside the exe is the one portable convention (the server
            // uses the same default); anything else is picked through File > Muls...
            var cand = Path.Combine(AppContext.BaseDirectory, "muls");
            if (Directory.Exists(cand) && MulRadar.FindMap(cand) != null) project.MulsDir = cand;
        }
        LoadUiSettings();
        // the map loads only after the user picks Offline or connects (CentrED-style gate)
    }

    // client-side log: local rolling file, errors surfaced in the status bar,
    // and forwarded to the server so the admin can see every client's problems
    void ClientLog(string level, string text)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            var path = Path.Combine(AppDir, "client.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {text}{Environment.NewLine}");
        }
        catch { }
        if (level is "warn" or "error") status = text;
        try { net?.PushLog(level, text); } catch { }
    }

    void GoOffline()
    {
        exportPicking = false;
        net?.Dispose();
        net = null;
        onlineText = "OFFLINE MODE";
        onlineColU = 0xFF909090;
        appMode = AppMode.Offline;
        mulsSyncing = false;
        mulsSyncGen++;                  // a still-running sync task can no longer touch the UI state
        remoteDraws.Clear();
        remotePos.Clear();
        onlineUsers = new List<string>();
        // the files may still be the server's cached pack, but we're no longer tracking it
        if (mapLabel.StartsWith("MULS: SERVER PACK")) mapLabel = "MULS: SERVER CACHE (offline copy)";
        if (mapTex == 0 && !string.IsNullOrEmpty(project.MulsDir) && MulRadar.FindMap(project.MulsDir) != null)
            LoadMapAsync(project.MulsDir, fromServer: false);
        else if (mapTex == 0)
            PickMuls();
    }

    void OnRender(double delta)
    {
        while (mainQueue.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { status = "error: " + ex.Message; }
        }

        if (pendingLayoutReset)
        {
            pendingLayoutReset = false;
            // stored settings only apply when a window is CREATED - open windows keep
            // their runtime pos/size, so the reset must force defaults for a frame
            layoutResetFrames = 2;
            try { File.Delete(LayoutPath); } catch { }
            status = "Window layout reset.";
        }

        PushDirtyRegions((float)delta);

        gl.ClearColor(0.06f, 0.06f, 0.07f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        // The Silk ImGui backend feeds typed characters but not the modifier state, so
        // ImGui never saw Ctrl held: Ctrl+A/C/V/X/Z did nothing in any text field.
        // Push the modifiers (and the shortcut letters) in before NewFrame runs.
        FeedShortcutKeys();
        controller.Update((float)delta);

        // dockable tool windows around a see-through center, CentrED style
        ImGui.DockSpaceOverViewport(0, ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode);

        if (Gated)
        {
            // no map, no tools: like CentrED, you either log in or explicitly go offline
            DrawStartGate();
            DrawConnectPopup();
        }
        else if (mulsSyncing || (mapLoading && mapTex == 0))
        {
            // first-time connect / first load: only the progress bar until the map is actually ready,
            // never a half-loaded world
            DrawMulsSyncOverlay();
        }
        else
        {
            HandleMapInput();
            DrawMap();
            DrawUI();
            DrawStatusBar();
        }

        controller.Render();
        lock (keysDown) keysPressed.Clear();
        if (layoutResetFrames > 0) layoutResetFrames--;
    }

    void OnClosing()
    {
        try { ImGui.SaveIniSettingsToDisk(LayoutPath); } catch { }
        try { PushDirtyRegions(10f); } catch { }
        try { net?.Dispose(); } catch { }
        try { artView?.Dispose(); } catch { }
        try { Directory.CreateDirectory(AppDir); project.Save(LastProjectPath); } catch { }
    }

    // ---- textures ---------------------------------------------------------

    // the ankh mark as a GL texture, uploaded the first time something asks for it
    uint logoTex;
    int logoW, logoH;

    uint LogoTexture()
    {
        if (logoTex != 0) return logoTex;
        try
        {
            using var s = typeof(ImGuiApp).Assembly.GetManifestResourceStream("UORegionEditor.logo512.png");
            if (s == null) return 0;
            using var bmp = new Bitmap(s);
            logoW = bmp.Width;
            logoH = bmp.Height;
            logoTex = UploadBitmap(bmp, mipmap: true);
        }
        catch (Exception ex)
        {
            ClientLog("warn", "logo texture: " + ex.Message);
            logoTex = 0;
        }
        return logoTex;
    }

    uint UploadBitmap(Bitmap bmp, bool mipmap)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, SDPixelFormat.Format32bppArgb);
        var bytes = new byte[bd.Stride * bd.Height];
        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, (uint)(bd.Stride / 4));
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)bmp.Width, (uint)bmp.Height, 0, GLPixelFormat.Bgra, PixelType.UnsignedByte, bytes);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        if (mipmap)
        {
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.GenerateMipmap(TextureTarget.Texture2D);
        }
        else
        {
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        }
        return tex;
    }

    void LoadMapAsync(string dir, bool fromServer)
    {
        mapLabel = "Loading map...";
        mapLabelCol = 0xFF909090;
        mapLoading = true;
        Task.Run(() =>
        {
            try
            {
                var bmp = MulRadar.LoadOrRender(dir, s => mainQueue.Enqueue(() => mapLabel = s));
                mainQueue.Enqueue(() =>
                {
                    if (mapTex != 0) gl.DeleteTexture(mapTex);
                    mapTex = UploadBitmap(bmp, mipmap: true);
                    mapW = bmp.Width;
                    mapH = bmp.Height;
                    loadedMulsDir = dir;   // only a SUCCESSFUL load may become the export source
                    sharedMapData = null;   // stale data + new bounds must not mix (wand reads tiles by x,y)
                    ClearWandPreview();     // a cached fill from the old muls must never be committed
                    // offline, the muls-derived size IS the map bound; online, the server's
                    // configured size already won (set on connect) so don't clobber it
                    if (appMode != AppMode.Online) { boundW = mapW; boundH = mapH; }
                    bmp.Dispose();
                    // unambiguous muls source: green = the server pack, orange = your local files
                    mapLabel = fromServer
                        ? $"MULS: SERVER PACK ({net?.HostDisplay})"
                        : $"MULS: LOCAL ({Path.GetFileName(Path.TrimEndingDirectorySeparator(dir))})";
                    mapLabelCol = fromServer ? 0xFF40A040 : 0xFF30A0E0;
                    mapLoading = false;
                    if (!mapFitPending) { ZoomFit(); mapFitPending = true; }
                });
                // raw map data for the Z readout / P picking (reused by the iso renderer)
                var md = MulRadar.LoadData(dir);
                mainQueue.Enqueue(() => sharedMapData = md);
            }
            catch (Exception ex)
            {
                mainQueue.Enqueue(() =>
                {
                    mapLabel = "MAP LOAD FAILED: " + ex.Message;
                    mapLabelCol = 0xFF3030C8;
                    mapLoading = false;
                    ClientLog("error", "map load failed (" + dir + "): " + ex.Message);
                });
            }
        });
    }

    // ---- detail chunks ----------------------------------------------------

    uint GetIsoChunkTexture(int cx, int cy, int lod = 0, bool peekOnly = false)
    {
        int marker = lod == 0 ? 1 : 2;
        long key = ((long)marker << 44) | ((long)cx << 22) | (uint)cy;
        if (chunkTex.TryGetValue(key, out var t))
        {
            chunkLru.Remove(key);
            chunkLru.AddFirst(key);
            return t;
        }
        var bmp = artView?.TryGetCached(cx, cy, marker);
        if (bmp != null)
        {
            // mipmapped: overview chunks are often drawn minified 2-5x when zoomed out
            var tex = UploadBitmap(bmp, mipmap: true);
            chunkTex[key] = tex;
            chunkLru.AddFirst(key);
            while (chunkLru.Count > 500)
            {
                var old = chunkLru.Last.Value;
                chunkLru.RemoveLast();
                gl.DeleteTexture(chunkTex[old]);
                chunkTex.Remove(old);
            }
            return tex;
        }
        if (peekOnly) return 0;   // underlay pass: use it if baked, never queue work for it
        lock (chunkGate)
        {
            if (chunkQueued.Add(key)) chunkWork.Add((cx, cy, lod));
            while (chunkWorkersRunning < ChunkWorkerLimit && chunkWorkersRunning < chunkWork.Count)
            {
                chunkWorkersRunning++;
                Task.Run(ChunkWorker);
            }
        }
        return 0;
    }

    // written by DrawIsoWorld each frame (under chunkGate): the worker uses it to
    // render what the user is looking at first and drop work that scrolled away
    int prioLod = -1, prioC0x, prioC0y, prioC1x, prioC1y;
    double prioCenX, prioCenY;

    void UpdateChunkPriority(int lod, double minU, double maxU, double minV, double maxV)
    {
        int span = ArtView.IsoChunkPx << (lod == 0 ? 0 : 2);
        lock (chunkGate)
        {
            prioLod = lod;
            prioC0x = (int)Math.Floor(minU / span);
            prioC1x = (int)Math.Floor(maxU / span);
            prioC0y = (int)Math.Floor(minV / span);
            prioC1y = (int)Math.Floor(maxV / span);
            prioCenX = (minU + maxU) / 2 / span;
            prioCenY = (minV + maxV) / 2 / span;
        }
    }

    static long ChunkKeyOf((int cx, int cy, int lod) j) =>
        ((long)(j.lod == 0 ? 1 : 2) << 44) | ((long)j.cx << 22) | (uint)j.cy;

    // workers grab artView by reference: null it and drain the queue BEFORE disposing,
    // or a worker could start rendering against unmapped mul memory (uncatchable AV)
    void TearDownArtView()
    {
        var av = artView;
        artView = null;
        artLoadTask = null;
        lock (chunkGate)
        {
            chunkWork.Clear();
            chunkQueued.Clear();
        }
        av?.Dispose();
    }

    void ChunkWorker()
    {
        while (true)
        {
            (int cx, int cy, int lod) job;
            lock (chunkGate)
            {
                // prune: wrong LOD or outside the current view (+1 chunk margin) is
                // stale panning debris - rendering it would starve what's on screen
                if (prioLod != -1)
                    chunkWork.RemoveAll(j =>
                    {
                        bool keep = j.lod == prioLod &&
                            j.cx >= prioC0x - 1 && j.cx <= prioC1x + 1 &&
                            j.cy >= prioC0y - 1 && j.cy <= prioC1y + 1;
                        if (!keep) chunkQueued.Remove(ChunkKeyOf(j));
                        return !keep;
                    });
                if (chunkWork.Count == 0) { chunkWorkersRunning--; return; }
                int best = 0;
                double bestD = double.MaxValue;
                for (int i = 0; i < chunkWork.Count; i++)
                {
                    double dx = chunkWork[i].cx + 0.5 - prioCenX, dy = chunkWork[i].cy + 0.5 - prioCenY;
                    double d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = i; }
                }
                job = chunkWork[best];
                chunkWork.RemoveAt(best);
                chunkQueued.Remove(ChunkKeyOf(job));
            }
            try { artView?.RenderIsoChunk(job.cx, job.cy, job.lod); }
            catch { }
        }
    }

    void ClearChunkTextures()
    {
        foreach (var t in chunkTex.Values) gl.DeleteTexture(t);
        chunkTex.Clear();
        chunkLru.Clear();
        foreach (var t in spriteTex.Values) gl.DeleteTexture(t);
        spriteTex.Clear();
        spriteLru.Clear();
    }

    // hovered-item glow: hued static sprites as GL textures, small LRU
    readonly Dictionary<long, uint> spriteTex = new();
    readonly LinkedList<long> spriteLru = new();

    uint GetStaticSpriteTexture(ushort id, ushort hue)
    {
        long key = ((long)id << 16) | hue;
        if (spriteTex.TryGetValue(key, out var t))
        {
            spriteLru.Remove(key);
            spriteLru.AddFirst(key);
            return t;
        }
        using var bmp = artView?.StaticSpriteBitmap(id, hue);
        if (bmp == null) return 0;
        var tex = UploadBitmap(bmp, mipmap: false);
        spriteTex[key] = tex;
        spriteLru.AddFirst(key);
        while (spriteLru.Count > 128)
        {
            var old = spriteLru.Last.Value;
            spriteLru.RemoveLast();
            gl.DeleteTexture(spriteTex[old]);
            spriteTex.Remove(old);
        }
        return tex;
    }

    void EnsureArtView()
    {
        if (artView != null || artLoadTask is { IsCompleted: false }) return;
        var dir = project.MulsDir;
        if (string.IsNullOrEmpty(dir) || MulRadar.FindMap(dir) == null)
        {
            status = "Detail view needs a muls folder first.";
            detailMode = false;
            return;
        }
        if (!ArtView.HasArt(dir))
        {
            status = "Detail view needs art files (artLegacyMUL.uop + texmaps + tiledata + hues) in the muls folder.";
            detailMode = false;
            return;
        }
        var packErr = ArtView.CheckArtPack(dir);
        if (packErr != null)
        {
            status = "Detail view: " + packErr;
            detailMode = false;
            return;
        }
        status = "Loading art for the CentrED view...";
        var shared = sharedMapData;
        artLoadTask = Task.Run(() =>
        {
            try
            {
                var av = new ArtView(dir, shared);
                av.SetFilter((sbyte)minZ, (sbyte)maxZ, showLand, showStatics, showNoDraw);
                mainQueue.Enqueue(() =>
                {
                    // grab the current view center (still flat), then flip the projection around it
                    var center = TileAt(new Vector2(window.Size.X / 2f, window.Size.Y / 2f));
                    artView = av;
                    sharedMapData = av.Map;
                    if (detailMode)
                    {
                        zoom = Math.Clamp(zoom / 22.0 * 8.0, 0.05, 4.0);
                        CenterOn(center.x, center.y);
                    }
                    status = "";   // the view speaks for itself - no "mode on" chatter
                });
            }
            catch (Exception ex)
            {
                mainQueue.Enqueue(() => { status = "CentrED view failed: " + ex.Message; detailMode = false; });
            }
        });
    }

    void ApplyDetailFilter()
    {
        if (artView == null) return;
        minZ = Math.Clamp(minZ, -128, 127);
        maxZ = Math.Clamp(maxZ, -128, 127);
        if (minZ > maxZ) minZ = maxZ;
        artView.SetFilter((sbyte)minZ, (sbyte)maxZ, showLand, showStatics, showNoDraw);
        ClearChunkTextures();
        lock (chunkGate) { chunkWork.Clear(); chunkQueued.Clear(); }
    }

    // ---- undo / dirty / regions glue --------------------------------------

    void SnapshotFor(string desc, RegionDef r, string key = null)
        => undoMgr.Snapshot(desc, project, new[] { r }, coalesceKey: key);

    void MarkDirty(RegionDef r)
    {
        if (r == null || net is not { Connected: true } || net.ReadOnly) return;
        dirtyPush.Add(r);
    }

    void PushDirtyRegions(float dt)
    {
        // list order changed by drag-reorder: push once the mouse button is up
        if (reorderDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (net is { Connected: true, ReadOnly: false })
                net.PushReorder(project.Regions.Select(r => r.DefName).ToList());
            reorderDirty = false;
        }
        pushAccum += dt;
        if (pushAccum < 0.7f) return;
        pushAccum = 0;
        if (net is not { Connected: true } || dirtyPush.Count == 0) return;
        foreach (var r in dirtyPush.ToList())
        {
            if (!project.Regions.Contains(r)) { dirtyPush.Remove(r); continue; }
            if (r == selReg && defnameActive) continue;      // don't push half-typed defnames
            if (string.IsNullOrWhiteSpace(r.DefName)) continue;
            pendingRenames.TryGetValue(r, out var prev);
            if (prev != null && prev.Equals(r.DefName, StringComparison.OrdinalIgnoreCase)) prev = null;
            net.PushRegion(r, prev ?? "");
            pendingRenames.Remove(r);
            dirtyPush.Remove(r);
        }
    }

    RegionDef CreateRegionObject()
    {
        int n = project.Regions.Count + 1;
        string dn;
        do { dn = $"A_REGION_{n}"; n++; }
        while (project.Regions.Any(r => r.DefName.Equals(dn, StringComparison.OrdinalIgnoreCase)));
        var reg = new RegionDef
        {
            DefName = dn,
            Name = dn.Substring(2).Replace('_', ' '),
            Events = project.DefaultEvents,
            Flags = project.DefaultFlags,
        };
        reg.Color = Palette.Next(project.Regions.Count);
        return reg;
    }

    // Re-decompose freshly added tiles TOGETHER with the target's own boxes they touch,
    // when that covers exactly the same tiles with FEWER boxes (a strip painted against
    // an existing box fuses into it instead of stacking beside it). null = no saving.
    const long FuseTileBudget = 600_000;

    (List<RegionRect> rects, List<RegionRect> replaced)? FuseIntoRegion(
        RegionDef target, HashSet<(int x, int y)> addTiles, List<RegionRect> plain)
    {
        if (target is not { Rects.Count: > 0 } || addTiles.Count == 0) return null;
        int mnX = addTiles.Min(t => t.x) - 1, mnY = addTiles.Min(t => t.y) - 1;
        int mxX = addTiles.Max(t => t.x) + 1, mxY = addTiles.Max(t => t.y) + 1;
        var near = target.Rects
            .Where(rc => rc.X2 >= mnX && rc.X1 <= mxX && rc.Y2 >= mnY && rc.Y1 <= mxY).ToList();
        if (near.Count == 0) return null;
        if (near.Sum(rc => (long)rc.W * rc.H) + addTiles.Count > FuseTileBudget) return null;
        var union = new HashSet<(int x, int y)>(addTiles);
        union.UnionWith(RegionOps.Coverage(near));
        var merged = RegionOps.MaskToRectsCompact(union);
        return merged.Count < near.Count + plain.Count ? (merged, near) : null;
    }

    void CommitDrawnRect(RegionRect rr)
    {
        RegionDef target = addToSelected ? selReg : null;
        var pieces = new List<RegionRect> { rr };
        int skipped = 0;
        if (avoidOverlap)
        {
            // carve the blockers out with rect algebra (never rasterize: a full-map
            // box would be 29M tiles) - the drawn box becomes a few bands instead
            var idx = BuildOverlapIndex(target);
            foreach (var cut in idx.Intersecting(rr))
            {
                // a neighbour built from hundreds of strips can shatter the box; stop once
                // the result is past the cap (the check below then refuses it cleanly)
                if (pieces.Count == 0 || pieces.Count > maxRectsPerRegion) break;
                pieces = RegionOps.SubtractBox(pieces, cut);
            }
            long kept = pieces.Sum(p => (long)p.W * p.H);
            skipped = (int)Math.Min(int.MaxValue, (long)rr.W * rr.H - kept);
            if (pieces.Count == 0)
            {
                status = $"Nothing drawn - that box is fully inside {(idx.Blocker(rr.X1, rr.Y1) is { } n ? $"'{n}'" : "another region")}.";
                return;
            }
        }
        // tiles this region already has need no second box: carve them out with rect
        // algebra, then fuse what remains with the boxes it touches
        int redundant = 0;
        List<RegionRect> replaced = null;
        if (target is { Rects.Count: > 0 })
        {
            long beforeArea = pieces.Sum(p => (long)p.W * p.H);
            foreach (var own in target.Rects.Where(t =>
                t.X2 >= rr.X1 && t.X1 <= rr.X2 && t.Y2 >= rr.Y1 && t.Y1 <= rr.Y2))
            {
                if (pieces.Count == 0 || pieces.Count > maxRectsPerRegion) break;
                pieces = RegionOps.SubtractBox(pieces, own);
            }
            long keptArea = pieces.Sum(p => (long)p.W * p.H);
            redundant = (int)Math.Min(int.MaxValue, beforeArea - keptArea);
            if (pieces.Count == 0)
            {
                status = $"Nothing drawn - {target.DefName} already covers that box.";
                return;
            }
            if (keptArea <= FuseTileBudget &&
                FuseIntoRegion(target, RegionOps.Coverage(pieces), pieces) is { } fused)
            {
                pieces = fused.rects;
                replaced = fused.replaced;
            }
        }
        bool created = target == null;
        if (created) target = CreateRegionObject();
        int totalBoxes = target.Rects.Count - (replaced?.Count ?? 0) + pieces.Count;
        if (totalBoxes > maxRectsPerRegion)
        {
            status = $"That box would need {totalBoxes:N0} boxes (cap {maxRectsPerRegion:N0}) - draw a simpler box.";
            return;
        }
        undoMgr.Snapshot(created ? $"create {target.DefName}" : $"draw box in {target.DefName}", project, new[] { target });
        if (created) project.Regions.Add(target);
        target.Visible = true;
        if (replaced != null) foreach (var rc in replaced) target.Rects.Remove(rc);
        target.Rects.AddRange(pieces);
        MarkDirty(target);
        selReg = target;
        selRect = target.Rects[^1];
        string note = "";
        if (replaced != null) note += $", {replaced.Count} existing merged in";
        if (redundant > 0) note += $", {redundant:N0} tiles already covered";
        if (skipped > 0) note += $", {skipped:N0} skipped (other regions)";
        if (note.Length > 0) status = $"draw: {target.DefName} now {target.Rects.Count} box(es){note}";
    }
    void CommitLassoAdd(List<(int x, int y)> path) => CommitMaskAdd(RegionOps.LassoFill(path), "lasso");
    void CommitLassoErase(List<(int x, int y)> path) => CommitMaskErase(RegionOps.LassoFill(path), "erase lasso");

    void CommitMaskAdd(HashSet<(int x, int y)> tiles, string what)
    {
        int skipped = 0;
        RegionDef target = addToSelected ? selReg : null;
        if (avoidOverlap)
        {
            var idx = BuildOverlapIndex(target);
            if (!idx.IsEmpty)
            {
                int before = tiles.Count;
                tiles = tiles.Where(t => !idx.Occupied(t.x, t.y)).ToHashSet();
                skipped = before - tiles.Count;
            }
            if (tiles.Count == 0)
            {
                status = $"{what}: nothing added - that area is inside another region.";
                return;
            }
        }

        // tiles the target ALREADY covers need no new boxes, and the boxes touching the
        // painted area are re-decomposed together with it - so a strip drawn against an
        // existing box fuses into one box instead of stacking a redundant one on top
        int redundant = 0;
        List<RegionRect> replaced = null;
        if (target is { Rects.Count: > 0 })
        {
            var own = new TileIndex(mapW, mapH);
            foreach (var rc in target.Rects) own.Add(rc, target.Name);
            int before = tiles.Count;
            tiles = tiles.Where(t => !own.Occupied(t.x, t.y)).ToHashSet();
            redundant = before - tiles.Count;
            if (tiles.Count == 0)
            {
                status = $"{what}: nothing added - {target.DefName} already covers that area.";
                return;
            }
        }
        var rects = RegionOps.MaskToRectsCompact(tiles);
        if (rects.Count == 0) return;
        if (target is { Rects.Count: > 0 })
        {
            if (FuseIntoRegion(target, tiles, rects) is { } fused)
            {
                rects = fused.rects;
                replaced = fused.replaced;
            }
        }

        bool created = target == null;
        if (created) target = CreateRegionObject();
        int total = target.Rects.Count - (replaced?.Count ?? 0) + rects.Count;
        if (total > maxRectsPerRegion)
        {
            status = $"{what}: would need {total:N0} boxes (cap {maxRectsPerRegion:N0}) - select a smaller area.";
            return;
        }
        undoMgr.Snapshot(created ? $"create {target.DefName}" : $"{what} in {target.DefName}", project, new[] { target });
        if (created) project.Regions.Add(target);
        target.Visible = true;
        if (replaced != null) foreach (var rc in replaced) target.Rects.Remove(rc);
        target.Rects.AddRange(rects);
        MarkDirty(target);
        selReg = target;
        selRect = target.Rects[^1];
        string extra = "";
        if (replaced != null) extra += $", {replaced.Count} existing merged in";
        if (redundant > 0) extra += $", {redundant:N0} tiles already covered";
        if (skipped > 0) extra += $", {skipped:N0} skipped (other regions)";
        status = $"{what}: {target.DefName} now {target.Rects.Count} box(es){extra}";
    }
    void CommitEraseRect(RegionRect cut)
    {
        if (selReg == null) { status = "Select a region first - the eraser cuts from the selected region."; return; }
        ApplyErased(RegionOps.SubtractBox(selReg.Rects, cut), $"erase box in {selReg.DefName}");
    }

    void CommitMaskErase(HashSet<(int x, int y)> tiles, string what)
    {
        if (selReg == null) { status = "Select a region first - the eraser cuts from the selected region."; return; }
        ApplyErased(RegionOps.SubtractMask(selReg.Rects, tiles), $"{what} in {selReg.DefName}");
    }

    // swap in the post-erase rect list. Splitting can GROW the box count, so results
    // past the per-region cap are refused up front (the server rejects oversized puts,
    // which would leave the local copy silently diverged). Snapshot only when applying.
    void ApplyErased(List<RegionRect> newRects, string why)
    {
        if (newRects.Count > maxRectsPerRegion)
        {
            status = $"Erase refused: the region would fragment into {newRects.Count:N0} boxes (cap {maxRectsPerRegion:N0}) - erase a simpler shape.";
            return;
        }
        undoMgr.Snapshot(why, project, new[] { selReg });
        int before = selReg.Rects.Count;
        selReg.Rects.Clear();
        selReg.Rects.AddRange(newRects);
        selRect = selReg.Rects.Count > 0 ? selReg.Rects[^1] : null;
        MarkDirty(selReg);
        status = $"erased: {selReg.DefName} now {selReg.Rects.Count} box(es) (was {before})";
    }

    void DeleteRegion(RegionDef r)
    {
        if (r == null) return;
        if (MessageBox.Show($"Delete region {r.DefName} ({r.Rects.Count} boxes)?", "Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        undoMgr.Snapshot($"delete {r.DefName}", project, new[] { r });
        project.Regions.Remove(r);
        dirtyPush.Remove(r);
        if (pendingRenames.TryGetValue(r, out var oldName) &&
            !oldName.Equals(r.DefName, StringComparison.OrdinalIgnoreCase))
            net?.PushDelete(oldName);
        pendingRenames.Remove(r);
        net?.PushDelete(r.DefName);
        if (selReg == r) { selReg = null; selRect = null; }
    }

    void DuplicateRegion(RegionDef r)
    {
        if (r == null) return;
        string dn = r.DefName + "_COPY";
        for (int n = 2; project.Regions.Any(x => x.DefName.Equals(dn, StringComparison.OrdinalIgnoreCase)); n++)
            dn = r.DefName + "_COPY" + n;
        var copy = new RegionDef
        {
            DefName = dn, Name = r.Name + " copy", Kind = r.Kind,
            Events = r.Events, Flags = r.Flags, Group = r.Group,
            PX = r.PX, PY = r.PY, PZ = r.PZ, MapPlane = r.MapPlane,
            Extra = new List<string>(r.Extra), Comments = new List<string>(r.Comments),
            Rects = r.Rects.Select(t => t.Clone()).ToList(),
        };
        copy.Color = Palette.Next(project.Regions.Count);
        undoMgr.Snapshot($"duplicate {r.DefName}", project, new[] { copy });
        project.Regions.Add(copy);
        MarkDirty(copy);
        selReg = copy;
        selRect = copy.Rects.Count > 0 ? copy.Rects[^1] : null;
    }

    void AutoDefNameFor(RegionDef r)
    {
        var baseName = SphereScp.SanitizeDefName(r.Name, project.Regions.Count).ToUpperInvariant();
        if (!baseName.StartsWith("A_") && !baseName.StartsWith("R_")) baseName = "A_" + baseName;
        var dn = baseName;
        for (int n = 2; project.Regions.Any(x => x != r && x.DefName.Equals(dn, StringComparison.OrdinalIgnoreCase)); n++)
            dn = baseName + "_" + n;
        if (dn.Equals(r.DefName, StringComparison.OrdinalIgnoreCase)) return;
        if (!pendingRenames.ContainsKey(r)) pendingRenames[r] = r.DefName;
        r.DefName = dn;
    }

    void DoUndo() => ApplyUndoResults(undoMgr.Undo(project), "undo");
    void DoRedo() => ApplyUndoResults(undoMgr.Redo(project), "redo");

    void ApplyUndoResults(List<UndoResult> results, string why)
    {
        if (results == null) return;
        AbortDrag();
        if (net is { Connected: true })
        {
            foreach (var res in results)
            {
                if (res.Now != null)
                {
                    var prev = res.DefNameBefore != null &&
                               !res.DefNameBefore.Equals(res.Now.DefName, StringComparison.OrdinalIgnoreCase)
                        ? res.DefNameBefore : "";
                    net.PushRegion(res.Now, prev);
                }
                else if (res.DefNameBefore != null)
                {
                    net.PushDelete(res.DefNameBefore);
                }
            }
        }
        var selUid = selReg?.Uid;
        selReg = selUid == null ? null : project.Regions.FirstOrDefault(r => r.Uid == selUid);
        selRect = selReg?.Rects.Count > 0 ? selReg.Rects[^1] : null;
        foreach (var res in results) dirtyPush.RemoveWhere(r => r.Uid == res.Uid);
        status = $"{why}: {results.Count} region(s) restored";
    }

    // ---- network glue -----------------------------------------------------

    void ConnectNow()
    {
        if (net is { Connected: true })
        {
            ClientLog("info", "disconnect requested by user");
            net.Dispose();
            net = null;
            onlineText = "offline";
            onlineColU = 0xFF909090;
            status = "Disconnected - reconnect, or pick Work offline to keep editing locally.";
            appMode = AppMode.Startup;   // deliberate disconnect -> neutral gate, not "connection lost"
            return;
        }

        activeMulsCache = cMulsCache?.Trim() ?? "";
        var client = new NetClient();
        client.Synced += d => mainQueue.Enqueue(() => { if (net == client) OnServerSync(d); });
        client.RegionPut += ch => mainQueue.Enqueue(() => { if (net == client) OnServerPut(ch); });
        client.RegionDeleted += ch => mainQueue.Enqueue(() => { if (net == client) OnServerDel(ch); });
        client.Reordered += rm => mainQueue.Enqueue(() =>
        {
            if (net != client || rm.BySession == client.Session || awaitingFirstSync) return;
            Project.ApplyOrder(project.Regions, rm.Order);
            status = $"{rm.By} reordered the region list";
        });
        client.Preview += p => mainQueue.Enqueue(() =>
        {
            if (net != client || p.BySession == client.Session) return;
            // never trust the sender's sizes: a rogue server could send a giant Path and
            // freeze our render loop (the DrawPreview relay does not re-clamp per client)
            if (p.Kind is < 0 or > 2) return;
            if (p.Path is { Count: > 4000 }) p.Path.RemoveRange(4000, p.Path.Count - 4000);
            if (p.Kind == 0) remoteDraws.Remove(p.BySession);
            else remoteDraws[p.BySession] = (p, Environment.TickCount64);
        });
        client.Notice += t => mainQueue.Enqueue(() => { if (net == client) status = "SERVER: " + t; });
        client.PosChanged += pm => mainQueue.Enqueue(() =>
        {
            if (net != client || pm.BySession == client.Session) return;
            remotePos[pm.BySession] = (pm.By, Math.Clamp(pm.X, 0, Math.Max(0, boundW - 1)),
                Math.Clamp(pm.Y, 0, Math.Max(0, boundH - 1)), Environment.TickCount64);
        });
        client.UsersChanged += users => mainQueue.Enqueue(() =>
        {
            if (net == client)
            {
                onlineUsers = users;
                onlineText = $"online: {net.User}@{net.HostDisplay}{(net.ReadOnly ? " (viewer)" : "")} ({users.Count})";
            }
        });
        client.Disconnected += reason => mainQueue.Enqueue(() =>
        {
            if (net != client) return;
            onlineText = "connection lost";
            onlineColU = 0xFF3030C8;
            ClientLog("warn", $"connection lost ({reason})");
            status = $"Connection lost ({reason}). Recent edits may not have reached the server.";
            // Gated becomes true -> the start gate reappears with Reconnect / Go offline
        });

        status = $"Connecting to {cHost}:{cPort}...";
        dirtyPush.Clear();
        pendingRenames.Clear();
        pendingIncoming.Clear();
        remoteDraws.Clear();
        remotePos.Clear();
        onlineUsers = new List<string>();
        prevActive = false;
        lastPrevSig = "";
        awaitingFirstSync = true;
        net?.Dispose();
        net = client;
        string host = cHost, user = cUser, pass = cPass;
        int port = cPort;
        Task.Run(async () =>
        {
            var err = await client.ConnectAsync(host, port, user, pass);
            mainQueue.Enqueue(() =>
            {
                if (net != client) { client.Dispose(); return; }
                if (err != null)
                {
                    net = null;
                    awaitingFirstSync = false;
                    status = "Connect failed: " + err;
                    ClientLog("warn", $"connect to {host}:{port} failed: {err}");
                    return;
                }
                appMode = AppMode.Online;
                // adopt the server's operator-configured bounds + box limit
                if (net.MapWidth > 0 && net.MapHeight > 0) { boundW = net.MapWidth; boundH = net.MapHeight; }
                if (net.MaxRects > 0) maxRectsPerRegion = net.MaxRects;
                onlineText = $"online: {net.User}@{net.HostDisplay}{(net.ReadOnly ? " (viewer)" : "")}";
                onlineColU = 0xFF40A040;
                ClientLog("info", $"connected as {net.User}");
                if (net.ReadOnly) status = "Connected as VIEWER - your edits stay local.";
                if (pendingProfileSave != null)
                {
                    var s = LoadNetSettings();
                    s.Profiles.RemoveAll(x => x.Name == pendingProfileSave.Name);
                    s.Profiles.Add(pendingProfileSave);
                    s.LastProfile = pendingProfileSave.Name;
                    SaveNetSettings(s);
                    pendingProfileSave = null;
                }
            });
        });
    }

    void OnServerSync(SyncData data)
    {
        if (!awaitingFirstSync) { AdoptServerRegions(data.Regions); return; }

        // only ROOT (access 255) gets the first-sync choice - everyone else always
        // adopts the server copy, so nobody below root can overwrite the server by
        // mistake on login (their local list is autosaved before being replaced)
        bool root = net is { Access: >= 255 };
        bool adoptServer = true, pushLocal = false;
        if (root && project.Regions.Count > 0 && data.Regions.Count == 0)
        {
            var choice = MessageBox.Show(
                $"The server has no regions yet.\n\nYES = push your {project.Regions.Count} local regions to the server\nNO = disconnect and keep working locally",
                "First sync", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice == DialogResult.No) { awaitingFirstSync = false; ConnectNow(); return; }
            adoptServer = false; pushLocal = true;
        }
        else if (root && project.Regions.Count > 0)
        {
            var choice = MessageBox.Show(
                $"Server has {data.Regions.Count} regions, you have {project.Regions.Count} locally.\n\nYES = use the SERVER copy (local autosaved first)\nNO = PUSH your local regions (overwrites same defnames, keeps the rest)\nCANCEL = disconnect",
                "First sync", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) { awaitingFirstSync = false; ConnectNow(); return; }
            if (choice == DialogResult.No) { adoptServer = false; pushLocal = true; }
        }
        else if (!root && project.Regions.Count > 0)
        {
            status = "Server copy loaded (local list autosaved).";
        }

        awaitingFirstSync = false;
        undoMgr.Clear();
        if (adoptServer)
        {
            if (project.Regions.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(AppDir);
                    project.Save(Path.Combine(AppDir, $"autosave-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
                }
                catch { }
            }
            AdoptServerRegions(data.Regions);
        }
        else if (pushLocal)
        {
            Project.EnsureUids(project.Regions);
            foreach (var r in project.Regions.Where(r => !string.IsNullOrWhiteSpace(r.DefName)))
                net.PushRegion(r);
            foreach (var sr in data.Regions)
                if (!project.Regions.Any(r => r.DefName.Equals(sr.DefName, StringComparison.OrdinalIgnoreCase)))
                    project.Regions.Add(sr);
            Project.EnsureUids(project.Regions);
            status = $"Pushed {project.Regions.Count} regions to the server.";
        }

        var replay = pendingIncoming.ToList();
        pendingIncoming.Clear();
        foreach (var item in replay)
        {
            if (item is ChangePut p) OnServerPut(p);
            else if (item is ChangeDel d) OnServerDel(d);
        }

        StartMulsSync();
    }

    void AdoptServerRegions(List<RegionDef> regions)
    {
        AbortDrag();
        Project.EnsureUids(regions);
        project.Regions = regions;
        var selName = selReg?.DefName;
        selReg = selName == null ? null
            : project.Regions.FirstOrDefault(r => r.DefName.Equals(selName, StringComparison.OrdinalIgnoreCase));
        selRect = selReg?.Rects.Count > 0 ? selReg.Rects[^1] : null;
        dirtyPush.Clear();
        pendingRenames.Clear();
        status = "Synced from server";
    }

    void OnServerPut(ChangePut ch)
    {
        if (ch.Region == null) return;
        if (net != null && ch.BySession == net.Session) return;
        if (awaitingFirstSync) { pendingIncoming.Add(ch); return; }
        Project.EnsureUids(new[] { ch.Region });
        bool affectsSelected = selReg != null &&
            (selReg.DefName.Equals(ch.Region.DefName, StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrEmpty(ch.PrevDefName) && selReg.DefName.Equals(ch.PrevDefName, StringComparison.OrdinalIgnoreCase)));
        // only Move/Resize hold references to existing boxes and go stale when the region
        // is replaced under us; a rubber band or lasso is just tile coordinates - a
        // teammate finishing THEIR box must not cancel a stroke someone else is drawing
        if (affectsSelected && drag is Drag.Move or Drag.Resize) AbortDrag();
        if (!string.IsNullOrEmpty(ch.PrevDefName))
            project.Regions.RemoveAll(r => r.DefName.Equals(ch.PrevDefName, StringComparison.OrdinalIgnoreCase));
        int i = project.Regions.FindIndex(r => r.DefName.Equals(ch.Region.DefName, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) project.Regions[i] = ch.Region;
        else project.Regions.Add(ch.Region);
        if (affectsSelected)
        {
            selReg = ch.Region;
            selRect = ch.Region.Rects.Count > 0 ? ch.Region.Rects[^1] : null;
        }
        status = $"{ch.By} updated {ch.Region.DefName}";
    }

    void OnServerDel(ChangeDel ch)
    {
        if (net != null && ch.BySession == net.Session) return;
        if (awaitingFirstSync) { pendingIncoming.Add(ch); return; }
        int removed = project.Regions.RemoveAll(r => r.DefName.Equals(ch.DefName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        if (selReg != null && selReg.DefName.Equals(ch.DefName, StringComparison.OrdinalIgnoreCase))
        {
            // same rule as puts: keep live rubber/lasso strokes; if the region is gone
            // by the time they commit, the stroke simply lands in a fresh region
            if (drag is Drag.Move or Drag.Resize) AbortDrag();
            selReg = null;
            selRect = null;
        }
        status = $"{ch.By} deleted {ch.DefName}";
    }

    void StartMulsSync()
    {
        var client = net;
        if (client is not { Connected: true }) return;
        // profile can point the download anywhere; default is the per-host cache dir
        string cacheDir = activeMulsCache.Length > 0
            ? activeMulsCache
            : Path.Combine(AppDir, "mulscache",
                string.Join("_", client.HostDisplay.Split(Path.GetInvalidFileNameChars())));
        // release memory-mapped muls FIRST or File.Move onto in-use files fails mid-download
        if (project.MulsDir == cacheDir)
        {
            TearDownArtView();
            ClearChunkTextures();
        }
        mulsSyncing = true;
        mulsFrac = -1;
        mulsProgress = "Checking muls against the server...";
        int gen = ++mulsSyncGen;
        Task.Run(async () =>
        {
            var err = await client.SyncMulsAsync(cacheDir,
                s => mainQueue.Enqueue(() => { if (net == client) { mapLabel = s; mulsProgress = s; } }),
                f => mainQueue.Enqueue(() => { if (gen == mulsSyncGen) mulsFrac = (float)f; }));
            mainQueue.Enqueue(() =>
            {
                if (gen == mulsSyncGen) mulsSyncing = false;
                if (net != client) return;
                if (err == null && MulRadar.FindMap(cacheDir) != null)
                {
                    project.MulsDir = cacheDir;
                    TearDownArtView();
                    ClearChunkTextures();
                    LoadMapAsync(cacheDir, fromServer: true);
                    ClientLog("info", $"muls synced from server into {cacheDir}");
                    if (detailMode) EnsureArtView();
                }
                else if (err == "server has no muls pack")
                {
                    // fall back to whatever local muls we know, but say so loudly
                    bool haveLocal = !string.IsNullOrEmpty(project.MulsDir) && MulRadar.FindMap(project.MulsDir) != null;
                    if (mapTex == 0 && haveLocal)
                        LoadMapAsync(project.MulsDir, fromServer: false);
                    else if (mapTex != 0)
                    {
                        mapLabel = "MULS: LOCAL (server has NO muls pack)";
                        mapLabelCol = 0xFF30A0E0;
                    }
                    else
                    {
                        mapLabel = "NO MULS (server has none, no local folder set)";
                        mapLabelCol = 0xFF3030C8;
                    }
                    ClientLog("warn", haveLocal || mapTex != 0
                        ? "server has no muls pack - using local muls"
                        : "server has no muls pack and no local muls are set - File > Muls folder");
                }
                else if (err != null)
                {
                    mapLabel = "MULS SYNC FAILED: " + err;
                    mapLabelCol = 0xFF3030C8;
                    ClientLog("error", "muls sync failed: " + err);
                }
            });
        });
    }

    // ---- profiles ---------------------------------------------------------

    class ConnectProfile
    {
        public string Name { get; set; } = "default";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 2599;
        public string User { get; set; } = "";
        public string PassB64 { get; set; } = "";
        public string MulsCache { get; set; } = "";   // custom muls download folder; empty = default
        public string Target { get; set; } = "Sphere";   // which server this shard runs
    }

    class NetSettings
    {
        public List<ConnectProfile> Profiles { get; set; } = new();
        public string LastProfile { get; set; } = "";
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string PassB64 { get; set; }
    }

    static string NetSettingsPath => Path.Combine(AppDir, "connect.json");

    class UiSettings
    {
        public int RenderQuality { get; set; } = 10;
        public bool HoverHighlight { get; set; } = true;   // pre-hoverMode files: false = off
        public int HoverMode { get; set; } = 2;
        public uint HoverColor { get; set; } = DefaultHoverColor;
        public string Target { get; set; } = "Sphere";   // offline default; profiles carry their own
    }

    static string UiSettingsPath => Path.Combine(AppDir, "ui.json");

    void LoadUiSettings()
    {
        try
        {
            if (File.Exists(UiSettingsPath))
            {
                var s = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiSettingsPath));
                if (s != null)
                {
                    renderQuality = Math.Clamp(s.RenderQuality, 1, 10);
                    hoverMode = s.HoverHighlight ? Math.Clamp(s.HoverMode, 0, 3) : 0;
                    hoverColor = ColU(s.HoverColor == OldDefaultHoverColor ? DefaultHoverColor : s.HoverColor);
                    scriptTarget = TargetFromName(s.Target);
                }
            }
        }
        catch { }
    }

    void SaveUiSettings()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(UiSettingsPath, JsonSerializer.Serialize(
                new UiSettings
                {
                    RenderQuality = renderQuality, HoverHighlight = hoverMode != 0,
                    HoverMode = hoverMode, HoverColor = UCol(hoverColor),
                    Target = TargetName(scriptTarget),
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static NetSettings LoadNetSettings()
    {
        var s = new NetSettings();
        try
        {
            if (File.Exists(NetSettingsPath))
                s = JsonSerializer.Deserialize<NetSettings>(File.ReadAllText(NetSettingsPath)) ?? s;
        }
        catch { }
        if (s.Profiles.Count == 0 && !string.IsNullOrEmpty(s.Host))
        {
            s.Profiles.Add(new ConnectProfile
            {
                Name = "default", Host = s.Host, Port = s.Port == 0 ? 2599 : s.Port,
                User = s.User ?? "", PassB64 = s.PassB64 ?? "",
            });
            s.LastProfile = "default";
        }
        return s;
    }

    static void SaveNetSettings(NetSettings s)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            s.Host = null; s.User = null; s.PassB64 = null; s.Port = 0;
            File.WriteAllText(NetSettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static string Encode(string s) => string.IsNullOrEmpty(s) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    static string Decode(string s)
    {
        try { return string.IsNullOrEmpty(s) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return ""; }
    }
}
