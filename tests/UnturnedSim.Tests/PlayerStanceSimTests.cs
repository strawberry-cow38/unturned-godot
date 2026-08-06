using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // The stance FSM extracted from PlayerController (MP_PLAN §4 Phase 4: the sim-core split). These pin
    // the exact key-edge semantics the controller shipped with: X toggles stand<->crouch (prone->crouch),
    // Z toggles stand<->prone (crouch->prone), sprint overlays STAND gated by stamina + broken legs, and
    // the headroom callback blocks rising into a ceiling (also demoting BaseStance).
    [TestFixture]
    public class PlayerStanceSimTests
    {
        static bool Roomy(float h) => true;
        const float StandCap = PlayerMovementDef.HEIGHT_STAND;

        static EPlayerStance Step(PlayerStanceSim s, bool x = false, bool z = false, bool sprint = false,
                                  float stamina = 1f, bool broken = false, EPlayerStance? scripted = null,
                                  float cap = StandCap, System.Func<float, bool> headroom = null,
                                  bool equipmentAllowsSprint = true)
            => s.Step(x, z, sprint, stamina, broken, scripted, cap, headroom ?? Roomy, equipmentAllowsSprint);

        // ADS AND SPRINT ARE MUTUALLY EXCLUSIVE (strawberry). Source picks a specific one of the two possible
        // readings: PlayerStance.cs:701 folds `canAimDuringSprint || !gun.isAiming` into the gate that ENTERS the
        // sprint stance, so aiming WITHHOLDS the sprint -- it does not cancel the aim. The distinction is the whole
        // behaviour: cancel-the-aim would rip your sights away every time sprint was brushed.
        [Test]
        public void Aiming_WithholdsSprint_RatherThanCancellingTheAim()
        {
            var s = new PlayerStanceSim();
            Assert.That(Step(s, sprint: true), Is.EqualTo(EPlayerStance.SPRINT), "sprint works with a free weapon");
            Assert.That(Step(s, sprint: true, equipmentAllowsSprint: false), Is.EqualTo(EPlayerStance.STAND),
                        "a shouldered gun withholds the sprint stance");
            // ...and the base stance is untouched, so releasing the aim resumes the sprint rather than needing a
            // fresh key press -- a withheld sprint is not a cancelled one.
            Assert.That(Step(s, sprint: true), Is.EqualTo(EPlayerStance.SPRINT), "and it comes straight back");
        }

        [Test]
        public void Aiming_DoesNotDisturbCrouchOrProne()
        {
            // The gate rides on the STAND->SPRINT overlay only. An aiming player who is crouched must stay crouched,
            // not get pushed to STAND by a rule that is about sprinting.
            var s = new PlayerStanceSim();
            Assert.That(Step(s, x: true), Is.EqualTo(EPlayerStance.CROUCH));
            Assert.That(Step(s, x: false, sprint: true, equipmentAllowsSprint: false), Is.EqualTo(EPlayerStance.CROUCH),
                        "aiming while crouched leaves the crouch alone");
        }

        [Test]
        public void CrouchKey_TogglesStandCrouch_OnPressEdgeOnly()
        {
            var s = new PlayerStanceSim();
            Assert.That(Step(s, x: true), Is.EqualTo(EPlayerStance.CROUCH), "press edge crouches");
            Assert.That(Step(s, x: true), Is.EqualTo(EPlayerStance.CROUCH), "holding X does not re-toggle");
            Assert.That(Step(s), Is.EqualTo(EPlayerStance.CROUCH), "release keeps the stance");
            Assert.That(Step(s, x: true), Is.EqualTo(EPlayerStance.STAND), "second press edge stands back up");
        }

        [Test]
        public void ProneKey_TogglesStandProne_AndCrossToggles()
        {
            var s = new PlayerStanceSim();
            Assert.That(Step(s, z: true), Is.EqualTo(EPlayerStance.PRONE), "Z prones");
            Assert.That(Step(s), Is.EqualTo(EPlayerStance.PRONE));
            Assert.That(Step(s, x: true), Is.EqualTo(EPlayerStance.CROUCH), "X from prone -> crouch (intertwined FSM)");
            Assert.That(Step(s), Is.EqualTo(EPlayerStance.CROUCH));
            Assert.That(Step(s, z: true), Is.EqualTo(EPlayerStance.PRONE), "Z from crouch -> prone");
            Assert.That(Step(s, z: false), Is.EqualTo(EPlayerStance.PRONE));
            Assert.That(Step(s, z: true), Is.EqualTo(EPlayerStance.STAND), "Z from prone -> stand");
        }

        [Test]
        public void Sprint_OverlaysStandOnly_GatedByStaminaAndLegs()
        {
            var s = new PlayerStanceSim();
            Assert.That(Step(s, sprint: true), Is.EqualTo(EPlayerStance.SPRINT), "shift on stand sprints");
            Assert.That(Step(s, sprint: true, stamina: 0.05f), Is.EqualTo(EPlayerStance.STAND), "winded (<= 0.05 stamina) can't sprint");
            Assert.That(Step(s, sprint: true, broken: true), Is.EqualTo(EPlayerStance.STAND), "broken legs can't sprint (PlayerStance.cs:703)");
            Step(s, x: true);   // crouch
            Assert.That(Step(s, sprint: true), Is.EqualTo(EPlayerStance.CROUCH), "sprint never overlays crouch");
        }

        [Test]
        public void ScriptedStance_Overrides_ButHeadroomStillGates()
        {
            var s = new PlayerStanceSim();
            Assert.That(Step(s, scripted: EPlayerStance.PRONE), Is.EqualTo(EPlayerStance.PRONE), "scripted stance bypasses the keys");
            Assert.That(s.BaseStance, Is.EqualTo(EPlayerStance.STAND), "scripting does not rewrite the base stance");
            // scripted STAND under a low ceiling while the capsule is prone-height -> stays low
            Assert.That(Step(s, scripted: EPlayerStance.STAND, cap: PlayerMovementDef.HEIGHT_PRONE, headroom: _ => false),
                        Is.EqualTo(EPlayerStance.PRONE), "no headroom -> can't rise even when scripted");
        }

        [Test]
        public void NoHeadroom_BlocksRising_AndDemotesBaseStance()
        {
            var s = new PlayerStanceSim();
            Step(s, x: true);   // crouched, capsule 1.2
            // X again wants STAND, but the ceiling says no -> stay crouched AND the base stance is rewritten
            Assert.That(Step(s, x: true, cap: PlayerMovementDef.HEIGHT_CROUCH, headroom: _ => false),
                        Is.EqualTo(EPlayerStance.CROUCH), "blocked overhead -> stay in the stance that fits");
            Assert.That(s.BaseStance, Is.EqualTo(EPlayerStance.CROUCH), "base stance demoted so releasing the key doesn't bounce");
            // prone capsule under a ceiling stays prone
            var p = new PlayerStanceSim();
            Step(p, z: true);
            Assert.That(Step(p, z: true, cap: PlayerMovementDef.HEIGHT_PRONE, headroom: _ => false),
                        Is.EqualTo(EPlayerStance.PRONE));
        }

        [Test]
        public void FirstTick_CapsuleSentinel_SkipsHeadroomGate()
        {
            // the controller's _capStance starts at -1 (no capsule sized yet); the gate must not fire
            var s = new PlayerStanceSim();
            Assert.That(Step(s, cap: -1f, headroom: _ => false), Is.EqualTo(EPlayerStance.STAND));
        }
    }
}
