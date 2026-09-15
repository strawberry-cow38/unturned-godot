using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // HOVER-LOOTING FILLS THE STACKS THAT ARE ALREADY THERE (strawberry 2026-09-15: "should try to fill stacks to
    // full, filling each one before moving on to the next or an empty slot overflow. if theres not enough space in
    // the destination container, leave the remainder in the source container").
    //
    // The rule lives in tryAddItem and always did -- singleplayer was correct the whole time. What was wrong is
    // that the server-owned path resolved ONE EMPTY CELL on the client and sent a plain move, and an empty cell is
    // the single destination that cannot merge. So these test the shared primitive both paths now call.
    [TestFixture]
    public class QuickTransferTests
    {
        const byte From = 2;   // pockets
        const byte To = 7;     // STORAGE (the open crate)
        const ushort Shells = 5100;

        [SetUp]
        public void RegisterAssets()
        {
            if (Assets.find(Shells) == null)
                Assets.add(new ItemAsset { id = Shells, itemName = "Shells", size_x = 1, size_y = 1, stackSize = 32 });
            else Assets.find(Shells).stackSize = 32;
        }

        static PlayerInventory Rig(int carried, params int[] destStacks)
        {
            var inv = new PlayerInventory();
            inv.items[From].loadSize(5, 3);
            inv.items[To].loadSize((byte)System.Math.Max(2, destStacks.Length + 1), 1);
            inv.items[From].addItem(0, 0, 0, new Item(Shells) { amount = (ushort)carried });
            for (int i = 0; i < destStacks.Length; i++)
                inv.items[To].addItem((byte)i, 0, 0, new Item(Shells) { amount = (ushort)destStacks[i] });
            return inv;
        }

        static int Total(PlayerInventory inv, byte page)
        {
            int n = 0;
            var pg = inv.items[page];
            for (byte i = 0; i < pg.getItemCount(); i++) { var j = pg.getItem(i); if (j?.item != null) n += j.item.amount; }
            return n;
        }

        // ⭐ THE HEADLINE: it tops up what is already there instead of taking a fresh slot beside it.
        [Test]
        public void TransferFillsAnExistingStackRatherThanTakingANewSlot()
        {
            var inv = Rig(10, 20);                         // carry 10, crate holds 20 of 32
            Assert.That(inv.TryQuickTransfer(From, 0, 0, To), Is.True);

            Assert.That(inv.items[To].getItemCount(), Is.EqualTo(1), "still ONE stack in the crate");
            Assert.That(inv.items[To].getItem(0, 0).item.amount, Is.EqualTo(30), "...topped up to 30");
            Assert.That(inv.items[From].getItemCount(), Is.EqualTo(0), "and the source slot emptied");
        }

        // "filling each one before moving on to the next". Carrying 15 into two 20s (cap 32) is the case that
        // actually DISTINGUISHES the rule: filling in turn gives 32 and 23, while spreading it evenly would give
        // 27 and 28. A bigger load fills both to the cap either way and proves nothing about the order.
        [Test]
        public void EachStackIsFilledToItsCapBeforeTheNextIsTouched()
        {
            var inv = Rig(15, 20, 20);
            Assert.That(inv.TryQuickTransfer(From, 0, 0, To), Is.True);

            Assert.That(inv.items[To].getItem(0, 0).item.amount, Is.EqualTo(32), "the first stack went to FULL");
            Assert.That(inv.items[To].getItem(1, 0).item.amount, Is.EqualTo(23), "...and only what was left reached the second");
        }

        // "...or an empty slot overflow": what no existing stack can hold takes a fresh cell.
        [Test]
        public void WhatDoesNotFitTheStacksOverflowsIntoAnEmptySlot()
        {
            var inv = Rig(30, 30);                          // 2 fits in the stack, 28 does not
            Assert.That(inv.TryQuickTransfer(From, 0, 0, To), Is.True);

            Assert.That(inv.items[To].getItem(0, 0).item.amount, Is.EqualTo(32), "filled first");
            Assert.That(inv.items[To].getItemCount(), Is.EqualTo(2), "then a second stack appeared");
            Assert.That(Total(inv, To), Is.EqualTo(60), "and it is all there");
        }

        // "if theres not enough space in the destination container, leave the remainder in the source container."
        [Test]
        public void TheRemainderStaysInTheSourceWhenTheDestinationIsFull()
        {
            var inv = new PlayerInventory();
            inv.items[From].loadSize(5, 3);
            inv.items[To].loadSize(1, 1);                   // exactly one cell, already holding 30 of 32
            inv.items[From].addItem(0, 0, 0, new Item(Shells) { amount = 20 });
            inv.items[To].addItem(0, 0, 0, new Item(Shells) { amount = 30 });

            Assert.That(inv.TryQuickTransfer(From, 0, 0, To), Is.True, "some of it moved");
            Assert.That(inv.items[To].getItem(0, 0).item.amount, Is.EqualTo(32), "the crate took what it could");
            Assert.That(Total(inv, From), Is.EqualTo(18), "and the rest is still in the source");
        }

        // ⭐ THE CONTROL. Every check above passes just as well against a transfer that DUPLICATES: 32 is in the
        // crate either way. Only the two-page total tells a move from a mint.
        [Test]
        public void TransferConservesTheTotalAcrossBothPages()
        {
            var inv = Rig(30, 20, 20);
            inv.TryQuickTransfer(From, 0, 0, To);
            Assert.That(Total(inv, From) + Total(inv, To), Is.EqualTo(70), "70 in, 70 out");
        }

        // A destination with NO room at all must change nothing -- not partially raid the source and fail.
        [Test]
        public void ANoRoomDestinationIsAWholeNoOp()
        {
            var inv = new PlayerInventory();
            inv.items[From].loadSize(5, 3);
            inv.items[To].loadSize(1, 1);
            inv.items[From].addItem(0, 0, 0, new Item(Shells) { amount = 20 });
            inv.items[To].addItem(0, 0, 0, new Item(Shells) { amount = 32 });   // already full

            Assert.That(inv.TryQuickTransfer(From, 0, 0, To), Is.False, "reported as having moved nothing");
            Assert.That(Total(inv, From), Is.EqualTo(20), "the source is untouched");
            Assert.That(Total(inv, To), Is.EqualTo(32), "and so is the destination");
        }
    }
}
