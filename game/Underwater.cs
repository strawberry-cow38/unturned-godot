using Godot;

namespace UnturnedGodot
{
    // SUBMERGED CAMERA VIEW (strawberry 2026-09-08: "build an underwater submerged camera shader").
    //
    // A fullscreen SPATIAL pass on a 2x2 quad parented to the camera -- NOT a CanvasLayer like NightVision, and
    // that is forced rather than a style choice: the effect needs the depth buffer (things must get bluer and
    // murkier with distance, or it is a flat blue filter), and Godot rejects `hint_depth_texture` in a
    // canvas_item shader outright. Found by rendering it; the C# built clean either way, because a .gdshader is
    // compiled at runtime.
    //
    // Being a 3D pass also gets the layering right for free: it draws in the 3D pass, so NightVision's CanvasLayer
    // composites OVER it. Water is between the world and the goggles, and the goggles are between that and the
    // eye, which is the order these two should apply in.
    //
    // Driven from the CAMERA's height, not the body's: the two disagree exactly when it matters -- wading with
    // your head under, or swimming with the eye just clear of the surface -- and what you see is decided by where
    // the eye is.
    public partial class Underwater : Node3D
    {
        /// <summary>How far below the surface the effect reaches full strength. A hair under the waterline should
        /// be a hint of blue rather than the full murk, or breaking the surface strobes.</summary>
        public const float FadeDepth = 0.45f;

        public static bool Active;   // read by anything that wants to know the view is submerged

        MeshInstance3D _quad;
        ShaderMaterial _mat;
        float _shown = -1f;   // last submersion pushed, so a still camera does not re-set uniforms every frame
        float _forceDepth;    // UG_UNDERWATER: >0 pins the view submerged at this depth

        public override void _Ready()
        {
            var sh = GD.Load<Shader>("res://content/underwater.gdshader");
            if (sh == null) { Log.Err("[underwater] underwater.gdshader missing -- no submerged view"); return; }
            _mat = new ShaderMaterial { Shader = sh };
            _quad = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
                MaterialOverride = _mat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // The vertex shader writes clip space directly, so the quad's real transform is meaningless to the
                // renderer's culler -- without this it gets frustum-culled the moment the camera turns and the
                // effect blinks out. A big cull margin keeps it submitted.
                ExtraCullMargin = 16384f,
                // Drawn after the world so nothing can sort in front of it (depth test is off in the shader too).
                SortingOffset = 1000f,
            };
            AddChild(_quad);
            ApplyForce();
        }

        void ApplyForce()
        {
            if (_forceDepth <= 0f || _quad == null || _mat == null) return;
            _quad.Visible = true;
            Active = true;
            _mat.SetShaderParameter("submersion", 1f);
            _mat.SetShaderParameter("depth_below", _forceDepth);
        }

        /// <summary>Drive it from the eye. Cheap to call every frame; only writes a uniform when it moved.</summary>
        public void Drive(Camera3D cam)
        {
            if (_quad == null || _mat == null || _forceDepth > 0f) return;   // the harness owns the view when forced
            if (cam == null || !IsInstanceValid(cam) || !Terrain.HasWater) { Off(); return; }

            float below = Terrain.SeaLevelY - cam.GlobalPosition.Y;
            if (below <= 0f) { Off(); return; }

            float sub = Mathf.Clamp(below / FadeDepth, 0f, 1f);   // wash in over the first half metre
            _quad.Visible = true;
            Active = true;
            if (Mathf.Abs(sub - _shown) > 0.004f || _shown < 0f) { _shown = sub; _mat.SetShaderParameter("submersion", sub); }
            _mat.SetShaderParameter("depth_below", below);
        }

        void Off()
        {
            if (_quad != null && _quad.Visible) { _quad.Visible = false; _shown = -1f; }
            Active = false;
        }

        public override void _ExitTree() { if (Active) Active = false; }   // a torn-down pass must not leave the flag stuck on

        /// <summary>Harness: UG_UNDERWATER=&lt;metres&gt; pins the view submerged over any scene, so a render can show
        /// the effect without having to get a camera under the sea first. Attaches to the CURRENT camera, because
        /// the quad only covers the screen if it is drawn by the camera it belongs to.</summary>
        public static Underwater DebugAttach(Node root)
        {
            var v = System.Environment.GetEnvironmentVariable("UG_UNDERWATER");
            if (string.IsNullOrEmpty(v) || root == null) return null;
            if (!float.TryParse(v, out float depth) || depth <= 0f) depth = 4f;
            var cam = root.GetViewport()?.GetCamera3D();
            if (cam == null) { Log.Err("[underwater] UG_UNDERWATER set but there is no current camera"); return null; }
            var u = new Underwater { _forceDepth = depth };
            cam.AddChild(u);
            u.ApplyForce();   // in case _Ready already ran on AddChild
            Log.Print($"[underwater] forced on at {depth:0.0} m");
            return u;
        }
    }
}
