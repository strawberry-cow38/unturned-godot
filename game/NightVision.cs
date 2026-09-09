using Godot;

namespace UnturnedGodot
{
    /// <summary>The worn nightvision goggles' view (master 2026-09-05): a screen-space pass on the binoculars' layer that
    /// amplifies and tints the finished frame (content/nightvision.gdshader), plus the static state DayNightCycle reads to
    /// pull the fog back and open the glow up while the goggles are on. Two grades, both retail Glasses items: MILITARY
    /// (334) is phosphor green and the stronger tube; CIVILIAN (1044) is black-and-white and "slightly less effective" --
    /// less gain, grainier, more fog left, a softer bloom. PlayerController owns the N toggle and calls Set each frame.</summary>
    public partial class NightVision : CanvasLayer
    {
        /// <summary>Goggles on, anywhere in the process -- DayNightCycle.Apply modulates the Environment off this.</summary>
        public static bool Active;
        public static bool Military;
        /// <summary>Multiplier on the world's fog density while the goggles are on: the tube sees through haze the eye cannot.</summary>
        public static float FogScale => !Active ? 1f : Military ? 0.2f : 0.4f;
        /// <summary>Glow while the goggles are on: (intensity multiplier, bloom, threshold multiplier) -- light sources bloom harder.</summary>
        public static (float intensity, float bloom, float threshold) Glow => Military ? (2.2f, 0.45f, 0.55f) : (1.7f, 0.30f, 0.70f);

        // ---- OVERLAY ART UNDER THE TUBE (strawberry 2026-09-09: "make sure we dont NUKE the nightvision on
        // dropped item labels and outlines"). Two adaptors, not one, because the two pieces of art sit on
        // OPPOSITE sides of the tube pass and are ruined by opposite problems:
        //
        //   * an item's name tag is a Label3D IN THE WORLD, so the tube AMPLIFIES it. White text at gain 2.6
        //     saturates to a featureless blob and takes the surrounding image with it. It needs to be DIMMER
        //     going in, so that gain x brightness lands back inside range.
        //   * the focus outline is a CanvasLayer at 50, ABOVE the pass at 6, so it is never amplified -- it just
        //     paints full-brightness colour straight over a green picture and reads like a UI bug. It needs the
        //     tube's TINT, so it belongs to the image instead of sitting on top of it.
        /// <summary>Brightness multiplier for in-world overlay art the tube amplifies. Chosen from the gain: 0.32
        /// x 2.6 lands at ~0.85, bright enough to read and short of clipping.</summary>
        public static float OverlayDim => !Active ? 1f : Military ? 0.32f : 0.42f;
        /// <summary>Tint for screen-space art drawn ABOVE the tube pass, so it reads as part of the tube image.</summary>
        public static Color OverlayTint => !Active ? Colors.White : Military ? new Color(0.34f, 0.95f, 0.40f) : new Color(0.88f, 0.90f, 0.88f);

        // Intensifier noise (strawberry 2026-09-09: "up the intensity of the film grain on both nightvisions").
        // Both roughly doubled, and the civilian tube stays the grainier of the two -- that gap is the whole
        // characterisation of it as the cheaper unit, so raising them to the same number would flatten the pair
        // into one effect with two colours.
        public const float GrainMilitary = 0.11f;   // was 0.05
        public const float GrainCivilian = 0.19f;   // was 0.09

        ColorRect _rect; ShaderMaterial _mat;

        public override void _Ready()
        {
            Layer = 6; ProcessPriority = 200;
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/nightvision.gdshader") };
            _rect = new ColorRect { Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false, Color = Colors.White };
            _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_rect);
        }

        /// <summary>Drive the view: on/off and which tube. Cheap to call every frame; only writes on change.</summary>
        public void Set(bool on, bool military)
        {
            if (_rect == null) return;
            if (on && (!_rect.Visible || military != Military))
            {
                _mat.SetShaderParameter("gain", military ? 2.6f : 1.9f);
                _mat.SetShaderParameter("tint", military ? new Color(0.22f, 1.0f, 0.32f) : new Color(0.88f, 0.90f, 0.88f));
                _mat.SetShaderParameter("grain", military ? GrainMilitary : GrainCivilian);
                _mat.SetShaderParameter("vignette", military ? 0.35f : 0.45f);
                _mat.SetShaderParameter("hot", military ? 0.6f : 0.4f);
            }
            _rect.Visible = on;
            Active = on; Military = military;
        }

        public override void _ExitTree() { if (Active && _rect != null && _rect.Visible) Active = false; }   // a torn-down layer must not leave the world fogless

        /// <summary>Harness: UG_NIGHTVISION=military|civilian puts the goggles on over any scene with no player at all.</summary>
        public static void DebugAttach(Node root)
        {
            string mode = System.Environment.GetEnvironmentVariable("UG_NIGHTVISION");
            if (string.IsNullOrEmpty(mode) || root == null) return;
            var nv = new NightVision();
            root.AddChild(nv);
            nv.Set(true, mode == "military");
            GD.Print($"[nightvision] harness goggles on: {mode}");
        }
    }
}
