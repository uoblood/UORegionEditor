using System.Numerics;
using System.Text;
using ImGuiNET;

namespace UORegionEditor;

// The ImGui windows: menu bar, toolbar, regions list, properties, history, connect popup.
public partial class ImGuiApp
{
    // window defaults: FirstUseEver normally; during a layout reset they are FORCED
    // (Cond.Always) and the window is undocked, so reset really restores first-launch look
    void WinDefaults(float x, float y, float w = 0, float h = 0)
    {
        var cond = layoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(new Vector2(x, y), cond);
        if (w > 0) ImGui.SetNextWindowSize(new Vector2(w, h), cond);
        ImGui.SetNextWindowCollapsed(false, cond);
        if (layoutResetFrames > 0) ImGui.SetNextWindowDockID(0, ImGuiCond.Always);
    }

    void DrawUI()
    {
        DrawMenuBar();
        DrawToolbar();
        DrawFilterWindow();
        if (showMinimap) DrawMinimapWindow();
        DrawRegionsWindow();
        DrawPropertiesWindow();
        if (showHistory) DrawHistoryWindow();
        DrawConnectPopup();
        DrawExportImagePopup();
    }

    // File > Export map image: the shareable player-map PNG (MapExport.Render)
    void DrawExportImagePopup()
    {
        if (exportDialogOpen)
        {
            exportDialogOpen = false;
            ImGui.OpenPopup("Export map image");
        }
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Export map image", ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.SetNextItemWidth(160);
        ImGui.Combo("Area", ref exportScope, "Whole map\0Current view\0Picked area\0");
        if (exportScope == 2)
        {
            ImGui.TextUnformatted(exportArea == null ? "no area picked yet" : $"picked: {exportArea}");
            ImGui.SameLine();
            if (ImGui.Button("Pick on map"))
            {
                exportPicking = true;
                status = "Drag a rectangle on the map - the dialog returns when you release (Esc cancels).";
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SetNextItemWidth(160);
        ImGui.SliderInt("Scale (px per tile)", ref exportScale, 1, 4);
        ImGui.Checkbox("Region name labels", ref exportLabels);
        if (exportLabels)
        {
            ImGui.SetNextItemWidth(160);
            ImGui.SliderInt("Label size", ref exportLabelSize, 8, 20);
        }
        TextDim("Hidden regions (eye toggles) stay OFF the image -");
        TextDim("hide system zones first for a player-facing map.");

        ImGui.Separator();
        ImGui.BeginDisabled(exporting || mapTex == 0 || (exportScope == 2 && exportArea == null));
        if (ImGui.Button(exporting ? "Exporting..." : "Export...", new Vector2(120, 0)))
        {
            DoExportMapImage();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(90, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    void DoExportMapImage()
    {
        var area = exportScope switch
        {
            1 => new RegionRect(Math.Max(0, visMinX), Math.Max(0, visMinY),
                Math.Min(mapW - 1, Math.Max(0, visMaxX)), Math.Min(mapH - 1, Math.Max(0, visMaxY))),
            2 => exportArea,
            _ => new RegionRect(0, 0, Math.Max(0, mapW - 1), Math.Max(0, mapH - 1)),
        };
        if (area == null || area.W < 2 || area.H < 2) { status = "Export area is empty."; return; }
        long px = (long)area.W * exportScale * area.H * exportScale;
        if (px > MapExport.MaxPixels)
        {
            status = $"Too big: {px / 1_000_000}M px (cap {MapExport.MaxPixels / 1_000_000}M) - lower the scale or pick a smaller area.";
            return;
        }
        using var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = "region-map.png" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        string outPath = dlg.FileName;
        string muls = loadedMulsDir;
        if (string.IsNullOrEmpty(muls) || MulRadar.FindMap(muls) == null) { status = "No muls loaded - load a map first."; return; }
        // snapshot on the UI thread: the render task must not chase live edits
        var snapshot = project.Regions.Select(r =>
        {
            var c = new RegionDef { DefName = r.DefName, Name = r.Name, ColorArgb = r.ColorArgb, Visible = r.Visible };
            c.Rects.AddRange(r.Rects.Select(t => t.Clone()));
            return c;
        }).ToList();
        int scale = exportScale;
        bool labels = exportLabels;
        float labelSize = exportLabelSize;
        exporting = true;
        status = "Exporting map image...";
        Task.Run(() =>
        {
            try
            {
                using var radar = MulRadar.LoadOrRender(muls, null);
                using var bmp = MapExport.Render(radar, snapshot, area, scale, labels, labelSize);
                int bw = bmp.Width, bh = bmp.Height;
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                mainQueue.Enqueue(() =>
                {
                    exporting = false;
                    status = $"Map image saved: {Path.GetFileName(outPath)} ({bw}x{bh})";
                });
            }
            catch (Exception ex)
            {
                mainQueue.Enqueue(() =>
                {
                    exporting = false;
                    status = "Export failed: " + ex.Message;
                    ClientLog("error", "map image export failed: " + ex.Message);
                });
            }
        });
    }

    // CentrED-style minimap: the radar as a thumbnail, current view marked, click to jump
    void DrawMinimapWindow()
    {
        WinDefaults(window.Size.X - 346, 30, 330, 220);
        if (!ImGui.Begin("Minimap", ref showMinimap)) { ImGui.End(); return; }
        if (mapTex == 0)
        {
            ImGui.TextDisabled("no map loaded");
            ImGui.End();
            return;
        }
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 16 || avail.Y < 16) { ImGui.End(); return; }
        // zoomable window of the map around miniCenter. LETTERBOXED, never stretched:
        // the image is drawn at its exact uniform-scale size and centered, so the
        // overlay math (P dots, viewport quad) stays valid at ANY panel size/aspect
        float fitScale = Math.Min(avail.X / mapW, avail.Y / mapH);
        float scale = fitScale * miniZoom;
        float winW = Math.Min(avail.X / scale, mapW);
        float winH = Math.Min(avail.Y / scale, mapH);
        miniCX = Math.Clamp(miniCX, winW / 2, mapW - winW / 2);
        miniCY = Math.Clamp(miniCY, winH / 2, mapH - winH / 2);
        float wx0 = miniCX - winW / 2, wy0 = miniCY - winH / 2;
        var drawSz = new Vector2(winW * scale, winH * scale);
        var imgPos = ImGui.GetCursorScreenPos() + (avail - drawSz) / 2;
        ImGui.SetCursorScreenPos(imgPos);
        ImGui.Image((nint)mapTex, drawSz,
            new Vector2(wx0 / mapW, wy0 / mapH),
            new Vector2((wx0 + winW) / mapW, (wy0 + winH) / mapH));
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(imgPos, imgPos + drawSz, true);

        Vector2 MiniOf(double tx, double ty) => imgPos + new Vector2((float)((tx - wx0) * scale), (float)((ty - wy0) * scale));

        // current viewport as a shape (rect in flat, rotated diamond in iso)
        var size = window.Size;
        var c1 = TileAt(new Vector2(0, 0));
        var c2 = TileAt(new Vector2(size.X, 0));
        var c3 = TileAt(new Vector2(size.X, size.Y));
        var c4 = TileAt(new Vector2(0, size.Y));
        dl.AddQuad(MiniOf(c1.x, c1.y), MiniOf(c2.x, c2.y), MiniOf(c3.x, c3.y), MiniOf(c4.x, c4.y), 0xFF00D7FF, 1.5f);

        // region dots
        foreach (var r in project.Regions)
        {
            if (!r.Visible || r.Rects.Count == 0) continue;
            var (px, py) = r.EffectiveP();
            if (px < wx0 || py < wy0 || px > wx0 + winW || py > wy0 + winH) continue;
            dl.AddCircleFilled(MiniOf(px, py), 2f, Col(r.Color, 255));
        }
        dl.PopClipRect();

        if (ImGui.IsItemHovered())
        {
            var m = ImGui.GetMousePos() - imgPos;
            double tx = wx0 + m.X / scale, ty = wy0 + m.Y / scale;
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0)
            {
                miniZoom = Math.Clamp(miniZoom * MathF.Pow(1.3f, wheel), 1f, 16f);
                // keep the tile under the cursor fixed while zooming
                float ns = fitScale * miniZoom;
                miniCX = (float)(tx - (m.X - drawSz.X / 2) / ns);
                miniCY = (float)(ty - (m.Y - drawSz.Y / 2) / ns);
            }
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                CenterOn(Math.Clamp((int)tx, 0, mapW - 1), Math.Clamp((int)ty, 0, mapH - 1));
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                var d = ImGui.GetIO().MouseDelta;
                miniCX -= d.X / scale;
                miniCY -= d.Y / scale;
            }
        }
        ImGui.End();
    }

    // CentrED#'s ImGuiEx.DragInt: drag field + wheel-step on hover + repeating -/+ buttons
    static bool DragIntStep(string label, ref int value, int min, int max)
    {
        var io = ImGui.GetIO();
        float btn = ImGui.GetFrameHeight();
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.PushID(label);
        ImGui.SetNextItemWidth(140 - (btn + spacing) * 2);
        bool changed = ImGui.DragInt("##v", ref value, 1, min, max);
        if (ImGui.IsItemHovered() && io.MouseWheel != 0)
        {
            value += io.MouseWheel > 0 ? 1 : -1;
            changed = true;
        }
        ImGui.SameLine(0, spacing);
        ImGui.PushButtonRepeat(true);
        if (ImGui.Button("-", new Vector2(btn, btn))) { value--; changed = true; }
        ImGui.SameLine(0, spacing);
        if (ImGui.Button("+", new Vector2(btn, btn))) { value++; changed = true; }
        ImGui.PopButtonRepeat();
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.PopID();
        value = Math.Clamp(value, min, max);
        return changed;
    }

    // mirrors the CentrED# FilterWindow: draggable Min/Max Z + Land/Objects/NoDraw toggles
    void DrawFilterWindow()
    {
        WinDefaults(340, 30);
        if (!ImGui.Begin("Filter", ImGuiWindowFlags.AlwaysAutoResize)) { ImGui.End(); return; }
        bool changed = false;
        changed |= DragIntStep("Max Z", ref maxZ, minZ, 127);
        changed |= DragIntStep("Min Z", ref minZ, -128, maxZ);
        changed |= ImGui.Checkbox("Land", ref showLand);
        FilterHint("Show terrain");
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Objects", ref showStatics);
        FilterHint("Show statics");
        ImGui.SameLine();
        changed |= ImGui.Checkbox("NoDraw", ref showNoDraw);
        FilterHint("Show hidden tiles");
        if (changed) ApplyDetailFilter();
        ImGui.End();
    }

    // these three only change what the CentrED view renders. That used to be spelled out
    // on its own line under them; it rides the hover now, and only while it matters.
    void FilterHint(string what)
    {
        if (!ImGui.IsItemHovered()) return;
        if (detailMode) TooltipLines(what);
        else TooltipLines(what, "CentrED view only");
    }

    void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open project...")) OpenProject();
            if (ImGui.MenuItem("Save project")) SaveProject(false);
            if (ImGui.MenuItem("Save project as...")) SaveProject(true);
            ImGui.Separator();
            // bulk imports dump many regions onto the server at once - same overwrite
            // risk the first-sync gate closes, so while connected they are root-only
            bool canImport = net is not { Connected: true } || net.Access >= 255;
            // Four formats each way was eleven flat entries. Grouped into submenus, with
            // the one matching "Editing for" marked - importing one server's regions and
            // exporting another's is a feature, so nothing is hidden by target.
            if (ImGui.BeginMenu("Import"))
            {
                FormatItem("Sphere .scp...", 0, canImport, ImportScp);
                FormatItem("CentrED xml...", 2, canImport, ImportCentred);
                FormatItem("ServUO Regions.xml...", 1, canImport, ImportServuo);
                FormatItem("ModernUO regions.json...", 3, canImport, ImportModernUo);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Export"))
            {
                FormatItem("Sphere .scp...", 0, true, ExportSphere);
                FormatItem("CentrED xml...", 2, true, ExportCentred);
                FormatItem("ServUO Regions.xml...", 1, true, ExportServuo);
                FormatItem("ModernUO regions.json...", 3, true, ExportModernUo);
                ImGui.Separator();
                if (ImGui.MenuItem("Merge into cedserver.xml...")) MergeCentred();
                if (ImGui.IsItemHovered()) TooltipLines("Replace same-name regions in a live config");
                ImGui.EndMenu();
            }
            // not a region format - a picture for your players, so it stands on its own
            if (ImGui.MenuItem("Export map image (PNG)...")) exportDialogOpen = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Muls folder...")) PickMuls();
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) window.Close();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z", false, undoMgr.CanUndo)) DoUndo();
            if (ImGui.MenuItem("Redo", "Ctrl+Y", false, undoMgr.CanRedo)) DoRedo();
            ImGui.Separator();
            ImGui.MenuItem("History window", null, ref showHistory);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Server"))
        {
            bool online = net is { Connected: true };
            if (ImGui.MenuItem(online ? "Disconnect" : "Connect...", null, false, true))
            {
                if (online) ConnectNow();
                else connectOpenRequest = true;
            }
            if (ImGui.MenuItem("Go offline (work locally)", null, appMode == AppMode.Offline, appMode != AppMode.Offline))
                GoOffline();
            ImGui.Separator();
            if (ImGui.MenuItem("Sync muls from server", null, false, online)) StartMulsSync();
            if (ImGui.MenuItem("Open muls cache folder"))
            {
                var dir = Path.Combine(AppDir, "mulscache");
                try
                {
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                }
                catch (Exception ex) { status = "open folder failed: " + ex.Message; }
            }
            var quick = LoadNetSettings();
            if (quick.Profiles.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Quick connect");
                foreach (var p in quick.Profiles)
                {
                    if (ImGui.MenuItem($"{p.Name}  ({p.User}@{p.Host}:{p.Port})"))
                    {
                        cProfile = p.Name; cHost = p.Host; cPort = p.Port; cUser = p.User; cPass = Decode(p.PassB64);
                        cMulsCache = p.MulsCache ?? "";
                        if (online) ConnectNow();   // drop the current connection first
                        ConnectNow();
                    }
                }
            }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Fit map")) ZoomFit();
            ImGui.MenuItem("Minimap", null, ref showMinimap);
            if (ImGui.MenuItem("CentrED view", "", detailMode))
            {
                SwitchProjection(!detailMode);
                if (detailMode) EnsureArtView();
            }
            ImGui.Separator();
            ImGui.MenuItem("Options", null, ref showOptions);
            if (ImGui.MenuItem("Reset window layout")) pendingLayoutReset = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            ImGui.MenuItem("Controls", null, ref showControls);
            ImGui.MenuItem("About", null, ref showAbout);
            if (ImGui.MenuItem("Open client log"))
            {
                var p = Path.Combine(AppDir, "client.log");
                try
                {
                    if (File.Exists(p))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
                    else status = "no client.log yet";
                }
                catch (Exception ex) { status = "open log failed: " + ex.Message; }
            }
            ImGui.EndMenu();
        }
        ImGui.EndMainMenuBar();
        if (showControls) DrawControlsWindow();
        if (showOptions) DrawOptionsWindow();
        if (showAbout) DrawAboutWindow();
    }

    void DrawAboutWindow()
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.Begin("About", ref showAbout, ImGuiWindowFlags.AlwaysAutoResize)) { ImGui.End(); return; }

