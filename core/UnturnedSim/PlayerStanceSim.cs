using System;

namespace SDG.Unturned
{
    // The stance state machine extracted VERBATIM from PlayerController._PhysicsProcess (MP_PLAN §3.4:
    // the player sim-core is "movement, stance FSM, vitals, fall damage ... driven by an input struct,
    // runnable server-side headless"). X toggles stand<->crouch (and prone->crouch), Z toggles
    // stand<->prone (and crouch->prone), sprint overlays STAND while stamina allows, broken legs demote
    // sprint (PlayerStance.cs:703), and rising into a ceiling is blocked through the headroom callback --
    // the engine collision query stays outside the core, so this steps identically on the client shell
    // and a headless server.
    public sealed class PlayerStanceSim
    {
        public EPlayerStance BaseStance = EPlayerStance.STAND;
        bool _crouchHeld, _proneHeld;   // key-edge detection state (the old _xHeld/_zHeld)

        /// <summary>One 50 Hz step of the stance FSM. currentCapsuleHeight is the height of the capsule
        /// as it stands NOW (<= 0 on the very first tick, which skips the headroom gate exactly like the
        /// controller's _capStance sentinel did); headroomFor answers "is there space for a capsule this
        /// tall" (Godot shape query on the shell/server; a pure stub in L0 tests).</summary>
        public EPlayerStance Step(bool crouchKey, bool proneKey, bool sprintKey, float stamina, bool broken,
                                  EPlayerStance? scriptedStance, float currentCapsuleHeight, Func<float, bool> headroomFor)
        {
            // Intertwined stance STATE MACHINE (master): X = crouch key, Z = prone key, moving between STAND/CROUCH/PRONE from ANY state.
            if (crouchKey && !_crouchHeld) BaseStance = (BaseStance == EPlayerStance.CROUCH) ? EPlayerStance.STAND : EPlayerStance.CROUCH;   // X: stand<->crouch, and prone->crouch
            _crouchHeld = crouchKey;
            if (proneKey && !_proneHeld) BaseStance = (BaseStance == EPlayerStance.PRONE) ? EPlayerStance.STAND : EPlayerStance.PRONE;      // Z: stand<->prone, and crouch->prone
            _proneHeld = proneKey;
            var wantStance = scriptedStance ?? BaseStance;
            if (wantStance == EPlayerStance.STAND && sprintKey && stamina > 0.05f) wantStance = EPlayerStance.SPRINT;   // sprint overlays standing
            if (broken && wantStance == EPlayerStance.SPRINT) wantStance = EPlayerStance.STAND;   // broken legs can't sprint (PlayerStance.cs:703)
            // can't rise into a ceiling: if the target stance is TALLER than the current capsule and there's no headroom, stay low (master)
            float wantH = PlayerMovementDef.HeightForStance(wantStance);
            if (wantH > currentCapsuleHeight + 0.01f && currentCapsuleHeight > 0f && !headroomFor(wantH))
                wantStance = BaseStance = (currentCapsuleHeight <= PlayerMovementDef.HEIGHT_PRONE + 0.01f) ? EPlayerStance.PRONE : EPlayerStance.CROUCH;   // blocked overhead -> stay in the stance that fits
            return wantStance;
        }
    }
}
