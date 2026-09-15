using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // DRAGGING A STACK ONTO A STACK COMBINES IT (strawberry 2026-09-15: "dragging money stacks onto eachother
    // should combine the stack you dragged a stack onto, up to 500, and leaving the remainder (if any) in the
    // origin slot").
    //
    // Two things make this worth pinning rather than eyeballing. It MUTATES two jars at once, so it is the
    // easiest place in the grid math to create or destroy quantity; and `Item.amount` is OVERLOADED -- on a
    // magazine it is the loaded round count -- so a merge that goes by id alone breeds over-full magazines.
    [TestFixture]
    public class StackMergeDragTests
    {
        const byte Page = 2;                 // pockets: a real 5x3 grid, not a single-item holster
        const ushort Shells = 5000;
        const ushort Mag = 5001;

        [SetUp]
        public void RegisterAssets()
        {
            foreach (var id in Currency.Denominations)
                if (Assets.find(id) == null)
                    Assets.add(new ItemAsset { id = id, itemName = "money " + id, size_x = 1, size_y = 1 });
            Assets.find(Currency.StackId).stackSize = Currency.MaxPerStack;

            if (Assets.find(Shells) == null)
                Assets.add(new ItemAsset { id = Shells, itemName = "Shells", size_x = 1, size_y = 1, stackSize = 32 });
            else Assets.find(Shells).stackSize = 32;

            // A magazine that WOULD merge if the rule went by id and cap alone -- the control for the guard.
            if (Assets.find(Mag) == null)
                Assets.add(new ItemAsset { id = Mag, itemName = "Magazine", size_x = 1, size_y = 1, magCapacity = 30, magCaliber = 1 });
            Assets.find(Mag).stackSize = 8;
        }

        static PlayerInventory TwoAt(ushort id, int a, int b)
        {
            var inv = new PlayerInventory();
            inv.items[Page].loadSize(5, 3);   // pockets are 5x3 from the ctor; restated so the test does not depend on it
            inv.items[Page].addItem(0, 0, 0, new Item(id) { amount = (ushort)a });
            inv.items[Page].addItem(1, 0, 0, new Item(id) { amount = (ushort)b });
            return inv;
        }

        static int TotalOf(PlayerInventory inv, ushort id)
        {
            int n = 0;
            var pg = inv.items[Page];
            for (byte i = 0; i < pg.getItemCount(); i++)
            {
                var j = pg.getItem(i);
                if (j?.item != null && j.item.id == id) n += j.item.amount;
            }
            return n;
        }

        // ⭐ THE HEADLINE: it lands on the stack you dragged ONTO, and the origin slot empties.
        [Test]
        public void DraggingAStackOntoAnotherCombinesThem()
        {
            var inv = TwoAt(Currency.StackId, 120, 30);       // drag the $120 onto the $30
            Assert.That(inv.TryDrag(Page, 0, 0, Page, 1, 0, 0), Is.True);

            Assert.That(inv.items[Page].getItemCount(), Is.EqualTo(1), "one stack left");
            Assert.That(inv.items[Page].getItem(1, 0).item.amount, Is.EqualTo(150), "the DESTINATION holds the sum");
        }

        // The cap is the item's own stackSize, which for money is $500.
        [Test]
        public void TheRemainderStaysInTheOriginSlot()
        {
            var inv = TwoAt(Currency.StackId, 200, 400);      // 400 + 200 = 600, over the 500 ceiling
            Assert.That(inv.TryDrag(Page, 0, 0, Page, 1, 0, 0), Is.True);

            Assert.That(inv.items[Page].getItem(1, 0).item.amount, Is.EqualTo(Currency.MaxPerStack), "destination filled to the cap");
            Assert.That(inv.items[Page].getItem(0, 0), Is.Not.Null, "the origin slot still holds something");
            Assert.That(inv.items[Page].getItem(0, 0).item.amount, Is.EqualTo(600 - Currency.MaxPerStack), "...exactly the remainder");
        }

        // ⭐ THE CONTROL. Both assertions above pass just as well against a merge that DUPLICATES rather than
        // moves -- 150 is there either way. Only the total tells a combine from a mint.
        [Test]
        public void MergingConservesTheTotal()
        {
            var inv = TwoAt(Currency.StackId, 200, 400);
            inv.TryDrag(Page, 0, 0, Page, 1, 0, 0);
            Assert.That(TotalOf(inv, Currency.StackId), Is.EqualTo(600), "600 in, 600 out");
        }

        // Not money-specific: the rule is the item's cap, so loose shells combine to 32 the same way.
        [Test]
        public void AnyStackableCombinesToItsOwnCap()
        {
            var inv = TwoAt(Shells, 20, 20);
            Assert.That(inv.TryDrag(Page, 0, 0, Page, 1, 0, 0), Is.True);
            Assert.That(inv.items[Page].getItem(1, 0).item.amount, Is.EqualTo(32), "filled to the shell cap");
            Assert.That(inv.items[Page].getItem(0, 0).item.amount, Is.EqualTo(8), "and the rest stayed behind");
        }

        // ⚠ THE GUARD. A magazine's `amount` is its LOADED ROUNDS, so combining two of them by id would hand
        // back one magazine holding both loads. This asset is deliberately given stackSize 8 so the rule would
        // merge it if the magazine check were absent.
        [Test]
        public void MagazinesNeverMergeBecauseTheirAmountIsRounds()
        {
            var inv = TwoAt(Mag, 12, 15);
            Assert.That(inv.TryDrag(Page, 0, 0, Page, 1, 0, 0), Is.True, "the drag still resolves (as a swap)");

            Assert.That(inv.items[Page].getItemCount(), Is.EqualTo(2), "still two magazines");
            // ints on purpose: `amount` is a ushort, and NUnit compares collection members by Equals, where
            // (ushort)12 does not equal (int)12 -- the assertion would fail on the TYPE and read as a real bug.
            var loads = new[] { (int)inv.items[Page].getItem(0, 0).item.amount, (int)inv.items[Page].getItem(1, 0).item.amount };
            Assert.That(loads, Is.EquivalentTo(new[] { 12, 15 }), "each keeps its own load -- no super-magazine");
        }

        // Two FULL stacks cannot combine, and the gesture must still do something: they trade places.
        [Test]
        public void TwoFullStacksSwapRatherThanDoingNothing()
        {
            var inv = TwoAt(Shells, 32, 32);
            inv.items[Page].getItem(0, 0).item.quality = 11;   // tell them apart
            Assert.That(inv.TryDrag(Page, 0, 0, Page, 1, 0, 0), Is.True);
            Assert.That(inv.items[Page].getItem(1, 0).item.quality, Is.EqualTo(11), "the dragged one is now in the destination");
            Assert.That(TotalOf(inv, Shells), Is.EqualTo(64), "and nothing was lost doing it");
        }

        // Different items are not the same stack, so they swap -- otherwise a plank would absorb a shell.
        [Test]
        public void DifferentItemsDoNotCombine()
        {
            var inv = new PlayerInventory();
            inv.items[Page].loadSize(5, 3);
            inv.items[Page].addItem(0, 0, 0, new Item(Shells) { amount = 5 });
            inv.items[Page].addItem(1, 0, 0, new Item(Currency.StackId) { amount = 5 });
            Assert.That(inv.TryDrag(Page, 0, 0, Page, 1, 0, 0), Is.True);

            Assert.That(TotalOf(inv, Shells), Is.EqualTo(5), "the shells are still shells");
            Assert.That(TotalOf(inv, Currency.StackId), Is.EqualTo(5), "and the money is still money");
        }
    }
}
