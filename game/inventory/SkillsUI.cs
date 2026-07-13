using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // The skills menu: the 3 specialities x their skills, each showing level/max + an Upgrade button that spends the
    // player's XP pool (PlayerSkills.TryUpgrade at the source cost). Styled like CraftingUI (dimmed centred panel).
    // Toggled by a keybind (PlayerController). Source surface = PlayerDashboardSkillsUI.
    public partial class SkillsUI : CanvasLayer
    {
        public PlayerController Player;
        const int PANELW = 640, PANELH = 680;

        Control _root;
        Panel _panel;
        Label _header;
        VBoxContainer _list;
        bool _open;
        public bool IsOpen => _open;

        static readonly string[] SpecNames = { "OFFENSE", "DEFENSE", "SUPPORT" };
        static readonly string[][] SkillNames =
        {
            new[] { "Overkill", "Sharpshooter", "Dexterity", "Cardio", "Exercise", "Diving", "Parkour" },
            new[] { "Sneakybeaky", "Vitality", "Immunity", "Toughness", "Strength", "Warmblooded", "Survival" },
            new[] { "Healing", "Crafting", "Outdoors", "Cooking", "Fishing", "Agriculture", "Mechanic", "Engineer" },
        };

        public override void _Ready()
        {
            Layer = 11;
            Visible = false;
            _root = new Control();
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(_root);
            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.72f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(dim);
            _panel = new Panel { CustomMinimumSize = new Vector2(PANELW, PANELH), Size = new Vector2(PANELW, PANELH) };
            _root.AddChild(_panel);
            _header = new Label { Position = new Vector2(20, 14), Size = new Vector2(PANELW - 40, 30) };
            _header.AddThemeFontSizeOverride("font_size", 24);
            _panel.AddChild(_header);
            var scroll = new ScrollContainer { Position = new Vector2(14, 52), Size = new Vector2(PANELW - 28, PANELH - 66) };
            scroll.CustomMinimumSize = scroll.Size;
            _panel.AddChild(scroll);
            _list = new VBoxContainer { CustomMinimumSize = new Vector2(PANELW - 40, 0) };
            scroll.AddChild(_list);
        }

        public override void _Process(double delta)
        {
            if (_open && _panel != null)
                _panel.Position = new Vector2((_root.Size.X - PANELW) / 2f, (_root.Size.Y - PANELH) / 2f);
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        public void Open() { _open = true; Visible = true; Refresh(); }
        public void Close() { _open = false; Visible = false; }

        void Refresh()
        {
            foreach (Node c in _list.GetChildren()) c.QueueFree();
            var sk = Player?.Skills;
            if (sk == null) return;
            _header.Text = $"SKILLS    ·    {sk.experience} XP";
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
            {
                var head = new Label { Text = SpecNames[s] };
                head.AddThemeFontSizeOverride("font_size", 18);
                _list.AddChild(head);
                var arr = sk.skills[s];
                for (int i = 0; i < arr.Length; i++)
                {
                    int spec = s, idx = i;   // capture for the closure
                    var skill = arr[i];
                    var row = new HBoxContainer();
                    var lbl = new Label { Text = $"    {SkillNames[s][i]}   [{skill.level}/{skill.max}]", CustomMinimumSize = new Vector2(PANELW - 230, 0), VerticalAlignment = VerticalAlignment.Center };
                    lbl.AddThemeFontSizeOverride("font_size", 14);
                    row.AddChild(lbl);
                    bool maxed = skill.level >= skill.max;
                    var btn = new Button { Text = maxed ? "MAX" : $"Up  ({skill.Cost} XP)", CustomMinimumSize = new Vector2(160, 30) };
                    btn.Disabled = maxed || sk.experience < skill.Cost;
                    btn.Pressed += () => { if (sk.TryUpgrade(spec, idx)) Refresh(); };
                    row.AddChild(btn);
                    _list.AddChild(row);
                }
            }
        }
    }
}
