using Godot;

namespace UnturnedGodot
{
    /// <summary>Retail's chromatic aberration, ported (master 2026-09-06: "research implementing chromatic
    /// aberration to the edges of the screen").
    ///
    /// This is a GAP BEING FILLED rather than an invention. Retail's GraphicsSettingsData carries chromatic
    /// aberration beside Bloom and SunShafts; GraphicsOptions.cs listed it among the settings held back because
    /// there was "no Godot-side hook for those yet, so they are not shown rather than shown as dead rows". This
    /// is the hook, so the row can appear.
    ///
    /// LAYER 7, and the number is load-bearing. A canvas_item shader reading hint_screen_texture distorts
    /// whatever is drawn BELOW its CanvasLayer, so the layer chooses what counts as being behind the lens:
    ///   0..4  the 3D world        5  the viewmodel (your own hands and weapon)      6  nightvision
    ///   ---- 7: here ----
    ///   9  rain overlay           10 HUD / build hud        11 menus       12 vitals
    /// Above nightvision because the goggles are part of the optical path and their tint should fringe with
    /// everything else; below 9 because smearing HUD text and menu labels looks like a rendering fault rather
    /// than like a lens, and no lens sits between the interface and your eye.</summary>
    public partial class ChromaticAberration : CanvasLayer
    {
        /// <summary>The live pass, so the settings panel can push a change without walking the tree from a
        /// static context. Mirrors MapUI.Current. Null before a world is built and after teardown.</summary>
        public static ChromaticAberration Current;

        ColorRect _rect;
        ShaderMaterial _mat;

        /// <summary>Corner offset as a fraction of the half-width, before the r^2 weighting. 0.35 is a subtle
        /// lens, ~1.0 is an obvious one; the row in the options menu drives it.</summary>
        public static float Intensity = 0.35f;
        public static bool Enabled = true;
        /// <summary>Taps along the radial sweep. 3 is PPv2's "Fast Mode" -- visible red/cyan edges; 6+ fills
        /// between them into a smooth spectral smear. Costs one screen sample each, per pixel.</summary>
        public static int Samples = 6;

        public override void _Ready()
        {
            Layer = 7; ProcessPriority = 190;
            Current = this;
            AddToGroup("chromatic");
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/chromatic.gdshader") };
            _rect = new ColorRect { Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore, Color = Colors.White };
            _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_rect);
            Apply();
        }

        /// <summary>Push the current settings onto the shader. Cheap; call after changing any of them.</summary>
        public void Apply()
        {
            if (_rect == null || _mat == null) return;
            // OFF is a hidden rect, not a zero intensity: at intensity 0 the pass still samples the screen
            // once per tap for every pixel and returns what it started with, which is the whole cost of the
            // effect in exchange for nothing.
            _rect.Visible = Enabled && Intensity > 0.001f;
            _mat.SetShaderParameter("intensity", Intensity);
            _mat.SetShaderParameter("samples", Mathf.Clamp(Samples, 2, 16));
        }

        public override void _ExitTree() { if (Current == this) Current = null; }

        /// <summary>Test/harness seam: the live shader values, so a check can read what the pass is actually
        /// running with rather than what the static fields say it should be.</summary>
        public bool DebugVisible => _rect != null && _rect.Visible;
        public float DebugIntensity => _mat != null ? (float)_mat.GetShaderParameter("intensity") : 0f;
        public int DebugSamples => _mat != null ? (int)_mat.GetShaderParameter("samples") : 0;

        /// <summary>Harness: UG_CHROMATIC=&lt;intensity&gt; puts the effect over any scene, with no player at all.</summary>
        public static ChromaticAberration DebugAttach(Node root)
        {
            string s = System.Environment.GetEnvironmentVariable("UG_CHROMATIC");
            if (string.IsNullOrEmpty(s) || root == null || !float.TryParse(s, out float amt)) return null;
            Enabled = true; Intensity = amt;
            var ca = new ChromaticAberration();
            root.AddChild(ca);
            GD.Print($"[chromatic] harness on at intensity {amt}");
            return ca;
        }
    }
}
