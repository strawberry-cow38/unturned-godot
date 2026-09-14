using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // SPLITTING A STACK (strawberry 2026-09-14: "do we have a way to split stacks? if not, implement one").
    //
    // The dangerous half of a split is not the arithmetic, it is the ORDER: takeFrom reduces the source, so a
    // caller that takes before it knows where the items are going has destroyed them. These tests are aimed at
    // that, at the boundaries where "split" stops meaning split, and at the state a split must carry across.
    [TestFixture]
    public class StackSplitTests
    {
        const ushort Shells = 5000;   // a stackable item that is not money, so nothing here rides on the currency rules
        const byte Page = 7;

        [SetUp]
        public void RegisterAssets()
        {
            if (Assets.find(Shells) == null)
                Assets.add(new ItemAsset { id = Shells, itemName = "Test Shells", size_x = 1, size_y = 1, stackSize = 32 });
            else Assets.find(Shells).stackSize = 32;
        }

        static Items Page8x6()
        {
            var p = new Items(Page);
            p.loadSize(8, 6);
            return p;
        }

        static Items WithStack(int amount, out byte index)
        {
            var p = Page8x6();
            p.addItem(0, 0, 0, new Item(Shells) { amount = (ushort)amount });
            index = p.getIndex(0, 0);
            return p;
        }

        [Test]
        public void SplitLeavesTheRemainderBehindAndBothHalvesExist()
        {
            var p = WithStack(20, out byte i);
            var made = p.splitItem(i, 8);

            Assert.That(made, Is.Not.Null, "the split produced a stack");
            Assert.That(made.item.amount, Is.EqualTo(8), "...of the size asked for");
            Assert.That(p.getItem(i).item.amount, Is.EqualTo(12), "and the source kept the rest");
            Assert.That(p.getItemCount(), Is.EqualTo(2), "two stacks now exist where there was one");
        }

        // ⭐ THE CONTROL. Every assertion above passes just as well against a split that DUPLICATES rather than
        // divides -- 8 and 12 are both there either way. Only the total says which one happened.
        [Test]
        public void SplittingConservesTheTotal()
        {
            var p = WithStack(20, out byte i);
            p.splitItem(i, 8);

            int total = 0;
            for (byte k = 0; k < p.getItemCount(); k++) total += p.getItem(k).item.amount;
            Assert.That(total, Is.EqualTo(20), "a split divides a stack, it does not mint one");
        }

        [TestCase(0, TestName = "splitting off nothing")]
        [TestCase(-3, TestName = "splitting off a negative")]
        [TestCase(20, TestName = "splitting off the whole stack")]
        [TestCase(21, TestName = "splitting off more than there is")]
        public void IllegalSplitsChangeNothing(int amount)
        {
            var p = WithStack(20, out byte i);
            var made = p.splitItem(i, amount);

            Assert.That(made, Is.Null, "refused");
            Assert.That(p.getItemCount(), Is.EqualTo(1), "no second stack appeared");
            Assert.That(p.getItem(i).item.amount, Is.EqualTo(20), "and the original is untouched");
        }

        // Taking the WHOLE stack is refused specifically because it would leave an amount-0 jar sitting in the
        // grid, and every "is there an item here" check in the game answers YES to one of those.
        [Test]
        public void AZeroAmountJarIsNeverLeftInTheGrid()
        {
            var p = WithStack(4, out byte i);
            p.splitItem(i, 4);
            for (byte k = 0; k < p.getItemCount(); k++)
                Assert.That(p.getItem(k).item.amount, Is.GreaterThan(0), $"jar {k} is a real stack");
        }

        // The split stack is the same ITEM, not just the same id: quality and the per-item state ride along.
        // Clone() is memberwise precisely so a field added later is not silently dropped here.
        [Test]
        public void TheSplitCarriesTheItemsStateNotJustItsId()
        {
            var p = Page8x6();
            p.addItem(0, 0, 0, new Item(Shells) { amount = 10, quality = 61, cooked = 3, frozen = 7 });
            byte i = p.getIndex(0, 0);

            var made = p.splitItem(i, 4);
            Assert.That(made.item.id, Is.EqualTo(Shells));
            Assert.That(made.item.quality, Is.EqualTo(61), "quality came across");
            Assert.That(made.item.cooked, Is.EqualTo(3), "so did the cook state");
            Assert.That(made.item.frozen, Is.EqualTo(7), "and the freeze state");
        }

        // ⚠ takeFrom REDUCES the source and hands the items back unplaced. If a caller takes first and only then
        // discovers it has nowhere to put them, those items are gone. This pins the contract that makes the
        // ordering matter, so anyone reading it knows the check-space-first rule is not stylistic.
        [Test]
        public void TakeFromDetachesWithoutPlacing()
        {
            var p = WithStack(10, out byte i);
            var taken = p.takeFrom(i, 3);

            Assert.That(taken, Is.Not.Null);
            Assert.That(taken.amount, Is.EqualTo(3), "the caller is holding three");
            Assert.That(p.getItem(i).item.amount, Is.EqualTo(7), "and the stack is already short by three");
            Assert.That(p.getItemCount(), Is.EqualTo(1), "...but nothing new is in the grid yet -- the caller must place it");
        }

        // A full page has nowhere to put the new stack, so the split must REFUSE rather than take first and
        // discover the problem afterwards.
        [Test]
        public void SplitIntoAFullPageTakesNothing()
        {
            var p = new Items(Page);
            p.loadSize(1, 1);
            p.addItem(0, 0, 0, new Item(Shells) { amount = 9 });
            byte i = p.getIndex(0, 0);

            Assert.That(p.splitItem(i, 4), Is.Null, "no room -> refused");
            Assert.That(p.getItem(i).item.amount, Is.EqualTo(9), "and crucially the source was not raided first");
        }
    }
}
