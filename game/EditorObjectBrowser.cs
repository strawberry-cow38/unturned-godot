using Godot;

namespace UnturnedGodot
{
    // The Objects-mode browser palette (ported from EditorLevelObjectsUI): a scrollable, searchable list of the
    // placeable prop catalog. Pick a name -> it becomes the active placement prop (EditorObjects.PlaceName), then
    // click in the 3D view to drop it. The "Select / move" button switches to selection mode (PlaceName = null).
    // The dashboard shows this only while the Objects tab is active.
    public partial class EditorObjectBrowser : Control
    {
        readonly EditorObjects _objects;
        ItemList _list;
        LineEdit _search;
        EditorPropPreview _preview;
        Label _stageName;
        readonly System.Collections.Generic.Dictionary<string, int> _rowOf = new();   // prop name -> its row, for attaching a thumbnail when it finishes
        OptionButton _tableDrop;   // loot-crate table picker (shown only when a loot crate is selected)
        Control _crateBox;
        OptionButton _presetDrop;  // grid-power preset picker (shown only when a grid box is selected)
        LineEdit _gridNameEdit, _gridWattEdit;
        Control _gridBox;
        LineEdit _stationEdit;     // gas-pump station-id field (shown only when a gas pump is selected)
        Control _pumpBox;

        public EditorObjectBrowser(EditorObjects objects) { _objects = objects; }

        public override void _Ready()
        {
            Position = new Vector2(12, 60);   // top-left, just under the dashboard's mode-tab bar
            // Sized off the retail editor rather than guessed: EditorLevelObjectsUI runs a 230-wide column of
            // 200x30 controls on a 40px pitch. Ours was a 252 panel of 240-wide controls with 11px hint text,
            // which is why it read as cramped (strawberry_cow: "make the entire ui bigger and more user
            // friendly, using the source as a reference"). W is the one number to change.
            const int W = 330, CTRL = W - 24;
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(W, 700) };
            AddChild(panel);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 8);
            panel.AddChild(box);

            var head = new Label { Text = $"OBJECTS  ({_objects.Catalog.Count})" };
            head.AddThemeFontSizeOverride("font_size", 22);
            box.AddChild(head);

            // Retail puts the KEY on the tool button (SleekButtonIcon + ControlsSettings.tool_N) instead of
            // hiding the bindings in a legend. Same three tools, same keys, and they read as buttons you can
            // press rather than as a list of things to memorise.
            var tools = new HBoxContainer();
            tools.AddThemeConstantOverride("separation", 4);
            foreach (var (label, mode) in new (string, EditorGizmo.EMode)[]
                     { ("Move  Q", EditorGizmo.EMode.Translate),
                       ("Rotate  W", EditorGizmo.EMode.Rotate),
                       ("Scale  R", EditorGizmo.EMode.Scale) })
            {
                var m = mode;
                var tb = new Button { Text = label, CustomMinimumSize = new Vector2((CTRL - 8) / 3f, 34), FocusMode = FocusModeEnum.None };
                tb.Pressed += () => _objects.SetGizmoMode(m);
                tools.AddChild(tb);
            }
            box.AddChild(tools);

