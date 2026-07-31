using System.Linq;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Server-side temperature. The client half shipped first and could SEE that it was standing in a
    // fire while nothing hurt it, because the server ran no temperature simulation at all -- burning is
    // damage, and damage is the server's call, so a client that burns itself is either wrong or
    // cheating. These pin the server resolving it from its OWN replicated deployables.
    [TestFixture]
    public class ServerTemperatureTests
    {
        const ushort CampfireId = 362;

        static NetWorldServer NewServerWithCampfireDef(MemNetwork net)
        {
            var s = new NetWorldServer(new MemServerTransport(net), (c, r, e) => { });
            // Same two radii the real campfire prefab authors: a 10 m warm sphere with a 0.75 m burning
            // core. Registered as a def rather than sent -- only the defId ever crosses the wire.
            s.Deployables.Schema.Register(new DeployableNetDef
            {
                DefId = CampfireId, Health = 200f, Range = 4f,
                HeatWarmRadius = 10f, HeatBurnRadius = 0.75f,
            });
            return s;
        }

        [Test]
        public void TheServerBurnsAPlayerStandingInAFire()
        {
            var net = new MemNetwork(50401);
            var server = NewServerWithCampfireDef(net);
            var client = new NetWorldClient(new MemClientTransport(net), "victim");
            client.Connect();
            for (int i = 0; i < 25; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }
            Assert.That(client.State, Is.EqualTo(NetSessionState.Connected), "test setup: client joined");

            var me = server.Players.All.FirstOrDefault(p => p.OwnerPlayerId == client.PlayerId);
            Assert.That(me, Is.Not.Null, "test setup: the server has an entity for this player");

            // Drop the fire exactly where the server thinks the player is, so this tests the temperature
            // resolve rather than whether a test can walk an avatar somewhere.
            server.Deployables.ServerPlace(server.Ids.Mint(), CampfireId, 0, me.Pos, 0f, server.Session.CurrentTick);
            for (int i = 0; i < 3; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }
            Assert.That(server.TemperatureOf(client.PlayerId), Is.EqualTo(PlayerTemperature.Burning),
                        "standing in the burning core, server-side");

            int hp0 = server.Combat.HealthOf(client.PlayerId);
            for (int i = 0; i < 150; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }   // ~3 s at 50 Hz
            int hp1 = server.Combat.HealthOf(client.PlayerId);
            Assert.That(hp1, Is.LessThan(hp0), $"the SERVER took health off ({hp0} -> {hp1})");
        }

        [Test]
        public void WarmDoesNotHurt()
        {
            var net = new MemNetwork(50402);
            var server = NewServerWithCampfireDef(net);
            var client = new NetWorldClient(new MemClientTransport(net), "camper");
            client.Connect();
            for (int i = 0; i < 25; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }

            var me = server.Players.All.First(p => p.OwnerPlayerId == client.PlayerId);
            // Inside the 10 m warm sphere, outside the 0.75 m core.
            var beside = new Vector3(me.Pos.x + 4f, me.Pos.y, me.Pos.z);
            server.Deployables.ServerPlace(server.Ids.Mint(), CampfireId, 0, beside, 0f, server.Session.CurrentTick);
            for (int i = 0; i < 3; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }
            Assert.That(server.TemperatureOf(client.PlayerId), Is.EqualTo(PlayerTemperature.Warm));

            int hp0 = server.Combat.HealthOf(client.PlayerId);
            for (int i = 0; i < 150; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }
            Assert.That(server.Combat.HealthOf(client.PlayerId), Is.EqualTo(hp0), "warming up is not damage");
        }

        [Test]
        public void ADeployableWithNoHeatIsNotABubble()
        {
            // Guards the filter. Without it every placed object would register a zero-radius bubble, and
            // "is the player inside a 0 m sphere" is a comparison that only accidentally says no.
            var net = new MemNetwork(50403);
            var server = new NetWorldServer(new MemServerTransport(net), (c, r, e) => { });
            server.Deployables.Schema.Register(new DeployableNetDef { DefId = 458, Health = 450f, Range = 4f });
            var client = new NetWorldClient(new MemClientTransport(net), "builder");
            client.Connect();
            for (int i = 0; i < 25; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }

            var me = server.Players.All.First(p => p.OwnerPlayerId == client.PlayerId);
            server.Deployables.ServerPlace(server.Ids.Mint(), 458, 0, me.Pos, 0f, server.Session.CurrentTick);
            for (int i = 0; i < 5; i++) { net.Tick(); client.Tick(); server.TickSimulation(); }
            Assert.That(server.TemperatureOf(client.PlayerId), Is.EqualTo(PlayerTemperature.None),
                        "a generator you are standing on is not a temperature");
        }
    }
}
