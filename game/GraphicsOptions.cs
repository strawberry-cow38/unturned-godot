using Godot;

namespace UnturnedGodot
{
    // Client GRAPHICS settings (master: "add the graphics settings menu to the main menu, and pause menu, cycle
    // through AA types, anistropic filtering, resolution settings, shadow quality, render distance").
    //
    // State + Apply live here rather than in the menus, because BOTH menus present the same options and a setting
    // that only takes effect from one of them is the obvious way for this to go wrong. The menus are views; this is
    // the model.
    //
    // Modelled on Units: a static holding the choice, applied on change. Persistence is not wired (neither is Units'),
    // so these reset on relaunch -- worth knowing before filing it as a bug.
    //
    // WHAT EACH KNOB REALLY DOES, since two of the six are not what they look like:
    //
    //  - RENDER DISTANCE is real but is consumed at WORLD BUILD time. LodTable.DrawDistance is retail's own
    //    normalizedDrawDistance and feeds both the layer cull and the LOD bias, but WorldBuilder bakes the result into
    //    each instance's VisibilityRangeEnd as it places props. So changing it has to walk the built scene and
    //    re-derive, which is what ApplyRenderDistance does -- it is not a value the renderer re-reads.
    //  - ANISOTROPIC FILTERING is a project setting that the renderer samples for materials using an *Anisotropic
    //    filter mode. Nothing in this port requests one today (everything is Nearest/Linear WithMipmaps), so changing
    //    the level currently has NO visible effect. Shipped anyway because the menu was asked for and the setting is
    //    real -- but it is honest to say the wiring ends here until materials opt in.
    public static class GraphicsOptions
    {
        // ---- ANTI-ALIASING. Godot splits this across two viewport properties plus TAA, so one user-facing "AA type"
        // cycle maps onto a combination rather than a single enum -- which is why this is a list and not a cast.
        public enum AAMode { Off, Fxaa, Msaa2x, Msaa4x, Msaa8x, Taa }
        public static readonly AAMode[] AAOrder = { AAMode.Off, AAMode.Fxaa, AAMode.Msaa2x, AAMode.Msaa4x, AAMode.Msaa8x, AAMode.Taa };
        public static string Label(AAMode m) => m switch
        {
            AAMode.Off => "Off", AAMode.Fxaa => "FXAA",
            AAMode.Msaa2x => "MSAA 2x", AAMode.Msaa4x => "MSAA 4x", AAMode.Msaa8x => "MSAA 8x",
            _ => "TAA",
        };

        public enum ShadowQuality { Off, Low, Medium, High, Ultra }
        public static readonly ShadowQuality[] ShadowOrder = { ShadowQuality.Off, ShadowQuality.Low, ShadowQuality.Medium, ShadowQuality.High, ShadowQuality.Ultra };

        /// <summary>Anisotropy levels the renderer accepts, as the enum's own values (1x/2x/4x/8x/16x).</summary>
        public static readonly int[] AnisoOrder = { 1, 2, 4, 8, 16 };

        /// <summary>Render-distance steps as retail's normalizedDrawDistance (0..1). Retail's own default is 1.0 --
        /// the "Ultra" end -- so the list runs up to it rather than past it.</summary>
        public static readonly float[] DrawOrder = { 0.25f, 0.5f, 0.75f, 1.0f };
        public static string DrawLabel(float d) => d <= 0.25f ? "Low" : d <= 0.5f ? "Medium" : d <= 0.75f ? "High" : "Ultra";

        public static AAMode AA = AAMode.Fxaa;
        public static ShadowQuality Shadows = ShadowQuality.High;
        public static int Aniso = 4;
        public static float DrawDistance = 1.0f;
        /// <summary>Directional shadow distance in metres (strawberry 2026-09-04 "add options for shadow distance"). The world's sun
        /// (group "sun") gets DirectionalShadowMaxDistance = this at build and whenever the row is cycled. 120 was the fixed value.</summary>
        public static float ShadowDistance = 120f;

