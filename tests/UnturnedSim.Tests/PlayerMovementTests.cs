using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    [TestFixture]
    public class PlayerMovementTests
    {
        const float Dt = 0.02f; // 50 Hz

        [Test]
        public void Walk_Forward_UsesStandSpeed()
        {
            var m = new PlayerMovementSim { Stance = EPlayerStance.STAND };
            var v = m.Step(new Vector2(0f, 1f), false, true, Dt);
            Assert.That(v.z, Is.EqualTo(PlayerMovementDef.SPEED_STAND).Within(1e-4)); // 4.5
            Assert.That(v.x, Is.EqualTo(0f).Within(1e-4));
            Assert.That(v.y, Is.EqualTo(0f).Within(1e-4));
        }

        [TestCase(EPlayerStance.SPRINT, 7f)]
        [TestCase(EPlayerStance.STAND, 4.5f)]
        [TestCase(EPlayerStance.CROUCH, 2.5f)]
        [TestCase(EPlayerStance.PRONE, 1.5f)]
        public void StanceSpeeds_MatchSource(EPlayerStance stance, float expected)
        {
            var m = new PlayerMovementSim { Stance = stance };
            var v = m.Step(new Vector2(0f, 1f), false, true, Dt);
            Assert.That(v.z, Is.EqualTo(expected).Within(1e-4));
        }

        [Test]
        public void Diagonal_DoesNotExceedStanceSpeed()
        {
            var m = new PlayerMovementSim { Stance = EPlayerStance.STAND };
            var v = m.Step(new Vector2(1f, 1f), false, true, Dt);
            float horizontal = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            Assert.That(horizontal, Is.EqualTo(PlayerMovementDef.SPEED_STAND).Within(1e-3)); // not 4.5*sqrt2
        }

        [Test]
        public void Jump_SetsUpwardVelocity_ThenGravityBringsItDown()
        {
            var m = new PlayerMovementSim { Stance = EPlayerStance.STAND };
            var v = m.Step(Vector2.zero, true, true, Dt); // grounded + jump
            Assert.That(v.y, Is.EqualTo(PlayerMovementDef.JUMP).Within(1e-4)); // 7
            // one airborne tick: vy drops by GRAVITY*dt
            v = m.Step(Vector2.zero, false, false, Dt);
            Assert.That(v.y, Is.EqualTo(PlayerMovementDef.JUMP - PlayerMovementDef.GRAVITY * Dt).Within(1e-4));
        }

        [Test]
        public void Gravity_ClampsAtTerminalVelocity()
        {
            var m = new PlayerMovementSim();
            float vy = 0f;
            for (int i = 0; i < 2000; i++) vy = m.Step(Vector2.zero, false, false, Dt).y;
            Assert.That(vy, Is.EqualTo(PlayerMovementDef.TERMINAL_VELOCITY).Within(1e-4)); // -100
        }

        [Test]
        public void JumpArc_ApexHeight_IsInUnturnedBand()
        {
            // Integrate a jump over a flat ground at y=0 and record the peak height.
            var m = new PlayerMovementSim { Stance = EPlayerStance.STAND };
            var pos = new Vector3(0, 0, 0);
            bool grounded = true;
            float peak = 0f;
            for (int i = 0; i < 200; i++)
            {
                bool wantJump = i == 0;
                var v = m.Step(Vector2.zero, wantJump, grounded, Dt);
                pos.x += v.x * Dt; pos.y += v.y * Dt; pos.z += v.z * Dt;
                if (pos.y < 0f) { pos.y = 0f; grounded = true; } else grounded = false;
                if (pos.y > peak) peak = pos.y;
                if (i > 0 && grounded) break; // landed
            }
            // analytic apex JUMP^2/(2*GRAVITY) = 0.832; discrete Euler is near it.
            Assert.That(peak, Is.GreaterThan(0.8f));
            Assert.That(peak, Is.LessThan(0.95f));
        }

        [Test]
        public void ForwardDistance_MatchesSpeedTimesTime()
        {
            var m = new PlayerMovementSim { Stance = EPlayerStance.STAND };
            var pos = Vector3.zero;
            for (int i = 0; i < 50; i++) // 50 ticks = 1.0 s
            {
                var v = m.Step(new Vector2(0f, 1f), false, true, Dt);
                pos.z += v.z * Dt;
            }
            Assert.That(pos.z, Is.EqualTo(PlayerMovementDef.SPEED_STAND * 1.0f).Within(1e-3)); // 4.5 m in 1 s
        }

        [Test]
        public void Determinism_SameInputs_SameTrace()
        {
            List<Vector3> Run()
            {
                var m = new PlayerMovementSim { Stance = EPlayerStance.SPRINT };
                var pos = Vector3.zero; bool grounded = true; var trace = new List<Vector3>();
                for (int i = 0; i < 300; i++)
                {
                    var v = m.Step(new Vector2(0.3f, 1f), i % 40 == 0, grounded, Dt);
                    pos.x += v.x * Dt; pos.y += v.y * Dt; pos.z += v.z * Dt;
                    if (pos.y < 0f) { pos.y = 0f; grounded = true; } else grounded = false;
                    trace.Add(pos);
                }
                return trace;
            }
            Assert.That(Run(), Is.EqualTo(Run()));
        }
    }
}
