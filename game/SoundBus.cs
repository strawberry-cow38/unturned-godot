using Godot;
using UnturnedSim;

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

        // A single listener (ZombieChunkField) subscribes to hear every emitted sound -- one cheap callback, NOT the old
        // per-zombie broadcast. Static so the emit call sites (footsteps/gunshots/horn/doors) never reference the field.
        public static System.Action<Vector3, float> OnNoise;

        // Emit a sound at (pos, loudness). loudness<=0 is silent (e.g. suppressed / not moving).
        //
        // RAIN MASKS IT. bitvox approved rain as a stealth window: "make sure it is pretty limited, not a
        // free pass". The rule lives in UnturnedSim.NoiseMasking so the numbers are arguable in a test
        // rather than in the game; the short version is that it subtracts a distance instead of scaling
        // one, so heavy rain takes about a quarter off moving and a tenth off shooting.
        //
        // APPLIED HERE, at the one choke point every call site already funnels through -- footsteps,
        // gunshots, horns, doors. Putting it at the call sites instead would mean six copies of a rule that
        // has to agree, and the ones added later would not have it at all.
        //
        // Reads WeatherManager.Current rather than a field something has to remember to update: no static
        // weather state to leak across a scene reload, and in multiplayer the SERVER's own weather governs,
        // which is the side actually running the hearing check.
        public static void Emit(SceneTree tree, Vector3 pos, float loudness)
        {
            if (tree == null || loudness <= 0f) return;
            var wm = WeatherManager.Current;
            if (wm != null && GodotObject.IsInstanceValid(wm))
                loudness = NoiseMasking.Carry(loudness, wm.RainIntensity * wm.Severity);
            if (loudness <= 0f) return;
            OnNoise?.Invoke(pos, loudness);
        }
    }
}