        // ---- PORTED FROM RETAIL GraphicsSettingsData (strawberry 2026-09-04 "port more graphics options etc from the source") ----
        // Retail's list: FullscreenMode, UserInterfaceScale, TargetFrameRate, AmbientOcclusion, GrassDisplacement, Ragdolls, Wind, VSync,
        // Bloom, EffectQuality, SunShafts, ScreenSpaceReflection, PlanarReflection, ScopeQuality, OutlineQuality (+ chromatic aberration,
        // film grain, triplanar, nice-blend, debris/blast/puddle/glitter/clutter -- no Godot-side hook for those yet, so they are not
        // shown rather than shown as dead rows). Each one below maps onto a real switch in this codebase.
        public enum GfxQuality { Off, Low, Medium, High, Ultra }
        public static readonly GfxQuality[] QualityOrder = { GfxQuality.Off, GfxQuality.Low, GfxQuality.Medium, GfxQuality.High, GfxQuality.Ultra };
        public static readonly GfxQuality[] QualityOrderNoOff = { GfxQuality.Low, GfxQuality.Medium, GfxQuality.High, GfxQuality.Ultra };
        public enum FullscreenMode { Windowed, Borderless, Exclusive }
        public static readonly FullscreenMode[] FullscreenOrder = { FullscreenMode.Windowed, FullscreenMode.Borderless, FullscreenMode.Exclusive };
        public static FullscreenMode Fullscreen = FullscreenMode.Windowed;
        public static readonly float[] UiScaleOrder = { 0.75f, 0.85f, 1.0f, 1.15f, 1.3f };
        public static float UiScale = 1.0f;
        public static readonly int[] FpsOrder = { 0, 60, 120, 144, 240 };
        public static int TargetFps = 0;   // 0 = uncapped
        public static bool VSync = false;
        public static bool AmbientOcclusion = false;
        public static bool Bloom = true;
        public static bool SunShafts = false;          // -> Godot volumetric fog (the nearest thing to retail's sun shafts)
        public static bool ScreenSpaceReflections = false;
        public static GfxQuality EffectQuality = GfxQuality.High;      // particle count + size (ParticleFx) -- takes effect on the next map load
        public static GfxQuality PlanarReflection = GfxQuality.Medium;   // water mirror: Off / every 4th frame / every 2nd (the pre-option default) / every frame
        public static GfxQuality ScopeQuality = GfxQuality.High;       // scope PiP viewport: 360 / 540 / 720 / 1080 (applied when a scope is next mounted)
        public static bool Outline = true;              // look-at outline overlay
        public static bool GrassDisplacement = true;
        public static bool Wind = true;                 // foliage sway + flag cloth
        public static bool Ragdolls = true;
        public static float EffectMul => EffectQuality switch { GfxQuality.Off => 0.05f, GfxQuality.Low => 0.5f, GfxQuality.Medium => 0.75f, GfxQuality.High => 1f, _ => 1.5f };
        public static int ScopeSize => ScopeQuality switch { GfxQuality.Off or GfxQuality.Low => 360, GfxQuality.Medium => 540, GfxQuality.High => 720, _ => 1080 };
        public static int PlanarEvery => PlanarReflection switch { GfxQuality.Low => 4, GfxQuality.Medium => 2, _ => 1 };
        public static string OnOff(bool b) => b ? "On" : "Off";
        public static void ApplyWindow()
        {
            switch (Fullscreen)
            {
                case FullscreenMode.Exclusive: DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen); break;
                case FullscreenMode.Borderless: DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen); break;
                default: DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed); break;
            }
            DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = TargetFps;
        }
        public static void ApplyUiScale(Node ctx) { var root = ctx?.GetTree()?.Root; if (root != null) root.ContentScaleFactor = UiScale; }
        /// <summary>The world's environment (WorldEnvironment nodes in group "world_env"): AO, bloom, SSR, sun shafts.</summary>
        public static void ApplyEnvironment(Node ctx)
        {
            var tree = ctx?.GetTree(); if (tree == null) return;
            foreach (var n in tree.GetNodesInGroup("world_env")) if (n is WorldEnvironment we && we.Environment != null) ApplyEnvironment(we.Environment);
        }
        public static void ApplyEnvironment(Godot.Environment env)
        {
            if (env == null) return;
            env.SsaoEnabled = AmbientOcclusion;
            env.GlowEnabled = Bloom;
            env.SsrEnabled = ScreenSpaceReflections;
            env.VolumetricFogEnabled = SunShafts;
            // density: Godot's default 0.05 is the night key; DayNightCycle drives it by sun elevation (night 0.05, horizon 0.010, noon 0.003)
        }
        public static void ApplyEffects() { ParticleFx.QualityMul = EffectMul; }
        public static void ApplyWater() { WaterReflection.Enabled = PlanarReflection != GfxQuality.Off; WaterReflection.EveryFrames = PlanarEvery; }
        public static readonly float[] ShadowDistOrder = { 40f, 80f, 120f, 200f, 300f };
        public static string ShadowDistLabel(float d) => $"{d:0} m";
        public static void ApplyShadowDistance(Node ctx)
        {
            var tree = ctx?.GetTree(); if (tree == null) return;
            foreach (var n in tree.GetNodesInGroup("sun")) if (n is DirectionalLight3D d) d.DirectionalShadowMaxDistance = ShadowDistance;
        }
        public static Vector2I Resolution = Vector2I.Zero;   // (0,0) = leave the window alone / native

        // VERTEX LIGHTING A/B (strawberry 2026-08-16). Flips every StandardMaterial3D in the tree between
        // per-pixel and per-vertex shading at runtime, so the question can be ANSWERED on real hardware instead
        // of argued about. It is a diagnostic switch, not a setting: this box renders on lavapipe (software
        // rasterisation), where GPU lighting cost measured against a CPU rasteriser tells you nothing about a
        // real GPU, so the measurement has to happen on the player's machine.
        //
        // Worth knowing before reading the result: per-vertex shading only makes each lit PIXEL cheaper. It does
        // not touch shadow map rendering, and an omni light defaults to a cube shadow -- six faces re-rendered
        // when anything in range moves. A big win here means we were fill-bound; no change means the cost is
        // shadows, which is the more useful finding of the two.
        public static bool VertexShading;

        /// <summary>Apply the current VertexShading mode to every material under `root`. Returns how many
        /// materials it actually changed -- a count, not a bool, because "0 changed" and "applied fine" are the
        /// same green tick otherwise, and this exists to be trusted from a profiler reading.</summary>
        public static int ApplyShading(Node root)
        {
            var mode = VertexShading ? BaseMaterial3D.ShadingModeEnum.PerVertex : BaseMaterial3D.ShadingModeEnum.PerPixel;
            int n = 0;
            // Unshaded materials are left alone throughout. They are unshaded on purpose (build ghosts, port
            // arrows, wire overlays) and dragging them into a lit mode would be a visual change masquerading as
            // a perf experiment.
            void Flip(Material m)
            {
                if (m is StandardMaterial3D s && s.ShadingMode != BaseMaterial3D.ShadingModeEnum.Unshaded
                    && s.ShadingMode != mode) { s.ShadingMode = mode; n++; }
            }
            void Walk(Node node)
            {
                // GeometryInstance3D, not MeshInstance3D. This walk used to descend only into MeshInstance3D,
                // and MultiMeshInstance3D does NOT derive from it -- both come off GeometryInstance3D. So every
                // tree, rock, pebble and grass patch in the world (ResourceField, FoliageField, PropBatcher) was
                // skipped, along with the terrain and the grass, whose materials are ShaderMaterial rather than
                // StandardMaterial3D. The switch converted the placed-prop set, printed a large count, and left
                // the renderers that actually dominate the frame on per-pixel -- so an A/B through it measured a
                // lower bound of unknown tightness and would have read as "vertex lighting barely helps".
                // Review 2026-08-16.
                if (node is GeometryInstance3D gi)
                {
                    Flip(gi.MaterialOverride);
                    Flip(gi.MaterialOverlay);
                    if (gi is MeshInstance3D mi)
                    {
                        for (int i = 0; i < mi.GetSurfaceOverrideMaterialCount(); i++) Flip(mi.GetSurfaceOverrideMaterial(i));
                        if (mi.Mesh != null)
                            for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++) Flip(mi.Mesh.SurfaceGetMaterial(i));
                    }
                    else if (gi is MultiMeshInstance3D mmi && mmi.Multimesh?.Mesh is { } mm)
                        for (int i = 0; i < mm.GetSurfaceCount(); i++) Flip(mm.SurfaceGetMaterial(i));
                }
                foreach (var c in node.GetChildren()) Walk(c);
            }
            if (root != null) Walk(root);
            return n;
        }

        /// <summary>How many renderers this switch CANNOT convert, and why: a ShaderMaterial has no ShadingMode
        /// to flip -- its lighting is written into the shader itself.
        ///
        /// Reported alongside the changed count because the two together are the honest reading. The terrain and
        /// the grass are both ShaderMaterial, and they are a large share of the frame; an A/B that silently
        /// leaves them on per-pixel is not measuring "vertex lighting", it is measuring vertex lighting on the
        /// props only. Converting them means writing vertex-lit variants of those shaders, which is real work
        /// rather than a toggle -- so the switch names what it skipped instead of implying it covered everything.</summary>
        public static int CountShaderMaterialRenderers(Node root)
        {
            int n = 0;
            void Walk(Node node)
            {
                if (node is GeometryInstance3D gi)
                {
                    if (gi.MaterialOverride is ShaderMaterial) n++;
                    if (gi is MeshInstance3D mi && mi.Mesh != null)
                        for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                            if (mi.Mesh.SurfaceGetMaterial(i) is ShaderMaterial) n++;
                    if (gi is MultiMeshInstance3D mmi && mmi.Multimesh?.Mesh is { } mm)
                        for (int i = 0; i < mm.GetSurfaceCount(); i++)
                            if (mm.SurfaceGetMaterial(i) is ShaderMaterial) n++;
                }
                foreach (var c in node.GetChildren()) Walk(c);
            }
            if (root != null) Walk(root);
            return n;
        }

        /// <summary>Window sizes offered. Zero is "Native", which is first so the default is never a forced resize --
        /// a settings menu that resizes your window the moment you open it is its own kind of bug.</summary>
        public static readonly Vector2I[] ResOrder =
        {
            Vector2I.Zero, new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160),
        };
        public static string ResLabel(Vector2I r) => r == Vector2I.Zero ? "Native" : $"{r.X} x {r.Y}";

        /// <summary>Apply everything to the live renderer. Safe to call with no window (the L1 harness runs headless),
        /// which is why each step guards rather than assuming a viewport exists.</summary>
        // ---- MULTITHREADED RENDERER (restart required). Godot reads rendering/driver/threads/thread_model ONCE at boot,
        // but honours an override.cfg beside project.godot (source runs) / the executable (exports) at the next start.
        // The toggle writes exactly that one key and the row says "(restart)" until the running process matches.
        // Measured 2026-09-03 on the 4080S: +26% on the CPU-bound idle scene. Shipped as opt-in first; DEFAULT-ON since the
        // evening of 2026-09-03 (master: "flip multithreaded renderer on by default") -- project.godot carries thread_model=2,
        // so "Multi" = no override line and "Single" writes an explicit =1.
        public const string ThreadModelKey = "rendering/driver/threads/thread_model";
        public static bool RenderThreadActive => ProjectSettings.GetSetting(ThreadModelKey, 2).AsInt32() == 2;   // what THIS process booted with (project default: 2 = multi)
        static bool? _renderThreadWanted;
        public static bool RenderThreadWanted { get => _renderThreadWanted ?? RenderThreadActive; private set => _renderThreadWanted = value; }
        public static string RenderThreadLabel => (RenderThreadWanted ? "Multi" : "Single") + (RenderThreadWanted != RenderThreadActive ? " (restart)" : "");
        static string OverridePath => OS.HasFeature("template")
            ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "override.cfg")   // exported build: res:// is a read-only pack
            : ProjectSettings.GlobalizePath("res://override.cfg");
        public static void SetRenderThreadWanted(bool on)
        {
            RenderThreadWanted = on;
            // the project default is MULTI (2) now; only Single needs an explicit line
            WriteOverride("driver/threads/thread_model", on ? null : new[] { "driver/threads/thread_model=1" }, $"render thread -> {(on ? "multi" : "single")}");
        }

        // ---- VOLUMETRIC FOG QUALITY (restart required). The froxel grid (rendering/environment/volumetric_fog/volume_size x
        // volume_depth) and its filter are boot-time project settings like the thread model, so they ride override.cfg too.
        // strawberry 2026-09-04: "the volumetric fog looks amazing ... any way to make it perform better? -20fps". The cost is
        // the froxel count x lights in range: project.godot carries 48x48 (42% of Godot's 64x64 default) as Medium; Low is
        // 32x32 unfiltered (12.5% of default, temporal reprojection hides most of the blockiness); High is the engine default.
        public const string FogSizeKey = "rendering/environment/volumetric_fog/volume_size";
        public static readonly string[] FogQualityNames = { "Low", "Medium", "High" };
        static readonly int[] FogQualitySize = { 32, 48, 64 };
        public static int VolumetricFogActive
        {
            get { int sz = ProjectSettings.GetSetting(FogSizeKey, 48).AsInt32(); return sz <= 32 ? 0 : sz >= 64 ? 2 : 1; }   // what THIS process booted with
        }
        static int? _fogWanted;
        public static int VolumetricFogWanted { get => _fogWanted ?? VolumetricFogActive; private set => _fogWanted = value; }
        public static string VolumetricFogLabel => FogQualityNames[VolumetricFogWanted] + (VolumetricFogWanted != VolumetricFogActive ? " (restart)" : "");
        public static void SetVolumetricFogQuality(int q)
        {
            q = Mathf.Clamp(q, 0, 2);
            VolumetricFogWanted = q;
            string[] lines = q == 1 ? null : new[]   // Medium = the project default: no override lines
            {
                $"environment/volumetric_fog/volume_size={FogQualitySize[q]}",
                $"environment/volumetric_fog/volume_depth={FogQualitySize[q]}",
                $"environment/volumetric_fog/use_filter={(q == 0 ? 0 : 1)}",
            };
            WriteOverride("environment/volumetric_fog/", lines, $"volumetric fog -> {FogQualityNames[q]}");
        }

        /// <summary>Rewrite override.cfg's [rendering] section: drop every line starting with <paramref name="keyPrefix"/> (ours),
        /// add <paramref name="lines"/> (null = the project default, no line), leave anything else in the file alone.</summary>
        static void WriteOverride(string keyPrefix, string[] lines, string what)
        {
            try
            {
                string path = OverridePath;
                var all = System.IO.File.Exists(path) ? new System.Collections.Generic.List<string>(System.IO.File.ReadAllLines(path)) : new System.Collections.Generic.List<string>();
                all.RemoveAll(l => l.TrimStart().StartsWith(keyPrefix));
                if (lines != null && lines.Length > 0)
                {
                    int sec = all.FindIndex(l => l.Trim() == "[rendering]");
                    if (sec < 0) { if (all.Count > 0 && all[all.Count - 1].Trim() != "") all.Add(""); all.Add("[rendering]"); sec = all.Count - 1; }
                    all.InsertRange(sec + 1, lines);
                }
                for (int i = all.Count - 1; i >= 0; i--)   // drop a [rendering] header we emptied
                    if (all[i].Trim() == "[rendering]" && (i + 1 >= all.Count || all[i + 1].Trim() == "" || all[i + 1].TrimStart().StartsWith("["))) all.RemoveAt(i);
                if (all.TrueForAll(l => l.Trim() == "")) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                else System.IO.File.WriteAllLines(path, all);
                GD.Print($"[graphics] {what} on next start ({path})");
            }
            catch (System.Exception e) { GD.PrintErr($"[graphics] could not write override.cfg: {e.Message}"); }
        }

        // ---- PERSISTENCE (strawberry 2026-09-04 "make all persist"): user://graphics.cfg holds every graphics + controls row.
        // Loaded once at boot (Main._Ready) and applied; saved by the panel after every change. The render-thread toggle
        // lives in override.cfg (Godot reads it at boot) and is not duplicated here.
        const string ConfigPath = "user://graphics.cfg";
        public static void Save()
        {
            try
            {
                var cfg = new ConfigFile();
                cfg.SetValue("graphics", "aa", (int)AA);
                cfg.SetValue("graphics", "shadows", (int)Shadows);
                cfg.SetValue("graphics", "aniso", Aniso);
                cfg.SetValue("graphics", "draw_distance", DrawDistance);
                cfg.SetValue("graphics", "shadow_distance", ShadowDistance);
                cfg.SetValue("graphics", "resolution_x", Resolution.X);
                cfg.SetValue("graphics", "resolution_y", Resolution.Y);
                cfg.SetValue("graphics", "fullscreen", (int)Fullscreen); cfg.SetValue("graphics", "ui_scale", UiScale); cfg.SetValue("graphics", "target_fps", TargetFps); cfg.SetValue("graphics", "vsync", VSync);
                cfg.SetValue("graphics", "ambient_occlusion", AmbientOcclusion); cfg.SetValue("graphics", "bloom", Bloom); cfg.SetValue("graphics", "sun_shafts", SunShafts); cfg.SetValue("graphics", "ssr", ScreenSpaceReflections);
                cfg.SetValue("graphics", "effect_quality", (int)EffectQuality); cfg.SetValue("graphics", "planar_reflection", (int)PlanarReflection); cfg.SetValue("graphics", "scope_quality", (int)ScopeQuality);
                cfg.SetValue("graphics", "outline", Outline); cfg.SetValue("graphics", "grass_displacement", GrassDisplacement); cfg.SetValue("graphics", "wind", Wind); cfg.SetValue("graphics", "ragdolls", Ragdolls);
                cfg.SetValue("controls", "mouse_sensitivity", ControlsOptions.MouseSensitivity);
                cfg.SetValue("controls", "invert_look_y", ControlsOptions.InvertLookY);
                cfg.SetValue("controls", "invert_heli_pitch", ControlsOptions.InvertHeliPitch);
                cfg.SetValue("controls", "invert_plane_pitch", ControlsOptions.InvertPlanePitch);
                cfg.SetValue("controls", "heli_sensitivity", ControlsOptions.HeliSensitivity);
                cfg.Save(ConfigPath);
            }
            catch (System.Exception e) { GD.PrintErr($"[graphics] could not save {ConfigPath}: {e.Message}"); }
        }
        public static void Load()
        {
            try
            {
                if (System.Environment.GetEnvironmentVariable("UG_SUNSHAFTS") == "1") SunShafts = true;   // offline render hook: volumetric fog on for headless shots
                var cfg = new ConfigFile();
                if (cfg.Load(ConfigPath) != Error.Ok) return;   // first run: defaults
                AA = (AAMode)Mathf.Clamp((int)cfg.GetValue("graphics", "aa", (int)AA), 0, AAOrder.Length - 1);
                Shadows = (ShadowQuality)Mathf.Clamp((int)cfg.GetValue("graphics", "shadows", (int)Shadows), 0, ShadowOrder.Length - 1);
                Aniso = (int)cfg.GetValue("graphics", "aniso", Aniso);
                DrawDistance = Mathf.Clamp((float)cfg.GetValue("graphics", "draw_distance", DrawDistance), 0.25f, 1f);
                ShadowDistance = Mathf.Clamp((float)cfg.GetValue("graphics", "shadow_distance", ShadowDistance), 40f, 300f);
                Resolution = new Vector2I((int)cfg.GetValue("graphics", "resolution_x", Resolution.X), (int)cfg.GetValue("graphics", "resolution_y", Resolution.Y));
                Fullscreen = (FullscreenMode)Mathf.Clamp((int)cfg.GetValue("graphics", "fullscreen", (int)Fullscreen), 0, 2);
                UiScale = Mathf.Clamp((float)cfg.GetValue("graphics", "ui_scale", UiScale), 0.5f, 2f);
                TargetFps = Mathf.Clamp((int)cfg.GetValue("graphics", "target_fps", TargetFps), 0, 1000);
                VSync = (bool)cfg.GetValue("graphics", "vsync", VSync);
                AmbientOcclusion = (bool)cfg.GetValue("graphics", "ambient_occlusion", AmbientOcclusion); Bloom = (bool)cfg.GetValue("graphics", "bloom", Bloom);
                SunShafts = (bool)cfg.GetValue("graphics", "sun_shafts", SunShafts); ScreenSpaceReflections = (bool)cfg.GetValue("graphics", "ssr", ScreenSpaceReflections);
                EffectQuality = (GfxQuality)Mathf.Clamp((int)cfg.GetValue("graphics", "effect_quality", (int)EffectQuality), 0, 4);
                PlanarReflection = (GfxQuality)Mathf.Clamp((int)cfg.GetValue("graphics", "planar_reflection", (int)PlanarReflection), 0, 4);
                ScopeQuality = (GfxQuality)Mathf.Clamp((int)cfg.GetValue("graphics", "scope_quality", (int)ScopeQuality), 0, 4);
                Outline = (bool)cfg.GetValue("graphics", "outline", Outline); GrassDisplacement = (bool)cfg.GetValue("graphics", "grass_displacement", GrassDisplacement);
                Wind = (bool)cfg.GetValue("graphics", "wind", Wind); Ragdolls = (bool)cfg.GetValue("graphics", "ragdolls", Ragdolls);
                ControlsOptions.MouseSensitivity = Mathf.Clamp((float)cfg.GetValue("controls", "mouse_sensitivity", ControlsOptions.MouseSensitivity), ControlsOptions.MouseSensMin, ControlsOptions.MouseSensMax);
                ControlsOptions.InvertLookY = (bool)cfg.GetValue("controls", "invert_look_y", ControlsOptions.InvertLookY);
                ControlsOptions.InvertHeliPitch = (bool)cfg.GetValue("controls", "invert_heli_pitch", ControlsOptions.InvertHeliPitch);
                ControlsOptions.InvertPlanePitch = (bool)cfg.GetValue("controls", "invert_plane_pitch", ControlsOptions.InvertPlanePitch);
                ControlsOptions.HeliSensitivity = Mathf.Clamp((float)cfg.GetValue("controls", "heli_sensitivity", ControlsOptions.HeliSensitivity), ControlsOptions.HeliSensMin, ControlsOptions.HeliSensMax);
            }
            catch (System.Exception e) { GD.PrintErr($"[graphics] could not load {ConfigPath}: {e.Message}"); }
        }
        public static void ApplyAll(Node ctx)
        {
            ApplyAA(ctx);
            ApplyShadows();
            ApplyShadowDistance(ctx);
            ApplyWindow(); ApplyUiScale(ctx); ApplyEnvironment(ctx); ApplyEffects(); ApplyWater();
            ApplyAniso();
            ApplyResolution();
            ApplyRenderDistance(ctx?.GetTree()?.Root);
        }

        public static void ApplyAA(Node ctx)
        {
            var vp = ctx?.GetViewport();
            if (vp == null) return;
            vp.Msaa3D = AA switch
            {
                AAMode.Msaa2x => Viewport.Msaa.Msaa2X,
                AAMode.Msaa4x => Viewport.Msaa.Msaa4X,
                AAMode.Msaa8x => Viewport.Msaa.Msaa8X,
                _ => Viewport.Msaa.Disabled,
            };
            vp.ScreenSpaceAA = AA == AAMode.Fxaa ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
            vp.UseTaa = AA == AAMode.Taa;
        }

        public static void ApplyShadows()
        {
            // Off is a real state, not "very low": a zero-size atlas is how you actually stop paying for shadows,
            // and every DirectionalLight3D in the scene keeps its own ShadowEnabled untouched so the setting is
            // reversible without rebuilding anything.
            int size = Shadows switch
            {
                ShadowQuality.Off => 0, ShadowQuality.Low => 1024, ShadowQuality.Medium => 2048,
                ShadowQuality.High => 4096, _ => 8192,
            };
            RenderingServer.DirectionalShadowAtlasSetSize(Mathf.Max(size, 1), true);
            RenderingServer.DirectionalSoftShadowFilterSetQuality(Shadows switch
            {
                ShadowQuality.Off or ShadowQuality.Low => RenderingServer.ShadowQuality.Hard,
                ShadowQuality.Medium => RenderingServer.ShadowQuality.SoftLow,
                ShadowQuality.High => RenderingServer.ShadowQuality.SoftMedium,
                _ => RenderingServer.ShadowQuality.SoftHigh,
            });
        }

        public static void ApplyAniso()
            => ProjectSettings.SetSetting("rendering/textures/default_filters/anisotropic_filtering_level", AnisoIndex(Aniso));

        /// <summary>The renderer takes an INDEX (0=1x, 1=2x, 2=4x, 3=8x, 4=16x), not the multiplier. Passing 16
        /// straight through silently lands out of range and the setting does nothing.</summary>
        static int AnisoIndex(int level) => level switch { <= 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };

        public static void ApplyResolution()
        {
            if (Resolution == Vector2I.Zero) return;   // Native: leave the window as the user sized it
            if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen) return;   // resizing a fullscreen window fights the compositor
            DisplayServer.WindowSetSize(Resolution);
        }

        /// <summary>Re-derive every placed instance's cull distance from the new draw distance.
        ///
        /// This is the one that cannot just be assigned. WorldBuilder resolves LodTable.CullDistance per prop and
        /// BAKES it into VisibilityRangeEnd while placing, so the renderer never consults DrawDistance again. Rather
        /// than rebuild the world, the ORIGINAL value is stashed in node meta the first time this runs and every
        /// later change scales from that baseline -- scaling from the current value instead would compound, and two
        /// trips through "Low" would cull the map to arm's length.</summary>
        public static void ApplyRenderDistance(Node root)
        {
            LodTable.DrawDistance = DrawDistance;
            if (root == null) return;
            float scale = LodTable.DefaultCullDistance / 512f;   // 512 = the distance the world was built at (DrawDistance 1.0)
            Rescale(root, scale);
        }

        static readonly StringName BaseCullMeta = "base_cull";

        static void Rescale(Node n, float scale)
        {
            if (n is GeometryInstance3D gi && gi.VisibilityRangeEnd > 0f)
            {
                if (!gi.HasMeta(BaseCullMeta)) gi.SetMeta(BaseCullMeta, gi.VisibilityRangeEnd);
                gi.VisibilityRangeEnd = (float)gi.GetMeta(BaseCullMeta) * scale;
            }
            foreach (var c in n.GetChildren()) Rescale(c, scale);
        }

        /// <summary>Step a cycling option forward, wrapping. Shared by every control in the panel so "cycle" means
        /// the same thing everywhere, including on the value the setting currently holds not being in the list.</summary>
        public static T Next<T>(T[] order, T current)
        {
            int i = System.Array.IndexOf(order, current);
            return order[(i < 0 ? 0 : i + 1) % order.Length];
        }
    }
}
