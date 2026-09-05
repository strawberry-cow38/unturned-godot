using Godot;

namespace UnturnedGodot
{
    /// <summary>The tank's two seated first-person optics, drawn over the main camera (master 2026-09-05): the DRIVER's
    /// view out of the visor window wears the retail binoculars mask as a periscope (1x -- a window, not a telescope), and
    /// the GUNNER buttoned up inside the turret gets a round eyepiece with the ripped 8x-scope reticle, the magnification
    /// being the main camera's FOV as with the binoculars. One CanvasLayer on the binoculars' layer -- a seated player
    /// cannot be holding a pair, so the two never coexist. PlayerController.UpdateTankOptics drives Mode and the FOV.</summary>
    public partial class TankOptics : CanvasLayer
    {
        public enum OpticMode { None, Periscope, Gunsight }
        OpticMode _mode;
        TextureRect _periscope, _gunsight; Vector2 _win;
        public OpticMode Mode
        {
            get => _mode;
            set { _mode = value; if (_periscope != null) _periscope.Visible = value == OpticMode.Periscope; if (_gunsight != null) _gunsight.Visible = value == OpticMode.Gunsight; }
        }

        public override void _Ready()
        {
            Layer = 6; ProcessPriority = 200;
            _periscope = MakeOverlay("res://content/binoculars_overlay.gdshader", "mask", "res://content/ui/binoculars_overlay.png");
            _gunsight = MakeOverlay("res://content/tank_gunsight.gdshader", "reticle", "res://content/cross_scope_reticle.png");
            _win = GetViewport().GetVisibleRect().Size;   // read ONCE + on resize (per-frame GetVisibleRect is a RenderingServer sync -- the binoculars lesson)
            GetTree().Root.SizeChanged += () => { _win = GetViewport().GetVisibleRect().Size; Resize(); };
            Resize();
            Mode = _mode;
        }

        TextureRect MakeOverlay(string shaderPath, string texParam, string texPath)
        {
            var mat = new ShaderMaterial { Shader = GD.Load<Shader>(shaderPath) };
            string p = ProjectSettings.GlobalizePath(texPath);
            if (System.IO.File.Exists(p)) { var img = new Image(); if (ContentProvider.LoadOk(img, p)) mat.SetShaderParameter(texParam, ImageTexture.CreateFromImage(img)); }
            else GD.PushWarning($"[tankoptics] missing {texPath}");
            var rect = new TextureRect { Texture = new PlaceholderTexture2D { Size = Vector2.One }, Material = mat, StretchMode = TextureRect.StretchModeEnum.Scale,
                                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(rect);
            return rect;
        }

        void Resize()
        {
            float aspect = _win.X / Mathf.Max(1f, _win.Y);
            (_periscope?.Material as ShaderMaterial)?.SetShaderParameter("screen_aspect", aspect);
            (_gunsight?.Material as ShaderMaterial)?.SetShaderParameter("screen_aspect", aspect);
        }
    }
}
