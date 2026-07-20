using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Regression coverage for the SP/MP-unification code-review findings that live in the core net layer
    // (ServerTransactions command validators). Driven over the REAL client->server command path through the
    // TransactionalHarness, so a weakened validator actually surfaces here (unlike the pre-existing tests that
    // wired the server directly and bypassed the intent path).
    [TestFixture]
    public class ReviewFixNetTests
    {
        const ushort GEN = TransactionalFixtures.GeneratorId;   // 458: a normal (FixtureKind.None) player deployable
        const ushort PUMP = TransactionalFixtures.GasPumpId;    // 9201: a world FIXTURE (FixtureKind.GasPump)
        const ushort GRID = TransactionalFixtures.GridSourceId; // 9200: a world FIXTURE (FixtureKind.GridSource)

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        static bool Placed(TransactionalHarness h, uint netId) => h.Server.Deployables.TryGet(netId, out _);

        // review H2: PickupDeployable was existence-only validated -> a client could pick up ANY deployable
        // anywhere on the map from spawn (no reach). Now reach-gated like salvage. Player "a" spawns at origin;
        // WireReach = 16 m.
        [Test]
        public void pickup_out_of_reach_is_rejected()
        {
            var h = new TransactionalHarness(4201).Connected("a");
            var near = h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), GEN, 0, new Vector3(5f, 0f, 0f), 0f, h.Server.Session.CurrentTick);
            var far = h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), GEN, 0, new Vector3(100f, 0f, 0f), 0f, h.Server.Session.CurrentTick);

            h.Clients[0].SendPickupDeployable(far.NetIdValue);
            h.Clients[0].SendPickupDeployable(near.NetIdValue);
            Assert.That(h.StepUntil(() => !Placed(h, near.NetIdValue)), Is.True, "an IN-reach deployable picks up (removed server-side)");
            Assert.That(Placed(h, far.NetIdValue), Is.True, "the OUT-OF-reach deployable was NOT removed (reach-gated) -- pre-fix it would vanish from across the map");
        }

        // review M4: PickupDeployable didn't exclude world FIXTURES -> a crafted packet could permanently delete a
        // gas pump / grid source (they can never be re-placed). Now the validator requires FixtureKind.None.
        [Test]
        public void pickup_world_fixture_is_rejected()
        {
            var h = new TransactionalHarness(4202).Connected("a");
            var pump = h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), PUMP, 0, new Vector3(4f, 0f, 0f), 0f, h.Server.Session.CurrentTick);
            var grid = h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), GRID, 0, new Vector3(4f, 0f, 2f), 0f, h.Server.Session.CurrentTick);
            var gen = h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), GEN, 0, new Vector3(4f, 0f, 4f), 0f, h.Server.Session.CurrentTick);

            h.Clients[0].SendPickupDeployable(pump.NetIdValue);
            h.Clients[0].SendPickupDeployable(grid.NetIdValue);
            h.Clients[0].SendPickupDeployable(gen.NetIdValue);
            Assert.That(h.StepUntil(() => !Placed(h, gen.NetIdValue)), Is.True, "the normal deployable (in reach, FixtureKind.None) still picks up");
            Assert.That(Placed(h, pump.NetIdValue), Is.True, "a gas-pump FIXTURE is not pickup-able (unreplaceable)");
            Assert.That(Placed(h, grid.NetIdValue), Is.True, "a grid-source FIXTURE is not pickup-able (unreplaceable)");
        }

        // review M7: the global grid mains toggle ran BEFORE the AllowCheats gate -> any client could flip everyone's
        // power on a cheats-locked server. Now gated. With cheats OFF the toggle is a no-op; with cheats ON it works.
        [Test]
        public void grid_toggle_respects_allow_cheats()
        {
            static bool AnyGridOn(TransactionalHarness h)
            {
                foreach (var e in h.Server.Deployables.All)
                    if (h.Server.Deployables.Schema.TryGet(e.DefId, out var d) && d.FixtureKind == FixtureKind.GridSource && e.ToggledOn) return true;
                return false;
            }

            var h = new TransactionalHarness(4203).Connected("a");
            h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), GRID, 0, new Vector3(3f, 0f, 0f), 0f, h.Server.Session.CurrentTick);

            // cheats OFF (the public-server posture): the toggle is rejected -> mains stay OFF
            h.Server.Transactions.AllowCheats = false;
            h.Clients[0].SendConsole("grid on");
            h.Step(30);
            Assert.That(AnyGridOn(h), Is.False, "cheats-off: a client's toggleglobalpower is rejected (pre-fix it flipped the mains for everyone)");

            // cheats ON (SP + friendly co-op): the mains toggle works, as strawberry's F1 mechanic
            h.Server.Transactions.AllowCheats = true;
            h.Clients[0].SendConsole("grid on");
            Assert.That(h.StepUntil(() => AnyGridOn(h)), Is.True, "cheats-on: the toggle energizes the mains");
        }
    }
}
