using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests pinning the shared bullet-integration model (MP Phase 5): the source BulletInfo step
    // (UseableGun.cs:1539-1542) -- pos += vel*0.02 then vel.y += gravity*0.02 -- now lives in ONE place
    // (BallisticsMath), called by both PlayerController.StepBullets (SP) and ServerCombat (the MP server),
    // so these constants lock the trajectory both paths fly.
    [TestFixture]
    public class BallisticsMathTests
    {
        [Test]
        public void NextPos_IsPosPlusVelTimesStep()
        {
            var next = BallisticsMath.NextPos(new Vector3(1f, 2f, 3f), new Vector3(500f, -10f, 0f));
            Assert.That(next.x, Is.EqualTo(1f + 500f * 0.02f));
            Assert.That(next.y, Is.EqualTo(2f + -10f * 0.02f));
            Assert.That(next.z, Is.EqualTo(3f));
        }

        [Test]
        public void StepVel_DropsOnlyY_ByGravityTimesStep()
        {
            float gravity = -9.81f * 4f;   // the Eaglefire's Bullet_Gravity_Multiplier 4
            var vel = BallisticsMath.StepVel(new Vector3(500f, 0f, 0f), gravity);
            Assert.That(vel.x, Is.EqualTo(500f));
            Assert.That(vel.z, Is.EqualTo(0f));
            Assert.That(vel.y, Is.EqualTo(gravity * 0.02f));
        }

        [Test]
        public void EaglefireTrajectory_20Steps_DropsAboutThreeMetersOver200m()
        {
            // Eaglefire: MuzzleVelocity 500 m/s, 20 steps (0.4 s to 200 m), gravMult 4 -> the documented
            // "~3 m drop over 200 m" (GunDef.cs) -- computed through the exact step loop the game runs.
            var pos = Vector3.zero;
            var vel = new Vector3(500f, 0f, 0f);
            float gravity = -9.81f * 4f;
            for (int i = 0; i < 20; i++)
            {
                pos = BallisticsMath.NextPos(pos, vel);
                vel = BallisticsMath.StepVel(vel, gravity);
            }
            Assert.That(pos.x, Is.EqualTo(200f).Within(0.001f), "flies its full 200 m range");
            // discrete drop = g*dt^2 * (0+1+...+19) = -39.24 * 0.0004 * 190 = -2.98 m
            Assert.That(pos.y, Is.EqualTo(-39.24f * 0.02f * 0.02f * 190f).Within(0.001f), "gravity drop matches the closed-form discrete sum");
            Assert.That(pos.y, Is.EqualTo(-2.98f).Within(0.02f), "~3 m drop at max range (the GunDef doc value)");
        }
    }
}
