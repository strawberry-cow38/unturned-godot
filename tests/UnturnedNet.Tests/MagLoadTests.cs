using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // THE HALF THAT WAS MISSING, AND THE ONLY ASSERTION THAT WOULD HAVE CAUGHT IT.
    //
    // The magazine load/unload worked perfectly on the client and changed nothing on the server. Every
    // check you could make from the client passed -- the wheel filled, the count ticked, the stack shrank --
    // and then the next inventory move round-tripped through the authoritative server, whose magazine had
    // never changed, and the owner echo put the rounds straight back.
    //
    // So these tests assert on the SERVER's inventory, never the client's. A client-side assertion here
    // would have been green throughout the entire life of the bug.
    [TestFixture]
    public class MagLoadTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        static ItemJar Find(NetWorldServer server, ushort playerId, ushort id)
        {
            var inv = server.Transactions.InventoryForTest(playerId);
            foreach (var page in inv.items)
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var j = page.getItem(i);
                    if (j?.item != null && j.item.id == id) return j;
                }
            return null;
        }

        [Test]
        public void loading_a_round_changes_the_SERVER_magazine()
        {
            var h = new TransactionalHarness(4401).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.StanagId) { amount = 0 });   // empty mag
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.Round556Id, 10));
            h.Step(10);

            var mag = Find(h.Server, a.PlayerId, TransactionalFixtures.StanagId);
            var rounds = Find(h.Server, a.PlayerId, TransactionalFixtures.Round556Id);
            Assert.That(mag, Is.Not.Null); Assert.That(rounds, Is.Not.Null);
            byte before = rounds.item.amount;

            a.SendMagLoad(2, mag.x, mag.y, TransactionalFixtures.StanagId,
                          2, rounds.x, rounds.y, TransactionalFixtures.Round556Id, false);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.MagLoadsApplied == 1), Is.True,
                        $"the server applied the load (seed={h.Net.Seed})");

            Assert.That(mag.item.amount, Is.EqualTo(1), "the SERVER's magazine gained the round");
            Assert.That(mag.item.magLoadedRound, Is.EqualTo("556"), "an empty mag locks to the cartridge loaded");
            Assert.That(Find(h.Server, a.PlayerId, TransactionalFixtures.Round556Id).item.amount,
                        Is.EqualTo(before - 1), "and the server spent one from the stack");
        }

        [Test]
        public void unloading_returns_the_round_on_the_SERVER()
        {
            var h = new TransactionalHarness(4402).Connected("a");
            var a = h.Clients[0];
            var loaded = new Item(TransactionalFixtures.StanagId) { amount = 5, magLoadedRound = "556" };
            h.Grant(a.PlayerId, loaded);
            h.Step(10);
            var mag = Find(h.Server, a.PlayerId, TransactionalFixtures.StanagId);
            Assert.That(mag.item.amount, Is.EqualTo(5));

            a.SendMagLoad(2, mag.x, mag.y, TransactionalFixtures.StanagId,
                          0, 0, 0, TransactionalFixtures.Round556Id, true);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.MagLoadsApplied == 1), Is.True,
                        $"the server applied the unload (seed={h.Net.Seed})");

            Assert.That(mag.item.amount, Is.EqualTo(4), "the SERVER's magazine lost a round");
            var back = Find(h.Server, a.PlayerId, TransactionalFixtures.Round556Id);
            Assert.That(back, Is.Not.Null, "and the round came back into the server's bag, not nowhere");
            Assert.That(back.item.amount, Is.EqualTo(1));
        }

        [Test]
        public void emptying_a_magazine_unlocks_its_cartridge()
        {
            // Until it hits zero a part-loaded mag refuses a different round. At zero it must forget, or a
            // magazine is permanently married to the first cartridge anyone ever put in it.
            var h = new TransactionalHarness(4403).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.StanagId) { amount = 1, magLoadedRound = "556" });
            h.Step(10);
            var mag = Find(h.Server, a.PlayerId, TransactionalFixtures.StanagId);

            a.SendMagLoad(2, mag.x, mag.y, TransactionalFixtures.StanagId,
                          0, 0, 0, TransactionalFixtures.Round556Id, true);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.MagLoadsApplied == 1), Is.True);
            Assert.That(mag.item.amount, Is.EqualTo(0));
            Assert.That(mag.item.magLoadedRound, Is.Null, "emptied -> the cartridge lock clears");
        }

        [Test]
        public void the_server_refuses_what_the_client_rule_refuses()
        {
            var h = new TransactionalHarness(4404).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.ScarMagId) { amount = 0 });   // caliber group 2, empty
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.Round556Id, 5));    // group 1 round
            h.Step(10);
            var mag = Find(h.Server, a.PlayerId, TransactionalFixtures.ScarMagId);
            var rounds = Find(h.Server, a.PlayerId, TransactionalFixtures.Round556Id);

            a.SendMagLoad(2, mag.x, mag.y, TransactionalFixtures.ScarMagId,
                          2, rounds.x, rounds.y, TransactionalFixtures.Round556Id, false);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.MagLoadsRejected == 1), Is.True,
                        $"wrong caliber body refused server-side (seed={h.Net.Seed})");
            Assert.That(mag.item.amount, Is.EqualTo(0), "and nothing changed");
            Assert.That(rounds.item.amount, Is.EqualTo(5), "the round was not consumed by a refused load");
        }

        [Test]
        public void a_part_loaded_magazine_refuses_a_different_cartridge()
        {
            // A STANAG body feeds 5.56 AND .300, so this is NOT a caliber refusal -- it is the no-mix rule,
            // and it is the one a body-only check would wave through.
            var h = new TransactionalHarness(4405).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.StanagId) { amount = 3, magLoadedRound = "556" });
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.Round300Id, 5));
            h.Step(10);
            var mag = Find(h.Server, a.PlayerId, TransactionalFixtures.StanagId);
            var rounds = Find(h.Server, a.PlayerId, TransactionalFixtures.Round300Id);

            a.SendMagLoad(2, mag.x, mag.y, TransactionalFixtures.StanagId,
                          2, rounds.x, rounds.y, TransactionalFixtures.Round300Id, false);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.MagLoadsRejected == 1), Is.True,
                        $"no-mix refused server-side (seed={h.Net.Seed})");
            Assert.That(mag.item.amount, Is.EqualTo(3));

            // CONTROL: the SAME body accepts .300 when the magazine is EMPTY. Without this leg a server that
            // refused .300 outright -- i.e. one that got compatibility wrong in the other direction -- passes.
            var h2 = new TransactionalHarness(4406).Connected("a");
            var b = h2.Clients[0];
            h2.Grant(b.PlayerId, new Item(TransactionalFixtures.StanagId) { amount = 0 });
            h2.Grant(b.PlayerId, new Item(TransactionalFixtures.Round300Id, 5));
            h2.Step(10);
            var mag2 = Find(h2.Server, b.PlayerId, TransactionalFixtures.StanagId);
            var r2 = Find(h2.Server, b.PlayerId, TransactionalFixtures.Round300Id);
            b.SendMagLoad(2, mag2.x, mag2.y, TransactionalFixtures.StanagId,
                          2, r2.x, r2.y, TransactionalFixtures.Round300Id, false);
            Assert.That(h2.StepUntil(() => h2.Server.Transactions.Diag.MagLoadsApplied == 1), Is.True,
                        "an EMPTY stanag takes .300 -- the body feeds both");
            Assert.That(mag2.item.magLoadedRound, Is.EqualTo("300"));
        }

        [Test]
        public void a_stale_slot_cannot_load_whatever_moved_there()
        {
            // The client addresses the magazine by grid position. If something else is in that cell by the
            // time the command lands, loading rounds into it is worse than refusing.
            var h = new TransactionalHarness(4407).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.Round556Id, 5));
            h.Step(10);
            var rounds = Find(h.Server, a.PlayerId, TransactionalFixtures.Round556Id);

            // claim a magazine sits where the ROUNDS actually are
            a.SendMagLoad(2, rounds.x, rounds.y, TransactionalFixtures.StanagId,
                          2, rounds.x, rounds.y, TransactionalFixtures.Round556Id, false);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.MagLoadsRejected == 1), Is.True,
                        $"identity check refused a stale slot (seed={h.Net.Seed})");
            Assert.That(Find(h.Server, a.PlayerId, TransactionalFixtures.Round556Id).item.amount,
                        Is.EqualTo(5), "nothing was consumed");
        }
    }
}
