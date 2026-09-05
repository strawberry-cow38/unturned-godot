using Godot;

namespace UnturnedGodot
{
    /// <summary>BINOCULARS (master 2026-09-05: "copy the scope viewport thing method. one magnified viewport, cut out for
    /// each lens"). The scope's two-render PiP, full-screen: ONE SubViewport renders the main world from a second camera
    /// riding the main camera's transform at FOV / Zoom, and content/binoculars.gdshader shows it through two overlapping
    /// circular cut-outs (one per lens) with the housing black outside. Lives on CanvasLayer 6: over the viewmodel
    /// composite (5), under the HUD (10). The viewport inherits the main World3D (OwnWorld3D=true would duplicate an
    /// EMPTY world and render sky only -- the scope lesson).</summary>
    public partial class BinocularsView : CanvasLayer
    {
        public float Zoom = 4f;
        SubViewport _vp; Camera3D _cam; ColorRect _rect; ShaderMaterial _mat; Vector2I _size;

        public override void _Ready()
        {
            Layer = 6;
            ProcessPriority = 200;   // after the player has placed the main camera for this frame
            _vp = new SubViewport { OwnWorld3D = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, HandleInputLocally = false };
            AddChild(_vp);
            _cam = new Camera3D { Current = true };
            _vp.AddChild(_cam);
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/binoculars.gdshader") };
            _rect = new ColorRect { Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore };
            _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_rect);
            var main = GetViewport()?.GetCamera3D();
            if (main != null) _vp.World3D = main.GetWorld3D();
            Resize();
        }

        void Resize()
        {
            var win = GetViewport().GetVisibleRect().Size;
            int h = Mathf.Clamp(GraphicsOptions.ScopeSize * 2, 540, Mathf.Max(540, (int)win.Y));   // Low 720 / Medium 1080 / High = the screen
            var sz = new Vector2I(Mathf.Max(16, Mathf.RoundToInt(h * win.X / Mathf.Max(1f, win.Y))), h);
            if (sz == _size) return;
            _size = sz; _vp.Size = sz;
            _mat.SetShaderParameter("aspect", win.X / Mathf.Max(1f, win.Y));
            _mat.SetShaderParameter("view", _vp.GetTexture());
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
