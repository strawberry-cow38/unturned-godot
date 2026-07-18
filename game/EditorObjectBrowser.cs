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
    }
}
