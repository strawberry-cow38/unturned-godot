using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The quest log, in the Information page (master 2026-09-11: "quest log goes into the information
    /// panel, a summary appears in the top right").
    ///
    /// SHOWS WHAT YOU HAVE TOUCHED, not the catalog. There are 30 quests and you have taken three; a log that
    /// lists all thirty is a browser for content you have not met, and it buries the two lines that matter. The
    /// count of ones you have not started is worth one dim line at the bottom, and nothing more.
    ///
    /// Ordered ready -> active -> completed, because that is the order you want to act on them: the ones you can
    /// hand in now, the ones you are doing, and then the record. Clicking one TRACKS it, which is what the
    /// top-right summary shows -- the log is where you choose, the corner is where you glance.</summary>
    public partial class QuestLogPanel : Control
    {
        PlayerController _player;
        VBoxContainer _list;
        Label _head, _foot;
        double _t;
        string _shown = "";

        public QuestLogPanel(PlayerController p) { _player = p; MouseFilter = MouseFilterEnum.Pass; }

        public override void _Ready()
        {
            var panel = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            UITheme.Panel(panel, true, UITheme.RadiusCell);   // the roster's treatment: an opaque slab, not glass over trees
            panel.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(panel);

            // FontBody/TextDim like the roster's "PLAYERS" -- Accent is already spent on this screen by every
            // town dot and marker pin, so a yellow column title is a fourth thing claiming to be important.
            _head = new Label { Position = new Vector2(14, 10), MouseFilter = MouseFilterEnum.Ignore };
            _head.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            _head.AddThemeColorOverride("font_color", UITheme.TextDim);
            AddChild(_head);

            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, ClipContents = true };
            scroll.SetAnchorsPreset(LayoutPreset.FullRect);
            scroll.OffsetLeft = 8; scroll.OffsetTop = 34; scroll.OffsetRight = -8; scroll.OffsetBottom = -26;
            AddChild(scroll);

            _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(_list);

            _foot = new Label { MouseFilter = MouseFilterEnum.Ignore };
            _foot.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
            _foot.AddThemeColorOverride("font_color", UITheme.TextDim);
            _foot.SetAnchorsPreset(LayoutPreset.BottomLeft);
            _foot.Position = new Vector2(14, -20);
            AddChild(_foot);

            Rebuild();
        }

        public override void _Process(double delta)
        {
            if (!Visible) return;
            _t += delta;
            if (_t < 0.3) return;
            _t = 0;
            Rebuild();
        }

        void Rebuild()
        {
            if (_player == null || !GodotObject.IsInstanceValid(_player)) return;
            var w = _player.NpcState;

            // Ready first, then active, then completed. Sorted by STATUS rather than by id, because an id is an
            // extraction artefact and "what can I hand in" is the actual question.
            var ready = new List<NpcQuestDef>();
            var active = new List<NpcQuestDef>();
            var done = new List<NpcQuestDef>();
            int untouched = 0;
            foreach (var q in NpcCatalog.Quests)
                switch (QuestRules.StatusOf(q, w))
                {
                    case ENpcQuestStatus.Ready: ready.Add(q); break;
                    case ENpcQuestStatus.Active: active.Add(q); break;
                    case ENpcQuestStatus.Completed: done.Add(q); break;
                    default: untouched++; break;
                }

            // Repaint only when something would LOOK different -- this runs three times a second over every
            // quest's objectives, and rebuilding identical rows also throws away the scroll position you set.
            var sb = new System.Text.StringBuilder();
            sb.Append(_player.TrackedQuest).Append('\u001F');
            foreach (var q in ready) sb.Append('R').Append(q.Id);
            foreach (var q in active)
            {
                sb.Append('A').Append(q.Id);
                foreach (var (text, ok) in QuestRules.Objectives(q, w)) sb.Append(ok ? '1' : '0').Append(text);
            }
            foreach (var q in done) sb.Append('C').Append(q.Id);
            string state = sb.ToString();
            if (state == _shown) return;
            _shown = state;

            foreach (var c in _list.GetChildren()) ((Node)c).QueueFree();
            foreach (var q in ready) AddQuest(q, w, ENpcQuestStatus.Ready);
            foreach (var q in active) AddQuest(q, w, ENpcQuestStatus.Active);
            foreach (var q in done) AddQuest(q, w, ENpcQuestStatus.Completed);

            _head.Text = ready.Count + active.Count + done.Count == 0
                ? "QUESTS"
                : $"QUESTS   ·   {ready.Count} ready   ·   {active.Count} active   ·   {done.Count} done";
            _foot.Text = ready.Count + active.Count + done.Count == 0
                ? $"Nothing taken yet. {untouched} quest(s) are out there; people give them out."
                : $"{untouched} not started   ·   click a quest to track it in the corner";
        }

        void AddQuest(NpcQuestDef q, INpcWorld w, ENpcQuestStatus status)
        {
            bool tracked = _player.TrackedQuest == q.Id;
            var row = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 1);
            _list.AddChild(row);

            var head = new Button
            {
                Text = (tracked ? "▸  " : "    ") + TradeRules.PlainText(q.Name)
                     + (status == ENpcQuestStatus.Ready ? "   —   ready to hand in" : ""),
                Alignment = HorizontalAlignment.Left,
                Flat = true,
                CustomMinimumSize = new Vector2(0, 26),
                ClipText = true,
            };
            UITheme.Label(head, UITheme.FontHeading,
                status == ENpcQuestStatus.Ready ? UITheme.Good
                : status == ENpcQuestStatus.Completed ? UITheme.TextDisabled
                : tracked ? UITheme.Accent : UITheme.Text);
            int id = q.Id;
            // Clicking a COMPLETED one still tracks it, and that is deliberate: it is how you read its
            // objectives back without a second control for "expand".
            head.Pressed += () => { _player.TrackedQuest = _player.TrackedQuest == id ? 0 : id; _shown = ""; Rebuild(); };
            row.AddChild(head);

            // A completed quest's objectives are all ticked by definition -- printing six green lines for each
            // is a wall of noise under the record of what you did. The description says more.
            if (status == ENpcQuestStatus.Completed)
            {
                row.AddChild(Sub(TradeRules.PlainText(q.Description), UITheme.TextDisabled));
                return;
            }
            foreach (var (text, ok) in QuestRules.Objectives(q, w))
                row.AddChild(Sub((ok ? "      ✓  " : "      •  ") + text, ok ? UITheme.Good : UITheme.TextBody));
        }

        Label Sub(string text, Color c)
        {
            var l = new Label
            {
                Text = text,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                // ⚠ A WIDTH. An autowrap label with none measures against a 1 px column and reports an absurd
                // minimum height, which the container then grows to. See the trade window's description line.
                CustomMinimumSize = new Vector2(Mathf.Max(200f, Size.X - 40f), 0),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            return UITheme.Label(l, UITheme.FontBody, c);
        }

        // ---- test seams ----
        public int DebugRowCount => _list?.GetChildCount() ?? 0;
        public string DebugHead => _head?.Text ?? "";
        public string DebugFoot => _foot?.Text ?? "";
    }
}
