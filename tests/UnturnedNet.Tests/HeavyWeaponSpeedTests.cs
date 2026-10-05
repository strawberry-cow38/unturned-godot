using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>Carrying a belt-fed LMG, the minigun or a heavy sniper has to slow you down -- ON A SERVER.
    ///
    /// master 2026-10-05: "implement slower movement speed with heavy snipers, minigun and LMGs (make sure
    /// works in multiplayer)". The parenthesis is the whole test. A penalty applied in the shell alone looks
    /// perfect in singleplayer and then desyncs the moment it matters: the client predicts 0.95x, the server
    /// integrates 1.0x, and every step you take holding the gun is a mispredict the server rubberbands.
    ///
    /// So this drives PlayerReplication.IntegrateFlat -- not PlayerMovementSim directly -- because that is
    /// the seam the server's ServerStep and the client's prediction BOTH call. A penalty this test can see is
    /// one both sides compute from the same wire field (MoveInput.HeldItemId, on the wire since v22).
    ///
    /// ⭐ THE CONTROLS ARE THE POINT. Asserting only "the LMG walker went slower" passes just as happily if
    /// the sim got globally slower, or if merely holding ANY item slowed you -- so fists and an ordinary
    /// rifle are measured in the same run and must cover the FULL distance.</summary>
    [TestFixture]
    public class HeavyWeaponSpeedTests
    {
        const ushort Lmg = 60126;      // stands in for nykorev/dragonfang (retail .dat: 0.95)
        const ushort Minigun = 60364;  // ...and fury (retail .dat: 0.90)
        const ushort Rifle = 60004;    // an ordinary gun: declares nothing, so it must cost nothing

        [OneTimeSetUp]
        public void Register()
        {
            if (Assets.find(Lmg) == null)
                Assets.add(new ItemAsset { id = Lmg, itemName = "Test LMG", equipableMovementSpeedMultiplier = 0.95f });
            if (Assets.find(Minigun) == null)
                Assets.add(new ItemAsset { id = Minigun, itemName = "Test Minigun", equipableMovementSpeedMultiplier = 0.90f });
            if (Assets.find(Rifle) == null)
                Assets.add(new ItemAsset { id = Rifle, itemName = "Test Rifle" });   // default 1f, left unset deliberately
        }

        /// <summary>Walk due north for `Steps` seconds holding `held`, return metres covered. A fresh sim per
        /// call so no run inherits the last one's velocity.
        ///
        /// ⚠ TEN ONE-SECOND STEPS, NOT FIVE HUNDRED 50 Hz ONES, AND THAT IS THE MEASUREMENT NOT THE FEATURE.
        /// IntegrateFlat re-quantizes the position to the wire grid on EVERY call, so a tick whose delta does
        /// not land on the grid loses a fraction of it -- systematically, in the same direction, every tick.
        /// At 50 Hz the per-tick delta is ~9 cm and the loss ran to 0.39 m per simulated second, which is
        /// nearly twice the 0.22 m the LMG penalty itself is worth: the instrument was noisier than the thing
        /// it measured. On flat ground the sim ASSIGNS horizontal velocity outright rather than accelerating
        /// toward it, so distance is exactly speed x time at any dt and a longer step is not an approximation
        /// -- it just amortises one grid rounding over 4.5 m instead of 9 cm. Error drops to ~0.1%.</summary>
        const int Steps = 10;

        static float WalkMetres(ushort held, EPlayerStance stance = EPlayerStance.STAND)
        {
            var sim = new PlayerMovementSim { Stance = stance };
            var input = new MoveInput { MoveX = 0f, MoveY = 1f, YawDegrees = 0f, HeldItemId = held };
            var pos = new UnityEngine.Vector3(0f, 0f, 0f);
            for (int i = 0; i < Steps; i++)
            {
                input.Seq = (ushort)(i + 1);
                pos = PlayerReplication.IntegrateFlat(sim, in input, pos, 1f);
            }
            return pos.z;
        }

        /// <summary>Grid slack for a `Steps`-step walk: at most half a wire-grid unit per quantize. Generous
        /// against the ~2 m an LMG penalty is worth over this distance, so it cannot launder a real error.</summary>
        const float GridSlack = 0.05f;

        [Test]
        public void HeavyWeaponsSlowTheCarrierAndOrdinaryOnesDoNot()
        {
            float fists = WalkMetres(0);
            float rifle = WalkMetres(Rifle);
            float lmg = WalkMetres(Lmg);
            float minigun = WalkMetres(Minigun);

            // Controls first: if either of these is short, the measurement is wrong and the rest means nothing.
            Assert.That(fists, Is.GreaterThan(5f), "control: empty-handed walk covered no ground -- the harness is broken, not the feature");
            Assert.That(rifle, Is.EqualTo(fists).Within(GridSlack), "control: an ordinary rifle must not slow you -- the penalty is per-item, not 'holding anything'");

            // ...and the control that MUST fail if the wiring is removed: unwire IntegrateFlat and these two
            // collapse onto `fists`, which is exactly what a vacuous pass would look like.
            Assert.That(lmg, Is.LessThan(fists - 1f), "an LMG carrier must be measurably slower than an empty-handed one");
            Assert.That(lmg, Is.EqualTo(fists * 0.95f).Within(GridSlack), "...by its asset's own multiplier, not by some other amount");
            Assert.That(minigun, Is.EqualTo(fists * 0.90f).Within(GridSlack), "and the minigun's heavier penalty has to be ITS number, not the LMG's");
            Assert.That(minigun, Is.LessThan(lmg - 1f), "the two tiers must stay distinguishable");
        }

        [Test]
        public void ThePenaltyScalesTheStanceSpeedRatherThanReplacingIt()
        {
            // A sprint with an LMG is 0.95 x SPRINT, not 0.95 x STAND and not a flat cap. Getting this wrong
            // would make the heaviest guns FASTER than walking in some stances, which is the sort of thing a
            // single-stance test never sees.
            float standFists = WalkMetres(0, EPlayerStance.STAND);
            float sprintFists = WalkMetres(0, EPlayerStance.SPRINT);
            float sprintLmg = WalkMetres(Lmg, EPlayerStance.SPRINT);

            Assert.That(sprintFists, Is.GreaterThan(standFists + 1f), "control: sprinting must outpace standing, or the stance term is not reaching the sim");
            Assert.That(sprintLmg, Is.EqualTo(sprintFists * 0.95f).Within(GridSlack));
            Assert.That(sprintLmg, Is.GreaterThan(standFists), "a sprint under penalty should still beat an unencumbered walk at these magnitudes");
        }

        [Test]
        public void PuttingTheHeavyWeaponAwayGivesTheSpeedBack()
        {
            // The regression this exists for: if IntegrateFlat only ASSIGNED the multiplier when the held item
            // declared one, a sim that once carried an LMG would stay slow for the rest of the session -- and
            // every test above would still pass, because each of them uses a fresh sim.
            var sim = new PlayerMovementSim { Stance = EPlayerStance.STAND };
            var pos = new UnityEngine.Vector3(0f, 0f, 0f);

            var heavy = new MoveInput { MoveY = 1f, HeldItemId = Lmg };
            for (int i = 0; i < Steps; i++) { heavy.Seq = (ushort)(i + 1); pos = PlayerReplication.IntegrateFlat(sim, in heavy, pos, 1f); }
            float withLmg = pos.z;

            pos = new UnityEngine.Vector3(0f, 0f, 0f);
            var empty = new MoveInput { MoveY = 1f, HeldItemId = 0 };
            for (int i = 0; i < Steps; i++) { empty.Seq = (ushort)(i + 1); pos = PlayerReplication.IntegrateFlat(sim, in empty, pos, 1f); }
            float afterStowing = pos.z;

            Assert.That(afterStowing, Is.GreaterThan(withLmg + 1f), "dropping the LMG has to restore full speed on the SAME sim -- a sticky multiplier is the bug here");
            Assert.That(afterStowing, Is.EqualTo(WalkMetres(0)).Within(GridSlack), "...all the way back, not part of the way");
        }

        [Test]
        public void TheMultiplierScalesStanceSpeedExactly()
        {
            // The arithmetic claim, measured where there is no wire grid to blunt it -- straight off the sim's
            // velocity. The walk tests above prove the penalty reaches the MULTIPLAYER path; this proves the
            // number it applies is the stance speed times the item's own multiplier and not something near it.
            Assert.That(PlayerMovementSim.SpeedMultiplierForHeld(Lmg), Is.EqualTo(0.95f).Within(1e-6f));
            Assert.That(PlayerMovementSim.SpeedMultiplierForHeld(Rifle), Is.EqualTo(1f).Within(1e-6f), "an item that declares nothing resolves to 1");
            Assert.That(PlayerMovementSim.SpeedMultiplierForHeld(0), Is.EqualTo(1f).Within(1e-6f), "fists resolve to 1 without touching the asset table");

            var sim = new PlayerMovementSim { Stance = EPlayerStance.STAND, SpeedMultiplier = 0.95f };
            var v = sim.Step(new UnityEngine.Vector2(0f, 1f), wantJump: false, grounded: true, dt: 1f / 50f);
            Assert.That(v.z, Is.EqualTo(PlayerMovementDef.SPEED_STAND * 0.95f).Within(1e-5f));

            var full = new PlayerMovementSim { Stance = EPlayerStance.STAND };
            var fv = full.Step(new UnityEngine.Vector2(0f, 1f), wantJump: false, grounded: true, dt: 1f / 50f);
            Assert.That(fv.z, Is.EqualTo(PlayerMovementDef.SPEED_STAND).Within(1e-5f), "control: the default multiplier must leave stance speed untouched");
        }

        [Test]
        public void AnItemTheServerHasNoAssetForCostsNothing()
        {
            // A client can put any u16 in HeldItemId. An unknown one must read as 1x: the server declining to
            // move a player because it did not recognise their gun would be a far worse bug than a missed penalty.
            Assert.That(WalkMetres(60999), Is.EqualTo(WalkMetres(0)).Within(GridSlack));
        }
    }
}
