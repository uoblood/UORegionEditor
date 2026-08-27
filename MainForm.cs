using System.Text;
using System.Text.Json;
using UORegionEditor.Net;

namespace UORegionEditor;

public class MainForm : Form
{
    readonly MapCanvas canvas = new() { Dock = DockStyle.Fill };
    readonly ListView lvRegions = new()
    {
        Dock = DockStyle.Top, Height = 230, View = View.Details, FullRowSelect = true,
        HideSelection = false, CheckBoxes = true, MultiSelect = false,
    };
    readonly ListBox lbRects = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    readonly TextBox tbDefName = new() { Dock = DockStyle.Fill };
    readonly CheckBox chkAutoDef = new() { Text = "auto", AutoSize = true, Checked = true };
    readonly TextBox tbName = new() { Dock = DockStyle.Fill };
    readonly ComboBox cbKind = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox tbGroup = new() { Dock = DockStyle.Fill };
    readonly TextBox tbEvents = new() { Dock = DockStyle.Fill };
    readonly TextBox tbFlags = new() { Dock = DockStyle.Fill };
    readonly Label lblP = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly NumericUpDown numPZ = new() { Minimum = -128, Maximum = 127, Width = 60 };
    readonly TextBox tbExtra = new() { Dock = DockStyle.Fill, Multiline = true, Height = 48, ScrollBars = ScrollBars.Vertical, WordWrap = false };

    readonly ToolStripButton btnSelect = new("Select") { CheckOnClick = false, Checked = true };
    readonly ToolStripButton btnDraw = new("Draw box") { CheckOnClick = false };
    readonly ToolStripButton btnCorners = new("4 corners") { CheckOnClick = false };
    readonly ToolStripButton btnAddTo = new("Add to selected region") { CheckOnClick = true, Checked = true };
    readonly ToolStripButton btnPlusOne = new("Sphere +1 edge") { CheckOnClick = true, Checked = true };
    readonly ToolStripButton btnDetail = new("CentrED view") { CheckOnClick = true };
    readonly ToolStripTextBox tbMinZ = new() { Text = "-128", ToolTipText = "Min Z (detail view)" };
    readonly ToolStripTextBox tbMaxZ = new() { Text = "127", ToolTipText = "Max Z (detail view)" };
    DetailRenderer detailRenderer;

    readonly ToolStripStatusLabel lblStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel lblOnline = new() { ForeColor = Color.Gray, Text = "offline" };
    readonly ToolStripStatusLabel lblMap = new();

    readonly ImageList swatches = new() { ImageSize = new Size(12, 12) };

    readonly ToolStripButton btnUndo = new("Undo") { Enabled = false };
    readonly ToolStripButton btnRedo = new("Redo") { Enabled = false };
    readonly ToolStripDropDownButton ddHistory = new("History");
    readonly ToolStripButton btnConnect = new("Connect...");

    readonly UndoManager undoMgr = new();
    NetClient net;
    readonly HashSet<RegionDef> dirtyPush = new();
    readonly Dictionary<RegionDef, string> pendingRenames = new();
    readonly System.Windows.Forms.Timer pushTimer = new() { Interval = 700 };
    bool awaitingFirstSync;
    ConnectProfile wfPendingProfile;

    Project project = new();
    bool syncing;
    string projectPath;

    static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UORegionEditor");
    static string LastProjectPath => Path.Combine(AppDir, "last-project.json");

    public MainForm()
    {
        Text = "UORegionEditor";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1400, 860);
        MinimumSize = new Size(900, 600);

        cbKind.Items.AddRange(new object[] { "AREADEF", "ROOMDEF" });
        lvRegions.Columns.Add("Defname", 130);
        lvRegions.Columns.Add("Name", 110);
        lvRegions.Columns.Add("Boxes", 45);
        lvRegions.SmallImageList = swatches;

        var strip = BuildToolStrip();
        var status = new StatusStrip();
        status.Items.Add(lblStatus);
        status.Items.Add(lblOnline);
        status.Items.Add(lblMap);

        KeyPreview = true;
        KeyDown += OnFormKeyDown;
        undoMgr.Changed += () =>
        {
            btnUndo.Enabled = undoMgr.CanUndo;
            btnRedo.Enabled = undoMgr.CanRedo;
            btnUndo.ToolTipText = undoMgr.CanUndo ? "Undo: " + undoMgr.UndoDesc : "";
            btnRedo.ToolTipText = undoMgr.CanRedo ? "Redo: " + undoMgr.RedoDesc : "";
        };
        pushTimer.Tick += (_, _) => PushDirtyRegions();
        pushTimer.Start();

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(canvas);
        split.Panel2.Controls.Add(BuildSidePanel());
        Controls.Add(split);
        Controls.Add(status);
        Controls.Add(strip);
        Load += (_, _) => split.SplitterDistance = Math.Max(300, split.Width - 360);

        WireCanvas();
        WireProps();