            var hint = new Label { Text = "click a prop → E places it · click a placed one → E moves it\nDel removes · Ctrl+Z undoes" };
            hint.AddThemeFontSizeOverride("font_size", 13);
            hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.85f));
            box.AddChild(hint);

            // ---- turntable ---------------------------------------------------------------------------
            // "maybe a 3d spinning preview of the selected prop". It shows the SELECTED prop, which is the one
            // E is about to place -- a preview of something other than what the button does would be worse
            // than none.
            _preview = new EditorPropPreview(_objects.PreviewMesh, _objects.PreviewMaterial);
            AddChild(_preview);
            _preview.ThumbReady += (name, tex) =>
            {
                if (_rowOf.TryGetValue(name, out var row) && row < _list.ItemCount) _list.SetItemIcon(row, tex);
            };

            var stageWrap = new SubViewportContainer
            {
                CustomMinimumSize = new Vector2(CTRL, EditorPropPreview.StagePx),
                Stretch = false, MouseFilter = MouseFilterEnum.Ignore,
            };
            box.AddChild(stageWrap);
            // The preview builds its viewports in _Ready, which has already run -- AddChild on a node that is
            // itself in the tree fires _Ready synchronously -- so Stage exists by now. Move it under the
            // container that displays it; it was parented to the preview only so it had somewhere to live.
            var stage = _preview.Stage;
            stage.GetParent()?.RemoveChild(stage);
            stageWrap.AddChild(stage);

            _stageName = new Label { Text = "—", HorizontalAlignment = HorizontalAlignment.Center };
            _stageName.AddThemeFontSizeOverride("font_size", 14);
            box.AddChild(_stageName);

            // loot-crate table picker -- appears when a placed ★ Loot Crate is selected
            var cbox = new VBoxContainer { Visible = false };
            _crateBox = cbox;
            var cl = new Label { Text = "▼ LOOT CONTAINER — item table:" };
            cl.AddThemeFontSizeOverride("font_size", 13);
            cl.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
            cbox.AddChild(cl);
            _tableDrop = new OptionButton { CustomMinimumSize = new Vector2(CTRL, 32) };
            for (int i = 0; i < LootTables.TableCount; i++) _tableDrop.AddItem($"{i}: {LootTables.TableName(i)}", i);
            _tableDrop.ItemSelected += idx => _objects.SetSelectedCrateTable((int)idx);
            cbox.AddChild(_tableDrop);
            box.AddChild(cbox);

            // grid-power config -- appears when a placed ⚡ Grid Power box is selected
            var gbox = new VBoxContainer { Visible = false };
            _gridBox = gbox;
            var gl = new Label { Text = "▼ GRID POWER — preset / name / wattage:" };
            gl.AddThemeFontSizeOverride("font_size", 13);
            gl.AddThemeColorOverride("font_color", new Color(0.5f, 0.85f, 1f));
            gbox.AddChild(gl);
            _presetDrop = new OptionButton { CustomMinimumSize = new Vector2(CTRL, 32) };
            for (int i = 0; i < GridPowerSource.Presets.Length; i++) { var pr = GridPowerSource.Presets[i]; _presetDrop.AddItem($"{pr.name} ({pr.watts:0}W)"); }
            _presetDrop.AddItem("Custom");   // last item -> keep the current custom value
            _presetDrop.ItemSelected += id => { if ((int)id >= 0 && (int)id < GridPowerSource.Presets.Length) { var pr = GridPowerSource.Presets[(int)id]; _objects.SetSelectedGridName(pr.name); _objects.SetSelectedGridWatts(pr.watts); SyncGridPicker(); } };
            gbox.AddChild(_presetDrop);
            _gridNameEdit = new LineEdit { PlaceholderText = "name (e.g. Main Substation)", CustomMinimumSize = new Vector2(CTRL, 32) };
            _gridNameEdit.TextChanged += t => _objects.SetSelectedGridName(t);
            gbox.AddChild(_gridNameEdit);
            _gridWattEdit = new LineEdit { PlaceholderText = "watts (Enter to set)", CustomMinimumSize = new Vector2(CTRL, 32) };
            _gridWattEdit.TextSubmitted += t => { if (float.TryParse(t, out var w)) { _objects.SetSelectedGridWatts(w); SyncGridPicker(); } };
            gbox.AddChild(_gridWattEdit);
            box.AddChild(gbox);

            // gas-pump station-id config -- appears when a placed ⛽ Gas Pump is selected
            var pbox = new VBoxContainer { Visible = false };
            _pumpBox = pbox;
            var pl = new Label { Text = "▼ GAS PUMP — station id (same id shares a tank):" };
            pl.AddThemeFontSizeOverride("font_size", 13);
            pl.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.3f));
            pbox.AddChild(pl);
            _stationEdit = new LineEdit { PlaceholderText = "station id (Enter to set)", CustomMinimumSize = new Vector2(CTRL, 32) };
            _stationEdit.TextSubmitted += t => { if (int.TryParse(t, out var s)) { _objects.SetSelectedStationId(s); SyncPumpPicker(); } };
            pbox.AddChild(_stationEdit);
            box.AddChild(pbox);

            _objects.SelectionChanged += SyncCratePicker;
            _objects.SelectionChanged += SyncGridPicker;
            _objects.SelectionChanged += SyncPumpPicker;

            var sel = new Button { Text = "Select / move only", CustomMinimumSize = new Vector2(CTRL, 34), FocusMode = FocusModeEnum.None };
            sel.Pressed += () => { _objects.ClearPlaceType(); _list.DeselectAll(); ShowPreview(null); };
            box.AddChild(sel);

            // The search box has to be EASY TO LEAVE. While it holds focus every keypress goes into it and
            // never reaches the editor's _UnhandledInput -- correct Godot behaviour that reads as the editor
            // ignoring you (strawberry_cow: "really 'sticky', my inputs keep getting eaten by it"). Nothing
            // releases focus by itself, so there are three ways out: Enter, Escape, or clicking the world
            // (EditorObjects releases it on any viewport click).
            _search = new LineEdit { PlaceholderText = "search…  (Enter or Esc returns to the editor)", CustomMinimumSize = new Vector2(CTRL, 34) };
            _search.TextChanged += _ => Rebuild();
            _search.TextSubmitted += _ => _search.ReleaseFocus();
            _search.GuiInput += ev =>
            {
                if (ev is InputEventKey { Pressed: true } ke && ke.Keycode == Key.Escape)
                {
                    _search.ReleaseFocus();
                    AcceptEvent();   // swallow it -- Escape here means "leave the box", not "clear the selection"
                }
            };
            box.AddChild(_search);

            _list = new ItemList
            {
                CustomMinimumSize = new Vector2(CTRL, 300),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                FocusMode = Control.FocusModeEnum.None,
                FixedIconSize = new Vector2I(EditorPropPreview.ThumbPx / 2, EditorPropPreview.ThumbPx / 2),
            };
            _list.AddThemeFontSizeOverride("font_size", 15);
            _list.ItemSelected += idx =>
            {
                var n = _list.GetItemText((int)idx);
                _objects.SetPlaceType(n);   // pick the type to E-place (+ clears any instance selection)
                ShowPreview(n);
            };
            box.AddChild(_list);

            // A building baked in the Buildings tab has to show up here without reopening the editor, or the
            // bake looks like it silently failed.
            // A re-bake under the same name must not keep serving the old prop's picture, so the cached
            // thumbnails go with the catalog -- same reason EditorObjects drops its mesh cache on reload.
            _objects.CatalogChanged += () => { _preview.Clear(); Rebuild(); };

            Rebuild();
            ShowPreview(_objects.PlaceName);   // the palette opens with a prop already armed; show THAT one
        }

        void Rebuild()
        {
            string q = _search.Text.Trim().ToLower();
            _list.Clear();
            _rowOf.Clear();
            foreach (var name in _objects.Catalog)
            {
                if (q.Length > 0 && !name.ToLower().Contains(q)) continue;
                int row = _list.AddItem(name);
                _rowOf[name] = row;
                // Ask for a picture. Already-drawn props come back instantly; the rest arrive via ThumbReady,
                // which is why the row index is kept. Asking for all of them at once is fine -- the preview
                // renders one per couple of frames and a filtered search is usually a handful of rows.
                var tex = _preview.Thumb(name);
                if (tex != null) _list.SetItemIcon(row, tex);
            }
        }

        /// <summary>Put a prop on the turntable and name it underneath.</summary>
        void ShowPreview(string name)
        {
            _preview.ShowOnStage(name);
            if (_stageName != null) _stageName.Text = name ?? "—";
        }

        void SyncCratePicker()   // selection changed: show the table dropdown for a selected loot crate + reflect its table
        {
            if (_crateBox == null) return;
            _crateBox.Visible = _objects.CrateSelected;
            if (_objects.CrateSelected && _tableDrop != null && _tableDrop.ItemCount > 0)
                _tableDrop.Selected = Mathf.Clamp(_objects.SelectedCrateTable, 0, _tableDrop.ItemCount - 1);
        }

        void SyncGridPicker()   // selection changed: show the grid-power config for a selected box + reflect its name/wattage/preset
        {
            if (_gridBox == null) return;
            _gridBox.Visible = _objects.GridSelected;
            if (!_objects.GridSelected) return;
            if (_gridNameEdit != null && !_gridNameEdit.HasFocus()) _gridNameEdit.Text = _objects.SelectedGridName;
            if (_gridWattEdit != null && !_gridWattEdit.HasFocus()) _gridWattEdit.Text = _objects.SelectedGridWatts.ToString("0");
            if (_presetDrop != null)
            {
                int match = GridPowerSource.Presets.Length;   // no preset matches -> "Custom" (the last item)
                for (int i = 0; i < GridPowerSource.Presets.Length; i++) if (Mathf.Abs(GridPowerSource.Presets[i].watts - _objects.SelectedGridWatts) < 0.5f) { match = i; break; }
                _presetDrop.Selected = match;
            }
        }

        void SyncPumpPicker()   // selection changed: show the station-id field for a selected gas pump + reflect it
        {
            if (_pumpBox == null) return;
            _pumpBox.Visible = _objects.GasPumpSelected;
            if (_objects.GasPumpSelected && _stationEdit != null && !_stationEdit.HasFocus())
                _stationEdit.Text = _objects.SelectedStationId.ToString();
        }
    }
}
