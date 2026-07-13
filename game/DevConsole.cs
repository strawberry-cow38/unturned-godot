using Godot;
using System.Linq;
using SDG.Unturned;

namespace UnturnedGodot
{
    // F1 dev console (master). Press F1 to open a command bar at the top of the screen:
    //   give <item id|name>      -> spawns a WorldItem at the player's look-orb
    //   vehicle <id|name>        -> spawns that vehicle at the look-orb
    // TAB autocompletes (verb first, then the item/vehicle name); ENTER runs; ESC / F1 closes.
    // While it's open the mouse is freed, which gates player look/movement the same way the inventory UI does.
    public partial class DevConsole : CanvasLayer
    {
        public PlayerController Player;
        LineEdit _input;
        Label _log;
        static readonly string[] Verbs = { "give", "vehicle", "teleport", "plant", "skill" };
        readonly System.Collections.Generic.List<string> _history = new();
        int _histIdx;

        public override void _Ready()
        {
            Layer = 100;
            _log = new Label { Position = new Vector2(14, 10), Modulate = new Color(0.72f, 1f, 0.72f), Visible = false };
            _log.AddThemeFontSizeOverride("font_size", 15);
            AddChild(_log);
            _input = new LineEdit
            {
                PlaceholderText = "give <item> | vehicle <name>     (Tab autocompletes, Esc closes)",
                Visible = false,
                Size = new Vector2(820, 30),
                Position = new Vector2(14, 34),
            };
            AddChild(_input);
            _input.TextSubmitted += OnSubmit;
        }

        public override void _Input(InputEvent e)
        {
            if (e is not InputEventKey { Pressed: true } k) return;
            if (k.Keycode == Key.F1) { Toggle(); GetViewport().SetInputAsHandled(); }
            else if (_input.Visible && k.Keycode == Key.Tab) { Autocomplete(); GetViewport().SetInputAsHandled(); }
            else if (_input.Visible && k.Keycode == Key.Escape) { Toggle(); GetViewport().SetInputAsHandled(); }
            else if (_input.Visible && k.Keycode == Key.Up) { HistoryNav(-1); GetViewport().SetInputAsHandled(); }     // up = older command
            else if (_input.Visible && k.Keycode == Key.Down) { HistoryNav(1); GetViewport().SetInputAsHandled(); }    // down = newer (or a blank line at the end)
        }

        void Toggle()
        {
            bool open = !_input.Visible;
            _input.Visible = open;
            _log.Visible = open;
            if (open) { _input.GrabFocus(); Input.MouseMode = Input.MouseModeEnum.Visible; }
            else { _input.ReleaseFocus(); _input.Clear(); Input.MouseMode = Input.MouseModeEnum.Captured; }
        }

        void OnSubmit(string text)
        {
            var t = text.Trim();
            if (t.Length > 0 && (_history.Count == 0 || _history[^1] != t)) _history.Add(t);   // record for up/down recall (skip blanks + immediate dupes)
            _histIdx = _history.Count;   // reset the browse cursor to "past the newest"
            Run(t);
            _input.Clear();
            _input.GrabFocus();   // stay open for the next command
        }

