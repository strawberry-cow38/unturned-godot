using System;

namespace SDG.Unturned
{
    /// <summary>Holding your breath to steady a scope.
    ///
    /// Hold the sprint control while aiming through a magnifying optic and the sway all but stops, paid for
    /// out of the oxygen bar (strawberry 2026-09-13: "reduces scope sway to almost nothing while holding
    /// shift, but depletes your oxygen, stops before you could ever take damage, but prevents steadying
    /// while empty and for a bit after").
    ///
    /// Engine-free, because all four of the interesting parts are arithmetic: when it is allowed, what it
    /// costs, where it stops, and how long the lockout lasts. The client runs this to decide what the scope
    /// looks like and the server runs it to decide what the oxygen bar actually is; sharing the type is what
    /// keeps those two answers the same.
    ///
    /// ⚠ THE CONTROL IS SPRINT AND THAT IS NOT A CLASH. `equipmentAllowsSprint` is
    /// `!_viewmodel.IsAiming` (PlayerController ~11429), so sprinting is already impossible while aiming --
    /// the exclusivity is structural and pre-existing, not something declared here. See the BindContext note
    /// in Keybinds: tagging two actions as exclusive does not MAKE them exclusive; this pair already was.</summary>
    public sealed class ScopeSteadySim
    {
        /// <summary>Sway amplitude multiplier while steadying. "Almost nothing", deliberately not zero: a
        /// perfectly frozen optic reads as the game having paused rather than as a held breath.
        ///
        /// ⚠ 0.06 DID NOT DELIVER THAT AND THE FIRST VERSION OF THIS COMMENT CLAIMED IT DID. cow tools
        /// measured both arms on an augewehr (hardcoded 4x, Fov 22.5), 22k+ settled samples each:
        ///
        ///     unsteadied   peak 0.379°  =  1.69% of FOV  =  21.6 px of wander @1280
        ///     at 0.06      peak 0.023°  =  0.10% of FOV  =   1.3 px of wander @1280
        ///
        /// One to two pixels is not a tremor, it is frozen -- so 6% bought none of the thing it was chosen
        /// for while still being a magic number rather than an honest zero. 0.18 puts the residual at
        /// roughly 4 px, which moves visibly without undoing the 82% reduction that makes steadying worth
        /// the air. If a frozen optic turns out to be preferred, this should go to 0f rather than back to a
        /// value that splits the difference and achieves neither.</summary>
        public const float SteadySwayScale = 0.18f;

        /// <summary>Oxygen at which steadying cuts out.
        ///
        /// ⚠ THIS IS THE "never takes damage" GUARANTEE AND IT IS CURRENTLY LOAD-BEARING FOR NOTHING.
        /// There is no drowning damage in the port: PlayerVitalsSim's own note records that it was added
        /// once, flagged as unasked-for, and pulled on the owner's instruction (2026-09-07). So today the
        /// floor cannot save you from a harm that does not exist.
        ///
        /// It is written as a floor anyway, and written HERE next to that fact, so that whoever adds
        /// drowning damage later has one place to check. The rule to preserve: this floor must stay strictly
        /// above whatever oxygen level starts hurting the player. If damage ever begins at or above 0.20,
        /// this number moves first.</summary>
        public const float SteadyFloor = 0.20f;

        /// <summary>Oxygen you must recover to before steadying is available again after bottoming out.
        ///
        /// Hysteresis, and it is not optional. With a single threshold, a player sitting exactly at the floor
        /// re-arms the instant refill adds one frame's worth of air, steadies for one frame, drops below, and
        /// cuts out -- the scope strobes at frame rate. The gap between this and SteadyFloor is what makes
        /// the cutout feel like running out rather than like a broken toggle.</summary>
        public const float SteadyRearm = 0.35f;

        /// <summary>The "and for a bit after". Starts when steadying cuts out at the floor, and runs
        /// alongside the refill -- it is not additive with it.</summary>
        public const float LockoutSeconds = 2.5f;

        /// <summary>Seconds of steady available from a full bar, which sets the drain rate. Long enough for
        /// a considered shot, short enough that it cannot be held through a fight.</summary>
        public const float SteadySecondsFromFull = 6f;

        /// <summary>Oxygen per second spent while steadying, derived from the budget above rather than
        /// tuned separately -- two independent numbers would drift apart the first time either moved.</summary>
        public static float DrainPerSecond => (1f - SteadyFloor) / SteadySecondsFromFull;

        /// <summary>Seconds of lockout remaining. Counts down only while the control is RELEASED -- see
        /// Step. Public for the HUD, which greys the reticle hint.</summary>
        public float Lockout { get; private set; }

        /// <summary>True if the last Step actually steadied. This is the value the sway multiplier and the
        /// wire bit both come from, so they cannot disagree about what happened this tick.</summary>
        public bool Steadying { get; private set; }

        /// <summary>Set once steadying has cut out at the floor, cleared when oxygen recovers past
        /// SteadyRearm. Separate from the lockout timer: the timer expires on its own, this one waits for
        /// air, and BOTH must clear.</summary>
        bool _spent;

        /// <summary>Advance one tick.
        ///
        /// <paramref name="wants"/> is the player asking (control held, aiming, magnifying optic).
        /// <paramref name="oxygen"/> is the CURRENT bar, 0..1, and is returned modified. Returns whether
        /// steadying is in effect this tick.
        ///
        /// Oxygen is passed by reference rather than returned as a delta on purpose: the caller must not be
        /// able to apply the sway reduction and forget to apply the cost.</summary>
        public bool Step(bool wants, ref float oxygen, float dt)
        {
            if (dt < 0f) dt = 0f;

            // THE LOCKOUT ONLY RUNS DOWN WHILE THE CONTROL IS RELEASED, and that is a correction, not a
            // detail. Decaying it unconditionally meant a player who kept holding through the empty period
            // burned the timer off while they could not have used it anyway -- so holding LONGER earned a
            // SHORTER penalty than letting go straight away, which is exactly backwards. Tying it to the
            // release also gives it a physical reading: you cannot hold one breath through running out of
            // it. You have to stop, and take another.
            if (!wants && Lockout > 0f) Lockout = MathF.Max(0f, Lockout - dt);
            // Re-arming needs the bar back above SteadyRearm AND the timer expired. Air alone is not enough
            // or a surfacing player steadies again instantly, which is the opposite of "for a bit after".
            if (_spent && oxygen >= SteadyRearm) _spent = false;

            bool allowed = wants && !_spent && Lockout <= 0f && oxygen > SteadyFloor;
            if (!allowed) { Steadying = false; return false; }

            oxygen -= DrainPerSecond * dt;
            if (oxygen <= SteadyFloor)
            {
                // CUT OUT AT THE FLOOR, and never below it. Clamping to the floor rather than to 0 is the
                // whole guarantee: steadying can empty you down to the reserve and no further, whatever the
                // frame time was. A long frame must not be able to spend past it.
                oxygen = SteadyFloor;
                _spent = true;
                Lockout = LockoutSeconds;
                Steadying = false;
                return false;
            }

            Steadying = true;
            return true;
        }

        /// <summary>Sway multiplier for the current state. 1 = full sway.</summary>
        public float SwayScale => Steadying ? SteadySwayScale : 1f;

        /// <summary>Drop all state -- death, respawn, or a peer id being recycled. A lockout that outlives
        /// the life that earned it is a bug nobody would attribute to this class.</summary>
        public void Reset() { Lockout = 0f; Steadying = false; _spent = false; }
    }
}
