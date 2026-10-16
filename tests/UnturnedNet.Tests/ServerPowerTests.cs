using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>v78 accepted power inputs: the host-stepped ServerPower owns generator fuel, battery charge,
    /// turbine wind and side triggers, and every replica solves the same accepted values.</summary>
    [TestFixture]
    public class ServerPowerTests
    {
        // Source DeployableDef values (game/DeployableDef.cs): Generator 458, Spotlight 459, Battery 1450,
        // Switch 9105, Wind Turbine 9106.
        const ushort Gen = 458, Spot = 459, Battery = 1450, Switch = 9105, Turbine = 9106;
        const float Dt = 0.02f;

        static DeployablePortSpec P(PowerPortKind k, float w = 0f, byte role = 0) => new DeployablePortSpec { Kind = (byte)k, Watts = w, Role = role };

        static void Register(DeployableSchema s)
        {
            s.Register(new DeployableNetDef { DefId = Gen, Health = 450, FuelCapacity = 150_000f, Range = 4,
                Ports = new[] { P(PowerPortKind.Output, 4000), P(PowerPortKind.Consumer, 0, 1), P(PowerPortKind.Consumer, 0, 2) } });
            s.Register(new DeployableNetDef { DefId = Spot, Health = 300, Range = 4,
                Ports = new[] { P(PowerPortKind.Consumer, 250), P(PowerPortKind.Passthrough) } });
            s.Register(new DeployableNetDef { DefId = Battery, Health = 200, Range = 4, IsBattery = true,
                EnergyMax = 600f * 3600f, ChargeWatts = 600f,
                Ports = new[] { P(PowerPortKind.Consumer, 600), P(PowerPortKind.Output, 600) } });
            s.Register(new DeployableNetDef { DefId = Switch, Health = 200, Range = 4, IsSwitch = true,
                Ports = new[] { P(PowerPortKind.Consumer), P(PowerPortKind.Passthrough), P(PowerPortKind.Consumer, 0, 1), P(PowerPortKind.Consumer, 0, 2) } });
            s.Register(new DeployableNetDef { DefId = Turbine, Health = 300, Range = 5, IsWindTurbine = true,
                Ports = new[] { P(PowerPortKind.Output, 2500) } });
        }

        static TransactionalHarness Rig(int seed, params string[] clients)
        {
            TransactionalFixtures.RegisterAssets();
            var h = new TransactionalHarness(seed).Connected(clients);
            Register(h.Server.Deployables.Schema);
            foreach (var c in h.Clients) Register(c.Deployables.Schema);
            return h;
        }

        static DeployableReplication.DeployableEntity Place(TransactionalHarness h, ushort def, Vector3 at, float? fuel = null)
            => h.Server.Deployables.ServerPlace(h.Server.Ids.Mint(), def, 0, at, 0, h.Server.Session.CurrentTick, fuel: fuel);

        static void Wire(TransactionalHarness h, DeployableReplication.DeployableEntity a, byte ap, DeployableReplication.DeployableEntity b, byte bp)
            => h.Server.Deployables.ServerConnectWire(h.Server.Ids.Mint(), a.NetIdValue, ap, b.NetIdValue, bp, h.Server.Session.CurrentTick);

        static void Run(TransactionalHarness h, int ticks) => h.Step(ticks, () => h.Server.Power.Step(Dt));

        static DeployableReplication.PowerPortResult Port(DeployableReplication g, uint id, int index)
        {
            g.Solve();
            Assert.That(g.TryGet(id, out var e), Is.True);
            return e.Solved[index];
        }

        [Test]
        public void GeneratorFuelBurnsByLoadRunsDryAndStaysOffUntilRefuelledAndRestarted()
        {
            var h = Rig(7801, "owner");
            var g = h.Server.Deployables;
            var full = Place(h, Gen, new Vector3(0, 0, 9));
            Assert.That(full.Fuel, Is.EqualTo(150_000f), "the source 150 L tank no longer clips at the old 2047.75 grid");
            var gen = Place(h, Gen, new Vector3(-2, 0, 0), fuel: 3f);
            var lamp = Place(h, Spot, new Vector3(2, 0, 0));
            Wire(h, gen, 0, lamp, 0);
            g.ServerToggle(gen.NetIdValue, true, h.Server.Session.CurrentTick);
            Assert.That(Port(g, lamp.NetIdValue, 0).Powered, Is.True);

            // One step at 250 W of 4000 W: 100 mL/s x (0.2 + 0.8 x 0.0625) x 0.02 s = 0.5 mL.
            h.Server.Power.Step(Dt);
            Assert.That(gen.SimFuel, Is.EqualTo(3f - 0.5f).Within(1e-5f));
            Assert.That(gen.Fuel, Is.EqualTo(2.5f), "published on the wire's 1/4 mL grid");
            // An unloaded generator idles at 20%.
            var idle = Place(h, Gen, new Vector3(-4, 0, 0), fuel: 1000f);
            g.ServerToggle(idle.NetIdValue, true, h.Server.Session.CurrentTick);
            h.Server.Power.Step(Dt);
            Assert.That(idle.SimFuel, Is.EqualTo(1000f - 0.4f).Within(1e-4f));

            Run(h, 6);
            Assert.That(gen.Fuel, Is.EqualTo(0f));
            Assert.That(gen.ToggledOn, Is.False, "running dry drops the toggle; it does not auto-resume");
            Assert.That(h.StepUntil(() => h.Clients[0].Deployables.TryGet(gen.NetIdValue, out var r) && !r.ToggledOn && r.Fuel == 0f), Is.True);
            Assert.That(Port(h.Clients[0].Deployables, lamp.NetIdValue, 0).Powered, Is.False);
            Assert.That(full.Fuel, Is.EqualTo(150_000f), "an untoggled generator never burns");

            // Zero-fuel conduction: the accepted toggle still lands, but nothing is produced.
            h.Clients[0].SendToggleDeployable(gen.NetIdValue, true);
            Assert.That(h.StepUntil(() => h.Clients[0].Deployables.TryGet(gen.NetIdValue, out var r) && r.ToggledOn), Is.True);
            Run(h, 3);
            Assert.That(Port(g, lamp.NetIdValue, 0).Powered, Is.False);
            Assert.That(Port(h.Clients[0].Deployables, lamp.NetIdValue, 0).Powered, Is.False);
            g.ServerSetScalars(gen.NetIdValue, gen.Health, 50f, false, h.Server.Session.CurrentTick);
            Run(h, 2);
            Assert.That(Port(h.Clients[0].Deployables, lamp.NetIdValue, 0).Powered, Is.True, "refuelled while on: runs again");
            Assert.That(h.StepUntil(() => h.Clients[0].Deployables.StateHash() == g.StateHash()), Is.True);
        }

        [Test]
        public void BatteryChargesFromItsInThenCarriesTheLampAfterTheGeneratorStops()
        {
            var h = Rig(7802, "owner", "observer");
            var g = h.Server.Deployables;
            var gen = Place(h, Gen, new Vector3(-3, 0, 0));
            var bat = Place(h, Battery, Vector3.zero);
            var lamp = Place(h, Spot, new Vector3(3, 0, 0));
            Wire(h, gen, 0, bat, 0);
            Wire(h, bat, 1, lamp, 0);
            Assert.That(Port(g, bat.NetIdValue, 1).Live, Is.EqualTo(0f), "a fresh battery is empty");
            g.ServerToggle(gen.NetIdValue, true, h.Server.Session.CurrentTick);
            Run(h, 50);
            Assert.That(bat.Energy, Is.GreaterThan(0f));
            // The first step charges; from then on the OUT also discharges into the 250 W lamp each step.
            Assert.That(bat.SimEnergy, Is.EqualTo(600f * Dt + 49 * (600f - 250f) * Dt).Within(0.05f));
            Assert.That(Port(g, lamp.NetIdValue, 0).Powered, Is.True);
            Assert.That(Port(g, gen.NetIdValue, 0).Draw, Is.EqualTo(600f), "the IN terminal loads the generator");

            g.ServerToggle(gen.NetIdValue, false, h.Server.Session.CurrentTick);
            float before = bat.SimEnergy;
            Run(h, 25);
            Assert.That(bat.SimEnergy, Is.EqualTo(before - 25 * 250f * Dt).Within(0.05f), "the OUT drains by its load");
            Assert.That(h.StepUntil(() => h.Clients.TrueForAll(c => c.Deployables.StateHash() == g.StateHash())), Is.True);
            foreach (var c in h.Clients)
            {
                Assert.That(c.Deployables.TryGet(bat.NetIdValue, out var r) && r.Energy == bat.Energy, Is.True);
                Assert.That(Port(c.Deployables, lamp.NetIdValue, 0).Powered, Is.True, "replicas solve the accepted charge");
            }

            var json = WorldSave.Capture(h.Server, "", 0, 0.5f, 1200f).ToJson();
            Assert.That(WorldSave.TryParse(json, "", out var save, out var err), Is.True, err);
            var reload = Rig(7803);
            save.ApplyWorld(reload.Server, 0);
            var back = reload.Server.Deployables.All.Single(e => e.DefId == Battery);
            Assert.That(back.Energy, Is.EqualTo(bat.Energy), "accepted charge survives the world save");
            Assert.That(back.SimEnergy, Is.EqualTo(bat.Energy));
        }

        [Test]
        public void TurbineOutputFollowsServerWindAndHeightAboveSea()
        {
            var h = Rig(7804, "owner");
            var g = h.Server.Deployables;
            float wind = 0f;
            h.Server.Power.WindAt = _ => wind;
            h.Server.SeaLevelY = 25.6f;
            var sea = Place(h, Turbine, new Vector3(0, 25.6f, 0));
            var high = Place(h, Turbine, new Vector3(5, 65.6f, 0));
            var lamp = Place(h, Spot, new Vector3(0, 25.6f, 3));
            Wire(h, sea, 0, lamp, 0);
            Run(h, 2);
            Assert.That(sea.Producing(Def(g, Turbine)), Is.False, "dead calm");
            Assert.That(Port(g, lamp.NetIdValue, 0).Powered, Is.False);

            wind = 0.6f;
            Run(h, 2);
            Assert.That(sea.Wind, Is.EqualTo(DeployableReplication.QuantizeWind(0.6f)));
            Assert.That(high.Wind, Is.EqualTo(DeployableReplication.QuantizeWind(1.2f)), "+40 m doubles the factor");
            Assert.That(Port(g, sea.NetIdValue, 0).Live, Is.EqualTo(2500f * sea.Wind), "the output cap scales with wind");
            Assert.That(Port(g, lamp.NetIdValue, 0).Powered, Is.True);
            // Deployable._lastWindDirty: a drift within 0.04 keeps the accepted factor (and the solve) still.
            long version = g.GraphVersion;
            wind = 0.615f;   // +0.015 at sea, +0.03 on the 2x mast: both inside 0.04
            Run(h, 3);
            Assert.That(sea.Wind, Is.EqualTo(DeployableReplication.QuantizeWind(0.6f)));
            Assert.That(g.GraphVersion, Is.EqualTo(version), "no re-solve churn inside the source hysteresis");
            wind = 0.65f;
            Run(h, 1);
            Assert.That(sea.Wind, Is.EqualTo(DeployableReplication.QuantizeWind(0.65f)));
            wind = 1.5f;
            Run(h, 1);
            Assert.That(high.Wind, Is.EqualTo(2f), "the factor caps at 2");
            Assert.That(h.StepUntil(() => h.Clients[0].Deployables.StateHash() == g.StateHash()), Is.True);
            Assert.That(Port(h.Clients[0].Deployables, sea.NetIdValue, 0).Live, Is.EqualTo(2500f * sea.Wind));
            wind = 0.02f;
            Run(h, 1);
            Assert.That(h.StepUntil(() => h.Clients[0].Deployables.StateHash() == g.StateHash()), Is.True);
            Assert.That(h.Clients[0].Deployables.TryGet(sea.NetIdValue, out var calm) && !calm.Producing(Def(g, Turbine)), Is.True,
                "0.02 is below the 0.03 production threshold");
            Assert.That(Port(h.Clients[0].Deployables, sea.NetIdValue, 0).Live, Is.EqualTo(0f), "a non-producing turbine exports nothing");
        }

        [Test]
        public void SideTriggersSetTheSwitchAndCommandTheGenerator()
        {
            var h = Rig(7805, "owner");
            var g = h.Server.Deployables;
            var feed = Place(h, Gen, new Vector3(-3, 0, 0));
            var gate = Place(h, Switch, Vector3.zero);
            g.ServerToggle(feed.NetIdValue, true, h.Server.Session.CurrentTick);
            Wire(h, feed, 0, gate, 3);                        // TurnOff trigger
            Run(h, 1);
            Assert.That(gate.ToggledOn, Is.False, "a fed TurnOff sets the switch off");
            Assert.That(h.StepUntil(() => h.Clients[0].Deployables.TryGet(gate.NetIdValue, out var r) && !r.ToggledOn), Is.True);

            var engine = Place(h, Gen, new Vector3(3, 0, 0));
            var sense = Place(h, Gen, new Vector3(3, 0, 3));
            g.ServerToggle(sense.NetIdValue, true, h.Server.Session.CurrentTick);
            var on = g.ServerConnectWire(h.Server.Ids.Mint(), sense.NetIdValue, 0, engine.NetIdValue, 1, h.Server.Session.CurrentTick);
            Run(h, 1);
            Assert.That(engine.ToggledOn, Is.True, "TurnOn starts a fuelled generator");
            g.ServerRemoveWire(on.NetIdValue, h.Server.Session.CurrentTick);
            Run(h, 1);
            Assert.That(engine.ToggledOn, Is.True, "dropping the sense wire does not stop it");
            Wire(h, sense, 0, engine, 2);
            Run(h, 1);
            Assert.That(engine.ToggledOn, Is.False, "TurnOff stops it");
            g.ServerSetScalars(engine.NetIdValue, engine.Health, 0f, false, h.Server.Session.CurrentTick);
            g.ServerRemoveWire(g.AllWires.Single(w => w.DstId == engine.NetIdValue).NetIdValue, h.Server.Session.CurrentTick);
            Wire(h, sense, 0, engine, 1);
            Run(h, 1);
            Assert.That(engine.ToggledOn, Is.False, "TurnOn needs fuel");

            // Both triggers fed: applied in port order to one state, TurnOff (later) wins and nothing flaps.
            var both = Place(h, Switch, new Vector3(0, 0, 4));
            var feedOn = Place(h, Gen, new Vector3(-2, 0, 4));
            g.ServerToggle(feedOn.NetIdValue, true, h.Server.Session.CurrentTick);
            var feedOff = Place(h, Gen, new Vector3(2, 0, 4));
            g.ServerToggle(feedOff.NetIdValue, true, h.Server.Session.CurrentTick);
            Wire(h, feedOn, 0, both, 2);
            Wire(h, feedOff, 0, both, 3);
            Run(h, 2);
            Assert.That(both.ToggledOn, Is.False);
            long settled = both.LastChangedTick;
            Run(h, 10);
            Assert.That(both.LastChangedTick, Is.EqualTo(settled), "a stable feed toggles once, not every tick");
        }

        [Test]
        public void WithoutTheHostStepTheUnifiedServerInputsStayStatic()
        {
            var h = Rig(7806, "owner");
            var g = h.Server.Deployables;
            var gen = Place(h, Gen, new Vector3(-3, 0, 0), fuel: 10f);
            var bat = Place(h, Battery, Vector3.zero);
            Wire(h, gen, 0, bat, 0);
            g.ServerToggle(gen.NetIdValue, true, h.Server.Session.CurrentTick);
            h.Step(50);
            Assert.That(gen.Fuel, Is.EqualTo(10f));
            Assert.That(bat.Energy, Is.EqualTo(0f));
            Assert.That(bat.Producing(Def(g, Battery)), Is.False, "a battery is never a toggled generator");
        }

        [Test]
        public void HeldFuelCanPoursIntoAGeneratorOnlyThroughTheAcceptedExactInstance()
        {
            var h = Rig(7807, "owner");
            var client = h.Clients[0];
            var g = h.Server.Deployables;
            var gen = Place(h, Gen, new Vector3(0, 0, 2), fuel: 149_960f);
            var lamp = Place(h, Spot, new Vector3(2, 0, 2));
            var far = Place(h, Gen, new Vector3(80, 0, 0), fuel: 10f);
            var can = new Item(TransactionalFixtures.GasCanId) { fuelLevel = 80f };
            h.Server.Inventories.ItemIdentityProvider = item => ReferenceEquals(item, can) ? 77UL : 99UL;
            h.Grant(client.PlayerId, can);
            Assert.That(h.Server.Inventories.TryGet(client.PlayerId, out var owned), Is.True);
            byte page = 0, x = 0, y = 0;
            for (byte p = 0; p < PlayerInventory.OWNPAGES; p++)
                for (byte i = 0; i < owned.Inventory.items[p].getItemCount(); i++)
                    if (ReferenceEquals(owned.Inventory.items[p].getItem(i).item, can))
                    { page = p; x = owned.Inventory.items[p].getItem(i).x; y = owned.Inventory.items[p].getItem(i).y; }
            Assert.That(h.StepUntil(() => client.Inventories.TryGetItemToken(client.PlayerId, page, x, y, out ulong t) && t == 77), Is.True);
            void Pour(uint id, ulong token) { client.SendDepositFuel(id, page, x, y, TransactionalFixtures.GasCanId, token); h.Step(6); }

            Pour(gen.NetIdValue, 99);
            Assert.That(gen.SimFuel, Is.EqualTo(149_960f), "a replacement can at the same address pours nothing");
            Pour(lamp.NetIdValue, 77);
            Pour(far.NetIdValue, 77);
            Assert.That(far.SimFuel, Is.EqualTo(10f), "out of reach");
            Assert.That(can.fuelLevel, Is.EqualTo(80f), "non-generators and refusals keep the can");
            client.SendDepositFuel(gen.NetIdValue, page, x, y, TransactionalFixtures.GasCanId, 77);
            Assert.That(gen.SimFuel, Is.EqualTo(149_960f), "the intent alone pours nothing");
            h.Step(6);
            Assert.That(gen.Fuel, Is.EqualTo(150_000f), "min(can, free tank space)");
            Assert.That(can.fuelLevel, Is.EqualTo(40f));
            Pour(gen.NetIdValue, 77);
            Assert.That(can.fuelLevel, Is.EqualTo(40f), "a full tank refuses");
            Assert.That(h.StepUntil(() => client.Deployables.TryGet(gen.NetIdValue, out var r) && r.Fuel == 150_000f), Is.True);

            g.ServerSetScalars(gen.NetIdValue, gen.Health, 100f, true, h.Server.Session.CurrentTick);
            Pour(gen.NetIdValue, 77);
            Assert.That(gen.SimFuel, Is.EqualTo(100f), "a burning generator refuses");
            g.ServerSetScalars(gen.NetIdValue, gen.Health, 100f, false, h.Server.Session.CurrentTick);
            Pour(gen.NetIdValue, 77);
            Assert.That(gen.SimFuel, Is.EqualTo(140f));
            Assert.That(can.fuelLevel, Is.EqualTo(0f));
        }

        [Test]
        public void LegacySaveRestoresAClippedGeneratorFullAndKeepsLowerTanks()
        {
            var h = Rig(7808);
            Place(h, Gen, new Vector3(0, 0, 0), fuel: 2047.75f);
            Place(h, Gen, new Vector3(3, 0, 0), fuel: 900f);
            var json = WorldSave.Capture(h.Server, "", 0, 0.5f, 1200f).ToJson();
            Assert.That(WorldSave.TryParse(json, "", out var current, out var err), Is.True, err);
            Assert.That(current.LegacyFuelGrid, Is.False);
            var reload = Rig(7809);
            current.ApplyWorld(reload.Server, 0);
            Assert.That(reload.Server.Deployables.All.Select(e => e.Fuel).OrderBy(f => f), Is.EqualTo(new[] { 900f, 2047.75f }),
                "a v3 save is exact");
            var legacy = json.Replace("\"Version\":3", "\"Version\":2");
            Assert.That(legacy, Is.Not.EqualTo(json));
            Assert.That(WorldSave.TryParse(legacy, "", out var old, out err), Is.True, err);
            Assert.That(old.LegacyFuelGrid, Is.True);
            var migrated = Rig(7810);
            old.ApplyWorld(migrated.Server, 0);
            Assert.That(migrated.Server.Deployables.All.Select(e => e.Fuel).OrderBy(f => f), Is.EqualTo(new[] { 900f, 150_000f }),
                "the old 12.2 grid saved every fuller tank at its 2047.75 ceiling");
        }

        [Test]
        public void FilledFuelCansSurviveTheOwnerEchoExactly()
        {
            // ItemCatalog.WireFuelCans: Portable 20 L, Industrial 50 L. The pre-v78 12.2 jar grid echoed both
            // as 2047.75 mL, so a can filled at a pump read back as 2 L on its owner's screen.
            foreach (float level in new[] { 20_000f, 50_000f, 2048f })
            {
                byte[] pak = NetMessagePak.Pack(0, w => InventoryReplication.WriteJar(w,
                    new ItemJar(0, 0, 0, new Item(TransactionalFixtures.GasCanId) { fuelLevel = level })));
                var r = new NetPakReader(); r.SetBufferSegment(pak, pak.Length); r.ReadUInt8(out _);
                Assert.That(InventoryReplication.ReadJar(r, out _, out _, out _, out var item), Is.True);
                Assert.That(item.fuelLevel, Is.EqualTo(level));
            }
        }

        static DeployableNetDef Def(DeployableReplication g, ushort id) { g.Schema.TryGet(id, out var d); return d; }
    }
}
