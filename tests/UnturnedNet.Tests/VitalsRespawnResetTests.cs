using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // A NEW LIFE GETS NEW VITALS.
    //
    // ⚠ THE GAME LAYER ALREADY BELIEVED IT DID. PlayerController.Respawn has carried
    // `Stamina = Food = Water = 1f; Infection = 0f; Bleeding = false; Broken = false;` under the comment "fresh
    // vitals on respawn" for a long time -- correct, and completely overwritten every time, because the fine
    // vitals are SERVER-OWNED and AdoptReplicatedFineVitals is their only writer on an adopting client. The
    // local reset landed, the next owner echo carried the dead player's hunger, thirst and infection straight
    // back, and you respawned starving (strawberry 2026-09-13: "fix vitals not being reset on death").
    //
    // Exactly the shape of the throwable-spend bug found the same day: the client mutated, the authority
    // disagreed, the echo won. A reset has to happen where the value lives.
    //
    // ⭐ SO THESE DRIVE A REAL SERVER AND KILL A REAL PLAYER rather than calling ServerResetForNewLife
    // directly. The method working was never in doubt; the bug is that NOTHING CALLED IT, and a test that
    // invokes it by hand passes just as happily on a tree where the subscription was never written.
    [TestFixture]
    public class VitalsRespawnResetTests
    {
        [SetUp]
        public void RegisterAssets() => TransactionalFixtures.RegisterAssets();

        /// <summary>Run a player's vitals into the ground, so a reset has something to undo.</summary>
        static void Wreck(PlayerVitalsReplication.VitalsEntry e)
        {
            e.Sim.Food = 0.05f; e.Sim.Water = 0.04f; e.Sim.Stamina = 0.12f;
            e.Sim.Infection = 0.90f; e.Sim.Radiation = 0.55f; e.Sim.Oxygen = 0.08f;
            e.Sim.StaminaRegenDelay = 1f;
            e.Sim.NotifyDamaged();          // arms the 10 s post-damage regen lock
            e.Bleeding = true; e.Broken = true;
        }

        [Test]
        public void RespawningRestoresEveryVital()
        {
            var h = new TransactionalHarness(9410).Connected("victim");
            var a = h.Clients[0];
            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out var ve), Is.True);
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var cs), Is.True);

            Wreck(ve);
            h.Server.Combat.DamagePlayerExternal(a.PlayerId, 500f, 0);
            h.Step(3);

            // THE CONTROL, and it is what makes the rest mean anything: a corpse still carries the wreckage.
            // Without it, "everything is fresh after the respawn" also passes on a tree where something else
            // had already wiped the vitals long before the death.
            Assert.That(cs.Alive, Is.False, "fixture: the player actually died");
            Assert.That(ve.Sim.Food, Is.LessThan(0.2f), "fixture: the dead player is still starving");
            Assert.That(ve.Sim.Infection, Is.GreaterThan(0.5f), "fixture: ...and still infected");

            Assert.That(h.Server.Combat.ServerRequestRespawn(a.PlayerId, h.Server.Session.CurrentTick), Is.True);
            h.Step(5);
            Assert.That(cs.Alive, Is.True, "fixture: and came back");

            Assert.That(ve.Sim.Food, Is.EqualTo(1f).Within(1e-6f), "respawned FED");
            Assert.That(ve.Sim.Water, Is.EqualTo(1f).Within(1e-6f), "...and hydrated");
            Assert.That(ve.Sim.Stamina, Is.EqualTo(1f).Within(1e-6f), "...and rested");
            Assert.That(ve.Sim.Infection, Is.EqualTo(0f).Within(1e-6f), "...and clean");
            Assert.That(ve.Bleeding, Is.False, "...and not bleeding");
            Assert.That(ve.Broken, Is.False, "...and not limping");
        }

        // The two the game layer's own respawn line never listed, and both are a real death on arrival.
        [Test]
        public void RespawningGivesBackBreathAndClearsTheDose()
        {
            var h = new TransactionalHarness(9411).Connected("drowner");
            var a = h.Clients[0];
            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out var ve), Is.True);
            Wreck(ve);
            h.Server.Combat.DamagePlayerExternal(a.PlayerId, 500f, 0);
            h.Step(3);
            Assume.That(ve.Sim.Oxygen, Is.LessThan(0.2f), "fixture: died with an empty breath");

            h.Server.Combat.ServerRequestRespawn(a.PlayerId, h.Server.Session.CurrentTick);
            h.Step(5);

            Assert.That(ve.Sim.Oxygen, Is.EqualTo(1f).Within(1e-6f),
                        "a drowned player must surface with a full breath, or they re-drown on arrival");
            Assert.That(ve.Sim.Radiation, Is.EqualTo(0f).Within(1e-6f),
                        "dying in a deadzone must not hand the new body the old one's dose");
        }

        // ⚠ THE INVISIBLE ONE. Die with the post-damage regen lock armed and, without this, you respawn into it:
        // ten seconds of a brand-new life with no passive regen and nothing on screen to say why.
        [Test]
        public void RespawningClearsThePostDamageRegenLock()
        {
            var h = new TransactionalHarness(9412).Connected("shot");
            var a = h.Clients[0];
            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out var ve), Is.True);
            Wreck(ve);
            Assume.That(ve.Sim.RegenLockDelay, Is.GreaterThan(0f), "fixture: the lock is armed going into the death");

            h.Server.Combat.DamagePlayerExternal(a.PlayerId, 500f, 0);
            h.Step(3);
            h.Server.Combat.ServerRequestRespawn(a.PlayerId, h.Server.Session.CurrentTick);
            h.Step(5);

            Assert.That(ve.Sim.RegenLockDelay, Is.EqualTo(0f).Within(1e-6f),
                        "the new body did not take the old one's bullet");
        }

        // The reset is per PLAYER, not "everyone alive". A second player's vitals are not touched by someone
        // else's death -- the failure this rejects is a reset keyed off anything but the respawning id.
        [Test]
        public void OnlyTheRespawningPlayersVitalsAreReset()
        {
            var h = new TransactionalHarness(9413).Connected("victim", "bystander");
            var a = h.Clients[0];
            var b = h.Clients[1];
            Assert.That(h.Server.Vitals.TryGet(a.PlayerId, out var av), Is.True);
            Assert.That(h.Server.Vitals.TryGet(b.PlayerId, out var bv), Is.True);
            Wreck(av); Wreck(bv);
            bv.Sim.Food = 0.33f;   // a distinctive value nothing else in the fixture writes

            h.Server.Combat.DamagePlayerExternal(a.PlayerId, 500f, 0);
            h.Step(3);
            h.Server.Combat.ServerRequestRespawn(a.PlayerId, h.Server.Session.CurrentTick);
            h.Step(5);

            Assert.That(av.Sim.Food, Is.EqualTo(1f).Within(1e-6f), "the respawner was reset");
            Assert.That(bv.Sim.Food, Is.LessThan(0.5f), "...and the bystander, who never died, was not");
            Assert.That(bv.Broken, Is.True, "...including their status bits");
        }
    }
}
