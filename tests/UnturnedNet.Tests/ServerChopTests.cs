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

        // ---- the tree health bar's only source of truth ------------------------------------------------
        //
        // Tree health is NOT replicated state (one alive-bit per resource is the whole system, on purpose --
        // a health word per tree would cost the map's worth of bandwidth to draw one player one bar), so the
        // ONLY way a client can know it is this unicast. If it stops arriving the bar silently freezes at
        // whatever it last saw, which looks like a rendering bug and is not one.

        static System.Collections.Generic.List<ResourceHealthEvent> Watch(NetWorldClient c)
        {
            var seen = new System.Collections.Generic.List<ResourceHealthEvent>();
            c.ResourceHealth += e => seen.Add(e);
            return seen;
        }

        [Test]
        public void EveryLandedSwingTellsTheChopperWhatIsLeft()
        {
            var net = new MemNetwork(50506);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);
            var seen = Watch(client);

            server.Transactions.ChopResource(client.PlayerId, TreeIndex, 300, Axe);
            Pump(net, client, server, 10);
            Assert.That(seen, Has.Count.EqualTo(1), "the swing that did not fell it still reports");
            Assert.That(seen[0].Index, Is.EqualTo(TreeIndex));
            Assert.That(seen[0].Health, Is.EqualTo(500), "800 - 300");
            Assert.That(seen[0].Max, Is.EqualTo(800), "and the max, so the client can draw a fraction it did not have to guess");

            server.Transactions.ChopResource(client.PlayerId, TreeIndex, 200, Axe);
            Pump(net, client, server, 10);
            Assert.That(seen[^1].Health, Is.EqualTo(300));
        }

        [Test]
        public void TheFellingSwingReportsZeroRatherThanGoingSilent()
        {
            // The easy half-implementation: report only while the tree survives. Then the bar's last drawn
            // value is some arbitrary sliver and it vanishes -- on the one swing the player is watching
            // most closely.
            var net = new MemNetwork(50507);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);
            var seen = Watch(client);

            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe), Is.True);
            Pump(net, client, server, 10);
            Assert.That(seen, Has.Count.EqualTo(1));
            Assert.That(seen[0].Health, Is.Zero, "the bar reaches empty before the tree disappears");
        }

        [Test]
        public void ARefusedSwingTellsTheClientNothing()
        {
            // A knife is gated before the damage, so there is no new fact to report -- and a report would
            // leak a tree's health to a weapon that cannot touch it.
            var net = new MemNetwork(50508);
            var server = NewServerWithABirch(net, out var client);
            client.Connect();
            Pump(net, client, server, 25);
            var seen = Watch(client);

            server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Knife);
            Pump(net, client, server, 10);
            Assert.That(seen, Is.Empty);
        }

        // ---- where the logs land, and which way the tree goes -------------------------------------------

        static readonly Vector3 TreeAt = new Vector3(40f, 12f, -25f);

        [Test]
        public void LogsLieInALineOutFromTheTrunkAlongTheSwing()
        {
            // Retail: dropPosition = tree.position + dropDirection*(2 + reward) + up*2, with the direction
            // flattened. That is what makes a felled tree read as felled -- the logs lie where the trunk
            // went. Dropping them around the CHOPPER (what this did before a direction existed) looks like
            // loot from a kill and puts them behind you when you chop facing away.
            var net = new MemNetwork(50509);
            var server = NewServerWithABirch(net, out var client);
            server.Transactions.Harvest.RegisterInstance(TreeIndex, 3, TreeAt);
            client.Connect();
            Pump(net, client, server, 25);

            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe,
                                                         direction: new Vector3(1f, -0.4f, 0f)), Is.True);
            var drops = server.WorldItems.All.Select(w => w.Pos).ToList();
            Assert.That(drops, Is.Not.Empty);

            // Asserted as a SET of offsets rather than by enumeration order -- WorldItems.All is a
            // registry walk, and pinning this on its iteration order would be testing the container.
            var offsets = drops.Select(d => d - TreeAt).ToList();
            foreach (var o in offsets)
            {
                Assert.That(o.y, Is.EqualTo(2f).Within(0.01f), "up*2, and the swing's downward pitch never reaches the layout");
                Assert.That(System.Math.Abs(o.z), Is.LessThan(0.01f), "a +X swing lays them along +X only");
            }
            var alongX = offsets.Select(o => (int)System.Math.Round(o.x)).OrderBy(v => v).ToList();
            var expected = Enumerable.Range(2, drops.Count).ToList();   // 2, 3, 4, ... one metre apart
            Assert.That(alongX, Is.EqualTo(expected),
                        "a line stepping out from the trunk: " + string.Join(",", alongX));
        }

        [Test]
        public void WithNoSwingDirectionTheDropsScatterRatherThanStack()
        {
            // An explosion or an admin command fells with no direction. Retail's no-direction shape is the
            // scattered branch -- the line formula with a zero vector would pile every log on one spot.
            var net = new MemNetwork(50510);
            var server = NewServerWithABirch(net, out var client);
            server.Transactions.Harvest.RegisterInstance(TreeIndex, 3, TreeAt);
            server.Transactions.Rand = RollingRand();
            client.Connect();
            Pump(net, client, server, 25);

            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe), Is.True);
            var drops = server.WorldItems.All.Select(w => w.Pos).ToList();
            Assert.That(drops.Count, Is.GreaterThan(1));
            Assert.That(drops.Select(d => $"{d.x:0.00},{d.z:0.00}").Distinct().Count(), Is.GreaterThan(1),
                        "scattered, not one stack");
            foreach (var d in drops)
                Assert.That((new Vector3(d.x, 0f, d.z) - new Vector3(TreeAt.x, 0f, TreeAt.z)).magnitude,
                            Is.LessThan(2.01f), "and still within retail's +-2 of the stump");
        }

        [Test]
        public void TheFallingTreeCarriesTheServersRagdollToEveryClient()
        {
            // The tree does not animate: every peer spawns the model as physics debris and shoves it with
            // THIS vector. If it were derived locally, two players would watch the same trunk fall two
            // different ways -- and the peer who did not swing has no direction to derive it from at all.
            var net = new MemNetwork(50511);
            var server = NewServerWithABirch(net, out var client);
            server.Transactions.Harvest.RegisterInstance(TreeIndex, 3, TreeAt);
            client.Connect();
            Pump(net, client, server, 25);

            var felled = new System.Collections.Generic.List<ResourceHarvestedEvent>();
            client.ResourceHarvested += e => felled.Add(e);

            var dir = new Vector3(0.6f, -0.2f, 0.8f);
            Assert.That(server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe, direction: dir), Is.True);
            Pump(net, client, server, 10);

            Assert.That(felled, Has.Count.EqualTo(1));
            Assert.That(felled[0].Index, Is.EqualTo(TreeIndex));
            var expected = dir * 800;   // retail: direction * totalDamage
            Assert.That((felled[0].Ragdoll - expected).magnitude, Is.LessThan(0.5f),
                        $"got {felled[0].Ragdoll}, expected {expected}");
        }

        [Test]
        public void ARespawnCarriesNoRagdollAndDoesNotPretendTo()
        {
            var net = new MemNetwork(50512);
            var server = NewServerWithABirch(net, out var client);
            server.Transactions.Harvest.RegisterDef(new ResourceHarvestDef
            {
                AssetId = 3, Health = 800, ResetSeconds = 1f, RewardMin = 1, RewardMax = 1, BladeId = 0,
                Drops = new (ushort, int)[] { (37, 1) },
            });
            client.Connect();
            Pump(net, client, server, 25);

            int respawns = 0;
            client.ResourceRespawned += _ => respawns++;
            server.Transactions.ChopResource(client.PlayerId, TreeIndex, 800, Axe, direction: new Vector3(1f, 0f, 0f));
            Pump(net, client, server, 100);
            Assert.That(respawns, Is.EqualTo(1), "it stood back up");
            Assert.That(server.Resources.IsAlive(TreeIndex), Is.True);
        }

        /// <summary>A rand that walks 0..1 instead of returning 0.5 forever, so a "did it scatter" check is
        /// not silently testing a constant.</summary>
        static System.Func<float> RollingRand()
        {
            int n = 0;
            return () => ((n++ * 37) % 100) / 100f;
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
