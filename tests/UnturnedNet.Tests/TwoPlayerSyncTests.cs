using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Proves the 2-player loop: two clients, sending their state each tick, see each other's positions
    // through the authoritative server -- over the real UDP transport + NetPak. The whole slice's netcode.
    [TestFixture]
    public class TwoPlayerSyncTests
    {
        [Test]
        public void TwoClients_SeeEachOther_ThroughServer()
        {
            const ushort port = 47869;
            var server = new NetServer(port);
            var a = new NetClient("127.0.0.1", port);
            var b = new NetClient("127.0.0.1", port);

            for (int tick = 0; tick < 30; tick++)
            {
                a.SendState(new PlayerState { X = tick, Y = 1f, Z = 2f, Yaw = 0.5f, Tick = (uint)tick });
                b.SendState(new PlayerState { X = -tick, Y = 3f, Z = 4f, Yaw = 1.5f, Tick = (uint)tick });
                Thread.Sleep(3);
                server.Poll();
                server.Broadcast();
                Thread.Sleep(3);
                a.Poll();
                b.Poll();
            }

            Assert.That(server.ClientCount, Is.EqualTo(2), "server sees 2 clients");
            Assert.That(a.Remote.Count, Is.EqualTo(2), "client A sees 2 players");
            Assert.That(b.Remote.Count, Is.EqualTo(2), "client B sees 2 players");

            var aXs = new List<float>(); foreach (var s in a.Remote.Values) aXs.Add(s.X);
            var bXs = new List<float>(); foreach (var s in b.Remote.Values) bXs.Add(s.X);
            Assert.That(aXs, Does.Contain(29f), "A sees A's echoed pos");
            Assert.That(aXs, Does.Contain(-29f), "A sees B's pos");
            Assert.That(bXs, Does.Contain(29f), "B sees A's pos");
            Assert.That(bXs, Does.Contain(-29f), "B sees B's echoed pos");

            a.TearDown(); b.TearDown(); server.TearDown();
        }
    }
}