        void Run(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            var parts = cmd.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";
            if (arg.Length == 0) { Log("usage: give <item> | vehicle <name>"); return; }
            Vector3 at = Player?.LookPoint() ?? Vector3.Zero;

            if (verb == "give")
            {
                var asset = ResolveItem(arg);
                if (asset == null) { Log($"no item matching '{arg}'"); return; }
                Player.DropWorldItem(new Item(asset.id), at);
                Log($"gave {asset.itemName} (#{asset.id})");
            }
            else if (verb == "vehicle" || verb == "veh")
            {
                string name = Vehicle.SpecNames.FirstOrDefault(n => n.Equals(arg, System.StringComparison.OrdinalIgnoreCase))
                           ?? Vehicle.SpecNames.FirstOrDefault(n => n.StartsWith(arg, System.StringComparison.OrdinalIgnoreCase));
                if (name == null) { Log($"no vehicle '{arg}' (try: {string.Join(", ", Vehicle.SpecNames)})"); return; }
                var v = Vehicle.BuildByName(name, (int)(GD.Randi() % 8));
                (Player?.GetParent() ?? GetTree().Root).AddChild(v);
                v.GlobalPosition = at + Vector3.Up * 1.5f;
                Log($"spawned {name}");
            }
            else if (verb == "teleport" || verb == "tp")
            {
                var m = MapNodes.Locations.Where(n => n.Name.Replace(" ", "").StartsWith(arg.Replace(" ", ""), System.StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Name.Length).ToList();
                if (m.Count == 0) { Log($"no location '{arg}'"); return; }
                Player?.TeleportTo(m[0].Pos + Vector3.Up * 3f);   // teleport the vehicle if driving, else the player (master)
                Log($"teleported to {m[0].Name}");
            }
            else if (verb == "plant")
            {
                // plant <crop> [grown]  -- spawn a growing crop at the look point (or already grown, for harvest testing)
                var pp = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                bool grown = pp.Length > 1 && (pp[1].Equals("grown", System.StringComparison.OrdinalIgnoreCase) || pp[1] == "1");
                if (!CropManager.Ready) { Log("no crop manager in this scene"); return; }
                var crop = CropManager.Plant(pp[0], at, grown);
                if (crop == null) { Log($"no crop '{pp[0]}' (try: carrot, corn, wheat, potato, tomato, pumpkin...)"); return; }
                Log($"planted {pp[0]}{(grown ? " (grown)" : "")} -- UG_FARMSPEED speeds growth; E near a grown crop to harvest");
            }
            else if (verb == "skill")
            {
                // skill <name> [level]  -- set a skill's level (e.g. `skill crafting 3`, `skill agriculture`) for testing gates/effects
                if (Player?.Skills == null) { Log("no player skills"); return; }
                var pp = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (!Player.Skills.TryFind(pp[0], out var sk, out var label)) { Log($"no skill '{pp[0]}' (try: crafting, agriculture, sharpshooter, strength...)"); return; }
                int target = pp.Length > 1 && int.TryParse(pp[1], out var lv) ? lv : sk.level + 1;
                sk.level = (byte)Mathf.Clamp(target, 0, sk.max);
                Log($"{label} skill -> level {sk.level}/{sk.max}");
            }
            else Log($"unknown command '{verb}' -- give / vehicle / teleport / plant / skill");
        }

        static ItemAsset ResolveItem(string arg)
        {
            if (ushort.TryParse(arg, out var id)) return Assets.find(id);
            string a = arg.Replace(" ", "");
            return Assets.all().FirstOrDefault(x => string.Equals(x.itemName, arg, System.StringComparison.OrdinalIgnoreCase))
                ?? Assets.all().Where(x => !string.IsNullOrEmpty(x.itemName) && x.itemName.Replace(" ", "").StartsWith(a, System.StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => x.itemName.Length).FirstOrDefault();
        }

        void Autocomplete()
        {
            var parts = _input.Text.Split(' ', 2, System.StringSplitOptions.None);
            if (parts.Length < 2)
            {
                var v = Verbs.FirstOrDefault(x => x.StartsWith(_input.Text.Trim(), System.StringComparison.OrdinalIgnoreCase));
                if (v != null) SetInput(v + " ");
                return;
            }
            string verb = parts[0].ToLowerInvariant(), pre = parts[1].Replace(" ", "");
            string match = verb.StartsWith("veh") ? Vehicle.SpecNames.FirstOrDefault(n => n.StartsWith(pre, System.StringComparison.OrdinalIgnoreCase))
                : (verb == "teleport" || verb == "tp") ? MapNodes.Locations.Select(n => n.Name).Where(n => n.Replace(" ", "").StartsWith(pre, System.StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).FirstOrDefault()
                : Assets.all().Select(x => x.itemName).Where(n => !string.IsNullOrEmpty(n) && n.Replace(" ", "").StartsWith(pre, System.StringComparison.OrdinalIgnoreCase))
                            .OrderBy(n => n.Length).FirstOrDefault();
            if (match != null) SetInput(parts[0] + " " + match);
        }

        void HistoryNav(int dir)   // up/down arrow: browse this session's command history (bash-style)
        {
            if (_history.Count == 0) return;
            _histIdx = Mathf.Clamp(_histIdx + dir, 0, _history.Count);
            SetInput(_histIdx < _history.Count ? _history[_histIdx] : "");   // one-past-the-end = a fresh blank line
        }
        void SetInput(string s) { _input.Text = s; _input.CaretColumn = s.Length; }
        void Log(string msg) { _log.Text = msg; GD.Print("[console] " + msg); }
    }

    // PEI's real named LOCATION nodes (towns/POIs), ripped byte-exact from Maps/PEI/Environment/Nodes.dat -> content/nodes.tsv
    // (tools/parse_nodes.py). Used by the F1 `teleport` command and the map.
    public static class MapNodes
    {
        public static readonly System.Collections.Generic.List<(string Name, Vector3 Pos)> Locations = Load();
        static System.Collections.Generic.List<(string, Vector3)> Load()
        {
            var list = new System.Collections.Generic.List<(string, Vector3)>();
            string path = ProjectSettings.GlobalizePath("res://content/nodes.tsv");
            if (!System.IO.File.Exists(path)) return list;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                var q = c[1].Split(',');
                if (q.Length < 3) continue;
                list.Add((c[0], new Vector3(float.Parse(q[0], ci), float.Parse(q[1], ci), float.Parse(q[2], ci))));
            }
            return list;
        }
    }
}
