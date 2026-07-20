using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // A2 (SP/MP-unify) -- the gas-station PUMP as a server-placed deployable FIXTURE with a server-authoritative
    // ExtractFuel. Instead of an SP-local FluidTank the client never sees, every Gas_Pump_0 becomes a
    // DeployableEntity (FixtureKind.GasPump) the server places; the shared 8000 L tank lives ONLY on the server
    // (behind IFuelStation), and CommandExtractFuel(29) is the SOLE mutation: a fresh deterministic Solve() gates
    // on the pump's Consumer port being Powered, pulled = min(can free space, station remaining) so the tank can't
    // be double-spent, the tank drains, the held can fills, and the recomputed 0..100 PERCENT is written onto
    // EVERY same-station pump's entity.Fuel in ONE tick (atomic fan-out). These drive that choke over the real
    // client->server command path with a fake station tank the test controls.
    [TestFixture]
    public class GasPumpExtractTests
    {
        const ushort GEN = TransactionalFixtures.GeneratorId;   // 458: 4000W Output source (powers the pump when wired + toggled on)
        const ushort PUMP = TransactionalFixtures.GasPumpId;    // 9201: FixtureKind.GasPump, one 750W Consumer
        const ushort CAN = TransactionalFixtures.GasCanId;      // 28: a fuel container (fuelCapacity 100 in the fixtures)

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        // A tiny server-side station the extract choke reads through: a per-station remaining + capacity + the
        // pump->station map. The drain math + min-clamp + percent + fan-out all live in ServerTransactions, NOT
        // here, so this fake can't mask a bug in that logic.
        sealed class FakeFuelStation : IFuelStation
        {
            readonly Dictionary<uint, int> _map = new();
            readonly Dictionary<int, float> _remaining = new();
            readonly Dictionary<int, float> _cap = new();
            readonly Dictionary<int, List<uint>> _pumps = new();

            public void Add(uint pumpNetId, int stationId, float remaining, float capacity)
            {
                _map[pumpNetId] = stationId;
                _remaining[stationId] = remaining;
                _cap[stationId] = capacity;
                if (!_pumps.TryGetValue(stationId, out var l)) { l = new List<uint>(); _pumps[stationId] = l; }
                l.Add(pumpNetId);
            }

            public bool TryGetStation(uint pumpNetId, out int stationId) => _map.TryGetValue(pumpNetId, out stationId);
            public float Remaining(int stationId) => _remaining.TryGetValue(stationId, out var v) ? v : 0f;
            public float Capacity(int stationId) => _cap.TryGetValue(stationId, out var v) ? v : 0f;
            public float Drain(int stationId, float requested)
            {
                float r = Remaining(stationId);
                float d = Mathf.Clamp(requested, 0f, r);
                _remaining[stationId] = r - d;
                return d;
            }
            public IReadOnlyList<uint> Pumps(int stationId) => _pumps.TryGetValue(stationId, out var l) ? l : System.Array.Empty<uint>();
        }

        // ServerPlace `count` pumps on ONE station (S=1), a generator source, and wire+toggle the FIRST pump on
        // (so it's Powered) unless `power` is false. Returns the placed pump NetIds + the station + the fake tank.
        static (TransactionalHarness h, uint[] pumps, int station, FakeFuelStation fake) BuildStation(
            int seed, int count, float remaining, float capacity, bool power = true)
        {
            const int S = 1;
            var h = new TransactionalHarness(seed).Connected("a");
            var srv = h.Server.Deployables;
            var tick = h.Server.Session.CurrentTick;

            var pumps = new uint[count];
            var fake = new FakeFuelStation();
            for (int i = 0; i < count; i++)
            {
                var e = srv.ServerPlace(h.Server.Ids.Mint(), PUMP, 0, new Vector3(2f + i * 1.5f, 0f, 0f), 0f, tick);
                Assert.That(e, Is.Not.Null, "GasPump def registered -> the pump fixture places");
                pumps[i] = e.NetIdValue;
                fake.Add(e.NetIdValue, S, remaining, capacity);
            }
            h.Server.Transactions.FuelStations = fake;

            if (power)
            {
                var gen = srv.ServerPlace(h.Server.Ids.Mint(), GEN, 0, new Vector3(-2f, 0f, 0f), 0f, tick);
                srv.ServerToggle(gen.NetIdValue, true, tick);                       // fuelled + on -> a live source
                srv.ServerConnectWire(h.Server.Ids.Mint(), gen.NetIdValue, 0, pumps[0], 0, tick);   // Output(0) -> Consumer(0)
                srv.Solve();
                srv.TryGet(pumps[0], out var p0);
                Assert.That(p0.Solved[0].Powered, Is.True, "the first pump is Powered off the wired generator");
            }
            return (h, pumps, S, fake);
        }

        [Test]
        public void extract_drains_station_and_fans_out_to_all_pumps()
        {
            // two pumps sharing a full 1000 L station (100%); a 100-space can. pulled = min(100, 1000) = 100 ->
            // station 900 -> 90%. TEETH: the recomputed percent lands on BOTH pumps in ONE tick (same
            // LastChangedTick), not just the one extracted -> a fan-out revert leaves pump#2 at 0.
            var (h, pumps, station, fake) = BuildStation(9210, count: 2, remaining: 1000f, capacity: 1000f);
            var a = h.Clients[0];
            h.Grant(a.PlayerId, Assets.makeLoot(CAN));   // a fresh EMPTY can (fuelLevel 0), 100 free space

            a.SendExtractFuel(pumps[0]);
            Assert.That(h.StepUntil(() => h.Server.Deployables.TryGet(pumps[0], out var e) && e.Fuel > 0.1f), Is.True,
                        $"extract applied -> the pump's replicated percent moved off 0 (seed={h.Net.Seed})");

            Assert.That(fake.Remaining(station), Is.EqualTo(900f), "the ABSOLUTE station tank drained by min(canSpace=100, remaining=1000)=100");

            // the recomputed percent (900/1000 = 90) fanned out onto BOTH pumps, atomically (same tick)
            h.Server.Deployables.TryGet(pumps[0], out var p0);
            h.Server.Deployables.TryGet(pumps[1], out var p1);
            Assert.That(p0.Fuel, Is.EqualTo(90f), "pump#0 replicates 90% fill");
            Assert.That(p1.Fuel, Is.EqualTo(90f), "pump#1 (same station, never extracted from) replicates the SAME 90% fill");
            Assert.That(p1.LastChangedTick, Is.EqualTo(p0.LastChangedTick), "both pumps updated in the SAME tick (atomic fan-out -- no divergent replicated fill)");

            // the can filled server-side by the pulled amount (the owner-inventory echo re-adopts the fuller can)
            var can = FindItem(h, a.PlayerId, CAN);
            Assert.That(can, Is.Not.Null);
            Assert.That(can.fuelLevel, Is.EqualTo(100f), "the held can filled by the 100 pulled");
        }

        [Test]
        public void extract_capped_by_station_remaining_cannot_over_drain()
        {
            // a nearly-empty station: 40 L of a 1000 L tank (4%). pulled = min(canSpace=100, remaining=40) = 40 ->
            // the can gets 40, the station hits 0. A SECOND extract can pull NOTHING (min(60, 0) = 0) -> the tank
            // can't be over-drained. TEETH: a revert dropping the min clamp would pull the full 100 (negative tank).
            var (h, pumps, station, fake) = BuildStation(9211, count: 1, remaining: 40f, capacity: 1000f);
            var a = h.Clients[0];
            h.Grant(a.PlayerId, Assets.makeLoot(CAN));   // a fresh EMPTY can (fuelLevel 0), like the real give/loot path

            a.SendExtractFuel(pumps[0]);
            Assert.That(h.StepUntil(() => fake.Remaining(station) <= 0.01f), Is.True,
                        $"first extract drained the 40 remaining (min(100,40)=40) (seed={h.Net.Seed})");
            var can = FindItem(h, a.PlayerId, CAN);
            Assert.That(can.fuelLevel, Is.EqualTo(40f), "the can took only the 40 that was left, not its full 100 space");
            Assert.That(fake.Remaining(station), Is.EqualTo(0f), "the tank is exactly empty (no over-drain)");

            // second extract: station empty -> pulled = min(60, 0) = 0 -> rejected, nothing changes
            float canBefore = can.fuelLevel;
            a.SendExtractFuel(pumps[0]);
            h.Step(20);
            Assert.That(fake.Remaining(station), Is.EqualTo(0f), "the empty tank stays at 0 (a second extract can't go negative)");
            Assert.That(can.fuelLevel, Is.EqualTo(canBefore), "the can gained nothing from the empty station");
        }

        [Test]
        public void extract_rejected_when_unpowered()
        {
            // the pump exists + has fuel + the player holds a can, but it's NOT wired to a live source. The fresh
            // server-side Solve() finds the Consumer port dark -> no drain. TEETH: a revert dropping the powered
            // gate would drain an unpowered pump.
            var (h, pumps, station, fake) = BuildStation(9212, count: 1, remaining: 500f, capacity: 1000f, power: false);
            var a = h.Clients[0];
            h.Grant(a.PlayerId, Assets.makeLoot(CAN));   // a fresh EMPTY can (fuelLevel 0), like the real give/loot path

            a.SendExtractFuel(pumps[0]);
            h.Step(30);
            Assert.That(fake.Remaining(station), Is.EqualTo(500f), "an UNPOWERED pump drains nothing (the Solve gate rejects it)");
            var can = FindItem(h, a.PlayerId, CAN);
            Assert.That(can.fuelLevel, Is.EqualTo(0f), "the can stays empty at an unpowered pump");
        }

        [Test]
        public void extract_rejected_without_a_gas_can()
        {
            // powered pump, full station, but the player holds NO fuel container -> nothing to fill -> no drain.
            var (h, pumps, station, fake) = BuildStation(9213, count: 1, remaining: 500f, capacity: 1000f);
            var a = h.Clients[0];   // no Grant -> empty bag

            a.SendExtractFuel(pumps[0]);
            h.Step(30);
            Assert.That(fake.Remaining(station), Is.EqualTo(500f), "with no gas can in hand there is nothing to fill -> the tank is untouched");
        }

        static Item FindItem(TransactionalHarness h, ushort playerId, ushort id)
        {
            if (!h.Server.Inventories.TryGet(playerId, out var e)) return null;
            foreach (var page in e.Inventory.items)
                foreach (var jar in page.items)
                    if (jar.item != null && jar.item.id == id) return jar.item;
            return null;
        }
    }
}
