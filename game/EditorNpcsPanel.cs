using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>The NPC tab's palette: who you are about to place, who is already down, and the way into the
    /// per-character editor (master: "place preset/custom npcs. separate npc editor for clothes, appearance,
    /// dialogue, trades etc").
    ///
    /// A SCROLLING LIST, not the Spawns tab's grid of buttons. Spawns has five categories; this has forty
    /// people whose names are the only way to tell them apart, and forty two-word buttons in a grid is a wall
    /// nobody reads. Each row carries the count already placed, because the question you actually have while
    /// dressing a map is "did I already put the mechanic somewhere".</summary>
    public partial class EditorNpcsPanel : Control
    {
        readonly EditorNpcs _npcs;
        VBoxContainer _rows;
        Label _picked, _summary;
        readonly System.Collections.Generic.List<(string key, Button btn, Label count)> _entries = new();

        public EditorNpcsPanel(EditorNpcs npcs) { _npcs = npcs; }

        /// <summary>Raised when a row's edit/view button is pressed. Wired by the dashboard to the character
        /// editor, so this panel does not have to know that window exists.</summary>
        [Signal] public delegate void EditRequestedEventHandler(string key);

        /// <summary>Raised by the New button (master: "is there a 'create new npc' button?").</summary>
        [Signal] public delegate void NewRequestedEventHandler();

        public override void _Ready()
        {
            Position = new Vector2(12, 60);
            var panel = new PanelContainer();
            AddChild(panel);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(268, 0) };
            box.AddThemeConstantOverride("separation", 4);
            panel.AddChild(box);

            var head = new Label { Text = "NPCS" };
            head.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(head);

            var mk = new Button { Text = "+  New character", CustomMinimumSize = new Vector2(0, 28) };
            mk.Pressed += () => EmitSignal(SignalName.NewRequested);
            box.AddChild(mk);

            _picked = Dim("");
            box.AddChild(_picked);
            box.AddChild(Dim("click ground to place  ·  click a person to select"));
            box.AddChild(Dim(", / .  rotate   ·   Del  remove   ·   Tab  next"));
            box.AddChild(new HSeparator());

            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(268, 420),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                ClipContents = true,
            };
            box.AddChild(scroll);
            _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _rows.AddThemeConstantOverride("separation", 1);
            scroll.AddChild(_rows);

            BuildRows();

            box.AddChild(new HSeparator());
            _summary = Dim("");
            box.AddChild(_summary);
            Refresh();
        }


        /// <summary>One row per catalog character. Split out of _Ready so Rebuild can re-run it.</summary>
        void BuildRows()
        {
                for (int i = 0; i < _npcs.Keys.Count; i++)
                {
                    string key = _npcs.Keys[i];
                    int idx = i;   // captured: IReadOnlyList has no IndexOf, and re-deriving it per click is a scan
                    var def = NpcCatalog.CharacterByKey(key);
                    var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    row.AddThemeConstantOverride("separation", 2);
                    _rows.AddChild(row);

                    string k = key;
                    var b = new Button
                    {
                        Text = TradeRules.PlainText(def?.Name) is { Length: > 0 } n ? n : key,
                        Alignment = HorizontalAlignment.Left,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        CustomMinimumSize = new Vector2(0, 26),
                        ClipText = true,
                    };
                    b.Pressed += () => { _npcs.SetPick(idx); Refresh(); };
                    row.AddChild(b);

                    var count = new Label { Text = "", CustomMinimumSize = new Vector2(24, 0), HorizontalAlignment = HorizontalAlignment.Right };
                    count.AddThemeFontSizeOverride("font_size", 11);
                    row.AddChild(count);

                    // The way into the character editor. On the ROW rather than one button for "the selected one":
                    // you edit a person by pointing at them, and a mode you have to select into first is a mode you
                    // forget you are in.
                    //
                    // ⚠ THE ICON TELLS YOU WHETHER IT WILL LET YOU. Vanilla characters are locked (master: "lock all
                    // the vanilla game npcs from edits"), so they get a padlock -- the window still opens, to look at
                    // them and to duplicate, but a pencil on a row you cannot edit is a promise the window breaks.
                    bool custom = NpcCatalog.IsCustom(key);
                    var edit = new Button
                    {
                        Text = custom ? "✎" : "🔒",
                        CustomMinimumSize = new Vector2(26, 26),
                        TooltipText = custom ? "Edit appearance, clothes, dialogue and trades"
                                             : "Vanilla character — view, or duplicate into an editable copy",
                    };
                    edit.Pressed += () => EmitSignal(SignalName.EditRequested, k);
                    row.AddChild(edit);

                    _entries.Add((k, b, count));
                }
        }

        static Label Dim(string t)
        {
            var l = new Label { Text = t };
            l.AddThemeFontSizeOverride("font_size", 11);
            l.AddThemeColorOverride("font_color", new Color(0.62f, 0.64f, 0.68f));
            return l;
        }

        /// <summary>Repaint the picked highlight and the counts. Driven from _Process rather than from an event
        /// because placements also arrive from the 3D view and from undo, and a panel that only updates when a
        /// BUTTON was pressed goes stale exactly when you are working in the viewport.</summary>
        public void Refresh()
        {
            if (_entries.Count == 0) return;
            string pick = _npcs.PickKey;
            int total = 0;
            foreach (var (key, btn, count) in _entries)
            {
                int n = _npcs.CountOf(key);
                total += n;
                count.Text = n > 0 ? n.ToString() : "";
                btn.Modulate = key == pick ? new Color(1f, 0.86f, 0.35f) : Colors.White;
            }
            _picked.Text = "placing:  " + _npcs.PickName;
            _summary.Text = $"{total} placed  ·  saves to editor_<map>_npcs.txt";
        }

        /// <summary>Rebuild the roster from the catalog. Called when the character editor saves a NEW key --
        /// a list built once at _Ready cannot show a person who did not exist then, and "my new character is
        /// missing" would be a reopen-the-editor bug rather than the one-line refresh it is.</summary>
        public void Rebuild()
        {
            foreach (var c in _rows.GetChildren()) ((Node)c).QueueFree();
            _entries.Clear();
            _npcs.ReloadKeys();
            BuildRows();
            Refresh();
        }

        double _t;
        public override void _Process(double delta)
        {
            if (!Visible) return;
            _t += delta;
            if (_t < 0.25) return;   // four times a second: the counts are not worth a per-frame walk of forty rows
            _t = 0;
            Refresh();
        }
    }
}
