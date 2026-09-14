using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // SPLITTING A STACK OVER THE WIRE (strawberry 2026-09-14: "verified so we dont dupe items").
    //
    // The dangerous outcome of a split is not a wrong number, it is a CHANGED TOTAL -- items minted or items
    // deleted. Every assertion here is on the SERVER's inventory and most of them are on the sum, because the
    // shape of a split looks right in both failure modes: after "20 splits into 8" you can see an 8 and a 12
    // whether the 12 was reduced from 20 or a second 20 was conjured beside it. Only the total tells them apart.
    //
    // This matters more than usual because the UI drags a PREVIEW: the stack you can see is the only copy that
    // exists, and the command that lands it is evaluated against the stack as it stands at the drop, not as it
    // stood when the drag began.
    [TestFixture]
    public class SplitStackTests
    {
        const byte Pockets = 2;

        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.find(TransactionalFixtures.Round556Id).stackSize = 64;   // stackable, so a split has something to divide
        }

        static Items ServerPocket(NetWorldServer server, ushort playerId)
            => server.Transactions.InventoryForTest(playerId).items[Pockets];

        static int TotalOf(Items page, ushort id)
        {
            int n = 0;
            for (byte i = 0; i < page.getItemCount(); i++)
            {
                var j = page.getItem(i);
                if (j?.item != null && j.item.id == id) n += j.item.amount;
            }
            return n;
        }

        static ItemJar FirstOf(Items page, ushort id)
        {
            for (byte i = 0; i < page.getItemCount(); i++)
            {
                var j = page.getItem(i);
                if (j?.item != null && j.item.id == id) return j;
            }
            return null;
        }

        // A cell the grid says is genuinely empty, so a test never fails because it guessed an occupied one.
        static bool FreeCell(Items page, out byte fx, out byte fy)
        {
            for (byte y = 0; y < page.height; y++)
                for (byte x = 0; x < page.width; x++)
                    if (page.checkSpaceEmpty(x, y, 1, 1, 0)) { fx = x; fy = y; return true; }
            fx = fy = 0;
            return false;
        }

        static (TransactionalHarness h, NetWorldClient a, Items page, ItemJar src) WithStack(int port, int amount)
        {
            var h = new TransactionalHarness(port).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.Round556Id) { amount = (ushort)amount });
            h.Step(10);
            var page = ServerPocket(h.Server, a.PlayerId);
            return (h, a, page, FirstOf(page, TransactionalFixtures.Round556Id));
        }

        // ⭐ THE HEADLINE: a split divides, it does not mint.
        [Test]
        public void splitting_over_the_wire_conserves_the_total()
        {
            var (h, a, page, src) = WithStack(4501, 20);
            Assert.That(src, Is.Not.Null, "the stack reached the server");
            Assert.That(FreeCell(page, out byte fx, out byte fy), Is.True, "the page has somewhere to put it");

            a.SendSplitItem(Pockets, src.x, src.y, 8, Pockets, fx, fy, 0);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.SplitsApplied == 1), Is.True,
                        $"the server applied the split (seed={h.Net.Seed})");

            Assert.That(TotalOf(page, TransactionalFixtures.Round556Id), Is.EqualTo(20),
                        "the SERVER holds exactly what it held before -- no item was created or destroyed");
            Assert.That(src.item.amount, Is.EqualTo(12), "the source kept the remainder");
        }

        // ⚠ THE PREVIEW-GOES-STALE CASE, which is the one the dragging UI can actually produce: split once, then
        // send a second command still asking for more than the stack now has. It must be REFUSED, not clamped --
        // clamping would silently hand out items the player never had.
        [Test]
        public void a_stale_split_amount_is_refused_not_clamped()
        {
            var (h, a, page, src) = WithStack(4502, 20);
            Assert.That(FreeCell(page, out byte fx, out byte fy), Is.True);
            a.SendSplitItem(Pockets, src.x, src.y, 8, Pockets, fx, fy, 0);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.SplitsApplied == 1), Is.True);

            // the stack is 12 now; ask for 15 off it, as a UI holding a stale preview would
            Assert.That(FreeCell(page, out byte gx, out byte gy), Is.True);
            a.SendSplitItem(Pockets, src.x, src.y, 15, Pockets, gx, gy, 0);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.SplitsRejected >= 1), Is.True,
                        $"the server refused the stale amount (seed={h.Net.Seed})");

            Assert.That(TotalOf(page, TransactionalFixtures.Round556Id), Is.EqualTo(20), "still exactly twenty");
            Assert.That(src.item.amount, Is.EqualTo(12), "and the source was not raided for the refused split");
        }

        // Splitting a stack onto ITSELF is the easiest accidental dupe: the source is both ends of the operation,
        // so a handler that takes before it checks would add to the very jar it just reduced.
        [Test]
        public void splitting_onto_its_own_cell_changes_nothing()
        {
            var (h, a, page, src) = WithStack(4503, 20);

            a.SendSplitItem(Pockets, src.x, src.y, 8, Pockets, src.x, src.y, 0);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.SplitsRejected >= 1), Is.True,
                        $"the server refused it (seed={h.Net.Seed})");

            Assert.That(TotalOf(page, TransactionalFixtures.Round556Id), Is.EqualTo(20), "no items appeared");
            Assert.That(src.item.amount, Is.EqualTo(20), "and the stack is untouched");
        }

        // Dropping onto a matching stack MERGES, which is the one path where a split writes to two jars at once --
        // so it is the one most able to lose or double the difference between them.
        [Test]
        public void merging_onto_a_matching_stack_conserves_the_total()
        {
            var (h, a, page, src) = WithStack(4504, 20);
            Assert.That(FreeCell(page, out byte fx, out byte fy), Is.True);
            a.SendSplitItem(Pockets, src.x, src.y, 8, Pockets, fx, fy, 0);   // 20 -> 12 + 8
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.SplitsApplied == 1), Is.True);

            a.SendSplitItem(Pockets, src.x, src.y, 5, Pockets, fx, fy, 0);   // 5 more onto the 8
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.SplitsApplied == 2), Is.True,
                        $"the server merged it (seed={h.Net.Seed})");

            Assert.That(TotalOf(page, TransactionalFixtures.Round556Id), Is.EqualTo(20), "twenty in, twenty out");
            Assert.That(src.item.amount, Is.EqualTo(7), "the source paid for it");
        }
    }
}
