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
        OptionButton _tableDrop;   // loot-crate table picker (shown only when a loot crate is selected)
        Control _crateBox;
        OptionButton _presetDrop;  // grid-power preset picker (shown only when a grid box is selected)
        LineEdit _gridNameEdit, _gridWattEdit;
        Control _gridBox;

        public EditorObjectBrowser(EditorObjects objects) { _objects = objects; }

        public override void _Ready()
        {
            Position = new Vector2(12, 60);   // top-left, just under the dashboard's mode-tab bar
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(252, 580) };
            AddChild(panel);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 5);
            panel.AddChild(box);

            var head = new Label { Text = $"OBJECTS  ({_objects.Catalog.Count})" };
            head.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(head);

            var hint = new Label { Text = "click a prop below → E places it\nclick a placed prop → E moves it\nT gizmo · Del · Ctrl+Z undo" };
            hint.AddThemeFontSizeOverride("font_size", 11);
            hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.85f));
            box.AddChild(hint);

            // loot-crate table picker -- appears when a placed ★ Loot Crate is selected
            var cbox = new VBoxContainer { Visible = false };
            _crateBox = cbox;
            var cl = new Label { Text = "▼ LOOT CONTAINER — item table:" };
            cl.AddThemeFontSizeOverride("font_size", 13);
            cl.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
            cbox.AddChild(cl);
            _tableDrop = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
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
            _presetDrop = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            for (int i = 0; i < GridPowerSource.Presets.Length; i++) { var pr = GridPowerSource.Presets[i]; _presetDrop.AddItem($"{pr.name} ({pr.watts:0}W)"); }
            _presetDrop.AddItem("Custom");   // last item -> keep the current custom value
            _presetDrop.ItemSelected += id => { if ((int)id >= 0 && (int)id < GridPowerSource.Presets.Length) { var pr = GridPowerSource.Presets[(int)id]; _objects.SetSelectedGridName(pr.name); _objects.SetSelectedGridWatts(pr.watts); SyncGridPicker(); } };
            gbox.AddChild(_presetDrop);
            _gridNameEdit = new LineEdit { PlaceholderText = "name (e.g. Main Substation)", CustomMinimumSize = new Vector2(240, 0) };
            _gridNameEdit.TextChanged += t => _objects.SetSelectedGridName(t);
            gbox.AddChild(_gridNameEdit);
            _gridWattEdit = new LineEdit { PlaceholderText = "watts (Enter to set)", CustomMinimumSize = new Vector2(240, 0) };
            _gridWattEdit.TextSubmitted += t => { if (float.TryParse(t, out var w)) { _objects.SetSelectedGridWatts(w); SyncGridPicker(); } };
            gbox.AddChild(_gridWattEdit);
            box.AddChild(gbox);

            _objects.SelectionChanged += SyncCratePicker;
            _objects.SelectionChanged += SyncGridPicker;

            var sel = new Button { Text = "Select-only (clear place type)" };
            sel.Pressed += () => { _objects.ClearPlaceType(); _list.DeselectAll(); };
            box.AddChild(sel);

            _search = new LineEdit { PlaceholderText = "search…" };
            _search.TextChanged += _ => Rebuild();
            box.AddChild(_search);

            _list = new ItemList { CustomMinimumSize = new Vector2(240, 500), SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
            _list.ItemSelected += idx => _objects.SetPlaceType(_list.GetItemText((int)idx));   // pick the type to E-place (+ clears any instance selection)
            box.AddChild(_list);

            Rebuild();
        }

        void Rebuild()
        {
            string q = _search.Text.Trim().ToLower();
            _list.Clear();
            foreach (var name in _objects.Catalog)
                if (q.Length == 0 || name.ToLower().Contains(q)) _list.AddItem(name);
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
    }
}
