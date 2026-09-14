using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // MONEY IS ONE STACK WITH A VALUE (strawberry 2026-09-14: "the loonie ($1), toonie($2), $5, $10, $20, $50,
    // $100 collapse into one 'Canadian Dollars' stack... theres a $x number, and the inventory icon changes
    // depending on the value of the stack").
    //
    // The carrier is the LOONIE, so Item.amount means dollars literally and every existing mechanism that
    // moves, merges, splits, drops, saves and replicates a stack keeps working untouched. These tests are
    // aimed at the two ways that can quietly go wrong: money that does not COMBINE (seven slots of change
    // instead of one wallet), and money that VANISHES on the way in.
    [TestFixture]
    public class CurrencyTests
    {
        const byte Page = 7;

        [SetUp]
        public void RegisterAssets()
        {
            foreach (var id in Currency.Denominations)
                if (Assets.find(id) == null)
                    Assets.add(new ItemAsset { id = id, itemName = "money " + id, size_x = 1, size_y = 1, stackSize = Currency.MaxPerStack });
            // The carrier needs the big stack size specifically; the others keep whatever they have.
            Assets.find(Currency.StackId).stackSize = Currency.MaxPerStack;
            Assume.That(Assets.find(Currency.StackId), Is.Not.Null, "fixture: the carrier registered");
        }

        static Items Page8x6() { var p = new Items(Page); p.loadSize(8, 6); return p; }
        static int TotalDollars(Items p)
        {
            int t = 0;
            for (byte i = 0; i < p.getItemCount(); i++)
            {
                var j = p.getItem(i);
                if (j?.item != null && Currency.IsCurrency(j.item.id)) t += Currency.ValueOf(j.item.id) * j.item.amount;
            }
            return t;
        }

        [Test]
        public void EveryDenominationIsWorthWhatItSays()
        {
            Assert.That(Currency.ValueOf(1056), Is.EqualTo(1), "loonie");
            Assert.That(Currency.ValueOf(1057), Is.EqualTo(2), "toonie");
            Assert.That(Currency.ValueOf(1051), Is.EqualTo(5));
            Assert.That(Currency.ValueOf(1052), Is.EqualTo(10));
            Assert.That(Currency.ValueOf(1053), Is.EqualTo(20));
            Assert.That(Currency.ValueOf(1054), Is.EqualTo(50));
            Assert.That(Currency.ValueOf(1055), Is.EqualTo(100));
            // THE CONTROL: not everything is money. Without it, a ValueOf that returned 1 for anything would
            // pass every check above and turn every rock in the game into a coin.
            Assert.That(Currency.IsCurrency(13), Is.False, "canned beans are not legal tender");
            Assert.That(Currency.ValueOf(13), Is.EqualTo(0));
        }

        // ⭐ THE HEADLINE: seven different things go in, ONE stack comes out, and it is worth their sum.
        [Test]
        public void AllSevenDenominationsCollapseIntoOneStack()
        {
            var p = Page8x6();
            foreach (var id in Currency.Denominations) p.tryAddItem(new Item(id));   // 100+50+20+10+5+2+1

            Assert.That(p.getItemCount(), Is.EqualTo(1), "seven pickups, ONE wallet -- not seven slots of change");
            Assert.That(p.getItem(0).item.id, Is.EqualTo(Currency.StackId), "...carried by the $1 coin");
            Assert.That(p.getItem(0).item.amount, Is.EqualTo(188), "...worth exactly their sum");
        }

        [Test]
        public void AStackOfNotesIsWorthItsFaceValueTimesTheCount()
        {
            var p = Page8x6();
            p.tryAddItem(new Item(1053) { amount = 6 });   // six $20 notes
            Assert.That(p.getItemCount(), Is.EqualTo(1));
            Assert.That(p.getItem(0).item.amount, Is.EqualTo(120), "6 x $20 = $120, not 6");
        }

        // ⚠ THE ONE THAT LOSES MONEY IF IT IS WRONG. amount is a BYTE, so a wallet caps at $255 and the rest
        // has to spill into another stack rather than being clamped away. A clamp would look identical in the
        // common case and silently eat $45 here.
        [Test]
        public void MoneyPastTheStackCeilingSpillsRatherThanVanishing()
        {
            var p = Page8x6();
            p.tryAddItem(new Item(1055) { amount = 3 });   // $300, past the 255 ceiling

            Assert.That(TotalDollars(p), Is.EqualTo(300), "not a cent lost to the byte");
            Assert.That(p.getItemCount(), Is.EqualTo(2), "...it needed a second stack to hold it");
            Assert.That(p.getItem(0).item.amount, Is.EqualTo(Currency.MaxPerStack), "the first fills to the ceiling");
            Assert.That(p.getItem(1).item.amount, Is.EqualTo(300 - Currency.MaxPerStack), "the rest follows");
        }

        [Test]
        public void AddingToAnExistingWalletTopsItUp()
        {
            var p = Page8x6();
            p.tryAddItem(new Item(1052));   // $10
            p.tryAddItem(new Item(1051));   // $5
            p.tryAddItem(new Item(1057));   // $2
            Assert.That(p.getItemCount(), Is.EqualTo(1), "one wallet, three pickups");
            Assert.That(p.getItem(0).item.amount, Is.EqualTo(17));
        }

        // THE ICON IS THE BIGGEST THING IN THE PILE -- the rule a player can read off the picture without being
        // told it. Boundaries included, because "largest that fits" and "largest strictly below" differ by
        // exactly one dollar and both look right on a $137 wallet.
        [TestCase(0, 1056)]     // an empty wallet still draws as something
        [TestCase(1, 1056)]     // loonie
        [TestCase(2, 1057)]     // toonie, exactly
        [TestCase(4, 1057)]
        [TestCase(5, 1051)]     // $5, exactly
        [TestCase(9, 1051)]
        [TestCase(10, 1052)]
        [TestCase(19, 1052)]
        [TestCase(20, 1053)]
        [TestCase(49, 1053)]
        [TestCase(50, 1054)]
        [TestCase(99, 1054)]
        [TestCase(100, 1055)]
        [TestCase(137, 1055)]
        [TestCase(255, 1055)]
        public void TheIconIsTheLargestDenominationTheStackCouldPayOut(int dollars, int expectedIcon)
            => Assert.That(Currency.IconIdFor(dollars), Is.EqualTo((ushort)expectedIcon), $"${dollars}");
    }
}
