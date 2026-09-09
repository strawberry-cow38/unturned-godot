using Godot;

namespace UnturnedGodot
{
    // COUNTERMEASURE FLARES (strawberry 2026-09-09: "add the ability to flare on rmb in military aircraft only.
    // 1 minute between flares. kills targetting for a full barrage").
    //
    // The state lives on the AIRCRAFT (Vehicle.FlareCooldown / FlareBlind) because that is whose it is, and it is
    // counted down in the vehicle's own physics tick rather than compared against a wall clock -- an offline
    // render must not get a different answer than a live session. This file owns only the ACT: the gate, the
    // timers it sets, and what it looks and sounds like.
    //
    // WHAT IT DOES TO A SAM is deliberately two things, because either alone is a half-measure. It blinds the
    // SITE, so no new lock can start, AND it breaks every missile already homing on the aircraft -- flares that
    // only stopped the next launch would leave six rounds still tracking, and flares that only broke the missiles
    // would be re-locked before the smoke cleared.
    public static class Flares
    {
        public const float CooldownSeconds = 60f;

        /// <summary>How long a salvo blinds the seekers, expressed in the SAM's own numbers rather than a literal
        /// so "a full barrage" cannot drift when those change: the lock plus every shot of a full rack.</summary>
        public static float BlindSeconds => SamSite.LockTime + SamSite.Rack * SamSite.ShotDelay;

        /// <summary>Fire a salvo. Returns false when this aircraft cannot (not a military helicopter, or still
        /// cooling down) so the caller can decide whether that click means something else.</summary>
        public static bool Deploy(Vehicle v)
        {
            if (v == null || !GodotObject.IsInstanceValid(v) || !v.FlaresReady) return false;
            v.FlareCooldown = CooldownSeconds;
            v.FlareBlind = BlindSeconds;

            // Break every round already in the air at this aircraft. Doing it here rather than letting each
            // missile notice the flag next tick means the beeps stop on the frame you press the button, which is
            // the feedback that tells you it worked.
            SamMissile.Decoy(v);

            Burst(v);
            return true;
        }

        static void Burst(Vehicle v)
        {
            var host = v.GetParent();
            if (host == null) return;

            var p = new CpuParticles3D
            {
                Amount = 26, Lifetime = 3.2, OneShot = true, Explosiveness = 0.75f, LocalCoords = false,
                Mesh = new QuadMesh { Size = new Vector2(0.7f, 0.7f) },
                Direction = Vector3.Down, Spread = 55f,
                InitialVelocityMin = 6f, InitialVelocityMax = 14f,
                ScaleAmountMin = 0.5f, ScaleAmountMax = 1.3f,
                Gravity = new Vector3(0f, -7f, 0f),   // they FALL away from the aircraft; that is the whole read
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    BillboardKeepScale = true,   // ⚠ without this ScaleAmount is dropped and every flare is a 1 m sheet
                    AlbedoColor = new Color(1f, 0.86f, 0.42f, 0.95f),
                },
                // ⚠ Position BEFORE AddChild: an explosive one-shot emits at the transform it had ENTERING the
                // tree, so a GlobalPosition written afterwards puts the whole salvo at the world origin.
                Position = v.GlobalPosition,
                VisibilityAabb = new Aabb(new Vector3(-60f, -60f, -60f), new Vector3(120f, 120f, 120f)),
            };
            host.AddChild(p);
            p.Emitting = true;
            p.Finished += p.QueueFree;

            // No dedicated flare clip in the rip; the smoke grenade's canister pop already stands in for a
            // launched countermeasure (see Grenade.cs), so it stands in here too rather than inventing one.
            var clip = GameAudio.Pick("casings", "general");
            if (clip != null) GameAudio.PlayAt(host, clip, v.GlobalPosition, 4f, 10f, 220f, 0.75f);
        }
    }
}
