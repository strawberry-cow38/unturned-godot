using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    /// <summary>You cannot jump again the instant you land.
    ///
    /// master 2026-10-05: "add a slight delay between landing and being able to jump again". Retail has no
    /// landing recovery at all, so this is a deliberate divergence rather than a port, and it lives in
    /// PlayerMovementSim for the reason the held-weapon speed penalty does: that class is the one movement sim
    /// on every machine, so the shell, the client's prediction and the server's integration all obey it without
    /// being told separately.
    ///
    /// ⭐ THE CONTROLS ARE WHAT MAKE THIS MEAN ANYTHING. "The player did not jump" is the passing state of a
    /// test where jumping is simply broken, so every case below is paired with one that MUST jump.</summary>
    [TestFixture]
    public class LandingJumpLockTests
    {
        const float Dt = 1f / 50f;
        static readonly Vector2 Still = new Vector2(0f, 0f);

        static bool Jumped(PlayerMovementSim sim) => sim.Velocity.y > PlayerMovementDef.JUMP * 0.5f;

        /// <summary>Fly for a moment, then touch down on this tick asking to jump. Returns the sim mid-landing.</summary>
        static PlayerMovementSim Land(bool jumpOnTouchdown)
        {
            var sim = new PlayerMovementSim();
            for (int i = 0; i < 5; i++) sim.Step(Still, wantJump: false, grounded: false, Dt);   // airborne
            sim.Step(Still, wantJump: jumpOnTouchdown, grounded: true, Dt);                      // touchdown
            return sim;
        }

        [Test]
        public void a_player_on_the_ground_can_jump()
        {
            // CONTROL, and it comes first on purpose: if this fails every other assert here is vacuous.
            var sim = new PlayerMovementSim();
            sim.Step(Still, wantJump: false, grounded: true, Dt);
            Assert.That(sim.JumpLocked, Is.False, "standing still on the floor is not a landing");
            sim.Step(Still, wantJump: true, grounded: true, Dt);
            Assert.That(Jumped(sim), Is.True, "control: an ordinary standing jump has to work");
        }

        [Test]
        public void a_fresh_sim_is_not_locked()
        {
            // ⚠ THE REGRESSION THIS GUARDS. _wasGrounded defaults to TRUE deliberately -- with the C# default of
            // false, a sim's FIRST grounded tick reads as an airborne->grounded transition and locks a freshly
            // spawned player out of jumping for no reason anybody could see.
            var sim = new PlayerMovementSim();
            sim.Step(Still, wantJump: true, grounded: true, Dt);
            Assert.That(Jumped(sim), Is.True, "a player who just spawned standing on the ground must be able to jump at once");
        }

        [Test]
        public void landing_refuses_the_jump_on_the_touchdown_tick()
        {
            var sim = Land(jumpOnTouchdown: true);
            Assert.That(Jumped(sim), Is.False, "the tick you touch down is not a tick you may jump on");
            Assert.That(sim.JumpLocked, Is.True, "...and the sim says why");
        }

        [Test]
        public void the_lock_lasts_the_configured_time_and_then_lets_go()
        {
            var sim = Land(jumpOnTouchdown: false);
            Assert.That(sim.JumpLocked, Is.True, "touchdown armed the lock");

            // DERIVED, not hardcoded: step until the jump is accepted and compare the elapsed time with the
            // constant. Asserting "blocked for 10 ticks" would pass a 50 Hz build and silently mean something
            // else at another rate, and would not notice the constant being changed out from under it.
            float waited = 0f;
            int guard = 0;
            while (guard++ < 500)
            {
                sim.Step(Still, wantJump: true, grounded: true, Dt);   // keep asking every tick, as a held key does
                if (Jumped(sim)) break;
                waited += Dt;
            }
            Assert.That(guard, Is.LessThan(500), "the lock has to let go eventually -- this is a delay, not a ban");
            Assert.That(waited, Is.EqualTo(PlayerMovementDef.LANDING_JUMP_LOCK).Within(Dt * 1.5f),
                        $"the wait has to be LANDING_JUMP_LOCK ({PlayerMovementDef.LANDING_JUMP_LOCK}s), not some other delay");
        }

        [Test]
        public void a_jump_asked_for_during_the_lock_is_dropped_not_queued()
        {
            // The nastier half of "no spamming": if the refused press were BUFFERED, holding the key through the
            // lock would auto-fire the moment it expired and the chain this prevents would come straight back.
            var sim = Land(jumpOnTouchdown: true);          // asked, and refused
            int ticks = (int)(PlayerMovementDef.LANDING_JUMP_LOCK / Dt) + 2;
            for (int i = 0; i < ticks; i++) sim.Step(Still, wantJump: false, grounded: true, Dt);   // key RELEASED from here on
            Assert.That(Jumped(sim), Is.False, "the refused press must not fire itself once the lock expires");
            Assert.That(sim.JumpLocked, Is.False, "...and by now the lock is genuinely gone");

            sim.Step(Still, wantJump: true, grounded: true, Dt);   // a fresh press
            Assert.That(Jumped(sim), Is.True, "control: a NEW press after the lock works, so the drop above is not the jump being broken");
        }

        [Test]
        public void the_lock_does_not_slow_you_down()
        {
            // Master asked for a delay on JUMPING. Walking out of a landing is untouched, and if this ever
            // starts failing the lock has grown into something nobody asked for.
            var locked = Land(jumpOnTouchdown: false);
            Assert.That(locked.JumpLocked, Is.True);
            var vLocked = locked.Step(new Vector2(0f, 1f), wantJump: false, grounded: true, Dt);

            var free = new PlayerMovementSim();
            free.Step(Still, wantJump: false, grounded: true, Dt);
            var vFree = free.Step(new Vector2(0f, 1f), wantJump: false, grounded: true, Dt);

            Assert.That(vLocked.z, Is.EqualTo(vFree.z).Within(1e-5f), "landing recovery must not touch walk speed");
            Assert.That(vFree.z, Is.GreaterThan(0.5f), "control: the walk being measured is a real one");
        }
    }
}
