using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 tests pinning the extracted combat math (proposal phase 3): explosion falloffs (DamageTool.explode),
    // fall damage (PlayerLife.onLanded), and the stance -> stealth-detection-radius table (PlayerStance).
    [TestFixture]
    public class CombatMathTests
    {
        // --- explosion falloff: zombies/vehicles LINEAR (Zombie.cs:270), player SQUARED (Player.cs:1975) ---

        [TestCase(0f, 175f)]        // point blank = full damage
        [TestCase(4f, 87.5f)]       // half range = half damage
        [TestCase(6f, 43.75f)]
        [TestCase(8f, 0f)]          // exactly at radius = zero
        [TestCase(9f, 0f)]          // out of radius = nothing
        public void Explosion_Linear_Falloff_Grenade_Values(float range, float expected)
        {
            Assert.That(ExplosionMath.Linear(175f, range, 8f), Is.EqualTo(expected).Within(0.01f));
        }

        [Test]
        public void Explosion_Squared_Falloff_Hits_The_Thrower_Harder_Up_Close()
        {
            Assert.That(ExplosionMath.Squared(175f, 0f, 8f), Is.EqualTo(175f).Within(0.01f));
            Assert.That(ExplosionMath.Squared(175f, 4f, 8f), Is.EqualTo(175f * 0.75f).Within(0.01f));   // 1-(1/2)^2
            Assert.That(ExplosionMath.Squared(175f, 8f, 8f), Is.EqualTo(0f).Within(0.01f));
            Assert.That(ExplosionMath.Squared(175f, 10f, 8f), Is.EqualTo(0f).Within(0.01f));
            // squared > linear at every interior range (the thrower is punished for being close)
            Assert.That(ExplosionMath.Squared(175f, 4f, 8f), Is.GreaterThan(ExplosionMath.Linear(175f, 4f, 8f)));
        }

        // --- fall damage: threshold 22 m/s, damage = min(101, |v|) x armor, rounded ---

        [Test]
        public void Fall_Below_Threshold_Is_Free()
        {
            Assert.That(FallMath.Hurts(-7f), Is.False);      // a normal jump
            Assert.That(FallMath.Hurts(-22f), Is.False);     // exactly at threshold
            Assert.That(FallMath.Damage(-21.9f), Is.EqualTo(0));
            Assert.That(FallMath.Hurts(-22.1f), Is.True);
        }

        [Test]
        public void Fall_Damage_Scales_And_Caps()
        {
            Assert.That(FallMath.Damage(-50f), Is.EqualTo(50));            // dmg = |v|
            Assert.That(FallMath.Damage(-50f, 0.4f), Is.EqualTo(20));      // vest .5 x hat .8 armor product
            Assert.That(FallMath.Damage(-200f), Is.EqualTo(101));          // RoundAndClampToByte cap
            Assert.That(FallMath.Damage(-50.4f), Is.EqualTo(50));          // rounded
        }

        [Test]
        public void Bone_Break_Gated_By_Clothing()
        {
            Assert.That(FallMath.BreaksLegs(-40f, preventsBoneBreak: false), Is.True);
            Assert.That(FallMath.BreaksLegs(-40f, preventsBoneBreak: true), Is.False);   // Prevents_Falling_Broken_Bones
            Assert.That(FallMath.BreaksLegs(-10f, preventsBoneBreak: false), Is.False);  // soft landing
        }

        // --- stealth detection radius. Retail's table is STAND 12 / CROUCH 6 / PRONE 3 / SPRINT 20, x1.1 moving,
        // clamp [1,64]; DETECT_SCALE 0.4 shrinks the ON-FOOT four (strawberry 2026-09-17 "reduce em by a lot").
        //
        // ⚠ These are the SCALED ABSOLUTE numbers on purpose, not `DETECT_STAND * DETECT_SCALE`. Writing the
        // arithmetic back would restate the implementation and pass for any scale whatsoever, including one
        // changed by accident -- the point is that moving the knob has to be a deliberate edit here too.

        [TestCase(EPlayerStance.STAND, 4.8f)]
        [TestCase(EPlayerStance.CROUCH, 2.4f)]
        [TestCase(EPlayerStance.PRONE, 1.2f)]
        [TestCase(EPlayerStance.SPRINT, 8f)]
        public void Stance_Radius_Table_Matches_Source(EPlayerStance stance, float expected)
        {
            Assert.That(StealthDetection.Radius(stance, moving: false), Is.EqualTo(expected).Within(1e-4f));
        }

        [Test]
        public void Moving_Multiplies_By_1_1()
        {
            Assert.That(StealthDetection.Radius(EPlayerStance.STAND, moving: true), Is.EqualTo(5.28f).Within(1e-4f));
            Assert.That(StealthDetection.Radius(EPlayerStance.PRONE, moving: true), Is.EqualTo(1.32f).Within(1e-4f));
        }

        [Test]
        public void Unlisted_Stances_Fall_Back_To_Stand()
        {
            Assert.That(StealthDetection.Radius(EPlayerStance.SWIM, moving: false), Is.EqualTo(4.8f).Within(1e-4f));
        }

        // The invariant that OUTLIVES any retune: the stances have to keep ranking in that order, and none may
        // land on the MIN clamp, or several stances detect at exactly the same distance and the whole stealth
        // system reads as applied while doing nothing. A scale of 0.05 would pass every absolute check above if
        // someone updated them to match, and would fail this.
        [Test]
        public void Quieter_Stances_Stay_Strictly_Quieter()
        {
            float sprint = StealthDetection.Radius(EPlayerStance.SPRINT, false);
            float stand  = StealthDetection.Radius(EPlayerStance.STAND, false);
            float crouch = StealthDetection.Radius(EPlayerStance.CROUCH, false);
            float prone  = StealthDetection.Radius(EPlayerStance.PRONE, false);
            Assert.That(prone, Is.LessThan(crouch), "prone must beat crouching");
            Assert.That(crouch, Is.LessThan(stand), "crouching must beat standing");
            Assert.That(stand, Is.LessThan(sprint), "standing must beat sprinting");
            Assert.That(prone, Is.GreaterThan(StealthDetection.MIN), "nothing may sit ON the clamp -- it flattens the table");
        }

        // DELIBERATE, and recorded because it is a THRESHOLD crossing rather than a scaling: PlayerController only
        // emits a footstep when the radius exceeds 2 m, so at this scale crawling is completely silent while
        // crouching (2.4) still just clears it. If that floor ever moves, this is the test that notices.
        [Test]
        public void Prone_Falls_Under_The_Game_Emit_Floor_And_Crouch_Does_Not()
        {
            const float EmitFloor = 2f;   // PlayerController: `if (loud > 2f) SoundBus.Emit(...)`
            Assert.That(StealthDetection.Radius(EPlayerStance.PRONE, moving: true), Is.LessThan(EmitFloor));
            Assert.That(StealthDetection.Radius(EPlayerStance.CROUCH, moving: true), Is.GreaterThan(EmitFloor));
        }

        [Test]
        public void Driving_Radius_Scales_With_Forward_Speed_And_Clamps()
        {
            Assert.That(StealthDetection.DrivingRadius(1f), Is.EqualTo(48f).Within(1e-4f));    // flat out
            Assert.That(StealthDetection.DrivingRadius(0.5f), Is.EqualTo(24f).Within(1e-4f));
            Assert.That(StealthDetection.DrivingRadius(0f), Is.EqualTo(1f).Within(1e-4f));     // parked ~silent (clamp floor)
            Assert.That(StealthDetection.DrivingRadius(2f), Is.EqualTo(64f).Within(1e-4f));    // clamp ceiling
        }
    }
}
