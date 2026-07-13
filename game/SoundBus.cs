using Godot;

namespace UnturnedGodot
{
    // Phase 3 HEARING: every in-world sound routes through here carrying a LOUDNESS (how far it carries, ~metres).
    // The bus broadcasts (position, loudness) to all zombies; each zombie's Hear() applies its OWN hearing sphere and
    // ranks what it heard, pathing to the LOUDEST + CLOSEST source (master's design). This generalizes Unturned's
    // AlertTool.alert -- which broadcast a fixed detection RADIUS (gunshot 48, horn 32, clamped <=64) to zombies in
    // range -- into a per-emitter loudness + per-zombie ranking. Suppressed guns / idle emitters just don't Emit.
    public static class SoundBus
    {
        // Loudness = carry radius (m). Grounded in the source AlertTool radii (gunshot 48 / horn 32 / 64 clamp) with
        // stance-scaled footsteps below. Tunable -- these set how far each sound pulls zombies.
        public const float Gunshot    = 48f;   // = PlayerController.GunshotRadius, unsuppressed (suppressed emits nothing)
        public const float Horn       = 32f;   // = source tellHorn AlertTool.alert(pos, 32)
        public const float Explosion  = 64f;   // grenades / rockets -- the source alert clamp (loudest)
        public const float Sprint     = 18f;   // running footsteps
        public const float Walk       = 10f;   // normal footsteps
        public const float CrouchWalk = 5f;    // crouched footsteps -- quiet
        public const float SneakWalk  = 2f;    // prone / sneaking -- barely audible

        // Emit a sound the zombies can hear. loudness<=0 is silent (e.g. suppressed / not moving).
        public static void Emit(SceneTree tree, Vector3 pos, float loudness)
        {
            if (tree == null || loudness <= 0f) return;
            tree.CallGroup("zombies", "Hear", pos, loudness);
        }
    }
}
