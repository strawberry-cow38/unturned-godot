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
        public static void ApplyAll(Node ctx)
        {
            ApplyAA(ctx);
            ApplyShadows();
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
