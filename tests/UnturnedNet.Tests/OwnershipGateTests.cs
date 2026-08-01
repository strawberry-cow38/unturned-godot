using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Base ownership (review M2, deferred since Phase 6 and previously five TODO(mp-security) markers).
    //
    // Salvage / pickup / wire / toggle were reach-gated only: any connected player standing next to your
    // base could take it apart. That was a DELIBERATE choice for a friendly co-op box, and the point of
    // these tests is that it stays the default -- what changes is that the gate now exists, is off by
    // default, and demonstrably bites when a public host turns it on.
    //
    // Every assertion here is written twice on purpose: once with EnforceOwnership off (today's behaviour,
    // which must not regress) and once with it on. A gate that is only tested in one position tells you
    // nothing about the other.
    [TestFixture]
    public class OwnershipGateTests
    {
        const ushort GEN = TransactionalFixtures.GeneratorId;
        const ushort SPOT = TransactionalFixtures.SpotlightId;

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        /// <summary>a places a generator; b is a bystander standing in reach of it.</summary>
        static (TransactionalHarness h, uint genId) ARigOwnedByA(int seed, bool enforce)
        {
            var h = new TransactionalHarness(seed).Connected("a", "b");
            h.Server.Transactions.EnforceOwnership = enforce;
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(GEN));
            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            h.Step(20);
            uint genId = 0;
            foreach (var e in h.Server.Deployables.All) if (e.DefId == GEN) genId = e.NetIdValue;
            Assert.That(genId, Is.Not.Zero, "a's generator was placed");
            Assert.That(h.Server.Deployables.TryGet(genId, out var g) && g.OwnerPlayerId == a.PlayerId, Is.True,
                        "and it is stamped with a's player id -- the gate has nothing to read otherwise");
            return (h, genId);
        }

        [Test]
        public void a_stranger_cannot_pick_up_your_generator_when_enforced()
        {
            // The worst of the five: salvage at least needs the target ON FIRE first, but pickup takes a
            // healthy deployable straight into the taker's bag. Ungated, a base walks away piece by piece.
            var (h, genId) = ARigOwnedByA(4101, enforce: true);
            var b = h.Clients[1];
            long rejected = h.Server.Commands.Diag.ValidationRejected;

            b.SendPickupDeployable(genId);
            h.Step(20);

            Assert.That(h.Server.Deployables.TryGet(genId, out _), Is.True, "the generator is still standing");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected),
                        "and the attempt was rejected at the validator, never reaching authoritative state");
            Assert.That(h.Server.Inventories.TryGet(b.PlayerId, out var inv) && inv.Inventory.getItemCount(GEN) == 0,
                        Is.True, "nothing landed in the thief's bag");
        }

        [Test]
        public void the_owner_can_still_pick_up_their_own_generator_when_enforced()
        {
            // The gate must not lock people out of their OWN base, which is the obvious way to get this wrong.
            var (h, genId) = ARigOwnedByA(4102, enforce: true);
            var a = h.Clients[0];

            a.SendPickupDeployable(genId);
            Assert.That(h.StepUntil(() => !h.Server.Deployables.TryGet(genId, out _)
                                       && h.Server.Inventories.TryGet(a.PlayerId, out var i)
                                       && i.Inventory.getItemCount(GEN) == 1), Is.True,
                        $"the owner's own pickup still works (seed={h.Net.Seed})");
        }

        [Test]
        public void a_stranger_CAN_pick_it_up_with_the_gate_off_which_is_the_default()
        {
            // Today's behaviour, and the control for the test above: same call, same positions, gate off.
            // If this ever fails, the "defaults to off, nothing changes" promise has been broken.
            var (h, genId) = ARigOwnedByA(4103, enforce: false);
            var b = h.Clients[1];

            b.SendPickupDeployable(genId);
            Assert.That(h.StepUntil(() => !h.Server.Deployables.TryGet(genId, out _)
                                       && h.Server.Inventories.TryGet(b.PlayerId, out var i)
                                       && i.Inventory.getItemCount(GEN) == 1), Is.True,
                        $"friendly co-op still lets anyone tidy anyone's base (seed={h.Net.Seed})");
        }

        [Test]
        public void a_default_constructed_server_does_not_enforce()
        {
            var h = new TransactionalHarness(4104).Connected("a");
            Assert.That(h.Server.Transactions.EnforceOwnership, Is.False,
                        "OFF unless a host opts in -- flipping this by surprise would be a worse bug than the hole");
        }

        [Test]
        public void a_stranger_cannot_salvage_your_burning_wreck_when_enforced()
        {
            var (h, genId) = ARigOwnedByA(4105, enforce: true);
            var b = h.Clients[1];
            h.Server.Deployables.ServerSetScalars(genId, 0f, 0f, onFire: true, h.Server.Session.CurrentTick);
            h.Step(10);
            long rejected = h.Server.Commands.Diag.ValidationRejected;

            b.SendSalvageDeployable(genId);
            h.Step(20);

            Assert.That(h.Server.Deployables.TryGet(genId, out _), Is.True, "the wreck is still there");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected));
        }

        [Test]
        public void a_stranger_cannot_toggle_your_generator_when_enforced()
        {
            var (h, genId) = ARigOwnedByA(4106, enforce: true);
            var b = h.Clients[1];
            long rejected = h.Server.Commands.Diag.ValidationRejected;

            b.SendToggleDeployable(genId, true);
            h.Step(20);

            Assert.That(h.Server.Deployables.TryGet(genId, out var g) && !g.ToggledOn, Is.True,
                        "someone else's generator did not switch on");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected));
        }

        [Test]
        public void WORLD_owned_fixtures_stay_usable_by_everyone_even_when_enforced()
        {
            // OwnerPlayerId 0 = placed by the level build, not a player: street lamps, gas pumps, grid
            // sources. If the gate treated those as unowned-and-therefore-forbidden it would silently make
            // every municipal light un-toggleable, which reads as a regression rather than a security fix.
            // This is the case that made me write MayModify instead of inlining `e.OwnerPlayerId == sender`.
            var h = new TransactionalHarness(4107).Connected("a");
            h.Server.Transactions.EnforceOwnership = true;
            var a = h.Clients[0];

            // the level places it, so owner 0 -- exactly what WorldBuilder/ContainerNetSync do
            var netId = h.Server.Ids.Mint();
            h.Server.Deployables.ServerPlace(netId, GEN, 0, new Vector3(-2f, 0f, 0f), 0f, h.Server.Session.CurrentTick);
            h.Step(10);
            Assert.That(h.Server.Deployables.TryGet(netId.Value, out var w) && w.OwnerPlayerId == 0, Is.True,
                        "the fixture is world-owned");

            Assert.That(h.Server.Transactions.MayModify(a.PlayerId, w), Is.True,
                        "a world fixture answers to any player, gate or no gate");
        }

        [Test]
        public void wiring_checks_BOTH_ends_not_just_the_one_you_stand_at()
        {
            // Wiring your own generator into someone else's grid is the same trespass as the reverse, and
            // the reach check only ever looks at one end -- so a single-ended ownership check would leave
            // half the hole open.
            var h = new TransactionalHarness(4108).Connected("a", "b");
            h.Server.Transactions.EnforceOwnership = true;
            var a = h.Clients[0];
            var b = h.Clients[1];
            h.Grant(a.PlayerId, new Item(GEN));
            h.Grant(b.PlayerId, new Item(SPOT));
            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            b.SendPlaceDeployable(SPOT, new Vector3(2f, 0f, 0f), 0f);
            h.Step(20);

            uint genId = 0, spotId = 0;
            foreach (var e in h.Server.Deployables.All)
            {
                if (e.DefId == GEN) genId = e.NetIdValue;
                if (e.DefId == SPOT) spotId = e.NetIdValue;
            }
            Assert.That(genId, Is.Not.Zero); Assert.That(spotId, Is.Not.Zero);

            int wires = h.Server.Deployables.WireCount;
            a.SendConnectWire(genId, 0, spotId, 0);   // a owns the generator but NOT the spotlight
            h.Step(20);
            Assert.That(h.Server.Deployables.WireCount, Is.EqualTo(wires),
                        "a could not wire their own generator into b's spotlight");
        }
    }
}
