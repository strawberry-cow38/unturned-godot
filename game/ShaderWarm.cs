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
    /// global dies). A new shader dropped into content/ is warmed automatically -- nothing to remember.</summary>
    public sealed partial class ShaderWarm : Node3D
    {
        public const int Frames = 4;
        static bool _done;
        int _frames = Frames;
        readonly List<MeshInstance3D> _quads = new();
        public static int LastCount { get; private set; }

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
            LastCount = n;
            GD.Print($"[shaderwarm] {n} spatial shaders drawn for {Frames} frames behind the load");
            if (n == 0) QueueFree();
        }

        public override void _Process(double delta)
        {
            var cam = GetViewport()?.GetCamera3D();
            if (cam != null) GlobalTransform = new Transform3D(cam.GlobalTransform.Basis, cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 0.6f);   // 0.6 m ahead, facing the camera
            if (--_frames <= 0) QueueFree();
        }
    }
}
