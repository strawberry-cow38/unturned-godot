using System.Linq;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Felling a tree end to end on the server: the blade gate, the health, the weighted drops as real
    // replicated world items, the XP, and the alive-bit flip clients render. The sim was tested on its
    // own; this is the part that makes it REACHABLE -- the port already had the alive bitmap and the
    // broadcast, with a comment saying no mechanic drove them.
    [TestFixture]
    public class ServerChopTests
    {
        const int TreeIndex = 4;

        static NetWorldServer NewServerWithABirch(MemNetwork net, out NetWorldClient client)
        {
            var s = new NetWorldServer(new MemServerTransport(net), (c, r, e) => { });
            s.Resources.ServerInit(16, 0);
            s.Transactions.Harvest.RegisterDef(new ResourceHarvestDef
            {
                AssetId = 3, Health = 800, RewardXp = 4, ResetSeconds = 450f,
                RewardMin = 7, RewardMax = 10, HasDebris = true, BladeId = 0,
                Drops = new (ushort, int)[] { (37, 60), (38, 40) },
            });
            s.Transactions.Harvest.RegisterInstance(TreeIndex, 3);
            s.Transactions.Rand = () => 0.5f;     // deterministic rolls
            client = new NetWorldClient(new MemClientTransport(net), "lumberjack");
            return s;
        }

        static void Pump(MemNetwork net, NetWorldClient c, NetWorldServer s, int n)
        {
            for (int i = 0; i < n; i++) { net.Tick(); c.Tick(); s.TickSimulation(); }
        }

        static System.Func<int, bool> Axe => id => id == 0;
        static System.Func<int, bool> Knife => id => id == 3;

        [Test]
        public void AnAxeFellsABirchAndDropsItsTable()
        {
            var net = new MemNetwork(50501);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);

            int items0 = server.WorldItems.All.Count();
            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 400, Axe), Is.False,
                        "half its health is not a felling");
            Assert.That(server.Resources.IsAlive(TreeIndex), Is.True);

            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 400, Axe), Is.True,
                        "the swing that crosses zero fells it");
            Assert.That(server.Resources.IsAlive(TreeIndex), Is.False, "the alive bit flipped, which is what clients render");

            int dropped = server.WorldItems.All.Count() - items0;
            Assert.That(dropped, Is.InRange(7, 10), $"birch yields 7-10, got {dropped}");
            var ids = server.WorldItems.All.Select(w => w.ItemId).Distinct().ToList();
            Assert.That(ids.All(i => i == 37 || i == 38), Is.True,
                        "only what the birch table can produce: " + string.Join(",", ids));
        }

        [Test]
        public void AKnifeCannotFellATreeAndDoesNotEvenChipIt()
        {
            // The blade gate runs BEFORE the damage, so a weapon that cannot cut also cannot quietly
            // whittle a tree down over time.
            var net = new MemNetwork(50502);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);

            for (int i = 0; i < 10; i++)
                Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 400, Knife), Is.False);
            Assert.That(server.Transactions.Harvest.HealthOf(TreeIndex), Is.EqualTo(800), "not a scratch");
            Assert.That(server.Resources.IsAlive(TreeIndex), Is.True);
        }

        [Test]
        public void AFelledTreeCannotBeChoppedAgainForMoreDrops()
        {
            var net = new MemNetwork(50503);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);

            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe), Is.True);
            int after = server.WorldItems.All.Count();
            for (int i = 0; i < 5; i++)
                Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe), Is.False,
                            "a stump yields nothing");
            Assert.That(server.WorldItems.All.Count(), Is.EqualTo(after), "and no extra loot");
        }

        [Test]
        public void ItStandsBackUpOnTheServersClock()
        {
            var net = new MemNetwork(50504);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);
            // A short reset so the test does not have to run 450 sim seconds.
            server.Transactions.Harvest.RegisterDef(new ResourceHarvestDef
            {
                AssetId = 3, Health = 800, RewardXp = 4, ResetSeconds = 1f,
                RewardMin = 7, RewardMax = 10, BladeId = 0,
                Drops = new (ushort, int)[] { (37, 60) },
            });
            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe), Is.True);
            Assert.That(server.Resources.IsAlive(TreeIndex), Is.False);

            Pump(net, client, server, 100);   // 2 s at 50 Hz
            Assert.That(server.Resources.IsAlive(TreeIndex), Is.True, "the host tick regrew it");
            Assert.That(server.Transactions.Harvest.HealthOf(TreeIndex), Is.EqualTo(800), "at full health");
        }

        [Test]
        public void ChoppingAwardsTheAssetsXp()
        {
            var net = new MemNetwork(50505);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);

            server.Skills.TryGet(client.PlayerId, out var before);
            uint xp0 = before?.Skills.experience ?? 0;
            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe), Is.True);
            server.Skills.TryGet(client.PlayerId, out var after);
            Assert.That(after.Skills.experience, Is.EqualTo(xp0 + 4), "birch is Reward_XP 4");
        }
    }
}
