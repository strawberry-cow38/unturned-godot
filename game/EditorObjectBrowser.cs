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
            _objects.SelectionChanged += SyncCratePicker;

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
    }
}
