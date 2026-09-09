using Godot;

namespace UnturnedGodot
{
    /// <summary>The ONE top tab strip every full-screen menu shows (Inventory / Craft / Skills / Information).
    /// Same geometry everywhere -- screen-wide, MARGIN 12, 60 px tall, tabs at y 8, 8 px gaps -- so switching tabs
    /// never shifts the bar (master 2026-09-03: "the top menu bar moves slightly, unify").
    /// Key labels come from Keybinds LIVE (the strip used to hardcode G/Y/U/M while the binds were Tab/Y/J/M).
    ///
    /// NO CLOSE BUTTON (strawberry 2026-09-06: "remove the X from the top right of that bar too, reformatting the
    /// top to fill the space"). The four tabs now divide the full width. Closing is ESC or the screen's own key --
    /// both already worked and neither ran through the X, so nothing lost a way out; OnClose survives for those
    /// callers, it just has no button of its own any more.</summary>
    public partial class MenuNavbar : Control
    {
        public enum Tab { Inventory, Craft, Skills, Information }
        public const int Height = 60, Margin = 12, Gap = 8;
        static readonly (Tab tab, string label, GameAction action)[] Defs =
        {
            (Tab.Inventory, "Inventory", GameAction.Inventory), (Tab.Craft, "Craft", GameAction.Craft),
            (Tab.Skills, "Skills", GameAction.Skills), (Tab.Information, "Information", GameAction.Map),
        };
        readonly (Panel bg, Label lbl, Button hit)[] _tabs = new (Panel, Label, Button)[4];
        public System.Action<Tab> OnTab;
        public System.Action OnClose;

        public static MenuNavbar Build(Control parent, Tab active, System.Action<Tab> onTab, System.Action onClose)
        {
            var nb = new MenuNavbar { OnTab = onTab, OnClose = onClose, MouseFilter = MouseFilterEnum.Ignore };
            nb.SetAnchorsPreset(LayoutPreset.TopWide);
            nb.OffsetBottom = Height;
            parent.AddChild(nb);
            var strip = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            strip.SetAnchorsPreset(LayoutPreset.FullRect);
            strip.AddThemeStyleboxOverride("panel", UITheme.Box(UITheme.Nav, 0));
            nb.AddChild(strip);
            for (int i = 0; i < Defs.Length; i++)
            {
                var bg = new Panel { MouseFilter = MouseFilterEnum.Ignore }; nb.AddChild(bg);
                var lbl = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
                lbl.AddThemeFontSizeOverride("font_size", 32);
                nb.AddChild(lbl);
                var hit = new Button { Flat = true, MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
                var t = Defs[i].tab; hit.Pressed += () => nb.OnTab?.Invoke(t);
                // HOVER FEEDBACK (strawberry 2026-09-09: "show feedback when hovering over the inv/craft/skills/
                // info ui tabs"). The hit button is FLAT and transparent -- it exists to take the click, so its own
                // hover stylebox is invisible and the strip gave you nothing back. The tab's `bg` panel is the
                // thing you can see, so the hover drives that instead, through the same Restyle() the active state
                // uses: one place decides what a tab looks like, so hovering the open tab cannot un-highlight it.
                int hi = i;
                hit.MouseEntered += () => { nb._hover = hi; nb.Restyle(); };
                hit.MouseExited += () => { if (nb._hover == hi) { nb._hover = -1; nb.Restyle(); } };
                nb.AddChild(hit);
                nb._tabs[i] = (bg, lbl, hit);
            }
            nb.SetActive(active);
            nb.Resized += nb.Layout;
            nb.Layout();
            return nb;
        }

        Tab _active; int _hover = -1;

        public void SetActive(Tab active) { _active = active; Restyle(); }

        /// <summary>The one place a tab's look is decided: OPEN beats hovered beats idle. Split out of SetActive
        /// so the hover cannot fight it -- driving the two from separate handlers is how you end up able to
        /// un-highlight the tab you are standing on by mousing over it.</summary>
        void Restyle()
        {
            for (int i = 0; i < Defs.Length; i++)
            {
                bool on = Defs[i].tab == _active;
                bool hov = !on && i == _hover;
                _tabs[i].bg.AddThemeStyleboxOverride("panel", UITheme.Box(on ? UITheme.Selected : hov ? UITheme.Hover : UITheme.Slot, UITheme.RadiusCell));
                _tabs[i].lbl.Text = $"{Defs[i].label} [{Keybinds.Get(Defs[i].action).Label}]";   // live bind, never a hardcoded key
                _tabs[i].lbl.AddThemeColorOverride("font_color", on ? new Color(1f, 1f, 1f) : hov ? UITheme.Text : UITheme.TextBody);
            }
        }

        void Layout()
        {
            float w = Size.X > 0 ? Size.X : GetViewport().GetVisibleRect().Size.X;
            float tabsW = w - Margin * 2;   // the X is gone; the tabs take the whole strip
            float tabW = (tabsW - Gap * (Defs.Length - 1)) / Defs.Length;
            for (int i = 0; i < Defs.Length; i++)
            {
                var p = new Vector2(Margin + i * (tabW + Gap), 8f); var sz = new Vector2(tabW, Height - 16f);
                _tabs[i].bg.Position = p; _tabs[i].bg.Size = sz;
                _tabs[i].lbl.Position = p; _tabs[i].lbl.Size = sz;
                _tabs[i].hit.Position = p; _tabs[i].hit.Size = sz;
            }
        }

    }
}
