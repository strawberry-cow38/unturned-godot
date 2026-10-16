using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public class PowerSwitchReplicationTests
    {
        const ushort SwitchId = 9105;
        static void RegisterSwitch(DeployableSchema schema) => schema.Register(new DeployableNetDef {
            DefId = SwitchId, Health = 100, Range = 4, IsSwitch = true,
            Ports = new[] {
                new DeployablePortSpec { Kind = (byte)PowerPortKind.Consumer },
                new DeployablePortSpec { Kind = (byte)PowerPortKind.Passthrough },
            },
        });

        [SetUp] public void SetUp() => TransactionalFixtures.RegisterAssets();

        [Test]
        public void FuelFreeSwitchStartsOnAndAcceptedToggleGatesTheWholeDownstreamCircuit()
        {
            var h = new TransactionalHarness(9105).Connected("owner", "observer");
            RegisterSwitch(h.Server.Deployables.Schema);
            foreach (var c in h.Clients) RegisterSwitch(c.Deployables.Schema);
            var graph = h.Server.Deployables;
            long tick = h.Server.Session.CurrentTick;
            var gen = graph.ServerPlace(h.Server.Ids.Mint(), TransactionalFixtures.GeneratorId, 0, new Vector3(-2, 0, 0), 0, tick);
            var gate = graph.ServerPlace(h.Server.Ids.Mint(), SwitchId, 0, Vector3.zero, 0, tick);
            var lamp = graph.ServerPlace(h.Server.Ids.Mint(), TransactionalFixtures.SpotlightId, 0, new Vector3(2, 0, 0), 0, tick);
            graph.ServerConnectWire(h.Server.Ids.Mint(), gen.NetIdValue, 0, gate.NetIdValue, 0, tick);
            graph.ServerConnectWire(h.Server.Ids.Mint(), gate.NetIdValue, 1, lamp.NetIdValue, 0, tick);
            graph.ServerToggle(gen.NetIdValue, true, tick);
            Assert.That(gate.ToggledOn, Is.True, "original _switchOn defaults ON, without fuel");
            Assert.That(h.StepUntil(() => h.Clients.TrueForAll(c => c.Deployables.WireCount == 2)), Is.True);

            void CheckCircuit(bool powered)
            {
                foreach (var g in new[] { graph, h.Clients[0].Deployables, h.Clients[1].Deployables })
                {
                    g.Solve();
                    Assert.That(g.TryGet(lamp.NetIdValue, out var consumer), Is.True);
                    Assert.That(consumer.Solved[0].Powered, Is.EqualTo(powered));
                    Assert.That(g.TryGet(gen.NetIdValue, out var source), Is.True);
                    Assert.That(source.Solved[0].Draw, Is.EqualTo(powered ? 250 : 0));
                }
            }
            CheckCircuit(true);
            h.Clients[0].SendToggleDeployable(gate.NetIdValue, false);
            Assert.That(gate.ToggledOn, Is.True, "sending a client intent must not locally mutate authority");
            Assert.That(h.StepUntil(() => h.Clients.TrueForAll(c =>
                c.Deployables.TryGet(gate.NetIdValue, out var e) && !e.ToggledOn)), Is.True,
                "old CanToggle rejected the fuel-free switch");
            CheckCircuit(false); // Old Solve left Conducting=true, even with the off bit replicated.
            h.Clients[0].SendToggleDeployable(gate.NetIdValue, true);
            Assert.That(h.StepUntil(() => h.Clients.TrueForAll(c =>
                c.Deployables.TryGet(gate.NetIdValue, out var e) && e.ToggledOn)), Is.True);
            CheckCircuit(true);
            Assert.That(h.Clients[1].Deployables.StateHash(), Is.EqualTo(graph.StateHash()));
        }

        [Test]
        public void BurningSwitchAndOrdinaryFuelFreeConsumerStillRejectToggles()
        {
            var h = new TransactionalHarness(9106).Connected("owner");
            RegisterSwitch(h.Server.Deployables.Schema);
            RegisterSwitch(h.Clients[0].Deployables.Schema);
            var graph = h.Server.Deployables;
            long tick = h.Server.Session.CurrentTick;
            var gate = graph.ServerPlace(h.Server.Ids.Mint(), SwitchId, 0, Vector3.zero, 0, tick);
            var lamp = graph.ServerPlace(h.Server.Ids.Mint(), TransactionalFixtures.SpotlightId, 0, Vector3.zero, 0, tick);
            graph.ServerSetScalars(gate.NetIdValue, gate.Health, 0, true, tick);
            Assert.That(graph.CanToggle(gate.NetIdValue, out _), Is.False);
            Assert.That(graph.CanToggle(lamp.NetIdValue, out _), Is.False);
            h.Clients[0].SendToggleDeployable(gate.NetIdValue, false);
            h.Clients[0].SendToggleDeployable(lamp.NetIdValue, true);
            h.Step(30);
            Assert.That(gate.ToggledOn, Is.True, "burning switch refuses the off command");
            Assert.That(lamp.ToggledOn, Is.False, "ordinary fuel-free devices do not become generators");
        }
    }
}
