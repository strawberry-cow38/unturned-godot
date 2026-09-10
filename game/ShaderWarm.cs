using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>LOAD-TIME SHADER WARM PASS (tinyclaw 2026-09-04, after the well shaft cost master two GPU-driver
    /// timeouts): a shader's FIRST draw is a synchronous pipeline compile, and only 3 of the 20 content shaders were
    /// pre-warmed by hand (wire arrows, RubbleFx, the well). Any of the rest whose first draw lands mid-teleport, on
    /// top of streaming uploads, with the separate render thread mid-frame, can do the same thing. So: once per world,
    /// for a few frames right after the build, draw a tiny quad with EVERY spatial `.gdshader` in content/ in front of
    /// the camera. Uniform values do not change pipelines, so default-uniform materials compile the real ones; the
    /// globals those shaders read are registered first (the GrassDisplacers lesson: a material that links a missing
    /// global dies). A new shader dropped into content/ is warmed automatically -- nothing to remember.
    ///
    /// CANVAS SHADERS TOO, since 2026-09-05. The first cut filtered to `shader_type spatial` and waved the rest
    /// off as "they draw elsewhere" -- which says where they draw, not when they COMPILE, and compiling is the
    /// whole hazard. A canvas_item shader's first draw is the same synchronous pipeline build, and a full-screen
    /// overlay is a worse case than a 5 cm quad: binoculars_overlay lands the frame someone first raises them,
    /// which is exactly the "mid-action, on top of streaming" shape that cost the two driver timeouts. Warmed on
    /// a CanvasLayer at the back, one pixel, transparent.</summary>
    public sealed partial class ShaderWarm : Node3D
    {
        public const int Frames = 4;
        static bool _done;
        int _frames = Frames;
        readonly List<MeshInstance3D> _quads = new();
        public static int LastCount { get; private set; }
        /// <summary>True while the warm quads are on screen. The loading screen holds its cover up until this
        /// clears, so the pass happens BEHIND the load rather than as a flash of coloured quads on the world the
        /// player just arrived in (strawberry 2026-09-10: "hide the shader pre-load that happens when loading
        /// into a map"). It cannot be hidden any other way: an invisible mesh is not drawn, and a shader that is
        /// not drawn is not compiled, which is the entire point of the pass.</summary>
        public static bool Busy { get; private set; }
        /// <summary>Test seam: drive Busy without a GPU. The thing under test is the loading screen's REACTION to
        /// it -- that it waits, and that it stops waiting -- which needs no real shader compile to exercise.</summary>
        public static void SetBusyForTest(bool v) => Busy = v;

        public static void Begin(Node root)
        {
            if (_done || root == null) return;
            _done = true;
            root.AddChild(new ShaderWarm());
        }

        /// <summary>Every global any content shader reads, registered through its owner (idempotent), so a warm
        /// material never links a missing one.</summary>
        public static void EnsureAllGlobals()
        {
            GrassDisplacers.EnsureGlobals();   // grass_displacement_point / wind_vec / grass_displacer_count / the displacer texture
            RainSystem3D.EnsureGlobals();      // rain_wetness / rain_intensity / rain_canopy
            WellShaft.EnsureGlobals();         // well_daylight
        }

        public override void _Ready()
        {
            EnsureAllGlobals();
            string dir = ProjectSettings.GlobalizePath("res://content/");
            int n = 0;
            try
            {
                foreach (var file in System.IO.Directory.GetFiles(dir, "*.gdshader"))
                {
                    string text;
                    try { text = System.IO.File.ReadAllText(file); } catch { continue; }
                    if (!text.Contains("shader_type spatial")) continue;   // canvas/sky/particles shaders draw elsewhere (some headers run past a dozen comment lines, so read it all)
                    var shader = GD.Load<Shader>("res://content/" + System.IO.Path.GetFileName(file));
                    if (shader == null) continue;
                    var mi = new MeshInstance3D
                    {
                        Mesh = new QuadMesh { Size = new Vector2(0.05f, 0.05f) },
                        MaterialOverride = new ShaderMaterial { Shader = shader },
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        Position = new Vector3((n % 5) * 0.06f - 0.12f, (n / 5) * 0.06f - 0.12f, 0f),   // a little grid, all in view
                    };
                    AddChild(mi);
                    _quads.Add(mi);
                    n++;
                }
            }
            catch (System.Exception e) { GD.PrintErr($"[shaderwarm] {e.Message}"); }
            int c = WarmCanvasShaders(dir);
            LastCount = n + c;
            GD.Print($"[shaderwarm] {n} spatial + {c} canvas shaders drawn for {Frames} frames behind the load");
            if (n + c == 0) { QueueFree(); return; }
            Busy = true;
        }

        // Belt and braces: whatever frees this node -- the countdown, a scene change, a failed load -- releases the
        // loading screen. A cover that never comes down would be a far worse bug than the flash it replaces.
        public override void _ExitTree() => Busy = false;

        /// <summary>Same trick on a CanvasLayer: one transparent pixel per canvas_item shader, behind everything.
        /// It builds the pipeline without being visible, and rides the same Frames countdown -- the layer is a child
        /// of this node, so QueueFree takes it with us.</summary>
        int WarmCanvasShaders(string dir)
        {
            int c = 0;
            try
            {
                var layer = new CanvasLayer { Layer = -128 };   // behind every real UI
                foreach (var file in System.IO.Directory.GetFiles(dir, "*.gdshader"))
                {
                    string text;
                    try { text = System.IO.File.ReadAllText(file); } catch { continue; }
                    if (!text.Contains("shader_type canvas_item")) continue;
                    var shader = GD.Load<Shader>("res://content/" + System.IO.Path.GetFileName(file));
                    if (shader == null) continue;
                    layer.AddChild(new ColorRect
                    {
                        Material = new ShaderMaterial { Shader = shader },
                        Size = new Vector2(1f, 1f),
                        Modulate = new Color(1f, 1f, 1f, 0.004f),   // non-zero: a fully transparent rect can be culled before it ever compiles
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                    });
                    c++;
                }
                if (c > 0) AddChild(layer); else layer.QueueFree();
            }
            catch (System.Exception e) { GD.PrintErr($"[shaderwarm/canvas] {e.Message}"); }
            return c;
        }

        public override void _Process(double delta)
        {
            var cam = GetViewport()?.GetCamera3D();
            if (cam != null) GlobalTransform = new Transform3D(cam.GlobalTransform.Basis, cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 0.6f);   // 0.6 m ahead, facing the camera
            if (--_frames <= 0) { Busy = false; QueueFree(); }
        }
    }
}
