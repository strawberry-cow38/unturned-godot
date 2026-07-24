using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 tests for the trap trigger gate + branch selection (src InteractableTrap.NotifyTrapEntered).
    // The subtle invariants under test: the gate ORDER, and that the cooldown latches BEFORE the PvP
    // decision (so a contact that does nothing still spends the re-arm window).
    [TestFixture]
    public class TrapRuleTests
    {
        // A live, armed, non-explosive shredder (Barbedwire-shaped) contacted by a PvP player on foot.
        static TrapDecision Eval(
            TrapTarget target = TrapTarget.Player,
            bool otherIsTrigger = false, bool isSelfOrChild = false,
            float now = 100f, float lastActive = 0f, float setupDelay = 0.25f,
            float lastTriggered = 0f, float cooldown = 0f,
            bool requiresPower = false, bool isWired = false,
            bool isExplosive = false, bool isBroken = false, float explosionLaunchSpeed = 0f,
            bool isPvP = true, bool targetRidingVehicle = false, bool zombieIsHyper = false)
            => TrapRule.Evaluate(target, otherIsTrigger, isSelfOrChild, now, lastActive, setupDelay,
                                 lastTriggered, cooldown, requiresPower, isWired,
                                 isExplosive, isBroken, explosionLaunchSpeed,
                                 isPvP, targetRidingVehicle, zombieIsHyper);

        // ---------------- the gate ladder ----------------

        [Test]
        public void TriggerCollider_Ignored()
        {
            var d = Eval(otherIsTrigger: true);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
            Assert.That(d.Consumed, Is.False, "a trigger-volume contact must not even spend the cooldown");
        }

        [Test]
        public void WithinSetupDelay_Inert()
        {
            // placed at t=100, contacted at t=100.2, delay 0.25 -> still arming
            var d = Eval(now: 100.2f, lastActive: 100f, setupDelay: 0.25f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
            Assert.That(d.Consumed, Is.False);
        }

        [Test]
        public void JustPastSetupDelay_Fires()
        {
            var d = Eval(now: 100.26f, lastActive: 100f, setupDelay: 0.25f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.ShredPlayer));
        }

        [Test]
        public void SelfOrChildCollider_Ignored()
        {
            var d = Eval(isSelfOrChild: true);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
            Assert.That(d.Consumed, Is.False, "the trap's own collider must never arm the cooldown");
        }

        [Test]
        public void RequiresPower_Unwired_Inert()
        {
            var d = Eval(requiresPower: true, isWired: false);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
            Assert.That(d.Consumed, Is.False);
        }

        [Test]
        public void RequiresPower_Wired_Fires()
        {
            var d = Eval(requiresPower: true, isWired: true);
            Assert.That(d.Action, Is.EqualTo(TrapAction.ShredPlayer));
        }

        [Test]
        public void WithinCooldown_Ignored()
        {
            // fired at t=100 with a 2 s re-arm; contacted again at t=101.5
            var d = Eval(now: 101.5f, lastTriggered: 100f, cooldown: 2f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
            Assert.That(d.Consumed, Is.False, "a cooled-down contact must not re-latch the window");
        }

        [Test]
        public void PastCooldown_Fires()
        {
            var d = Eval(now: 102.5f, lastTriggered: 100f, cooldown: 2f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.ShredPlayer));
            Assert.That(d.Consumed, Is.True);
        }

        // ---------------- the ordering invariant (the one a naive port gets wrong) ----------------

        [Test]
        public void NonPvpPlayer_DoesNothing_ButStillSpendsTheCooldown()
        {
            // src latches lastTriggered before the PvP branch: the contact is "used up" even though no damage lands.
            var d = Eval(isPvP: false);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None), "PvE server: a player is not shredded");
            Assert.That(d.Consumed, Is.True, "but the src still consumed the trigger window");
            Assert.That(d.SelfWear, Is.EqualTo(0f), "and the trap takes no wear for a contact that did nothing");
        }

        // ---------------- non-explosive branches ----------------

        [Test]
        public void PlayerInVehicle_NotShredded()
        {
            var d = Eval(targetRidingVehicle: true);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None), "riding a vehicle shields the player from a shredder");
        }

        [Test]
        public void BrokenTrap_BreaksLegs()
        {
            var d = Eval(isBroken: true);   // Snare
            Assert.That(d.Action, Is.EqualTo(TrapAction.ShredPlayer));
            Assert.That(d.BreakLegs, Is.True);
        }

        [Test]
        public void NonBrokenTrap_DoesNotBreakLegs()
        {
            Assert.That(Eval(isBroken: false).BreakLegs, Is.False);
        }

        [Test]
        public void Zombie_AlwaysDamaged_RegardlessOfPvp()
        {
            var d = Eval(target: TrapTarget.Zombie, isPvP: false);
            Assert.That(d.Action, Is.EqualTo(TrapAction.DamageZombie));
            Assert.That(d.SelfWear, Is.EqualTo(TrapRule.WearNormal));
        }

        [Test]
        public void HyperZombie_ChewsTheTrapTwiceAsFast()
        {
            var d = Eval(target: TrapTarget.Zombie, zombieIsHyper: true);
            Assert.That(d.SelfWear, Is.EqualTo(TrapRule.WearHyperZombie));
            Assert.That(TrapRule.WearHyperZombie, Is.EqualTo(10f));
            Assert.That(TrapRule.WearNormal, Is.EqualTo(5f));
        }

        [Test]
        public void Animal_Damaged_WithNormalWear()
        {
            var d = Eval(target: TrapTarget.Animal);
            Assert.That(d.Action, Is.EqualTo(TrapAction.DamageAnimal));
            Assert.That(d.SelfWear, Is.EqualTo(TrapRule.WearNormal));
        }

        [Test]
        public void UnknownTarget_DoesNothing_ButSpendsTheCooldown()
        {
            var d = Eval(target: TrapTarget.Other);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
            Assert.That(d.Consumed, Is.True);
        }

        // ---------------- explosive branches ----------------

        [Test]
        public void Explosive_PvpPlayer_Detonates_AndWearsItself()
        {
            var d = Eval(isExplosive: true);
            Assert.That(d.Action, Is.EqualTo(TrapAction.Explode));
            Assert.That(d.SelfWear, Is.EqualTo(TrapRule.WearNormal),
                        "src damages the barricade first so the trap dies even at zero barricade-armor multiplier");
        }

        [Test]
        public void Explosive_NonPvpPlayer_NoLaunch_DoesNotDetonate()
        {
            var d = Eval(isExplosive: true, isPvP: false, explosionLaunchSpeed: 0f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
            Assert.That(d.Consumed, Is.True);
        }

        [Test]
        public void Explosive_PlayerInVehicle_DoesNotDetonate()
        {
            var d = Eval(isExplosive: true, targetRidingVehicle: true, explosionLaunchSpeed: 0f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None), "a landmine ignores a player who is riding a vehicle");
        }

        [Test]
        public void Explosive_LauncherTrap_FiresEvenAtNonPvpPlayerInVehicle()
        {
            // src: `|| explosionLaunchSpeed > 0.01f` -- a launcher fires for anyone, PvP or not, vehicle or not.
            var d = Eval(isExplosive: true, isPvP: false, targetRidingVehicle: true, explosionLaunchSpeed: 9.1f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.Explode));
        }

        [Test]
        public void Explosive_LaunchSpeedAtEpsilon_DoesNotCountAsLauncher()
        {
            // the src comparison is strictly `> 0.01f`
            var d = Eval(isExplosive: true, isPvP: false, explosionLaunchSpeed: TrapRule.LaunchSpeedEpsilon);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
        }

        [Test]
        public void Explosive_Zombie_AlwaysDetonates()
        {
            var d = Eval(target: TrapTarget.Zombie, isExplosive: true, isPvP: false);
            Assert.That(d.Action, Is.EqualTo(TrapAction.Explode));
        }

        [Test]
        public void Explosive_StillGatedBySetupDelay()
        {
            // dropping a landmine at your own feet must not blow up in the same instant
            var d = Eval(target: TrapTarget.Zombie, isExplosive: true, now: 100.1f, lastActive: 100f, setupDelay: 0.25f);
            Assert.That(d.Action, Is.EqualTo(TrapAction.None));
        }
    }
}
