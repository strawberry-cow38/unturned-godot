using Godot;

namespace UnturnedGodot
{
    // The graphics settings PANEL, built once and used by both the main menu and the pause menu (master asked for it
    // in both). One builder rather than two: a settings screen that exists twice is a settings screen where one copy
    // silently falls behind, and the whole point of GraphicsOptions holding the state is that the two views cannot
    // disagree about it.
    //
    // Every control is a CYCLE button (master: "cycle through AA types") -- click to advance, wrapping. That suits a
    // small fixed option list better than a dropdown, and it keeps the panel usable with no mouse precision.
    public static class GraphicsPanel
    {
        /// <summary>Build the panel. `ctx` is the node whose viewport the AA setting applies to -- the menus live on
        /// different CanvasLayers, so this cannot be resolved statically.</summary>
        public static Control Build(Node ctx, System.Action onBack)
        {
            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(s, 24);

            var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
            vbox.AddThemeConstantOverride("separation", 10);
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(470, 640), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };   // the retail-ported rows no longer fit a screen: scroll
            margin.AddChild(scroll); scroll.AddChild(vbox);

            var title = new Label { Text = "GRAPHICS", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontSizeOverride("font_size", 28);
            vbox.AddChild(title);

            Row(vbox, "Anti-aliasing",
                () => GraphicsOptions.Label(GraphicsOptions.AA),
                () => { GraphicsOptions.AA = GraphicsOptions.Next(GraphicsOptions.AAOrder, GraphicsOptions.AA); GraphicsOptions.ApplyAA(ctx); });

            Row(vbox, "Anisotropic filtering",
                () => GraphicsOptions.Aniso + "x",
                () => { GraphicsOptions.Aniso = GraphicsOptions.Next(GraphicsOptions.AnisoOrder, GraphicsOptions.Aniso); GraphicsOptions.ApplyAniso(); });

            Row(vbox, "Resolution",
                () => GraphicsOptions.ResLabel(GraphicsOptions.Resolution),
                () => { GraphicsOptions.Resolution = GraphicsOptions.Next(GraphicsOptions.ResOrder, GraphicsOptions.Resolution); GraphicsOptions.ApplyResolution(); });

            Row(vbox, "Shadow quality",
                () => GraphicsOptions.Shadows.ToString(),
                () => { GraphicsOptions.Shadows = GraphicsOptions.Next(GraphicsOptions.ShadowOrder, GraphicsOptions.Shadows); GraphicsOptions.ApplyShadows(); });

            Row(vbox, "Shadow distance",
                () => GraphicsOptions.ShadowDistLabel(GraphicsOptions.ShadowDistance),
                () => { GraphicsOptions.ShadowDistance = GraphicsOptions.Next(GraphicsOptions.ShadowDistOrder, GraphicsOptions.ShadowDistance); GraphicsOptions.ApplyShadowDistance(ctx); });

            Row(vbox, "Render distance",
                () => GraphicsOptions.DrawLabel(GraphicsOptions.DrawDistance),
                () => { GraphicsOptions.DrawDistance = GraphicsOptions.Next(GraphicsOptions.DrawOrder, GraphicsOptions.DrawDistance);
                        GraphicsOptions.ApplyRenderDistance(ctx?.GetTree()?.Root); });

            Row(vbox, "Fullscreen", () => GraphicsOptions.Fullscreen.ToString(), () => { GraphicsOptions.Fullscreen = GraphicsOptions.Next(GraphicsOptions.FullscreenOrder, GraphicsOptions.Fullscreen); GraphicsOptions.ApplyWindow(); });
            Row(vbox, "V-Sync", () => GraphicsOptions.OnOff(GraphicsOptions.VSync), () => { GraphicsOptions.VSync = !GraphicsOptions.VSync; GraphicsOptions.ApplyWindow(); });
            Row(vbox, "Target frame rate", () => GraphicsOptions.TargetFps == 0 ? "Uncapped" : GraphicsOptions.TargetFps.ToString(), () => { GraphicsOptions.TargetFps = GraphicsOptions.Next(GraphicsOptions.FpsOrder, GraphicsOptions.TargetFps); GraphicsOptions.ApplyWindow(); });
            Row(vbox, "UI scale", () => $"{GraphicsOptions.UiScale * 100f:0}%", () => { GraphicsOptions.UiScale = GraphicsOptions.Next(GraphicsOptions.UiScaleOrder, GraphicsOptions.UiScale); GraphicsOptions.ApplyUiScale(ctx); });
            Row(vbox, "Effect quality (next map)", () => GraphicsOptions.EffectQuality.ToString(), () => { GraphicsOptions.EffectQuality = GraphicsOptions.Next(GraphicsOptions.QualityOrder, GraphicsOptions.EffectQuality); GraphicsOptions.ApplyEffects(); });
            Row(vbox, "Ambient occlusion", () => GraphicsOptions.OnOff(GraphicsOptions.AmbientOcclusion), () => { GraphicsOptions.AmbientOcclusion = !GraphicsOptions.AmbientOcclusion; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Bloom", () => GraphicsOptions.OnOff(GraphicsOptions.Bloom), () => { GraphicsOptions.Bloom = !GraphicsOptions.Bloom; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Sun shafts", () => GraphicsOptions.OnOff(GraphicsOptions.SunShafts), () => { GraphicsOptions.SunShafts = !GraphicsOptions.SunShafts; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Screen-space reflections", () => GraphicsOptions.OnOff(GraphicsOptions.ScreenSpaceReflections), () => { GraphicsOptions.ScreenSpaceReflections = !GraphicsOptions.ScreenSpaceReflections; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Planar water reflection", () => GraphicsOptions.PlanarReflection.ToString(), () => { GraphicsOptions.PlanarReflection = GraphicsOptions.Next(GraphicsOptions.QualityOrder, GraphicsOptions.PlanarReflection); GraphicsOptions.ApplyWater(); });
            Row(vbox, "Scope quality", () => GraphicsOptions.ScopeQuality.ToString(), () => { GraphicsOptions.ScopeQuality = GraphicsOptions.Next(GraphicsOptions.QualityOrderNoOff, GraphicsOptions.ScopeQuality); });
            Row(vbox, "Outline", () => GraphicsOptions.OnOff(GraphicsOptions.Outline), () => { GraphicsOptions.Outline = !GraphicsOptions.Outline; });
            Row(vbox, "Grass displacement", () => GraphicsOptions.OnOff(GraphicsOptions.GrassDisplacement), () => { GraphicsOptions.GrassDisplacement = !GraphicsOptions.GrassDisplacement; });
            Row(vbox, "Wind", () => GraphicsOptions.OnOff(GraphicsOptions.Wind), () => { GraphicsOptions.Wind = !GraphicsOptions.Wind; });
            Row(vbox, "Ragdolls", () => GraphicsOptions.OnOff(GraphicsOptions.Ragdolls), () => { GraphicsOptions.Ragdolls = !GraphicsOptions.Ragdolls; });

            Row(vbox, "Render thread (restart)",   // Single / Multi; "(restart)" on the value until the running process matches (see GraphicsOptions.SetRenderThreadWanted)
                () => GraphicsOptions.RenderThreadLabel,
                () => GraphicsOptions.SetRenderThreadWanted(!GraphicsOptions.RenderThreadWanted));

            var ctrlTitle = new Label { Text = "CONTROLS", HorizontalAlignment = HorizontalAlignment.Center };
            ctrlTitle.AddThemeFontSizeOverride("font_size", 22);
            vbox.AddChild(ctrlTitle);

            Row(vbox, "Helicopter pitch",
                () => ControlsOptions.InvertHeliPitchLabel,
                () => ControlsOptions.InvertHeliPitch = !ControlsOptions.InvertHeliPitch);

            Row(vbox, "Plane pitch",   // SEPARATE from the heli toggle (strawberry: "add inverted y option for planes too separately")
                () => ControlsOptions.InvertPlanePitchLabel,
                () => ControlsOptions.InvertPlanePitch = !ControlsOptions.InvertPlanePitch);

            SliderRow(vbox, "Helicopter sensitivity",
                ControlsOptions.HeliSensMin, ControlsOptions.HeliSensMax, 0.05f,
                () => ControlsOptions.HeliSensitivity,
                v => ControlsOptions.HeliSensitivity = v,
                () => ControlsOptions.HeliSensitivityLabel);

            // Said in the UI, not just in a commit message: the anisotropy row is wired to a real setting that
            // currently changes nothing, because no material in the port asks for an anisotropic filter mode. A
            // control that silently does nothing is worse than one that says so.
            var note = new Label
            {
                Text = "Anisotropic filtering has no effect yet — no material requests it.",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(1f, 1f, 1f, 0.45f),
            };
            note.AddThemeFontSizeOverride("font_size", 12);
            vbox.AddChild(note);

            if (onBack != null)
            {
                var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(0, 40) };
                back.Pressed += () => onBack();
                vbox.AddChild(back);
            }
            return margin;
        }

        /// <summary>Graphics-only rows, for the pause Settings screen's Graphics tab. The main menu still uses the
        /// combined Build() above; the pause screen splits graphics and controls into separate tabs (strawberry).
        /// Pass onBack:null when embedding as a tab -- the Settings screen owns the single Back button.</summary>
        public static Control BuildGraphics(Node ctx, System.Action onBack)
        {
            var (margin, vbox) = Frame();
            Row(vbox, "Anti-aliasing", () => GraphicsOptions.Label(GraphicsOptions.AA),
                () => { GraphicsOptions.AA = GraphicsOptions.Next(GraphicsOptions.AAOrder, GraphicsOptions.AA); GraphicsOptions.ApplyAA(ctx); });
            Row(vbox, "Anisotropic filtering", () => GraphicsOptions.Aniso + "x",
                () => { GraphicsOptions.Aniso = GraphicsOptions.Next(GraphicsOptions.AnisoOrder, GraphicsOptions.Aniso); GraphicsOptions.ApplyAniso(); });
            Row(vbox, "Resolution", () => GraphicsOptions.ResLabel(GraphicsOptions.Resolution),
                () => { GraphicsOptions.Resolution = GraphicsOptions.Next(GraphicsOptions.ResOrder, GraphicsOptions.Resolution); GraphicsOptions.ApplyResolution(); });
            Row(vbox, "Shadow quality", () => GraphicsOptions.Shadows.ToString(),
                () => { GraphicsOptions.Shadows = GraphicsOptions.Next(GraphicsOptions.ShadowOrder, GraphicsOptions.Shadows); GraphicsOptions.ApplyShadows(); });
            Row(vbox, "Shadow distance", () => GraphicsOptions.ShadowDistLabel(GraphicsOptions.ShadowDistance),
                () => { GraphicsOptions.ShadowDistance = GraphicsOptions.Next(GraphicsOptions.ShadowDistOrder, GraphicsOptions.ShadowDistance); GraphicsOptions.ApplyShadowDistance(ctx); });
            Row(vbox, "Render distance", () => GraphicsOptions.DrawLabel(GraphicsOptions.DrawDistance),
                () => { GraphicsOptions.DrawDistance = GraphicsOptions.Next(GraphicsOptions.DrawOrder, GraphicsOptions.DrawDistance);
                        GraphicsOptions.ApplyRenderDistance(ctx?.GetTree()?.Root); });

            Row(vbox, "Fullscreen", () => GraphicsOptions.Fullscreen.ToString(), () => { GraphicsOptions.Fullscreen = GraphicsOptions.Next(GraphicsOptions.FullscreenOrder, GraphicsOptions.Fullscreen); GraphicsOptions.ApplyWindow(); });
            Row(vbox, "V-Sync", () => GraphicsOptions.OnOff(GraphicsOptions.VSync), () => { GraphicsOptions.VSync = !GraphicsOptions.VSync; GraphicsOptions.ApplyWindow(); });
            Row(vbox, "Target frame rate", () => GraphicsOptions.TargetFps == 0 ? "Uncapped" : GraphicsOptions.TargetFps.ToString(), () => { GraphicsOptions.TargetFps = GraphicsOptions.Next(GraphicsOptions.FpsOrder, GraphicsOptions.TargetFps); GraphicsOptions.ApplyWindow(); });
            Row(vbox, "UI scale", () => $"{GraphicsOptions.UiScale * 100f:0}%", () => { GraphicsOptions.UiScale = GraphicsOptions.Next(GraphicsOptions.UiScaleOrder, GraphicsOptions.UiScale); GraphicsOptions.ApplyUiScale(ctx); });
            Row(vbox, "Effect quality (next map)", () => GraphicsOptions.EffectQuality.ToString(), () => { GraphicsOptions.EffectQuality = GraphicsOptions.Next(GraphicsOptions.QualityOrder, GraphicsOptions.EffectQuality); GraphicsOptions.ApplyEffects(); });
            Row(vbox, "Ambient occlusion", () => GraphicsOptions.OnOff(GraphicsOptions.AmbientOcclusion), () => { GraphicsOptions.AmbientOcclusion = !GraphicsOptions.AmbientOcclusion; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Bloom", () => GraphicsOptions.OnOff(GraphicsOptions.Bloom), () => { GraphicsOptions.Bloom = !GraphicsOptions.Bloom; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Sun shafts", () => GraphicsOptions.OnOff(GraphicsOptions.SunShafts), () => { GraphicsOptions.SunShafts = !GraphicsOptions.SunShafts; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Screen-space reflections", () => GraphicsOptions.OnOff(GraphicsOptions.ScreenSpaceReflections), () => { GraphicsOptions.ScreenSpaceReflections = !GraphicsOptions.ScreenSpaceReflections; GraphicsOptions.ApplyEnvironment(ctx); });
            Row(vbox, "Planar water reflection", () => GraphicsOptions.PlanarReflection.ToString(), () => { GraphicsOptions.PlanarReflection = GraphicsOptions.Next(GraphicsOptions.QualityOrder, GraphicsOptions.PlanarReflection); GraphicsOptions.ApplyWater(); });
            Row(vbox, "Scope quality", () => GraphicsOptions.ScopeQuality.ToString(), () => { GraphicsOptions.ScopeQuality = GraphicsOptions.Next(GraphicsOptions.QualityOrderNoOff, GraphicsOptions.ScopeQuality); });
            Row(vbox, "Outline", () => GraphicsOptions.OnOff(GraphicsOptions.Outline), () => { GraphicsOptions.Outline = !GraphicsOptions.Outline; });
            Row(vbox, "Grass displacement", () => GraphicsOptions.OnOff(GraphicsOptions.GrassDisplacement), () => { GraphicsOptions.GrassDisplacement = !GraphicsOptions.GrassDisplacement; });
            Row(vbox, "Wind", () => GraphicsOptions.OnOff(GraphicsOptions.Wind), () => { GraphicsOptions.Wind = !GraphicsOptions.Wind; });
            Row(vbox, "Ragdolls", () => GraphicsOptions.OnOff(GraphicsOptions.Ragdolls), () => { GraphicsOptions.Ragdolls = !GraphicsOptions.Ragdolls; });
            var note = new Label { Text = "Anisotropic filtering has no effect yet — no material requests it.",
                HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(1f, 1f, 1f, 0.45f) };
            note.AddThemeFontSizeOverride("font_size", 12);
            vbox.AddChild(note);
            AddBack(vbox, onBack);
            return margin;
        }

        /// <summary>Controls-only rows for the Settings screen's Controls tab: on-foot look, then the flight axes.</summary>
        public static Control BuildControls(Node ctx, System.Action onBack)
        {
            var (margin, vbox) = Frame();
            SliderRow(vbox, "Look sensitivity", ControlsOptions.MouseSensMin, ControlsOptions.MouseSensMax, 0.01f,
                () => ControlsOptions.MouseSensitivity, v => ControlsOptions.MouseSensitivity = v, () => ControlsOptions.MouseSensitivityLabel);
            Row(vbox, "Invert look Y", () => ControlsOptions.InvertLookYLabel, () => ControlsOptions.InvertLookY = !ControlsOptions.InvertLookY);
            Row(vbox, "Helicopter pitch", () => ControlsOptions.InvertHeliPitchLabel, () => ControlsOptions.InvertHeliPitch = !ControlsOptions.InvertHeliPitch);
            Row(vbox, "Plane pitch", () => ControlsOptions.InvertPlanePitchLabel, () => ControlsOptions.InvertPlanePitch = !ControlsOptions.InvertPlanePitch);
            SliderRow(vbox, "Helicopter sensitivity", ControlsOptions.HeliSensMin, ControlsOptions.HeliSensMax, 0.05f,
                () => ControlsOptions.HeliSensitivity, v => ControlsOptions.HeliSensitivity = v, () => ControlsOptions.HeliSensitivityLabel);
            AddBack(vbox, onBack);
            return margin;
        }

        static (MarginContainer, VBoxContainer) Frame()
        {
            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(s, 24);
            var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
            vbox.AddThemeConstantOverride("separation", 10);
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(470, 640), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };   // the retail-ported rows no longer fit a screen: scroll
            margin.AddChild(scroll); scroll.AddChild(vbox);
            return (margin, vbox);
        }

        static void AddBack(VBoxContainer vbox, System.Action onBack)
        {
            if (onBack == null) return;
            var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(0, 40) };
            back.Pressed += () => onBack();
            vbox.AddChild(back);
        }

        /// <summary>Label + slider + live readout, for a setting that is genuinely continuous. Cycling through
        /// fixed steps is right for AA modes and resolutions, where the options are a real list; it is wrong for
        /// mouse sensitivity, where the value someone wants is the one between two of your steps.
        ///
        /// Applies on drag, not on release, so the readout tracks the handle. The setting is read back through
        /// `get` when the row is built rather than cached, so two panels showing the same option agree.</summary>
        static void SliderRow(VBoxContainer parent, string name, float min, float max, float step,
                              System.Func<float> get, System.Action<float> set, System.Func<string> label)
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 12);
            h.AddChild(UITheme.Label(new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, UITheme.FontBody));
            var readout = UITheme.Label(new Label { Text = label(), CustomMinimumSize = new Vector2(56, 0), HorizontalAlignment = HorizontalAlignment.Right }, UITheme.FontBody);
            var slider = new HSlider
            {
                MinValue = min, MaxValue = max, Step = step, Value = get(),
                CustomMinimumSize = new Vector2(150, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            slider.ValueChanged += v => { set((float)v); readout.Text = label(); GraphicsOptions.Save(); };
            h.AddChild(slider);
            h.AddChild(readout);
            parent.AddChild(h);
        }

        /// <summary>One label + one cycling value button. The button re-reads its text from `value` after every press
        /// rather than caching it, so the two panels stay in step: change AA in the pause menu, open the main menu's,
        /// and it shows the current value instead of whatever it was built with.</summary>
        static void Row(VBoxContainer parent, string name, System.Func<string> value, System.Action advance)
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 12);
            var lbl = UITheme.Label(new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, UITheme.FontBody);
            h.AddChild(lbl);
            var btn = new Button { Text = value(), CustomMinimumSize = new Vector2(150, 34) };
            UITheme.Button(btn);
            btn.Pressed += () => { advance(); btn.Text = value(); GraphicsOptions.Save(); };   // every row change persists (strawberry 2026-09-04 "make all persist")
            h.AddChild(btn);
            parent.AddChild(h);
        }
    }
}
