using Godot;

namespace UnturnedGodot
{
    // Phase 3 HEARING: every in-world sound routes through here carrying a LOUDNESS (how far it carries, ~metres).
    // Originally the bus broadcast (position, loudness) to all zombies; each zombie's Hear() applied its OWN
    // hearing sphere and ranked what it heard, pathing to the LOUDEST + CLOSEST source (master's design). This
    // generalized Unturned's AlertTool.alert -- which broadcast a fixed detection RADIUS (gunshot 48, horn 32,
    // clamped <=64) to zombies in range -- into a per-emitter loudness + per-zombie ranking. The zombie system
    // (and its listener below) was removed (master 2026-08-2x: "rip out everything zombie related"); Emit is now
    // a no-op hook point for whatever next listens (e.g. a future animal/enemy hearing system) -- every call site
    // (footsteps, gunshots, horns, doors) is left in place since loudness-carry is still a meaningful concept.
    public static class SoundBus
    {
        // Loudness = carry radius (m). Grounded in the source AlertTool radii (gunshot 48 / horn 32 / 64 clamp)
        // with stance-scaled footsteps below. Tunable -- these set how far each sound would carry to a listener.
        public const float Gunshot    = 48f;   // = PlayerController.GunshotRadius, unsuppressed (suppressed emits nothing)
        public const float Horn       = 32f;   // = source tellHorn AlertTool.alert(pos, 32)
        public const float Explosion  = 64f;   // grenades / rockets -- the source alert clamp (loudest)
        public const float Sprint     = 18f;   // running footsteps
        public const float Walk       = 10f;   // normal footsteps
        public const float CrouchWalk = 5f;    // crouched footsteps -- quiet
        public const float SneakWalk  = 2f;    // prone / sneaking -- barely audible

        // Emit a sound at (pos, loudness). loudness<=0 is silent (e.g. suppressed / not moving).
        // (enemy hearing removed with the zombie system -- no listener is wired up right now; every
        // caller is unchanged so a future listener can be added here without touching them.)
        public static void Emit(SceneTree tree, Vector3 pos, float loudness)
        {
            if (tree == null || loudness <= 0f) return;
        }
    }
}
