using Godot;

namespace UnturnedGodot
{
    /// <summary>The screen effect for standing in contaminated ground (strawberry 2026-09-11: "add a heavy
    /// film grain effect that intensifies the longer you stay in the deadzone, desaturate color, starts
    /// tame").
    ///
    /// LAYER 8, between the lens (7) and the rain overlay (9), and the slot is chosen for the same reason
    /// ChromaticAberration documents its own: a canvas_item shader reading hint_screen_texture affects
    /// whatever is drawn BELOW its CanvasLayer.
    ///   0..4 world   5 viewmodel   6 nightvision   7 chromatic   -- 8: here --   9 rain   10 HUD   12 vitals
    /// ABOVE chromatic because grain is a sensor/film artefact and belongs at the end of the optical chain,
    /// after the lens has done its distorting. BELOW the HUD for the reason the lens is: graining out your own
    /// health bar reads as the game breaking, not as the world being poisonous -- and the radiation icon in
    /// particular has to stay legible precisely while this effect is at its loudest.
    ///
    /// The ramp is read from the PLAYER, not kept here. DeadzoneField publishes PlayerController.DeadzoneSeconds
    /// from the same sim clock that doses you, so the grain cannot drift out of step with the damage it is
    /// warning about.</summary>
    public partial class DeadzoneOverlay : CanvasLayer
    {
        /// <summary>Live instance, mirroring ChromaticAberration.Current so settings/tests can reach it
        /// without walking the tree. Null before a world is built.</summary>
        public static DeadzoneOverlay Current;

        /// <summary>The player whose exposure drives the effect. Set by whoever builds the world.</summary>
        public PlayerController Player;

        /// <summary>Seconds of continuous exposure at which the effect reaches full strength. Deliberately
        /// close to the unprotected time-to-death (~40 s at the default zone's 0.025 infection/second): the
        /// screen is at its worst about when you are, so "it looks bad" and "you are nearly dead" are the
        /// same signal rather than two things to learn separately.</summary>
        public static float FullExposureSeconds = 40f;

        public static bool Enabled = true;

        ColorRect _rect;
        ShaderMaterial _mat;
        double _t;

        public override void _Ready()
        {
            Layer = 8; ProcessPriority = 191;   // just after the lens, same frame
            Current = this;
            AddToGroup("deadzone_overlay");
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/deadzone.gdshader") };
            _rect = new ColorRect { Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore, Color = Colors.White };
            _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_rect);
            _rect.Visible = false;   // nothing to pay for until someone walks into a zone
        }

        public override void _Process(double delta)
        {
            _t += delta;
            float exposure = DebugForcedSeconds.HasValue
                ? Mathf.Clamp(DebugForcedSeconds.Value / Mathf.Max(0.01f, FullExposureSeconds), 0f, 1f)
                : ExposureFor(Player);

            // HIDDEN, not zero-intensity, when clear -- the same reasoning ChromaticAberration spells out: a
            // visible rect at exposure 0 still samples the screen for every pixel and hands back what it
            // started with, which is the entire cost of the pass in exchange for no visible effect.
            bool show = Enabled && exposure > 0.001f;
            if (_rect == null || _mat == null) return;
            _rect.Visible = show;
            if (!show) return;

            _mat.SetShaderParameter("exposure", exposure);
            _mat.SetShaderParameter("time_seconds", (float)_t);
        }

        /// <summary>0..1 exposure for a player. Static and null-safe so the L1 tests can assert the ramp
        /// without standing up a CanvasLayer and a shader.</summary>
        public static float ExposureFor(PlayerController p)
        {
            if (p == null || !GodotObject.IsInstanceValid(p)) return 0f;
            float secs = p.DeadzoneSeconds;
            if (secs <= 0f) return 0f;
            return Mathf.Clamp(secs / Mathf.Max(0.01f, FullExposureSeconds), 0f, 1f);
        }

        /// <summary>What the shader is currently being driven with, for tests.</summary>
        public float DebugExposure => DebugForcedSeconds.HasValue
            ? Mathf.Clamp(DebugForcedSeconds.Value / Mathf.Max(0.01f, FullExposureSeconds), 0f, 1f)
            : ExposureFor(Player);
        public bool DebugVisible => _rect != null && _rect.Visible;

        /// <summary>UG_DEADZONE=&lt;seconds&gt; over a harness scene, mirroring ChromaticAberration.DebugAttach and
        /// existing for the same stated reason: the real mount is on the PEI world path, PEI renders take ~400 s
        /// against ~120 for the deploy-test stage, and a purely visual change has to be lookable-at.
        ///
        /// Added after the first attempt at verifying this shader produced two BYTE-IDENTICAL captures -- the
        /// overlay simply was not mounted in the scene being rendered, and every other signal (clean build,
        /// shader compiles, PNG written) was perfectly happy about it.
        ///
        /// Drives exposure directly rather than through a player, because a harness stage has no deadzone and
        /// often no player: the point is to see the effect, not to simulate earning it.</summary>
        public static DeadzoneOverlay DebugAttach(Node root)
        {
            string s = System.Environment.GetEnvironmentVariable("UG_DEADZONE");
            if (string.IsNullOrEmpty(s) || root == null || !float.TryParse(s, out float secs)) return null;
            Enabled = true;
            var dz = new DeadzoneOverlay { DebugForcedSeconds = secs };
            root.AddChild(dz);
            Log.Print($"[deadzone] harness on at {secs:0.#}s exposure (ramp full at {FullExposureSeconds:0.#}s)");
            return dz;
        }

        /// <summary>Harness override: when set, drives the ramp instead of the player's own exposure.</summary>
        public float? DebugForcedSeconds;
    }
}