        Load += async (_, _) => await StartupAsync();
        FormClosing += (_, _) =>
        {
            try { PushDirtyRegions(); } catch { }
            try { net?.Dispose(); } catch { }
            try { detailRenderer?.Dispose(); } catch { }
            try { Directory.CreateDirectory(AppDir); project.Save(LastProjectPath); } catch { }
        };
    }

    ToolStrip BuildToolStrip()
    {
        var strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

        ToolStripButton B(string text, EventHandler onClick)
        {
            var b = new ToolStripButton(text);
            b.Click += onClick;
            strip.Items.Add(b);
            return b;
        }

        B("Muls...", (_, _) => PickMuls());
        B("Fit", (_, _) => canvas.ZoomFit());
        strip.Items.Add(new ToolStripSeparator());
        btnUndo.Click += (_, _) => DoUndo();
        btnRedo.Click += (_, _) => DoRedo();
        ddHistory.DropDownOpening += (_, _) => RebuildHistoryMenu();
        strip.Items.Add(btnUndo);
        strip.Items.Add(btnRedo);
        strip.Items.Add(ddHistory);
        strip.Items.Add(new ToolStripSeparator());
        btnConnect.Click += (_, _) => ConnectClicked();
        strip.Items.Add(btnConnect);
        strip.Items.Add(new ToolStripSeparator());
        B("Open...", (_, _) => OpenProject());
        B("Save", (_, _) => SaveProject(false));
        B("Save As...", (_, _) => SaveProject(true));
        strip.Items.Add(new ToolStripSeparator());
        B("Import SCP...", (_, _) => ImportScp());
        B("Import CentrED...", (_, _) => ImportCentred());
        strip.Items.Add(new ToolStripSeparator());
        B("Export Sphere...", (_, _) => ExportSphere());
        B("Export CentrED...", (_, _) => ExportCentred());
        B("Merge cedserver...", (_, _) => MergeCentred());
        strip.Items.Add(new ToolStripSeparator());

        btnSelect.Click += (_, _) => SetMode(CanvasMode.Select);
        btnDraw.Click += (_, _) => SetMode(CanvasMode.Draw);
        btnCorners.Click += (_, _) => SetMode(CanvasMode.Corners);
        strip.Items.Add(btnSelect);
        strip.Items.Add(btnDraw);
        strip.Items.Add(btnCorners);
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(btnAddTo);
        strip.Items.Add(btnPlusOne);
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(btnDetail);
        strip.Items.Add(new ToolStripLabel("Z:"));
        tbMinZ.Size = new Size(40, 24);
        tbMaxZ.Size = new Size(40, 24);
        strip.Items.Add(tbMinZ);
        strip.Items.Add(tbMaxZ);
        btnAddTo.CheckedChanged += (_, _) => canvas.AddToSelected = btnAddTo.Checked;
        btnPlusOne.CheckedChanged += (_, _) => project.SphereExclusiveEdge = btnPlusOne.Checked;
        btnDetail.CheckedChanged += (_, _) =>
        {
            if (btnDetail.Checked) EnableDetailView();
            else { canvas.DetailMode = false; canvas.Invalidate(); }
        };
        tbMinZ.Leave += (_, _) => ApplyZFilter();
        tbMaxZ.Leave += (_, _) => ApplyZFilter();
        tbMinZ.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyZFilter(); e.SuppressKeyPress = true; } };
        tbMaxZ.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyZFilter(); e.SuppressKeyPress = true; } };
        return strip;
    }

    Control BuildSidePanel()
    {
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

        var props = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 0),
        };
        props.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        props.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control c)
        {
            props.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) };
            props.Controls.Add(l);
            props.Controls.Add(c);
        }

        var defPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        defPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        defPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        defPanel.Controls.Add(tbDefName);
        defPanel.Controls.Add(chkAutoDef);
        Row("Defname", defPanel);
        Row("Name", tbName);
        Row("Type", cbKind);
        Row("Group", tbGroup);
        Row("Events", tbEvents);
        Row("Flags", tbFlags);

        var pPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        var btnPickP = new Button { Text = "Pick P", AutoSize = true };
        var btnAutoP = new Button { Text = "P = auto", AutoSize = true };
        btnPickP.Click += (_, _) =>
        {
            if (canvas.SelectedRegion == null) return;
            canvas.PickTileMode = true;
            lblStatus.Text = "Click on the map to place P...";
        };
        btnAutoP.Click += (_, _) =>
        {
            var r = canvas.SelectedRegion;
            if (r == null) return;
            undoMgr.Snapshot($"auto P of {r.DefName}", project, new[] { r });
            r.PX = -1; r.PY = -1;
            MarkDirty(r);
            RefreshPLabel(); canvas.Invalidate();
        };
        pPanel.Controls.Add(lblP);
        pPanel.Controls.Add(new Label { Text = "Z:", AutoSize = true, Margin = new Padding(8, 6, 2, 0) });
        pPanel.Controls.Add(numPZ);
        pPanel.Controls.Add(btnPickP);
        pPanel.Controls.Add(btnAutoP);
        Row("P point", pPanel);
        Row("Extra", tbExtra);

        var btns = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        Button BB(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += onClick;
            btns.Controls.Add(b);
            return b;
        }
        BB("New region", (_, _) => { var r = NewRegion(); SelectRegion(r, null); });
        BB("Duplicate", (_, _) => DuplicateRegion());
        BB("Delete", (_, _) => DeleteRegion());
        BB("Color", (_, _) => PickColor());
        BB("Zoom to", (_, _) => canvas.ZoomToRegion(canvas.SelectedRegion));

        var rectHeader = new Label { Text = "Boxes of the selected region (Del key removes the selected box):", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 2) };
        var btnDelRect = new Button { Text = "Remove box", AutoSize = true, Dock = DockStyle.Bottom };
        btnDelRect.Click += (_, _) => DeleteSelectedRect();

        var rectPanel = new Panel { Dock = DockStyle.Fill };
        rectPanel.Controls.Add(lbRects);
        rectPanel.Controls.Add(btnDelRect);

        root.Controls.Add(rectPanel);
        root.Controls.Add(rectHeader);
        root.Controls.Add(btns);
        root.Controls.Add(props);
        root.Controls.Add(lvRegions);
        return root;
    }

    void SetMode(CanvasMode m)
    {
        canvas.Mode = m;
        btnSelect.Checked = m == CanvasMode.Select;
        btnDraw.Checked = m == CanvasMode.Draw;
        btnCorners.Checked = m == CanvasMode.Corners;
    }

    // ---- canvas events ----------------------------------------------------

    void WireCanvas()
    {
        canvas.Project = project;
        canvas.Status += s => lblStatus.Text = s;

        canvas.EditStarting += (desc, key) =>
            undoMgr.Snapshot(desc, project, new[] { canvas.SelectedRegion }, coalesceKey: key);

        canvas.RectDrawn += rr =>
        {
            RegionDef target = btnAddTo.Checked ? canvas.SelectedRegion : null;
            bool created = target == null;
            if (created) target = CreateRegionObject();
            undoMgr.Snapshot(created ? $"create {target.DefName}" : $"draw box in {target.DefName}", project, new[] { target });
            if (created)
            {
                project.Regions.Add(target);
                RefreshRegionList();
            }
            target.Visible = true;   // never draw into an invisible region
            target.Rects.Add(rr);
            MarkDirty(target);
            SelectRegion(target, rr);
        };

        canvas.SelectionChanged += () =>
        {
            SelectRegion(canvas.SelectedRegion, canvas.SelectedRect, fromCanvas: true);
        };

        canvas.RegionsChanged += () =>
        {
            RefreshRectList();
            RefreshRegionRow(canvas.SelectedRegion);
            RefreshPLabel();
            MarkDirty(canvas.SelectedRegion);
        };

        canvas.TilePicked += tile =>
        {
            var r = canvas.SelectedRegion;
            if (r == null) return;
            undoMgr.Snapshot($"set P of {r.DefName}", project, new[] { r });
            MarkDirty(r);
            r.PX = tile.X; r.PY = tile.Y;
            RefreshPLabel();
            canvas.Invalidate();
            lblStatus.Text = $"P set to {tile.X},{tile.Y}";
        };
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

    RegionDef NewRegion()
    {
        var reg = CreateRegionObject();
        undoMgr.Snapshot($"create {reg.DefName}", project, new[] { reg });
        project.Regions.Add(reg);
        MarkDirty(reg);
        RefreshRegionList();
        return reg;
    }

    // ---- region list ------------------------------------------------------

    void RefreshRegionList()
    {
        syncing = true;
        lvRegions.BeginUpdate();
        lvRegions.Items.Clear();
        swatches.Images.Clear();
        for (int i = 0; i < project.Regions.Count; i++)
        {
            var r = project.Regions[i];
            var bmp = new Bitmap(12, 12);
            using (var g = Graphics.FromImage(bmp)) g.Clear(r.Color);
            swatches.Images.Add(bmp);
            var item = new ListViewItem(r.DefName) { Tag = r, Checked = r.Visible, ImageIndex = i };
            item.SubItems.Add(r.Name);
            item.SubItems.Add(r.Rects.Count.ToString());
            lvRegions.Items.Add(item);
        }
        lvRegions.EndUpdate();
        syncing = false;
    }

    void RefreshRegionRow(RegionDef r)
    {
        if (r == null) return;
        foreach (ListViewItem item in lvRegions.Items)
        {
            if (item.Tag == r)
            {
                syncing = true;
                item.Text = r.DefName;
                item.SubItems[1].Text = r.Name;
                item.SubItems[2].Text = r.Rects.Count.ToString();
                item.Checked = r.Visible;
                syncing = false;
                break;
            }
        }
    }

    void SelectRegion(RegionDef r, RegionRect rect, bool fromCanvas = false)
    {
        if (canvas.SelectedRegion != r) canvas.PickTileMode = false;   // stale Pick P must not hit another region
        canvas.SelectedRegion = r;
        canvas.SelectedRect = rect ?? (r?.Rects.Count > 0 ? r.Rects[^1] : null);
        syncing = true;
        foreach (ListViewItem item in lvRegions.Items)
            item.Selected = item.Tag == r;
        if (lvRegions.SelectedItems.Count > 0) lvRegions.SelectedItems[0].EnsureVisible();
        syncing = false;
        LoadProps();
        RefreshRectList();
        RefreshRegionRow(r);
        canvas.Invalidate();
    }

    void RefreshRectList()
    {
        var r = canvas.SelectedRegion;
        syncing = true;
        lbRects.Items.Clear();
        if (r != null)
            foreach (var rc in r.Rects) lbRects.Items.Add(rc.ToString());
        if (r != null && canvas.SelectedRect != null)
        {
            int i = r.Rects.IndexOf(canvas.SelectedRect);
            if (i >= 0) lbRects.SelectedIndex = i;
        }
        syncing = false;
    }

    // ---- properties panel -------------------------------------------------

    void WireProps()
    {
        lvRegions.SelectedIndexChanged += (_, _) =>
        {
            if (syncing) return;
            var r = lvRegions.SelectedItems.Count > 0 ? (RegionDef)lvRegions.SelectedItems[0].Tag : null;
            SelectRegion(r, null);
        };
        lvRegions.ItemChecked += (_, e) =>
        {
            if (syncing) return;
            if (e.Item.Tag is RegionDef r)
            {
                undoMgr.Snapshot($"{(e.Item.Checked ? "show" : "hide")} {r.DefName}", project, new[] { r },
                    coalesceKey: "visible|" + r.Uid);
                r.Visible = e.Item.Checked;
                MarkDirty(r);
                canvas.Invalidate();
            }
        };
        lvRegions.DoubleClick += (_, _) => canvas.ZoomToRegion(canvas.SelectedRegion);

        lbRects.SelectedIndexChanged += (_, _) =>
        {
            if (syncing) return;
            var r = canvas.SelectedRegion;
            if (r != null && lbRects.SelectedIndex >= 0 && lbRects.SelectedIndex < r.Rects.Count)
            {
                canvas.SelectedRect = r.Rects[lbRects.SelectedIndex];
                canvas.Invalidate();
            }
        };

        void OnProp(string field, Action<RegionDef> apply)
        {
            if (syncing) return;
            var r = canvas.SelectedRegion;
            if (r == null) return;
            undoMgr.Snapshot($"edit {field} of {r.DefName}", project, new[] { r }, coalesceKey: $"{field}|{r.Uid}");
            if (field == "defname")
            {
                // remember the original name so the server can rename instead of duplicating
                if (!pendingRenames.ContainsKey(r)) pendingRenames[r] = r.DefName;
            }
            apply(r);
            MarkDirty(r);
            RefreshRegionRow(r);
            canvas.Invalidate();
        }

        tbDefName.TextChanged += (_, _) => OnProp("defname", r =>
            r.DefName = tbDefName.Text.Trim().Replace(' ', '_').Replace('\t', '_'));
        tbName.TextChanged += (_, _) => OnProp("name", r =>
        {
            r.Name = tbName.Text;
            if (chkAutoDef.Checked) AutoDefNameFor(r);
        });
        chkAutoDef.CheckedChanged += (_, _) =>
        {
            tbDefName.ReadOnly = chkAutoDef.Checked;
            if (chkAutoDef.Checked && canvas.SelectedRegion != null && !syncing)
            {
                var r = canvas.SelectedRegion;
                undoMgr.Snapshot($"auto defname of {r.DefName}", project, new[] { r });
                AutoDefNameFor(r);
                MarkDirty(r);
                RefreshRegionRow(r);
            }
        };
        tbDefName.ReadOnly = chkAutoDef.Checked;
        cbKind.SelectedIndexChanged += (_, _) => OnProp("type", r => r.Kind = cbKind.SelectedItem?.ToString() ?? "AREADEF");
        tbGroup.TextChanged += (_, _) => OnProp("group", r => r.Group = tbGroup.Text);
        tbEvents.TextChanged += (_, _) => OnProp("events", r => r.Events = tbEvents.Text);
        tbFlags.TextChanged += (_, _) => OnProp("flags", r => r.Flags = tbFlags.Text);
        numPZ.ValueChanged += (_, _) => OnProp("z", r => r.PZ = (int)numPZ.Value);
        tbExtra.TextChanged += (_, _) => OnProp("extra", r =>
        {
            r.Extra = tbExtra.Text.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Trim().Length > 0).ToList();
        });
    }

    void LoadProps()
    {
        var r = canvas.SelectedRegion;
        syncing = true;
        bool en = r != null;
        tbDefName.Enabled = tbName.Enabled = cbKind.Enabled = tbGroup.Enabled = en;
        tbEvents.Enabled = tbFlags.Enabled = numPZ.Enabled = tbExtra.Enabled = en;
        tbDefName.Text = r?.DefName ?? "";
        tbName.Text = r?.Name ?? "";
        cbKind.SelectedItem = r?.Kind ?? "AREADEF";
        tbGroup.Text = r?.Group ?? "";
        tbEvents.Text = r?.Events ?? "";
        tbFlags.Text = r?.Flags ?? "";
        numPZ.Value = Math.Clamp(r?.PZ ?? 0, -128, 127);
        tbExtra.Text = r == null ? "" : string.Join(Environment.NewLine, r.Extra);
        syncing = false;
        RefreshPLabel();
    }

    // Defname follows the display name: "Town Square 8" -> A_TOWN_SQUARE_8 (uniquified).
    // Rename bookkeeping (pendingRenames) makes the server treat it as a rename, not a copy.
    void AutoDefNameFor(RegionDef r)
    {
        var baseName = SphereScp.SanitizeDefName(r.Name, project.Regions.Count).ToUpperInvariant();
        if (!baseName.StartsWith("A_") && !baseName.StartsWith("R_")) baseName = "A_" + baseName;
        var dn = baseName;
        for (int n = 2; project.Regions.Any(x => x != r && x.DefName.Equals(dn, StringComparison.OrdinalIgnoreCase)); n++)
            dn = baseName + "_" + n;
        if (dn.Equals(r.DefName, StringComparison.OrdinalIgnoreCase)) return;
        if (net is { Connected: true } && !pendingRenames.ContainsKey(r)) pendingRenames[r] = r.DefName;
        r.DefName = dn;
        bool was = syncing;
        syncing = true;
        tbDefName.Text = dn;
        syncing = was;
    }

    void RefreshPLabel()
    {
        var r = canvas.SelectedRegion;
        if (r == null) { lblP.Text = "-"; return; }
        var (px, py) = r.EffectiveP();
        lblP.Text = $"{px},{py}" + (r.PX < 0 ? " (auto)" : "");
    }

    void DuplicateRegion()
    {
        var r = canvas.SelectedRegion;
        if (r == null) return;
        string dn = r.DefName + "_COPY";
        for (int n = 2; project.Regions.Any(x => x.DefName.Equals(dn, StringComparison.OrdinalIgnoreCase)); n++)
            dn = r.DefName + "_COPY" + n;
        var copy = new RegionDef
        {
            DefName = dn, Name = r.Name + " copy", Kind = r.Kind,
            Events = r.Events, Flags = r.Flags, Group = r.Group,
            PX = r.PX, PY = r.PY, PZ = r.PZ,
            Extra = new List<string>(r.Extra), Comments = new List<string>(r.Comments),
            Rects = r.Rects.Select(t => t.Clone()).ToList(),
        };
        copy.Color = Palette.Next(project.Regions.Count);
        undoMgr.Snapshot($"duplicate {r.DefName}", project, new[] { copy });
        project.Regions.Add(copy);
        MarkDirty(copy);
        RefreshRegionList();
        SelectRegion(copy, null);
    }

    void DeleteRegion()
    {
        var r = canvas.SelectedRegion;
        if (r == null) return;
        if (MessageBox.Show(this, $"Delete region {r.DefName} ({r.Rects.Count} boxes)?", "Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        undoMgr.Snapshot($"delete {r.DefName}", project, new[] { r });
        project.Regions.Remove(r);
        dirtyPush.Remove(r);
        if (pendingRenames.TryGetValue(r, out var oldName) &&
            !oldName.Equals(r.DefName, StringComparison.OrdinalIgnoreCase))
            net?.PushDelete(oldName);   // the server still knows it under the pre-rename name
        pendingRenames.Remove(r);
        net?.PushDelete(r.DefName);
        canvas.SelectedRegion = null;
        canvas.SelectedRect = null;
        RefreshRegionList();
        LoadProps();
        RefreshRectList();
        canvas.Invalidate();
    }

    void DeleteSelectedRect()
    {
        var r = canvas.SelectedRegion;
        if (r == null || canvas.SelectedRect == null) return;
        undoMgr.Snapshot($"remove box from {r.DefName}", project, new[] { r });
        MarkDirty(r);
        r.Rects.Remove(canvas.SelectedRect);
        canvas.SelectedRect = r.Rects.Count > 0 ? r.Rects[^1] : null;
        RefreshRectList();
        RefreshRegionRow(r);
        canvas.Invalidate();
    }

    void PickColor()
    {
        var r = canvas.SelectedRegion;
        if (r == null) return;
        using var dlg = new ColorDialog { Color = r.Color, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            undoMgr.Snapshot($"recolor {r.DefName}", project, new[] { r });
            r.Color = dlg.Color;
            MarkDirty(r);
            RefreshRegionList();
            SelectRegion(r, canvas.SelectedRect);
        }
    }

    // ---- startup / muls ---------------------------------------------------

    async Task StartupAsync()
    {
        try
        {
            if (File.Exists(LastProjectPath))
            {
                project = Project.Load(LastProjectPath);
                projectPath = LastProjectPath;
            }
        }
        catch { project = new Project(); }

        canvas.Project = project;
        btnPlusOne.Checked = project.SphereExclusiveEdge;
        RefreshRegionList();
        LoadProps();

        if (string.IsNullOrEmpty(project.MulsDir) || !Directory.Exists(project.MulsDir))
        {
            // a "muls" folder beside the exe is the one portable convention (the server
            // uses the same default); anything else is picked through File > Muls...
            var cand = Path.Combine(AppContext.BaseDirectory, "muls");
            if (Directory.Exists(cand) && MulRadar.FindMap(cand) != null) project.MulsDir = cand;
        }
        if (!string.IsNullOrEmpty(project.MulsDir))
            await LoadMapAsync(project.MulsDir);
    }

    void PickMuls()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick the folder with map0LegacyMUL.uop / statics0.mul / radarcol.mul",
            SelectedPath = Directory.Exists(project.MulsDir) ? project.MulsDir : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            project.MulsDir = dlg.SelectedPath;
            detailRenderer?.Dispose();
            detailRenderer = null;
            canvas.DetailChunkProvider = null;
            _ = LoadMapAsync(dlg.SelectedPath);
            if (btnDetail.Checked) EnableDetailView();
        }
    }

    int mapLoadGen;

    async Task LoadMapAsync(string dir)
    {
        int gen = ++mapLoadGen;
        lblMap.Text = "Loading map...";
        try
        {
            var bmp = await Task.Run(() => MulRadar.LoadOrRender(dir, s => BeginInvoke(() =>
            {
                if (gen == mapLoadGen) lblMap.Text = s;
            })));
            if (gen != mapLoadGen) { bmp.Dispose(); return; }   // a newer load superseded this one
            canvas.SetMap(bmp);
            lblMap.Text = $"{Path.GetFileName(dir)}  {bmp.Width}x{bmp.Height}";
        }
        catch (Exception ex)
        {
            if (gen != mapLoadGen) return;
            lblMap.Text = "Map load failed";
            MessageBox.Show(this, ex.Message, "Map load failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- project ----------------------------------------------------------

    void OpenProject()
    {
        if (project.Regions.Count > 0)
        {
            if (MessageBox.Show(this,
                    "Opening a project replaces the current one.\n\n" +
                    "A safety copy of the current work is written to the autosave folder first. Continue?",
                    "Open project", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            try
            {
                Directory.CreateDirectory(AppDir);
                project.Save(Path.Combine(AppDir, $"autosave-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
                foreach (var old in Directory.GetFiles(AppDir, "autosave-*.json").OrderByDescending(f => f).Skip(10))
                    File.Delete(old);
            }
            catch { /* safety copy is best-effort */ }
        }
        using var dlg = new OpenFileDialog { Filter = "Region project (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            project = Project.Load(dlg.FileName);
            projectPath = dlg.FileName;
            undoMgr.Clear();
            dirtyPush.Clear();
            pendingRenames.Clear();
            canvas.Project = project;
            canvas.SelectedRegion = null; canvas.SelectedRect = null;
            btnPlusOne.Checked = project.SphereExclusiveEdge;
            RefreshRegionList(); LoadProps(); RefreshRectList();
            canvas.Invalidate();
            if (!string.IsNullOrEmpty(project.MulsDir) && Directory.Exists(project.MulsDir))
                _ = LoadMapAsync(project.MulsDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void SaveProject(bool saveAs)
    {
        if (saveAs || string.IsNullOrEmpty(projectPath) || projectPath == LastProjectPath)
        {
            using var dlg = new SaveFileDialog { Filter = "Region project (*.json)|*.json", FileName = "regions.json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            projectPath = dlg.FileName;
        }
        try
        {
            project.Save(projectPath);
            lblStatus.Text = $"Saved {projectPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- import / export --------------------------------------------------

    void ImportScp()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Sphere scripts (*.scp)|*.scp|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var allRegions = new List<RegionDef>();
        var allOther = new List<string>();
        var failed = new List<string>();
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var res = SphereScp.Import(File.ReadAllText(f), project.SphereExclusiveEdge,
                    project.Regions.Count + allRegions.Count);
                allRegions.AddRange(res.Regions);
                allOther.AddRange(res.OtherSections);
            }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(f)} ({ex.Message})"); }
        }
        if (allRegions.Count == 0 && allOther.Count == 0)
        {
            lblStatus.Text = failed.Count > 0 ? "Import failed: " + string.Join("; ", failed) : "No regions found in the selected files.";
            return;
        }
        undoMgr.Snapshot($"import {dlg.FileNames.Length} file(s)", project, allRegions, includeExtraSections: true);
        project.Regions.AddRange(allRegions);
        foreach (var r in allRegions) MarkDirty(r);
        project.ExtraSections.AddRange(allOther);
        RefreshRegionList();
        canvas.Invalidate();
        lblStatus.Text = $"Imported {allRegions.Count} regions ({allRegions.Sum(r => r.Rects.Count)} boxes) from {dlg.FileNames.Length} file(s)" +
            (allOther.Count > 0 ? $" + {allOther.Count} kept block(s)" : "") +
            (failed.Count > 0 ? $"  |  FAILED: {string.Join("; ", failed)}" : "");
    }

    void ImportCentred()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "cedserver.xml|*.xml|All files|*.*",
            InitialDirectory = "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var list = CentredXml.ImportFromConfig(dlg.FileName, project.Regions.Count);
            undoMgr.Snapshot($"import {Path.GetFileName(dlg.FileName)}", project, list);
            project.Regions.AddRange(list);
            foreach (var r in list) MarkDirty(r);
            RefreshRegionList();
            canvas.Invalidate();
            lblStatus.Text = $"Imported {list.Count} CentrED regions from {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void ExportSphere()
    {
        if (project.Regions.Count == 0) { lblStatus.Text = "Nothing to export."; return; }
        using var dlg = new SaveFileDialog { Filter = "Sphere script (*.scp)|*.scp", FileName = "uore_areas.scp" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, SphereScp.Export(project, project.Regions), new UTF8Encoding(true));
            lblStatus.Text = $"Exported {project.Regions.Count} regions to {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void ExportCentred()
    {
        if (project.Regions.Count == 0) { lblStatus.Text = "Nothing to export."; return; }
        using var dlg = new SaveFileDialog { Filter = "XML snippet (*.xml)|*.xml", FileName = "uore_regions.xml" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var snippet = CentredXml.ExportSnippet(project.Regions);
            File.WriteAllText(dlg.FileName, snippet, new UTF8Encoding(false));
            try { Clipboard.SetText(snippet); } catch { }
            lblStatus.Text = $"Exported CentrED snippet (also on clipboard). Paste it over <Regions/> in cedserver.xml, or use Merge.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void MergeCentred()
    {
        if (project.Regions.Count == 0) { lblStatus.Text = "Nothing to merge."; return; }
        if (MessageBox.Show(this,
                "Merge all regions into a cedserver.xml.\n\n" +
                "IMPORTANT: stop the CentrED server first - it rewrites its config on shutdown " +
                "and would overwrite this change.\n\nA timestamped backup of the file is created. Continue?",
                "Merge into cedserver.xml", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        using var dlg = new OpenFileDialog
        {
            Filter = "cedserver.xml|*.xml",
            InitialDirectory = "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var msg = CentredXml.MergeIntoConfig(dlg.FileName, project.Regions);
            MessageBox.Show(this, msg, "Merge done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Merge failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- undo / redo / history --------------------------------------------

    void OnFormKeyDown(object sender, KeyEventArgs e)
    {
        // let text fields keep their own Ctrl+Z, and never undo mid-drag
        var ac = ActiveControl;
        while (ac is ContainerControl cc && cc.ActiveControl != null) ac = cc.ActiveControl;
        if (ac is TextBoxBase or NumericUpDown or ComboBox) return;
        if (canvas.IsDragging) return;

        if (e.Control && e.KeyCode == Keys.Z && !e.Shift) { DoUndo(); e.Handled = true; e.SuppressKeyPress = true; }
        else if ((e.Control && e.KeyCode == Keys.Y) || (e.Control && e.Shift && e.KeyCode == Keys.Z))
        { DoRedo(); e.Handled = true; e.SuppressKeyPress = true; }
    }

    void DoUndo() => ApplyUndoResults(undoMgr.Undo(project), "undo");
    void DoRedo() => ApplyUndoResults(undoMgr.Redo(project), "redo");

    // Restore only the regions the undone operation touched, and push exactly those.
    void ApplyUndoResults(List<UndoResult> results, string why)
    {
        if (results == null) return;
        canvas.AbortDrag();
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
        // selection may point at replaced/removed objects - re-resolve by uid
        var selUid = canvas.SelectedRegion?.Uid;
        canvas.SelectedRegion = selUid == null ? null : project.Regions.FirstOrDefault(r => r.Uid == selUid);
        canvas.SelectedRect = canvas.SelectedRegion?.Rects.Count > 0 ? canvas.SelectedRegion.Rects[^1] : null;
        foreach (var res in results) { dirtyPush.RemoveWhere(r => r.Uid == res.Uid); }
        RefreshRegionList();
        LoadProps();
        RefreshRectList();
        canvas.Invalidate();
        lblStatus.Text = $"{why}: {results.Count} region(s) restored";
    }

    void RebuildHistoryMenu()
    {
        ddHistory.DropDownItems.Clear();
        int steps = 0;
        foreach (var entry in undoMgr.History.Take(30))
        {
            int n = ++steps;
            var item = new ToolStripMenuItem(entry);
            item.Click += (_, _) => { for (int i = 0; i < n; i++) DoUndo(); };
            ddHistory.DropDownItems.Add(item);
        }
        if (ddHistory.DropDownItems.Count == 0)
            ddHistory.DropDownItems.Add(new ToolStripMenuItem("(no history yet)") { Enabled = false });
    }

    // Adopt a full region list from the server (initial sync / pull). Never pushes.
    void ApplyRegionsList(List<RegionDef> newList, string why)
    {
        canvas.AbortDrag();
        Project.EnsureUids(newList);
        project.Regions = newList;
        canvas.Project = project;
        var selName = canvas.SelectedRegion?.DefName;
        canvas.SelectedRegion = selName == null ? null
            : project.Regions.FirstOrDefault(r => r.DefName.Equals(selName, StringComparison.OrdinalIgnoreCase));
        canvas.SelectedRect = canvas.SelectedRegion?.Rects.Count > 0 ? canvas.SelectedRegion.Rects[^1] : null;
        dirtyPush.Clear();
        pendingRenames.Clear();
        RefreshRegionList();
        LoadProps();
        RefreshRectList();
        canvas.Invalidate();
        lblStatus.Text = "Synced from server";
    }

    // ---- network ----------------------------------------------------------

    void MarkDirty(RegionDef r)
    {
        if (r == null || net is not { Connected: true }) return;
        if (net.ReadOnly) return;   // viewer accounts keep edits local; the server would refuse them anyway
        dirtyPush.Add(r);
    }

    void PushDirtyRegions()
    {
        if (net is not { Connected: true } || dirtyPush.Count == 0) return;
        foreach (var r in dirtyPush.ToList())
        {
            if (!project.Regions.Contains(r)) { dirtyPush.Remove(r); continue; }
            // don't push half-typed defnames: wait until the field loses focus / typing stops
            if (r == canvas.SelectedRegion && tbDefName.Focused) continue;
            if (string.IsNullOrWhiteSpace(r.DefName)) continue;
            pendingRenames.TryGetValue(r, out var prev);
            if (prev != null && prev.Equals(r.DefName, StringComparison.OrdinalIgnoreCase)) prev = null;
            net.PushRegion(r, prev ?? "");
            pendingRenames.Remove(r);
            dirtyPush.Remove(r);
        }
    }

    class ConnectProfile
    {
        public string Name { get; set; } = "default";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 2599;
        public string User { get; set; } = "";
        public string PassB64 { get; set; } = "";
    }

    class NetSettings
    {
        public List<ConnectProfile> Profiles { get; set; } = new();
        public string LastProfile { get; set; } = "";
        // legacy single-connection fields (migrated on load)
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string PassB64 { get; set; }
    }

    static string NetSettingsPath => Path.Combine(AppDir, "connect.json");

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
            s.Host = null; s.User = null; s.PassB64 = null; s.Port = 0;   // drop legacy fields
            File.WriteAllText(NetSettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    void ConnectClicked()
    {
        if (net is { Connected: true })
        {
            net.Dispose();
            net = null;
            lblOnline.Text = "offline";
            lblOnline.ForeColor = Color.Gray;
            btnConnect.Text = "Connect...";
            lblStatus.Text = "Disconnected. Your local copy stays editable.";
            return;
        }

        var s = LoadNetSettings();

        using var dlg = new Form
        {
            Text = "Connect to region server",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 230),
            MinimizeBox = false, MaximizeBox = false,
        };
        var cbProfile = new ComboBox { Left = 100, Top = 10, Width = 130, DropDownStyle = ComboBoxStyle.DropDown };
        var btnSaveProf = new Button { Text = "Save", Left = 234, Top = 9, Width = 44 };
        var btnDelProf = new Button { Text = "Del", Left = 281, Top = 9, Width = 40 };
        var tbHost = new TextBox { Left = 100, Top = 44, Width = 220 };
        var numPort = new NumericUpDown { Left = 100, Top = 72, Width = 80, Minimum = 1, Maximum = 65535, Value = 2599 };
        var tbUser = new TextBox { Left = 100, Top = 100, Width = 220 };
        var tbPass = new TextBox { Left = 100, Top = 128, Width = 220, UseSystemPasswordChar = true };
        var btnOk = new Button { Text = "Connect", Left = 120, Top = 168, Width = 95, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Left = 225, Top = 168, Width = 95, DialogResult = DialogResult.Cancel };

        void FillFrom(ConnectProfile p)
        {
            tbHost.Text = p.Host;
            numPort.Value = Math.Clamp(p.Port, 1, 65535);
            tbUser.Text = p.User;
            tbPass.Text = Decode(p.PassB64);
        }
        foreach (var p in s.Profiles) cbProfile.Items.Add(p.Name);
        cbProfile.SelectedIndexChanged += (_, _) =>
        {
            var p = s.Profiles.FirstOrDefault(x => x.Name == (string)cbProfile.SelectedItem);
            if (p != null) FillFrom(p);
        };
        var start = s.Profiles.FirstOrDefault(x => x.Name == s.LastProfile) ?? s.Profiles.FirstOrDefault();
        if (start != null) { cbProfile.Text = start.Name; FillFrom(start); }

        ConnectProfile Grab(string name) => new()
        {
            Name = name, Host = tbHost.Text.Trim(), Port = (int)numPort.Value,
            User = tbUser.Text.Trim(), PassB64 = Encode(tbPass.Text),
        };
        btnSaveProf.Click += (_, _) =>
        {
            var name = cbProfile.Text.Trim();
            if (name.Length == 0) return;
            s.Profiles.RemoveAll(x => x.Name == name);
            s.Profiles.Add(Grab(name));
            s.LastProfile = name;
            SaveNetSettings(s);
            if (!cbProfile.Items.Contains(name)) cbProfile.Items.Add(name);
            lblStatus.Text = $"Profile '{name}' saved.";
        };
        btnDelProf.Click += (_, _) =>
        {
            var name = cbProfile.Text.Trim();
            if (s.Profiles.RemoveAll(x => x.Name == name) > 0)
            {
                cbProfile.Items.Remove(name);
                SaveNetSettings(s);
                lblStatus.Text = $"Profile '{name}' removed.";
            }
        };

        dlg.Controls.AddRange(new Control[]
        {
            new Label { Text = "Profile", Left = 12, Top = 13, AutoSize = true }, cbProfile, btnSaveProf, btnDelProf,
            new Label { Text = "Host", Left = 12, Top = 47, AutoSize = true }, tbHost,
            new Label { Text = "Port", Left = 12, Top = 75, AutoSize = true }, numPort,
            new Label { Text = "User", Left = 12, Top = 103, AutoSize = true }, tbUser,
            new Label { Text = "Password", Left = 12, Top = 131, AutoSize = true }, tbPass,
            btnOk, btnCancel,
        });
        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // the connection parameters come from the dialog fields (SaveNetSettings clears the
        // legacy top-level fields, so never read s.Host/s.Port after this point!);
        // the profile itself is saved only after a SUCCESSFUL login
        var profName = cbProfile.Text.Trim().Length > 0 ? cbProfile.Text.Trim() : "default";
        var prof = Grab(profName);
        wfPendingProfile = prof;

        var client = new NetClient();
        client.Synced += data => BeginInvoke(() => { if (net == client) OnServerSync(data); });
        client.RegionPut += ch => BeginInvoke(() => { if (net == client) OnServerPut(ch); });
        client.RegionDeleted += ch => BeginInvoke(() => { if (net == client) OnServerDel(ch); });
        client.Notice += text => BeginInvoke(() => { if (net == client) lblStatus.Text = "SERVER: " + text; });
        client.UsersChanged += users => BeginInvoke(() =>
        {
            if (net == client)
                lblOnline.Text = $"online: {net.User}@{net.HostDisplay}{(net.ReadOnly ? " (viewer)" : "")}  ({users.Count} connected)";
        });
        client.Disconnected += reason => BeginInvoke(() =>
        {
            if (net != client) return;
            lblOnline.Text = "connection lost - working offline";
            lblOnline.ForeColor = Color.Firebrick;
            btnConnect.Text = "Reconnect...";
            lblStatus.Text = $"Connection lost ({reason}). Edits from the last moments may not have reached the server - reconnect and check.";
        });

        lblStatus.Text = $"Connecting to {prof.Host}:{prof.Port}...";
        dirtyPush.Clear();
        pendingRenames.Clear();
        pendingIncoming.Clear();
        awaitingFirstSync = true;
        net?.Dispose();                  // a dead previous connection may linger after a drop
        net = client;                    // set BEFORE ConnectAsync: sync can arrive immediately after login
        btnConnect.Enabled = false;
        _ = Task.Run(async () =>
        {
            var err = await client.ConnectAsync(prof.Host, prof.Port, prof.User, Decode(prof.PassB64));
            BeginInvoke(() =>
            {
                btnConnect.Enabled = true;
                if (net != client) { client.Dispose(); return; }
                if (err != null)
                {
                    net = null;
                    awaitingFirstSync = false;
                    MessageBox.Show(this, err, "Connect failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    lblStatus.Text = "Connect failed.";
                    return;
                }
                lblOnline.Text = $"online: {net.User}@{net.HostDisplay}{(net.ReadOnly ? " (viewer)" : "")}";
                lblOnline.ForeColor = Color.SeaGreen;
                btnConnect.Text = "Disconnect";
                if (net.ReadOnly) lblStatus.Text = "Connected as VIEWER - your edits stay local and are not sent.";
                if (wfPendingProfile != null)
                {
                    var ns = LoadNetSettings();
                    ns.Profiles.RemoveAll(x => x.Name == wfPendingProfile.Name);
                    ns.Profiles.Add(wfPendingProfile);
                    ns.LastProfile = wfPendingProfile.Name;
                    SaveNetSettings(ns);
                    wfPendingProfile = null;
                }
            });
        });
    }

    readonly List<object> pendingIncoming = new();   // broadcasts that arrive while the first-sync dialog is open

    void OnServerSync(SyncData data)
    {
        if (!awaitingFirstSync)
        {
            ApplyRegionsList(data.Regions, "sync");
            return;
        }

        // decide what the baseline is; buffer any broadcasts that arrive while the dialog is open
        bool adoptServer = true;
        bool pushLocal = false;
        if (project.Regions.Count > 0 && data.Regions.Count == 0)
        {
            var choice = MessageBox.Show(this,
                $"The server has no regions yet.\n\n" +
                $"YES = push your {project.Regions.Count} local regions to the server\n" +
                "NO = disconnect and keep working locally",
                "First sync", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice == DialogResult.No) { awaitingFirstSync = false; ConnectClicked(); return; }
            adoptServer = false;
            pushLocal = true;
        }
        else if (project.Regions.Count > 0)
        {
            var choice = MessageBox.Show(this,
                $"Server has {data.Regions.Count} regions, you have {project.Regions.Count} locally.\n\n" +
                "YES = use the SERVER copy (your local project is autosaved first)\n" +
                "NO = PUSH your local regions to the server (overwrites same defnames, keeps the rest)\n" +
                "CANCEL = disconnect",
                "First sync", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) { awaitingFirstSync = false; ConnectClicked(); return; }
            if (choice == DialogResult.No) { adoptServer = false; pushLocal = true; }
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
            ApplyRegionsList(data.Regions, "sync");
        }
        else if (pushLocal)
        {
            Project.EnsureUids(project.Regions);
            foreach (var r in project.Regions.Where(r => !string.IsNullOrWhiteSpace(r.DefName)))
                net.PushRegion(r);
            // also adopt the server regions we do NOT have (finding: staying blind to them)
            foreach (var sr in data.Regions)
                if (!project.Regions.Any(r => r.DefName.Equals(sr.DefName, StringComparison.OrdinalIgnoreCase)))
                    project.Regions.Add(sr);
            Project.EnsureUids(project.Regions);
            RefreshRegionList();
            canvas.Invalidate();
            lblStatus.Text = $"Pushed {project.Regions.Count} regions to the server.";
        }

        // replay broadcasts that arrived while the dialog was open
        var replay = pendingIncoming.ToList();
        pendingIncoming.Clear();
        foreach (var item in replay)
        {
            if (item is ChangePut p) OnServerPut(p);
            else if (item is ChangeDel d) OnServerDel(d);
        }

        StartMulsSync();
    }

    // Pull the server's muls pack into a per-server cache so every client renders the same map.
    void StartMulsSync()
    {
        var client = net;
        if (client is not { Connected: true }) return;
        string cacheDir = Path.Combine(AppDir, "mulscache",
            string.Join("_", client.HostDisplay.Split(Path.GetInvalidFileNameChars())));
        // release memory-mapped muls FIRST or File.Move onto in-use files fails mid-download
        if (project.MulsDir == cacheDir)
        {
            detailRenderer?.Dispose();
            detailRenderer = null;
            canvas.DetailChunkProvider = null;
        }
        _ = Task.Run(async () =>
        {
            var err = await client.SyncMulsAsync(cacheDir,
                s => BeginInvoke(() => { if (net == client) lblMap.Text = s; }));
            BeginInvoke(() =>
            {
                if (net != client) return;
                if (err == null && MulRadar.FindMap(cacheDir) != null)
                {
                    project.MulsDir = cacheDir;
                    detailRenderer?.Dispose();
                    detailRenderer = null;
                    canvas.DetailChunkProvider = null;
                    _ = LoadMapAsync(cacheDir);
                    if (btnDetail.Checked) EnableDetailView();
                }
                else if (err != null && err != "server has no muls pack")
                {
                    lblMap.Text = "muls sync: " + err;
                }
            });
        });
    }

    void OnServerPut(ChangePut ch)
    {
        if (ch.Region == null) return;
        if (net != null && ch.BySession == net.Session) return;  // our own echo
        if (awaitingFirstSync) { pendingIncoming.Add(ch); return; }

        Project.EnsureUids(new[] { ch.Region });
        var selected = canvas.SelectedRegion;
        bool affectsSelected = selected != null &&
            (selected.DefName.Equals(ch.Region.DefName, StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrEmpty(ch.PrevDefName) && selected.DefName.Equals(ch.PrevDefName, StringComparison.OrdinalIgnoreCase)));
        if (affectsSelected) canvas.AbortDrag();

        if (!string.IsNullOrEmpty(ch.PrevDefName))
            project.Regions.RemoveAll(r => r.DefName.Equals(ch.PrevDefName, StringComparison.OrdinalIgnoreCase));
        int i = project.Regions.FindIndex(r => r.DefName.Equals(ch.Region.DefName, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) project.Regions[i] = ch.Region;
        else project.Regions.Add(ch.Region);

        if (affectsSelected)
        {
            // follow the region through the rename/replace so property edits keep applying to a live object
            canvas.SelectedRegion = ch.Region;
            canvas.SelectedRect = ch.Region.Rects.Count > 0 ? ch.Region.Rects[^1] : null;
            LoadProps();
            RefreshRectList();
        }
        RefreshRegionList();
        canvas.Invalidate();
        lblStatus.Text = $"{ch.By} updated {ch.Region.DefName}";
    }

    void OnServerDel(ChangeDel ch)
    {
        if (net != null && ch.BySession == net.Session) return;
        if (awaitingFirstSync) { pendingIncoming.Add(ch); return; }
        int removed = project.Regions.RemoveAll(r => r.DefName.Equals(ch.DefName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        if (canvas.SelectedRegion != null && canvas.SelectedRegion.DefName.Equals(ch.DefName, StringComparison.OrdinalIgnoreCase))
        {
            canvas.AbortDrag();
            canvas.SelectedRegion = null;
            canvas.SelectedRect = null;
            LoadProps();
            RefreshRectList();
        }
        RefreshRegionList();
        canvas.Invalidate();
        lblStatus.Text = $"{ch.By} deleted {ch.DefName}";
    }

    // ---- CentrED-look detail view -----------------------------------------

    // wraps ArtView with a single background render worker; the canvas asks for chunks
    // during paint and gets null until they are ready (radar shows through meanwhile)
    class DetailRenderer : IDisposable
    {
        public ArtView Art;
        readonly HashSet<long> queued = new();
        readonly Queue<(int cx, int cy)> work = new();
        readonly object gate = new();
        readonly Action repaint;
        bool disposed;

        public DetailRenderer(ArtView art, Action repaint)
        {
            Art = art;
            this.repaint = repaint;
            Task.Run(Worker);
        }

        public Bitmap Get(int cx, int cy)
        {
            var b = Art.TryGetCached(cx, cy, ArtView.ClassicN);
            if (b != null) return b;
            long key = ((long)cx << 20) | (uint)cy;
            lock (gate)
            {
                if (!disposed && queued.Add(key)) work.Enqueue((cx, cy));
                Monitor.Pulse(gate);
            }
            return null;
        }

        void Worker()
        {
            while (true)
            {
                (int cx, int cy) job;
                lock (gate)
                {
                    while (!disposed && work.Count == 0) Monitor.Wait(gate);
                    if (disposed) return;
                    job = work.Dequeue();
                    queued.Remove(((long)job.cx << 20) | (uint)job.cy);
                }
                try
                {
                    Art.RenderChunk(job.cx, job.cy, ArtView.ClassicN);
                    repaint();
                }
                catch { /* a bad chunk must not kill the worker */ }
            }
        }

        public void Dispose()
        {
            lock (gate) { disposed = true; Monitor.Pulse(gate); }
            Art?.Dispose();
        }
    }

    void EnableDetailView()
    {
        if (detailRenderer != null)
        {
            canvas.DetailMode = true;
            canvas.Invalidate();
            return;
        }
        var dir = project.MulsDir;
        if (string.IsNullOrEmpty(dir) || MulRadar.FindMap(dir) == null)
        {
            lblStatus.Text = "Detail view needs a muls folder first (Muls... or a server muls pack).";
            btnDetail.Checked = false;
            return;
        }
        var packErr = ArtView.CheckArtPack(dir);
        if (packErr != null)
        {
            lblStatus.Text = "Detail view: " + packErr;
            btnDetail.Checked = false;
            return;
        }
        lblStatus.Text = "Loading art for the detail view...";
        btnDetail.Enabled = false;
        _ = Task.Run(() =>
        {
            try
            {
                var art = new ArtView(dir);
                BeginInvoke(() =>
                {
                    btnDetail.Enabled = true;
                    detailRenderer = new DetailRenderer(art, () => BeginInvoke(() => canvas.Invalidate()));
                    canvas.DetailChunkProvider = (cx, cy) => detailRenderer.Get(cx, cy);
                    canvas.DetailMode = true;
                    ApplyZFilter();
                    canvas.Invalidate();
                    lblStatus.Text = "CentrED view on - zoom in (1.5x+) to see the real art. Z filter applies to statics.";
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    btnDetail.Enabled = true;
                    btnDetail.Checked = false;
                    lblStatus.Text = "Detail view failed: " + ex.Message;
                });
            }
        });
    }

    void ApplyZFilter()
    {
        if (detailRenderer == null) return;
        sbyte min = sbyte.TryParse(tbMinZ.Text.Trim(), out var a) ? a : (sbyte)-128;
        sbyte max = sbyte.TryParse(tbMaxZ.Text.Trim(), out var b) ? b : (sbyte)127;
        if (min > max) (min, max) = (max, min);
        detailRenderer.Art.SetFilter(min, max, true, true, false);
        canvas.Invalidate();
    }

    static string Encode(string s) => string.IsNullOrEmpty(s) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    static string Decode(string s)
    {
        try { return string.IsNullOrEmpty(s) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return ""; }
    }
}