        ImGui.BeginGroup();
        ImGui.Text($"UO Region Editor  v{AppVersion}");
        ImGui.TextDisabled("visual region editor for custom UO maps");
        ImGui.Spacing();
        ImGui.Text("by chemist");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Special thanks");
        ImGui.BulletText("Kaczy and all CentrED# contributors");
        ImGui.BulletText("Andreas Schneider for the original CentrED");
        ImGui.BulletText("andreakarasho and all ClassicUO contributors");
        ImGui.BulletText("Voxpire and all ServUO contributors");
        ImGui.BulletText("the Sphere team and all Source-X contributors");
        ImGui.BulletText("False");
        ImGui.EndGroup();

        // the mark sits beside the credits, not above them - keeps the window compact
        uint lt = LogoTexture();
        if (lt != 0)
        {
            ImGui.SameLine(0, 28);
            float ih = 168f, iw = ih * logoW / Math.Max(1, logoH);
            ImGui.Image((nint)lt, new Vector2(iw, ih));
        }
        ImGui.End();
    }
    void DrawOptionsWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(400, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Options", ref showOptions, ImGuiWindowFlags.AlwaysAutoResize)) { ImGui.End(); return; }

        // the connect dialog asks this per profile; offline never sees that dialog, so this
        // is the way in. Every caption that used to sit under these rows is on the hover now.
        ImGui.TextDisabled("Shard");
        if (TargetCombo("Editing for", ref scriptTarget, 180f)) SaveUiSettings();

        ImGui.Spacing();
        ImGui.TextDisabled("Rendering");
        int q = renderQuality;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Render quality", ref q, 1, 10))
            renderQuality = q;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveUiSettings();
        if (ImGui.IsItemHovered())
            TooltipLines("Lower it if the map renders slowly",
                renderQuality == 10 ? "10 = full detail"
                : renderQuality <= 2 ? "Always the smoothed overview"
                : $"Smoothed overview below zoom {0.45 + (10 - renderQuality) * 0.15:0.##}x");
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Hover marker", ref hoverMode, "Off\0Tile diamond\0Item glow\0Item glow + tile diamond\0"))
            SaveUiSettings();
        if (ImGui.IsItemHovered()) TooltipLines("Marks what the Z readout refers to");
        // marker color: swatch opens a picker popup with Current/Default/Presets
        // filling the otherwise-blank right side (the classic ImGui palette layout)
        if (ImGui.ColorButton("Marker color##open", hoverColor,
                ImGuiColorEditFlags.AlphaPreview, new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight())))
            ImGui.OpenPopup("markercolor");
        ImGui.SameLine();
        ImGui.TextUnformatted("Marker color");
        if (ImGui.BeginPopup("markercolor"))
        {
            // keep the RGBA + #hex fields visible (no NoInputs!) but size the wheel down
            // so the popup stays compact with the preset column beside it
            ImGui.SetNextItemWidth(200);
            ImGui.ColorPicker4("##mpick", ref hoverColor,
                ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview |
                ImGuiColorEditFlags.AlphaBar);
            if (ImGui.IsItemDeactivatedAfterEdit()) SaveUiSettings();
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextUnformatted("Current");
            ImGui.ColorButton("##mcur", hoverColor, ImGuiColorEditFlags.AlphaPreviewHalf, new Vector2(58, 40));
            if (ImGui.Button("Default", new Vector2(58, 0)))
            {
                hoverColor = ColU(DefaultHoverColor);
                SaveUiSettings();
            }
            ImGui.Spacing();
            ImGui.TextUnformatted("Presets");
            for (int i = 0; i < HoverPresets.Length; i++)
            {
                if ((i % 2) != 0) ImGui.SameLine();
                var (name, col) = HoverPresets[i];
                if (ImGui.ColorButton($"{name}##pr{i}", ColU(col),
                        ImGuiColorEditFlags.AlphaPreview, new Vector2(27, 27)))
                {
                    hoverColor = ColU(col);
                    SaveUiSettings();
                }
            }
            ImGui.EndGroup();
            ImGui.EndPopup();
        }
        ImGui.Separator();
        int queued;
        lock (chunkGate) queued = chunkWork.Count;
        // one dim line instead of a headed section - it is a glance, not a setting
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.TextUnformatted($"{ImGui.GetIO().Framerate:0} fps   queue {queued}   textures {chunkTex.Count}   workers {ChunkWorkerLimit}");
        ImGui.PopStyleColor();
        ImGui.End();
    }

    // the old status-bar chatter lives here now, out of the way
    void DrawControlsWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Controls", ref showControls, ImGuiWindowFlags.AlwaysAutoResize)) { ImGui.End(); return; }
        ImGui.TextDisabled("Map");
        ImGui.BulletText("WASD / right-drag: pan    wheel: zoom at cursor");
        ImGui.BulletText("Middle-drag (iso): rotate view    double middle-click or Esc: reset");
        ImGui.TextDisabled("Editing");
        ImGui.BulletText("Select: click a box; drag moves it, corner handles resize");
        ImGui.BulletText("Draw box: drag a rectangle, or click two opposite corners");
        ImGui.BulletText("Lasso: drag a freehand loop - the tiles inside become boxes");
        ImGui.BulletText("Quick select: click = whole matching area, click+drag = limit to a radius (honors the Z filter)");
        ImGui.BulletText("Erase box / Erase lasso: cut the drawn shape OUT of the selected region");
        ImGui.BulletText("4 corners: click corners, Enter / right-click / double-click finishes");
        ImGui.BulletText("Arrows nudge the selected box    Del deletes it    Ctrl+Z / Ctrl+Y undo/redo");
        ImGui.BulletText("F1-F10 switch tools    P: pick the P point    Esc: cancel action, then deselect");
        ImGui.BulletText("Tools > \"Don't overlap other regions\": add tools stop at visible regions (hide one to draw through it)");
        ImGui.BulletText("Properties > Pick P: click the map to set the spawn point (auto land Z)");
        ImGui.End();
    }

    void DrawToolbar()
    {
        WinDefaults(8, 30);
        if (!ImGui.Begin("Tools", ImGuiWindowFlags.AlwaysAutoResize)) { ImGui.End(); return; }

        // CentrED-style tool list: one active tool, grouped Add / Remove
        ToolRow(Mode.Select, "Select", "F1", "Pick, move and resize boxes");
        ImGui.Separator();
        ImGui.TextDisabled("Add");
        ToolRow(Mode.Draw, "Draw box", "F2", "Drag a rectangle");
        ToolRow(Mode.Lasso, "Lasso", "F3", "Draw a freehand area");
        ToolRow(Mode.BrushAdd, "Brush", "F4", "Paint tiles");
        ToolRow(Mode.Wand, "Quick select", "F5", "Click fills matching tiles\nDrag to limit the radius");
        ToolRow(Mode.Corners, "4 corners", "F6", "Click corners, Enter finishes");
        ImGui.Separator();
        ImGui.TextDisabled("Remove");
        ToolRow(Mode.EraseBox, "Erase box", "F7", "Cut out a rectangle");
        ToolRow(Mode.EraseLasso, "Erase lasso", "F8", "Cut out a freehand area");
        ToolRow(Mode.BrushErase, "Erase brush", "F9", "Paint to erase");
        ToolRow(Mode.WandErase, "Erase quick select", "F10", "Click cuts matching tiles");
        ImGui.Separator();
        if (mode is Mode.BrushAdd or Mode.BrushErase)
        {
            // tool option pops in when a brush is active, CentrED-style
            ImGui.SetNextItemWidth(140);
            ImGui.SliderInt("Brush size", ref brushSize, 1, 32);
            ImGui.Separator();
        }

        if (mode is Mode.Wand or Mode.WandErase)
        {
            // wand options pop in when the tool is active, like the brush slider
            ImGui.BeginDisabled(wandMatch == 2);   // type match is exact - tolerance has no meaning
            ImGui.SetNextItemWidth(140);
            ImGui.SliderInt("Tolerance", ref wandTolerance, 0, 64);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How different a tile may look and still match");
            ImGui.EndDisabled();
            ImGui.SetNextItemWidth(140);
            ImGui.Combo("Match", ref wandMatch, "Land color\0Surface color\0Tile type\0");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("What counts as matching.\nAll modes follow the Z filter.");
            ImGui.Separator();
        }
        ImGui.Checkbox("Add to selected region", ref addToSelected);
        ImGui.Checkbox("Don't overlap other regions", ref avoidOverlap);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stop at other visible regions.\nHide one to draw through it.");
        // only the Sphere exporter/importer reads this, so it is noise on any other shard.
        // The value is left alone when hidden - switching target must not silently flip a
        // setting someone turned off on purpose, and it defaults on, which is correct for
        // a stock Sphere script even if you export .scp while editing for another server.
        if (TargetSphere)
        {
            bool plusOne = project.SphereExclusiveEdge;
            if (ImGui.Checkbox("Sphere +1 edge", ref plusOne)) project.SphereExclusiveEdge = plusOne;
            if (ImGui.IsItemHovered())
                TooltipLines("Right/bottom edges written exclusive", "off only for scripts that break the convention");
        }

        bool det = detailMode;
        if (ImGui.Checkbox("CentrED view", ref det))
        {
            SwitchProjection(det);
            if (det) EnsureArtView();
        }

        ImGui.BeginDisabled(!undoMgr.CanUndo);
        if (ImGui.Button("Undo")) DoUndo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!undoMgr.CanRedo);
        if (ImGui.Button("Redo")) DoRedo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Fit")) ZoomFit();

        ImGui.End();
    }

    void ToolRow(Mode m, string label, string key = "", string tip = "")
    {
        // radio circles like CentrED's toolbox: one active tool, obvious at a glance
        if (ImGui.RadioButton(label, mode == m)) SetTool(m);
        if (tip.Length > 0 && ImGui.IsItemHovered()) TooltipLines(tip.Split('\n'));
        if (key.Length > 0)
        {
            // right-align against the real content edge using the key's own width -
            // a fixed offset clipped the wider labels ("F10" lost its 0)
            ImGui.SameLine();
            float target = ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(key).X;
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX() + 6, target));
            ImGui.TextDisabled(key);
        }
    }

    void DrawRegionsWindow()
    {
        WinDefaults(8, 200, 320, 420);
        if (!ImGui.Begin("Regions")) { ImGui.End(); return; }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "filter...", ref regionFilter, 64);

        if (ImGui.BeginChild("list", new Vector2(0, -34)))
        {
            for (int i = 0; i < project.Regions.Count; i++)
            {
                var r = project.Regions[i];
                if (regionFilter.Length > 0 &&
                    !r.DefName.Contains(regionFilter, StringComparison.OrdinalIgnoreCase) &&
                    !r.Name.Contains(regionFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                ImGui.PushID(r.Uid.GetHashCode());

                var col = new Vector4(r.Color.R / 255f, r.Color.G / 255f, r.Color.B / 255f, 1f);
                if (ImGui.ColorButton("##col", col, ImGuiColorEditFlags.NoTooltip, new Vector2(16, 16)))
                    ImGui.OpenPopup("colpick");
                if (ImGui.BeginPopup("colpick"))
                {
                    var v = new Vector3(col.X, col.Y, col.Z);
                    if (ImGui.ColorPicker3("##pick", ref v))
                    {
                        SnapshotFor($"recolor {r.DefName}", r, "color|" + r.Uid);
                        r.Color = Color.FromArgb((int)(v.X * 255), (int)(v.Y * 255), (int)(v.Z * 255));
                        MarkDirty(r);
                    }
                    if (ImGui.Button("Random distinct color"))
                    {
                        SnapshotFor($"recolor {r.DefName}", r, "color|" + r.Uid);
                        r.Color = DistinctColor(r);
                        MarkDirty(r);
                    }
                    ImGui.EndPopup();
                }

                ImGui.SameLine();
                bool vis = r.Visible;
                if (ImGui.Checkbox("##vis", ref vis))
                {
                    SnapshotFor($"{(vis ? "show" : "hide")} {r.DefName}", r, "visible|" + r.Uid);
                    r.Visible = vis;
                    MarkDirty(r);
                }

                ImGui.SameLine();
                if (ImGui.Selectable($"{r.DefName}  ({r.Rects.Count})", r == selReg))
                {
                    selReg = r;
                    selRect = r.Rects.Count > 0 ? r.Rects[^1] : null;
                }
                if (scrollListToSel && r == selReg && regionFilter.Length == 0)
                {
                    ImGui.SetScrollHereY(0.4f);   // selection made on the map: bring the row into view
                    scrollListToSel = false;
                }
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    ZoomToRegion(r);
                // real drag-and-drop reorder: a "Move <name>" preview follows the cursor,
                // the row under it highlights, and the move happens ONCE on drop.
                // list order = script order = what overrides what in Sphere on overlap.
                // disabled while filtering - moving across hidden rows would be chaos
                if (regionFilter.Length == 0)
                {
                    if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoHoldToOpenOthers))
                    {
                        dragRegionUid = r.Uid;
                        ImGui.SetDragDropPayload("REGION_ROW", IntPtr.Zero, 0);
                        ImGui.Text($"Move {r.DefName}");
                        ImGui.EndDragDropSource();
                    }
                    if (ImGui.BeginDragDropTarget())
                    {
                        unsafe
                        {
                            if (ImGui.AcceptDragDropPayload("REGION_ROW").NativePtr != null)
                            {
                                int from = project.Regions.FindIndex(x => x.Uid == dragRegionUid);
                                if (from >= 0 && from != i)
                                {
                                    var moved = project.Regions[from];
                                    project.Regions.RemoveAt(from);
                                    // dragging down lands after this row, dragging up before it
                                    project.Regions.Insert(Math.Min(i, project.Regions.Count), moved);
                                    reorderDirty = true;
                                    status = $"{moved.DefName} moved - in Sphere, later regions win on overlap.";
                                }
                            }
                        }
                        ImGui.EndDragDropTarget();
                    }
                }

                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        scrollListToSel = false;   // row filtered out or list hidden: do not scroll later

        if (ImGui.Button("New"))
        {
            var reg = CreateRegionObject();
            undoMgr.Snapshot($"create {reg.DefName}", project, new[] { reg });
            project.Regions.Add(reg);
            MarkDirty(reg);
            selReg = reg;
            selRect = null;
        }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate")) DuplicateRegion(selReg);
        ImGui.SameLine();
        if (ImGui.Button("Delete")) DeleteRegion(selReg);
        ImGui.SameLine();
        if (ImGui.Button("Zoom to")) ZoomToRegion(selReg);
        ImGui.SameLine();
        if (ImGui.Button("Delete ALL")) DeleteAllRegions();

        ImGui.End();
    }

    void DeleteAllRegions()
    {
        if (project.Regions.Count == 0) return;
        if (MessageBox.Show(
                $"Delete ALL {project.Regions.Count} regions?{(net is { Connected: true } ? "\n\nThis also deletes them on the SERVER for everyone." : "")}\n\n(One Ctrl+Z brings everything back.)",
                "Delete all regions", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        undoMgr.Snapshot("delete ALL regions", project, project.Regions.ToList());
        var names = project.Regions.Select(r => r.DefName).ToList();
        foreach (var r in project.Regions)
            if (pendingRenames.TryGetValue(r, out var old) && !old.Equals(r.DefName, StringComparison.OrdinalIgnoreCase))
                net?.PushDelete(old);
        project.Regions.Clear();
        dirtyPush.Clear();
        pendingRenames.Clear();
        foreach (var n in names) net?.PushDelete(n);
        selReg = null;
        selRect = null;
        status = $"Deleted {names.Count} regions (Ctrl+Z restores them).";
    }

    void DrawPropertiesWindow()
    {
        WinDefaults(8, 630, 320, 260);
        if (!ImGui.Begin("Properties")) { ImGui.End(); return; }
        if (selReg == null)
        {
            ImGui.TextDisabled("no region selected");
            defnameActive = false;
            ImGui.End();
            return;
        }
        var r = selReg;

        string dn = r.DefName;
        ImGui.SetNextItemWidth(-60);
        if (ImGui.InputText("##defname", ref dn, 64, autoDef ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None)
            && !autoDef && dn != r.DefName)
        {
            undoMgr.Snapshot($"edit defname of {r.DefName}", project, new[] { r }, coalesceKey: $"defname|{r.Uid}");
            if (!pendingRenames.ContainsKey(r)) pendingRenames[r] = r.DefName;
            r.DefName = dn.Trim().Replace(' ', '_').Replace('\t', '_');
            MarkDirty(r);
        }
        defnameActive = ImGui.IsItemActive();
        ImGui.SameLine();
        if (ImGui.Checkbox("auto", ref autoDef) && autoDef)
        {
            undoMgr.Snapshot($"auto defname of {r.DefName}", project, new[] { r });
            AutoDefNameFor(r);
            MarkDirty(r);
        }

        PropText("Name", "name", r, () => r.Name, v =>
        {
            r.Name = v;
            if (autoDef) AutoDefNameFor(r);
        });

        // only the selected server's script fields are shown. Everything stays stored and
        // exported either way - this hides fields, it never clears them.
        if (TargetSphere)
        {
            int kind = r.Kind == "ROOMDEF" ? 1 : 0;
            ImGui.SetNextItemWidth(120);
            if (ImGui.Combo("Type", ref kind, "AREADEF\0ROOMDEF\0"))
            {
                SnapshotFor($"edit type of {r.DefName}", r, $"type|{r.Uid}");
                r.Kind = kind == 1 ? "ROOMDEF" : "AREADEF";
                MarkDirty(r);
            }

            PropTextPick("Group", "group", r, () => r.Group, v => r.Group = v, ',', multi: false, KnownGroups);
            PropTextPick("Events", "events", r, () => r.Events, v => r.Events = v, ',', multi: true, KnownEvents);
            PropTextPick("Flags", "flags", r, () => r.Flags, v => r.Flags = v, '|', multi: true, KnownFlags);
        }
        else if (TargetServuo)
        {
            PropTextPick("Type", "servuotype", r, () => r.ServuoType, v => r.ServuoType = v,
                ',', multi: false, () => ServuoXml.RegionTypes);
            if (ImGui.IsItemHovered()) TooltipLines("Region class ServUO creates", "(blank = plain region)");
            int prio = r.Priority;
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("Priority", ref prio, 0))
            {
                SnapshotFor($"edit priority of {r.DefName}", r, $"prio|{r.Uid}");
                r.Priority = Math.Clamp(prio, 0, 32767);
                MarkDirty(r);
            }
            if (ImGui.IsItemHovered()) TooltipLines("Higher wins where regions overlap");
            PropText("Music", "music", r, () => r.Music, v => r.Music = v);
        }

        var (px, py) = r.EffectiveP();
        // one stored point, but each server calls it something else (Sphere P=, ServUO <go>)
        ImGui.Text($"{(TargetServuo ? "Go" : "P")}: {px},{py}{(r.PX < 0 ? " (auto)" : "")}");
        ImGui.SameLine();
        int pz = r.PZ;
        ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("Z##pz", ref pz, 0))
        {
            SnapshotFor($"edit z of {r.DefName}", r, $"z|{r.Uid}");
            r.PZ = Math.Clamp(pz, -128, 127);
            MarkDirty(r);
        }
        ImGui.SameLine();
        if (ImGui.Button("Pick P")) { pickTile = true; status = "Click on the map to place P (Esc cancels)."; }
        ImGui.SameLine();
        if (ImGui.Button("P=auto"))
        {
            SnapshotFor($"auto P of {r.DefName}", r);
            r.PX = -1; r.PY = -1;
            MarkDirty(r);
        }

        // TAG.* lines are Sphere script, so they only make sense against a Sphere export
        if (TargetSphere)
        {
            ImGui.Text("Extra lines (TAG.* etc. - exported into the region as-is):");
            string extra = string.Join("\n", r.Extra);
            if (ImGui.InputTextMultiline("##extra", ref extra, 4096, new Vector2(-1, 46)))
            {
                SnapshotFor($"edit extra of {r.DefName}", r, $"extra|{r.Uid}");
                r.Extra = extra.Split('\n').Where(l => l.Trim().Length > 0).ToList();
                MarkDirty(r);
            }
        }

        // box count against our per-region limit (Sphere itself is RAM-unbounded here)
        if (r.Rects.Count >= maxRectsPerRegion) ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"Boxes ({r.Rects.Count} / {maxRectsPerRegion} - at limit):");
        else if (r.Rects.Count > maxRectsPerRegion * 9 / 10) ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), $"Boxes ({r.Rects.Count} / {maxRectsPerRegion}):");
        else ImGui.Text($"Boxes ({r.Rects.Count} / {maxRectsPerRegion}):");
        if (ImGui.BeginChild("boxes", new Vector2(0, 70)))
        {
            for (int i = 0; i < r.Rects.Count; i++)
            {
                var rc = r.Rects[i];
                if (ImGui.Selectable($"{rc}##box{i}", rc == selRect)) selRect = rc;
            }
        }
        ImGui.EndChild();
        if (ImGui.Button("Remove box") && selRect != null)
        {
            SnapshotFor($"remove box from {r.DefName}", r);
            r.Rects.Remove(selRect);
            selRect = r.Rects.Count > 0 ? r.Rects[^1] : null;
            MarkDirty(r);
        }
        ImGui.SameLine();
        if (ImGui.Button("Merge boxes") && r.Rects.Count > 1)
        {
            int before = r.Rects.Count;
            var merged = RegionOps.MaskToRectsCompact(RegionOps.Coverage(r.Rects));
            if (merged.Count < before)
            {
                SnapshotFor($"merge boxes of {r.DefName}", r);
                r.Rects.Clear();
                r.Rects.AddRange(merged);
                selRect = r.Rects[^1];
                MarkDirty(r);
                status = $"{r.DefName}: boxes merged {before} -> {merged.Count}";
            }
            else status = $"{r.DefName}: already as compact as it gets ({before} boxes)";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fewer boxes, same tiles");

        ImGui.End();
    }

    void PropText(string label, string field, RegionDef r, Func<string> get, Action<string> set)
    {
        string v = get() ?? "";
        if (ImGui.InputText(label, ref v, 512))
        {
            SnapshotFor($"edit {field} of {r.DefName}", r, $"{field}|{r.Uid}");
            set(v);
            MarkDirty(r);
        }
    }

    // like PropText, but with a "..." picker: known values collected from the loaded
    // regions (plus the standard Sphere flags). multi = checkbox bulk-select joined
    // with sep; single = click to replace. Editing by hand still works as before.
    void PropTextPick(string label, string field, RegionDef r, Func<string> get, Action<string> set,
        char sep, bool multi, Func<IEnumerable<string>> known)
    {
        string v = get() ?? "";
        ImGui.SetNextItemWidth(ImGui.CalcItemWidth() - 34);
        if (ImGui.InputText($"##{field}", ref v, 512))
        {
            SnapshotFor($"edit {field} of {r.DefName}", r, $"{field}|{r.Uid}");
            set(v);
            MarkDirty(r);
        }
        ImGui.SameLine();
        if (ImGui.Button($"...##{field}")) ImGui.OpenPopup($"pick_{field}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(multi ? "Pick several" : "Pick one");
        ImGui.SameLine();
        ImGui.TextUnformatted(label);

        if (ImGui.BeginPopup($"pick_{field}"))
        {
            var opts = known().ToList();
            string cur = get() ?? "";
            var parts = cur.Split(sep == '|' ? new[] { '|', ',' } : new[] { sep },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (opts.Count == 0)
                ImGui.TextDisabled("(nothing known yet - values appear here\n as regions start using them)");
            float hgt = Math.Min(opts.Count, 18) * ImGui.GetTextLineHeightWithSpacing() + 6;
            if (opts.Count > 0 && ImGui.BeginChild($"opts_{field}", new Vector2(300, hgt)))
            {
                foreach (var opt in opts)
                {
                    if (multi)
                    {
                        bool on = parts.Contains(opt, StringComparer.OrdinalIgnoreCase);
                        if (ImGui.Checkbox(opt, ref on))
                        {
                            SnapshotFor($"edit {field} of {r.DefName}", r, $"{field}|{r.Uid}");
                            if (on) parts.Add(opt);
                            else parts.RemoveAll(p => p.Equals(opt, StringComparison.OrdinalIgnoreCase));
                            set(string.Join(sep, parts));
                            MarkDirty(r);
                        }
                    }
                    else if (ImGui.Selectable(opt, opt.Equals(cur, StringComparison.OrdinalIgnoreCase)))
                    {
                        SnapshotFor($"edit {field} of {r.DefName}", r, $"{field}|{r.Uid}");
                        set(opt);
                        MarkDirty(r);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            if (opts.Count > 0) ImGui.EndChild();
            ImGui.EndPopup();
        }
    }

    // standard Sphere region flags (the shard's scripts add nothing beyond these)
    static readonly string[] SphereRegionFlags =
    {
        "REGION_ANTIMAGIC_ALL", "REGION_ANTIMAGIC_DAMAGE", "REGION_ANTIMAGIC_GATE",
        "REGION_ANTIMAGIC_RECALL_IN", "REGION_ANTIMAGIC_RECALL_OUT", "REGION_ANTIMAGIC_TELEPORT",
        "REGION_FLAG_ANNOUNCE", "REGION_FLAG_ARENA", "REGION_FLAG_GLOBALNAME", "REGION_FLAG_GUARDED",
        "REGION_FLAG_INSTA_LOGOUT", "REGION_FLAG_NOBUILDING", "REGION_FLAG_NODECAY",
        "REGION_FLAG_NO_PVP", "REGION_FLAG_SAFE", "REGION_FLAG_SHIP", "REGION_FLAG_UNDERGROUND",
        "REGION_FLAG_WALK_NOBLOCKHEIGHT",
    };

    IEnumerable<string> KnownEvents() =>
        project.Regions.SelectMany(x => (x.Events ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat((project.DefaultEvents ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

    IEnumerable<string> KnownFlags() =>
        SphereRegionFlags
            .Concat(project.Regions.SelectMany(x => (x.Flags ?? "").Split(new[] { '|', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

    IEnumerable<string> KnownGroups() =>
        project.Regions.Select(x => x.Group).Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

    // reroll: of a bunch of random candidates, keep the one farthest from every other
    // region's color - neighboring regions stop looking alike after a click or two
    Color DistinctColor(RegionDef except)
    {
        var others = project.Regions.Where(o => o != except).Select(o => o.Color).ToList();
        var best = Palette.Next(Random.Shared.Next(1000));
        double bestScore = -1;
        for (int i = 0; i < 24; i++)
        {
            var c = HsvColor(Random.Shared.NextDouble() * 360.0,
                0.55 + Random.Shared.NextDouble() * 0.4, 0.8 + Random.Shared.NextDouble() * 0.2);
            double score = others.Count == 0 ? 1 : others.Min(o =>
            {
                double dr = c.R - o.R, dg = c.G - o.G, db = c.B - o.B;
                return dr * dr + dg * dg + db * db;
            });
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    static Color HsvColor(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60.0 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    void DrawHistoryWindow()
    {
        WinDefaults(340, 400, 320, 300);
        if (!ImGui.Begin("History", ref showHistory)) { ImGui.End(); return; }
        int steps = 0;
        foreach (var entry in undoMgr.History.Take(50))
        {
            int n = ++steps;
            if (ImGui.Selectable($"{entry}##h{n}"))
            {
                for (int i = 0; i < n; i++) DoUndo();
                break;
            }
        }
        if (steps == 0) ImGui.TextDisabled("(no history yet)");
        ImGui.End();
    }

    void DrawConnectPopup()
    {
        if (connectOpenRequest)
        {
            connectOpenRequest = false;
            netSettings = LoadNetSettings();
            var start = netSettings.Profiles.FirstOrDefault(x => x.Name == netSettings.LastProfile)
                        ?? netSettings.Profiles.FirstOrDefault();
            if (start != null)
            {
                cProfile = start.Name; cHost = start.Host; cPort = start.Port;
                cUser = start.User; cPass = Decode(start.PassB64);
                cMulsCache = start.MulsCache ?? "";
                cTarget = TargetFromName(start.Target);
            }
            else cTarget = scriptTarget;
            ImGui.OpenPopup("Connect to region server");
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Connect to region server", ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("Profile", cProfile))
        {
            foreach (var p in netSettings.Profiles)
            {
                if (ImGui.Selectable(p.Name, p.Name == cProfile))
                {
                    cProfile = p.Name; cHost = p.Host; cPort = p.Port;
                    cUser = p.User; cPass = Decode(p.PassB64);
                    cMulsCache = p.MulsCache ?? "";
                    cTarget = TargetFromName(p.Target);
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Save##prof"))
        {
            var name = cProfile.Trim().Length > 0 ? cProfile.Trim() : "default";
            netSettings.Profiles.RemoveAll(x => x.Name == name);
            netSettings.Profiles.Add(new ConnectProfile
            {
                Name = name, Host = cHost.Trim(), Port = cPort, User = cUser.Trim(), PassB64 = Encode(cPass),
                MulsCache = cMulsCache.Trim(), Target = TargetName(cTarget),
            });
            netSettings.LastProfile = name;
            SaveNetSettings(netSettings);
            cProfile = name;
        }
        ImGui.SameLine();
        if (ImGui.Button("Del##prof"))
        {
            netSettings.Profiles.RemoveAll(x => x.Name == cProfile);
            SaveNetSettings(netSettings);
        }

        ImGui.InputText("Profile name", ref cProfile, 32);
        ImGui.InputText("Host", ref cHost, 128);
        ImGui.InputInt("Port", ref cPort, 0);
        ImGui.InputText("User", ref cUser, 64);
        ImGui.InputText("Password", ref cPass, 64, ImGuiInputTextFlags.Password);
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##mulsdir", "muls folder (empty = default)", ref cMulsCache, 260);
        ImGui.SameLine();
        if (ImGui.Button("...##mulsdir"))
        {
            using var fd = new FolderBrowserDialog { Description = "Where should server muls be downloaded?" };
            if (fd.ShowDialog() == DialogResult.OK) cMulsCache = fd.SelectedPath;
        }
        ImGui.SameLine();
        if (ImGui.Button("Open##mulsdir")) OpenFolder(ResolvedMulsCacheDir());
        if (ImGui.IsItemHovered())
            TooltipLines(ResolvedMulsCacheDir());   // paths can contain % - never printf them
        ImGui.SameLine();
        ImGui.TextUnformatted("Muls folder");

        TargetCombo("Editing for", ref cTarget);
        ImGui.Spacing();

        if (ImGui.Button("Connect", new Vector2(120, 0)))
        {
            // the profile (with password) is only saved AFTER a successful login,
            // so a typo can't destroy a stored good password
            pendingProfileSave = new ConnectProfile
            {
                Name = cProfile.Trim().Length > 0 ? cProfile.Trim() : "default",
                Host = cHost.Trim(), Port = cPort, User = cUser.Trim(), PassB64 = Encode(cPass),
                MulsCache = cMulsCache.Trim(), Target = TargetName(cTarget),
            };
            // the shard's server type applies as soon as Connect is pressed, whether or not
            // the login succeeds: it only changes which fields the panel shows, and picking
            // it here then seeing the old fields after a failed login would be confusing
            if (scriptTarget != cTarget) { scriptTarget = cTarget; SaveUiSettings(); }
            ImGui.CloseCurrentPopup();
            ConnectNow();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    void DrawStatusBar()
    {
        var dl = ImGui.GetForegroundDrawList();
        var size = window.Size;
        float h = 26;
        dl.AddRectFilled(new Vector2(0, size.Y - h), new Vector2(size.X, size.Y), 0xE0141414);

        // only things worth reading: coords, action prompts and problems (how-to lives in Help > Controls)
        string hint = mode switch
        {
            Mode.Draw when pendingAnchor != null =>
                $"first corner {pendingAnchor.Value.x},{pendingAnchor.Value.y} - click the opposite corner",
            Mode.Corners when cornerPts.Count > 0 =>
                $"{cornerPts.Count} corner(s) - Enter/right-click/double-click finishes",
            Mode.Lasso or Mode.EraseLasso when drag == Drag.Lasso =>
                "release to close the lasso",
            Mode.EraseBox or Mode.EraseLasso or Mode.BrushErase or Mode.WandErase when selReg == null =>
                "select a region to erase from",
            Mode.Wand or Mode.WandErase when WandData == null =>
                "quick select: waiting for the map data to load...",
            Mode.Wand or Mode.WandErase when WandTypeUnavailable(WandData) =>
                "quick select: Land type match needs tiledata.mul in the muls folder",
            Mode.Wand when avoidOverlap && wandMask is { Count: 0 } =>
                "quick select: that tile belongs to another region (or is hidden by the Z filter)",
            Mode.Wand or Mode.WandErase when wandMask is { Count: 0 } =>
                "quick select: tile hidden by the Z filter - nothing to select here",
            Mode.Wand or Mode.WandErase when wandOverflow =>
                $"quick select: area too large (over {WandMaxTiles:N0} tiles) - use Lasso or Draw box",
            Mode.Wand or Mode.WandErase when drag == Drag.Wand && wandRadius > 0 =>
                $"quick select: radius {wandRadius} tiles - release to apply (a plain click = whole area)",
            Mode.Wand or Mode.WandErase when wandRects != null &&
                    wandRects.Count + (addToSelected && mode == Mode.Wand && selReg != null ? selReg.Rects.Count : 0) > maxRectsPerRegion =>
                $"quick select: too many boxes for one region (cap {maxRectsPerRegion:N0}) - smaller area or adjust tolerance",
            Mode.Wand when wandMask != null =>
                $"quick select: {wandMask.Count:N0} tiles -> {wandRects.Count:N0} box(es) - click to add",
            Mode.WandErase when wandMask != null =>
                $"quick select: {wandMask.Count:N0} tiles - click to erase from {selReg.DefName}",
            _ => null,
        };
        if (pickTile) hint = "PICK P: click the map (Esc cancels)";
        // CentrED shows what's under the cursor AS RENDERED: with the item glow active
        // the bar reports the picked item's own tile/Z/id ("Object <x,y,z>" style);
        // otherwise the hovered tile with its top visible surface Z (filter-aware)
        string left;
        if (hoveredItem is { } hv)
            left = $"X={hv.tx}  Y={hv.ty}  Z={hv.z}  item 0x{hv.id:X4}  zoom {zoom:0.##}x";
        else
        {
            string zPart = "";
            if (artView != null)
                zPart = $"  Z={artView.TopVisibleZ(mouseTile.x, mouseTile.y)}";
            else if (sharedMapData != null)
                zPart = $"  Z={sharedMapData.LandAt(mouseTile.x, mouseTile.y).z}";
            left = $"X={mouseTile.x}  Y={mouseTile.y}{zPart}  zoom {zoom:0.##}x";
        }
        if (hint != null) left += "  |  " + hint;
        if (detailHint != null) left += "  |  " + detailHint;
        if (StatusFresh) left += "  |  " + status;
        dl.AddText(new Vector2(8, size.Y - h + 5), 0xFFDDDDDD, left);

        // right side: muls source (its own color, so server pack vs local is obvious) + connection
        float x = size.X - 10 - ImGui.CalcTextSize(onlineText).X;
        dl.AddText(new Vector2(x, size.Y - h + 5), onlineColU, onlineText);
        // hovering the online text lists everyone; double-click a name jumps to their view
        if (net is { Connected: true } &&
            ImGui.IsMouseHoveringRect(new Vector2(x, size.Y - h), new Vector2(size.X, size.Y), false))
            usersPopupUntil = Environment.TickCount64 + 300;
        if (Environment.TickCount64 < usersPopupUntil)
            DrawUsersOverlay(new Vector2(size.X - 8, size.Y - h - 4));
        x -= ImGui.CalcTextSize(mapLabel).X + 24;
        dl.AddText(new Vector2(x, size.Y - h + 5), mapLabelCol, mapLabel);
    }

    long usersPopupUntil;

    // who is online, anchored above the status bar; stays while hovered so the rows
    // are clickable. Positions come from the presence relay (may lag a second or two).
    void DrawUsersOverlay(Vector2 anchor)
    {
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Always, new Vector2(1f, 1f));
        ImGui.SetNextWindowBgAlpha(0.94f);
        if (ImGui.Begin("##onlineusers", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.ChildWindows))
                usersPopupUntil = Environment.TickCount64 + 300;
            ImGui.TextDisabled("online - double-click a name to jump to their view");
            foreach (var u in onlineUsers)
            {
                var pos = LatestPosFor(u);
                bool self = u == net?.User && pos == null;
                string row = self ? $"{u}  (you)"
                    : pos == null ? $"{u}  (no position yet)"
                    : $"{u}  ({pos.Value.x},{pos.Value.y})";
                ImGui.Selectable(row);
                if (pos != null && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    CenterOn(pos.Value.x, pos.Value.y);
                    status = $"jumped to {u} at {pos.Value.x},{pos.Value.y}";
                }
            }
            if (onlineUsers.Count == 0) ImGui.TextDisabled("(no user list from the server yet)");
        }
        ImGui.End();
    }

    // ---- file operations --------------------------------------------------

    void PickMuls()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick the folder with map0LegacyMUL.uop / statics0.mul / radarcol.mul",
            SelectedPath = Directory.Exists(project.MulsDir) ? project.MulsDir : "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        project.MulsDir = dlg.SelectedPath;
        TearDownArtView();
        ClearChunkTextures();
        LoadMapAsync(dlg.SelectedPath, fromServer: false);
        if (detailMode) EnsureArtView();
    }

    void OpenProject()
    {
        if (project.Regions.Count > 0)
        {
            if (MessageBox.Show("Opening a project replaces the current one.\n\nA safety copy of the current work is autosaved first. Continue?",
                    "Open project", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                Directory.CreateDirectory(AppDir);
                project.Save(Path.Combine(AppDir, $"autosave-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
                foreach (var old in Directory.GetFiles(AppDir, "autosave-*.json").OrderByDescending(f => f).Skip(10))
                    File.Delete(old);
            }
            catch { }
        }
        using var dlg = new OpenFileDialog { Filter = "Region project (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            project = Project.Load(dlg.FileName);
            projectPath = dlg.FileName;
            undoMgr.Clear();
            dirtyPush.Clear();
            pendingRenames.Clear();
            selReg = null;
            selRect = null;
            if (!string.IsNullOrEmpty(project.MulsDir) && Directory.Exists(project.MulsDir))
                LoadMapAsync(project.MulsDir, fromServer: false);
            status = $"Opened {Path.GetFileName(dlg.FileName)} ({project.Regions.Count} regions)";
        }
        catch (Exception ex) { status = "Open failed: " + ex.Message; }
    }

    void SaveProject(bool saveAs)
    {
        if (saveAs || string.IsNullOrEmpty(projectPath) || projectPath == LastProjectPath)
        {
            using var dlg = new SaveFileDialog { Filter = "Region project (*.json)|*.json", FileName = "regions.json" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            projectPath = dlg.FileName;
        }
        try
        {
            project.Save(projectPath);
            status = $"Saved {projectPath}";
        }
        catch (Exception ex) { status = "Save failed: " + ex.Message; }
    }

    void ImportScp()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Sphere scripts (*.scp)|*.scp|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
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
            status = failed.Count > 0 ? "Import failed: " + string.Join("; ", failed) : "No regions found in the selected files.";
            return;
        }
        undoMgr.Snapshot($"import {dlg.FileNames.Length} file(s)", project, allRegions, includeExtraSections: true);
        project.Regions.AddRange(allRegions);
        project.ExtraSections.AddRange(allOther);
        foreach (var r in allRegions) MarkDirty(r);
        status = $"Imported {allRegions.Count} regions ({allRegions.Sum(r => r.Rects.Count)} boxes) from {dlg.FileNames.Length} file(s)" +
            (allOther.Count > 0 ? $" + {allOther.Count} kept block(s)" : "") +
            (failed.Count > 0 ? $"  |  FAILED: {string.Join("; ", failed)}" : "");
    }

    void ImportServuo()
    {
        using var dlg = new OpenFileDialog { Filter = "ServUO Regions.xml|*.xml|All files|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var regs = ServuoXml.Import(dlg.FileName);
            if (regs.Count == 0) { status = "No regions found in that file."; return; }
            foreach (var r in regs)
            {
                var baseName = r.DefName;
                for (int n = 2; project.Regions.Any(x => x.DefName.Equals(r.DefName, StringComparison.OrdinalIgnoreCase)); n++)
                    r.DefName = baseName + "_" + n;
                r.Color = Palette.Next(project.Regions.Count + regs.IndexOf(r));
            }
            undoMgr.Snapshot($"import ServUO ({regs.Count})", project, regs);
            Project.EnsureUids(regs);
            project.Regions.AddRange(regs);
            foreach (var r in regs) MarkDirty(r);
            status = $"Imported {regs.Count} ServUO region(s) ({regs.Sum(r => r.Rects.Count)} boxes).";
        }
        catch (Exception ex) { status = "ServUO import failed: " + ex.Message; }
    }

    // One row in the Import/Export submenus. The format matching "Editing for" gets a
    // note in the shortcut column so the right one is obvious without hiding the rest.
    void FormatItem(string label, int target, bool enabled, Action act)
    {
        if (ImGui.MenuItem(label, scriptTarget == target ? "your shard" : "", false, enabled)) act();
        if (!enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("root only while connected");
    }

    void ImportModernUo()
    {
        using var dlg = new OpenFileDialog { Filter = "ModernUO regions.json|*.json|All files|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var list = ModernUoJson.Import(File.ReadAllText(dlg.FileName));
            if (list.Count == 0) { status = "No regions found in that file."; return; }
            undoMgr.Snapshot($"import {Path.GetFileName(dlg.FileName)}", project, list);
            project.Regions.AddRange(list);
            foreach (var r in list) MarkDirty(r);
            status = $"Imported {list.Count} regions from ModernUO json.";
        }
        catch (Exception ex) { status = "Import failed: " + ex.Message; }
    }

    void ExportModernUo()
    {
        if (project.Regions.Count == 0) { status = "Nothing to export."; return; }
        using var dlg = new SaveFileDialog { Filter = "ModernUO regions.json|*.json", FileName = "regions.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, ModernUoJson.Export(project.Regions), new UTF8Encoding(false));
            status = "Exported ModernUO regions.json - merge into Distribution/Data/regions.json.";
        }
        catch (Exception ex) { status = "Export failed: " + ex.Message; }
    }

    void ExportServuo()
    {
        if (project.Regions.Count == 0) { status = "Nothing to export."; return; }
        using var dlg = new SaveFileDialog { Filter = "ServUO Regions.xml|*.xml", FileName = "Regions.xml" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, ServuoXml.ExportXml(project.Regions), new UTF8Encoding(false));
            status = "Exported ServUO Regions.xml - merge into Data\\Regions.xml and set region types.";
        }
        catch (Exception ex) { status = "Export failed: " + ex.Message; }
    }

    void ImportCentred()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "cedserver.xml|*.xml|All files|*.*",
            InitialDirectory = "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var list = CentredXml.ImportFromConfig(dlg.FileName, project.Regions.Count);
            undoMgr.Snapshot($"import {Path.GetFileName(dlg.FileName)}", project, list);
            project.Regions.AddRange(list);
            foreach (var r in list) MarkDirty(r);
            status = $"Imported {list.Count} CentrED regions";
        }
        catch (Exception ex) { status = "Import failed: " + ex.Message; }
    }

    void ExportSphere()
    {
        if (project.Regions.Count == 0) { status = "Nothing to export."; return; }
        using var dlg = new SaveFileDialog { Filter = "Sphere script (*.scp)|*.scp", FileName = "uore_areas.scp" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, SphereScp.Export(project, project.Regions), new UTF8Encoding(true));
            status = $"Exported {project.Regions.Count(r => r.Rects.Count > 0)} regions to {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { status = "Export failed: " + ex.Message; }
    }

    void ExportCentred()
    {
        if (project.Regions.Count == 0) { status = "Nothing to export."; return; }
        using var dlg = new SaveFileDialog { Filter = "XML snippet (*.xml)|*.xml", FileName = "uore_regions.xml" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, CentredXml.ExportSnippet(project.Regions), new UTF8Encoding(false));
            status = "Exported CentrED snippet - paste it over <Regions/> in cedserver.xml, or use Merge.";
        }
        catch (Exception ex) { status = "Export failed: " + ex.Message; }
    }

    void MergeCentred()
    {
        if (project.Regions.Count == 0) { status = "Nothing to merge."; return; }
        if (MessageBox.Show("Merge all regions into a cedserver.xml.\n\nSTOP the CentrED server first - it rewrites its config on shutdown.\nA timestamped backup is created. Continue?",
                "Merge into cedserver.xml", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "cedserver.xml|*.xml",
            InitialDirectory = "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var msg = CentredXml.MergeIntoConfig(dlg.FileName, project.Regions);
            MessageBox.Show(msg, "Merge done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { status = "Merge failed: " + ex.Message; }
    }

    // ImGui.Text/TextDisabled/TextWrapped are printf-style: a '%' in the string makes
    // native cimgui read garbage varargs and fail-fast (0xc0000409). Any text that can
    // contain '%' (paths, server/error messages) must go through these unformatted helpers.
    // Tooltip text. NOT TextDim: that pushes PushTextWrapPos(0), which inside an
    // auto-sizing tooltip resolves against a zero-width content region and shreds the
    // text into a one-character column. Wrap against an explicit width instead.
    static void TooltipLines(params string[] lines)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        foreach (var s in lines) ImGui.TextUnformatted(s);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // the "which server is this shard" picker, shared by the start gate, the connect
    // dialog and Options so all three stay in step
    static bool TargetCombo(string label, ref int target, float width = 150f)
    {
        bool changed = false;
        ImGui.SetNextItemWidth(width);
        bool open = ImGui.BeginCombo(label, TargetName(target));
        bool hovered = ImGui.IsItemHovered();   // the combo button, valid only right here
        if (open)
        {
            for (int i = 0; i < TargetNames.Length; i++)
            {
                if (ImGui.Selectable(TargetNames[i], i == target) && i != target) { target = i; changed = true; }
                if (ImGui.IsItemHovered()) TooltipLines(TargetInfo[i]);
            }
            ImGui.EndCombo();
        }
        // each entry explains itself on hover, so the panels no longer carry a caption
        if (hovered && !open) TooltipLines("Which server your shard runs", "Only changes which fields are shown");
        return changed;
    }

    static void TextDim(string s)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(s);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    static void TextWrapSafe(string s)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(s);
        ImGui.PopTextWrapPos();
    }

    // indeterminate progress: a band sliding steadily left-to-right and wrapping.
    // (a pulsing ProgressBar sweeps back and forth, which users read as "going backwards")
    static void IndeterminateBar(float height = 22)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        dl.AddRectFilled(pos, pos + new Vector2(w, height), ImGui.GetColorU32(ImGuiCol.FrameBg), 3);
        float band = w * 0.25f;
        float t = (float)(ImGui.GetTime() % 1.2) / 1.2f;
        float xoff = t * (w + band) - band;
        float x0 = Math.Max(0, xoff), x1 = Math.Min(w, xoff + band);
        if (x1 > x0)
            dl.AddRectFilled(pos + new Vector2(x0, 0), pos + new Vector2(x1, height),
                ImGui.GetColorU32(ImGuiCol.PlotHistogram), 3);
        ImGui.Dummy(new Vector2(w, height));
    }

    // Shown instead of the map while the server muls download / the map loads:
    // only a progress bar until everything is ready, never a half-loaded world.
    void DrawMulsSyncOverlay()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Always);
        ImGui.Begin("Preparing the map", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        string label = mulsSyncing
            ? (mulsProgress.Length > 0 ? mulsProgress : "Checking muls against the server...")
            : mapLabel;   // muls done, radar/map data still loading
        TextWrapSafe(label);
        if (mulsSyncing && mulsFrac >= 0)
            ImGui.ProgressBar(mulsFrac, new Vector2(-1, 22), $"{mulsFrac * 100:0}%");
        else
            IndeterminateBar();   // no % known yet: a one-direction marquee, never "backwards"
        ImGui.Spacing();
        TextDim("Muls cache: " + (activeMulsCache.Length > 0
            ? activeMulsCache
            : "%LocalAppData%\\UORegionEditor\\mulscache\\<host>_<port>"));
        if (mulsSyncing)
        {
            ImGui.Spacing();
            if (ImGui.Button("Cancel and work offline", new Vector2(220, 0))) GoOffline();
        }
        ImGui.End();
    }

    // CentrED-style start gate: no map and no tools until you either log in or
    // explicitly choose to work offline. Also reappears when the connection drops.
    float gateBlockH = 260;   // last frame's gate window height, for centring the block

    // Open a folder in Explorer, creating it when missing. Used from the start gate and
    // the connect popup, where the muls folder usually does not exist yet - before this
    // there was no way to reach it without logging in first.
    void OpenFolder(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) { status = "No folder set yet."; return; }
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { status = "open folder failed: " + ex.Message; }
    }

    // where THIS connect profile's muls will land (mirrors StartMulsSync)
    string ResolvedMulsCacheDir()
    {
        string custom = cMulsCache?.Trim() ?? "";
        if (custom.Length > 0) return custom;
        string host = $"{cHost?.Trim()}:{cPort}";
        return Path.Combine(AppDir, "mulscache",
            string.Join("_", host.Split(Path.GetInvalidFileNameChars())));
    }

    void DrawStartGate()
    {
        var vp = ImGui.GetMainViewport();
        var c = vp.GetCenter();

        // the mark fills what used to be a big empty black panel: logo on top, the
        // connect panel right under it, the pair centred as one block
        uint lt = LogoTexture();
        float lh = 0, lw = 0;
        if (lt != 0)
        {
            lh = Math.Clamp(vp.Size.Y * 0.40f, 150f, 430f);
            lw = lh * logoW / Math.Max(1, logoH);
        }
        float gap = lt != 0 ? 22f : 0f;
        float blockTop = c.Y - (lh + gap + gateBlockH) / 2f;
        if (lt != 0)
        {
            var p0 = new Vector2(c.X - lw / 2f, blockTop);
            ImGui.GetBackgroundDrawList().AddImage((nint)lt, p0, p0 + new Vector2(lw, lh),
                Vector2.Zero, Vector2.One, 0xFFFFFFFF);
        }

        ImGui.SetNextWindowPos(new Vector2(c.X, blockTop + lh + gap), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Always);
        ImGui.Begin("UO Region Editor", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);

        bool lostConnection = appMode == AppMode.Online;   // was online, connection dropped
        TextWrapSafe(lostConnection
            ? "Connection to the region server was lost."
            : "Work with your team on a region server, or edit locally on your own.");
        if (status.Length > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF4040D0);
            TextWrapSafe(status);   // error texts can contain '%' - never printf them
            ImGui.PopStyleColor();
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(lostConnection ? "Reconnect..." : "Connect to server...", new Vector2(190, 34)))
            connectOpenRequest = true;
        // the muls explanation used to sit on the page as four dim lines and made the
        // login screen feel cluttered - it lives on hover now
        if (ImGui.IsItemHovered())
            TooltipLines("Muls come from the server");
        ImGui.SameLine();
        if (ImGui.Button("Work offline", new Vector2(190, 34)))
            GoOffline();
        // offline never sees the connect dialog, so point it at the other way in
        if (ImGui.IsItemHovered())
            TooltipLines("Use your own muls folder", "Shard type: Options > Editing for");

        ImGui.Spacing();
        if (ImGui.Button("Exit", new Vector2(80, 0))) window.Close();
        ImGui.SameLine();
        if (ImGui.Button("Open muls folder", new Vector2(150, 0)))
            OpenFolder(!string.IsNullOrWhiteSpace(project.MulsDir) && Directory.Exists(project.MulsDir)
                ? project.MulsDir
                : Path.Combine(AppDir, "mulscache"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the muls folder");
        ImGui.SameLine();
        ImGui.TextDisabled($"v{AppVersion} by chemist");
        gateBlockH = ImGui.GetWindowSize().Y;
        ImGui.End();
    }
}
