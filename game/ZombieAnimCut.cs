using Godot;

namespace UnturnedGodot
{
    // F6 debug toggle (strawberry, POI-fps hunt): freeze ALL skeletal animation so the physics-frame cost of the
    // zombie horde's rigs -- 17 bones x N zombies posed by the AnimationMixer in the PHYSICS callback
    // (RiggedCharacter.UsePhysicsAnimRate) -- can be read straight off F3's physics line. This is the "skeletons" leg
    // of the engine-side cut (bodies vs skeletons vs nav): freeze it, watch F3 physics ms; the drop is the skeleton
    // share. Player/animal rigs freeze too, but they're a handful next to a horde, so the delta is the zombies.
    // NOT a fix -- an instrument. z.rig can't see this cost (it times the near-no-op Tick(); the posing is engine-side).
    public partial class ZombieAnimCut : CanvasLayer
    {
        Label _label;

        public override void _Ready()
        {
            Instance = this;
            Layer = 90;
            ProcessMode = Node.ProcessModeEnum.Always;   // keep toggling even while the sim is paused
            _label = new Label { Position = new Vector2(10, 210), Visible = false };
            _label.AddThemeFontSizeOverride("font_size", 14);
            _label.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.55f));
            _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            _label.AddThemeConstantOverride("outline_size", 4);
            AddChild(_label);
        }

        // Console verb `freezerigs` rather than F6 -- the function keys are vehicle seat selection now
        // (strawberry 2026-08-16).
        public static ZombieAnimCut Instance;

        /// <summary>Freeze/unfreeze every rig's animation, to price skinning out of the frame.</summary>
        public bool ToggleFrozen()
        {
            RiggedCharacter.SetAnimFrozen(!RiggedCharacter.AnimFrozen);
            _label.Visible = RiggedCharacter.AnimFrozen;
            _label.Text = $"RIG ANIM FROZEN — {RiggedCharacter.LiveRigCount} rigs (`freezerigs`) — skeletons-cut: read `profiler` physics ms";
            return RiggedCharacter.AnimFrozen;
        }
    }
}
