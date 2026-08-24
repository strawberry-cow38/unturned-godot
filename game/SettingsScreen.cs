using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The pause menu's Settings screen: one screen, a tab strip, one Back. Graphics and Controls come from
    /// GraphicsPanel (split into two tabs); Key Binds is a live KeybindMenu node -- it captures input, so it must stay
    /// in the tree rather than be rebuilt as a static panel. strawberry: "one top level settings button ... tabs for
    /// graphics, key binds, controls". Built once by PauseMenu and reused; visibility is toggled, not rebuilt.</summary>
    public partial class SettingsScreen : PanelContainer
    {
        readonly List<(Button tab, Control content)> _tabs = new();

        public void Setup(Node ctx, Action onBack)
        {
            UITheme.Panel(this, solid: true);
            ProcessMode = ProcessModeEnum.Always;   // the pause menu pauses the tree

            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(s, 18);
            AddChild(margin);

            var root = new VBoxContainer { CustomMinimumSize = new Vector2(600, 0) };
            root.AddThemeConstantOverride("separation", UITheme.Gap);
            margin.AddChild(root);

            root.AddChild(UITheme.Label(new Label { Text = "SETTINGS", HorizontalAlignment = HorizontalAlignment.Center }, UITheme.FontTitle));

            var strip = new HBoxContainer();
            strip.AddThemeConstantOverride("separation", UITheme.Gap);
            UITheme.Strip(strip);
            root.AddChild(strip);

            var content = new MarginContainer();
            content.AddThemeConstantOverride("margin_top", UITheme.Gap);
            content.AddThemeConstantOverride("margin_bottom", UITheme.Gap);
            root.AddChild(content);

            AddTab(strip, content, "Graphics", GraphicsPanel.BuildGraphics(ctx, null));
            AddTab(strip, content, "Controls", GraphicsPanel.BuildControls(ctx, null));
            AddTab(strip, content, "Key Binds", new KeybindMenu());

            var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(0, 42) };
            UITheme.Button(back, primary: true);
            back.Pressed += () => onBack?.Invoke();
            root.AddChild(back);

            Select(0);
        }

        void AddTab(HBoxContainer strip, MarginContainer content, string name, Control body)
        {
            var tab = new Button { Text = name, CustomMinimumSize = new Vector2(140, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            UITheme.Button(tab);
            int idx = _tabs.Count;
            tab.Pressed += () => Select(idx);
            strip.AddChild(tab);
            content.AddChild(body);
            _tabs.Add((tab, body));
        }

        // One tab visible at a time; the active tab's label lights to Accent, the rest dim. (A background swap via
        // UITheme.Selected is the obvious upgrade if this reads too subtle.)
        void Select(int idx)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool on = i == idx;
                _tabs[i].content.Visible = on;
                _tabs[i].tab.AddThemeColorOverride("font_color", on ? UITheme.Accent : UITheme.TextDim);
            }
        }
    }
}
