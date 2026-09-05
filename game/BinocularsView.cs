using Godot;

namespace UnturnedGodot
{
    /// <summary>BINOCULARS (master 2026-09-05: "copy the scope viewport thing method. one magnified viewport, cut out for
    /// each lens" / "make it look like two distinct lenses, and use the binocular model"). Two renders:
    ///   1. the WORLD, magnified: a SubViewport inheriting the main World3D, its camera on the main camera's transform at
    ///      FOV / Zoom (OwnWorld3D=true would duplicate an EMPTY world and render sky only -- the scope lesson);
    ///   2. the RIG: the binoculars item model (content/items/333) held to the eyes in its own little world, eyepieces
    ///      toward a camera at the origin, with a lens disc in each eyepiece (content/binoculars.gdshader) that shows
    ///      render 1 through SCREEN_UV -- so the one image runs across both lenses and each eyepiece cuts its circle.
    /// Composited on CanvasLayer 6 (over the viewmodel composite at 5, under the HUD at 10): the rig over the normal 1x
    /// view, nothing black. The eyepiece rings were measured off the model: centres x = +-0.0791, inner radius 0.0372, on the
    /// end face y = -0.259; barrels run along +Y.</summary>
    public partial class BinocularsView : CanvasLayer
    {
        public float Zoom = 4f;
        const float EyeX = 0.0791f, EyeY = -0.259f, LensR = 0.0372f;   // the model's eyepiece rings (measured)
        const float EyeDist = 0.165f;   // eyepiece plane this far in front of the rig camera: two distinct lenses inside a 16:9 frame (0.113 put them off the sides, 0.150 kissed the edges)
        const float RigFov = 60f;

        SubViewport _world, _rig; Camera3D _cam, _rigCam; Node3D _roll, _model; ShaderMaterial _lens; TextureRect _rigRect; Vector2I _size;

        public override void _Ready()
        {
            Layer = 6;
            ProcessPriority = 200;   // after the player has placed the main camera for this frame

            // 1. the magnified world
            _world = new SubViewport { OwnWorld3D = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, HandleInputLocally = false };
            AddChild(_world);
            _cam = new Camera3D { Current = true };
            _world.AddChild(_cam);
            var main = GetViewport()?.GetCamera3D();
            if (main != null) _world.World3D = main.GetWorld3D();

            // 2. the held model, its own world, transparent where there is no model
            _rig = new SubViewport { OwnWorld3D = true, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, HandleInputLocally = false, Msaa3D = Viewport.Msaa.Msaa4X };
            AddChild(_rig);
            _rigCam = new Camera3D { Current = true, Fov = RigFov, Near = 0.01f, Far = 5f };
            _rig.AddChild(_rigCam);
            // _roll: the oriented binoculars rolled 180 about the VIEW axis (hinge up -- master 2026-09-05 "the binocular model is upside
            // down"). A roll folded into the model's own Euler flips the barrels end-for-end instead, so it is a parent.
            _roll = new Node3D { Name = "BinocularsRoll", RotationDegrees = new Vector3(0f, 0f, 180f), Position = new Vector3(0f, 0f, -(EyeDist - EyeY)) };
            _rig.AddChild(_roll);
            _model = new Node3D { Name = "Binoculars" };
            _roll.AddChild(_model);
            var mesh = ContentProvider.ParseObj("res://content/items/333.txt");
            if (mesh != null)
            {
                var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.42f, 0.42f, 0.42f) };
                string tp = ProjectSettings.GlobalizePath("res://content/items/333.png");
                if (System.IO.File.Exists(tp)) { var img = new Image(); if (ContentProvider.LoadOk(img, tp)) { mat.AlbedoTexture = ImageTexture.CreateFromImage(img); mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest; } }
                _model.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
            }
            _lens = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/binoculars.gdshader") };
            _lens.SetShaderParameter("flip_y", System.Environment.GetEnvironmentVariable("UG_BINOFLIP") == "1");
            foreach (float sx in new[] { -EyeX, EyeX })
            {
                var disc = new MeshInstance3D { Mesh = new QuadMesh { Size = Vector2.One * (LensR * 2f), Material = _lens },
                    Position = new Vector3(sx, EyeY - 0.002f, 0f), RotationDegrees = new Vector3(90f, 0f, 0f) };   // a hair OUTSIDE the end face so the round glass sits over the hex rim, facing -Y (the eye)
                _model.AddChild(disc);
            }
            // barrels (+Y) away from the eye: -90 about X maps +Y -> -Z; the eyepiece face then sits at z = +0.259 -> _roll pulls it to z = -EyeDist
            _model.RotationDegrees = new Vector3(-90f, 0f, 0f);

            // composite: the rig over the normal 1x view -- no black outside the housing (master 2026-09-05 "dont have the black background")
            _rigRect = new TextureRect { StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
            _rigRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_rigRect);
            Resize();
        }

        void Resize()
        {
            var win = GetViewport().GetVisibleRect().Size;
            int h = Mathf.Clamp(GraphicsOptions.ScopeSize * 2, 540, Mathf.Max(540, (int)win.Y));   // Low 720 / Medium 1080 / High = the screen
            var sz = new Vector2I(Mathf.Max(16, Mathf.RoundToInt(h * win.X / Mathf.Max(1f, win.Y))), h);
            if (sz == _size) return;
            _size = sz;
            _world.Size = sz;
            _rig.Size = new Vector2I(Mathf.Max(16, (int)win.X), Mathf.Max(16, (int)win.Y));
            _lens.SetShaderParameter("view", _world.GetTexture());
            _rigRect.Texture = _rig.GetTexture();
        }

        public override void _Process(double delta)
        {
            var main = GetViewport()?.GetCamera3D();
            if (main == null) return;
            Resize();
            _cam.GlobalTransform = main.GlobalTransform;
            _cam.Fov = main.Fov / Mathf.Max(1f, Zoom);
            _cam.Near = main.Near; _cam.Far = main.Far; _cam.CullMask = main.CullMask;
        }
    }
}
