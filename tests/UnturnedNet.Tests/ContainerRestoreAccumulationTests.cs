using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // LOOT THAT BRED EVERY TIME YOU LOADED.
    //
    // RestorePage applied a save's container contents with addItem and never cleared first -- but the page it
    // restores into is NOT empty, because the world has already rolled this crate's loot by the time the save
    // is applied. So every load added the saved set ON TOP of a freshly-looted container.
    //
    // Measured on a real PEI world: 15.8 items per container on a fresh save, 44.0 after eight loads, against a
    // 48-slot grid. That is what grew one save to 41.5 MB / 1.54M lines, and because WorldSaveDriver serialises
    // the whole file on the MAIN THREAD every 60 s, once that write passed 60 s the game sat at a permanent
    // ~1 fps with the GPU idle. The save-size fix cut the cost per item ~65x; this is the thing that was adding
    // the items. Duplicated loot is the same bug wearing its gameplay face.
    //
    // Each test fails if the clear is wrong, not merely if it is missing.
    [TestFixture]
    public class ContainerRestoreAccumulationTests
    {
        const ushort TomatoId = 66, SteakId = 67;
        const byte StoragePage = 7;   // >= PlayerInventory.SLOTS, so it is a real grid rather than a hand slot

        [SetUp]
        public void RegisterAssets()
        {
            // Additively, never Assets.clear() -- the fixture shares a process with every other test here.
            // Without sizes, addItem cannot mark slots and the whole test would pass vacuously on an empty page.
            if (Assets.find(TomatoId) == null)
                Assets.add(new ItemAsset { id = TomatoId, itemName = "Tomato", size_x = 1, size_y = 1, type = EItemType.FOOD });
            if (Assets.find(SteakId) == null)
                Assets.add(new ItemAsset { id = SteakId, itemName = "Steak", size_x = 1, size_y = 1, type = EItemType.FOOD });
            Assume.That(Assets.find(TomatoId), Is.Not.Null, "fixture: the tomato asset did not register");
        }

        static Items FreshlyLootedCrate()
        {
            var page = new Items(StoragePage);
            page.loadSize(8, 6);
            // what the world put there before any save is applied
            page.addItem(0, 0, 0, new Item(TomatoId));
            page.addItem(1, 0, 0, new Item(TomatoId));
            return page;
        }

        static WorldSave.PageSave SavedContents() => new WorldSave.PageSave
        {
            Width = 8, Height = 6,
            Items =
            {
                new WorldSave.JarSave { X = 4, Y = 4, Id = SteakId, Amount = 1, Quality = 100 },
            },
        };

        // THE BUG. Restore the save over an already-looted crate and the crate must hold the SAVE's contents,
        // not the save's plus the world's. Remove the page.clear() and this reads 3 instead of 1.
        [Test]
        public void RestoringOverALootedCrateDoesNotAccumulate()
        {
            var page = FreshlyLootedCrate();
            Assume.That(page.getItemCount(), Is.EqualTo(2), "fixture: the crate should start with rolled loot");

            WorldSave.RestorePageForTest(page, SavedContents());

            Assert.That(page.getItemCount(), Is.EqualTo(1),
                "the save is authoritative: 1 saved item, not 1 saved + 2 rolled");
            Assert.That(page.items[0].item.id, Is.EqualTo(SteakId), "and it must be the SAVED item that survived");
        }

        // Load twice, as a session does. Without the clear this grows every time, which is the actual failure
        // mode -- it is not visible in one load, only in the eighth.
        [Test]
        public void RepeatedLoadsAreIdempotent()
        {
            var page = FreshlyLootedCrate();
            for (int i = 0; i < 8; i++) WorldSave.RestorePageForTest(page, SavedContents());
            Assert.That(page.getItemCount(), Is.EqualTo(1),
                "eight loads of a one-item save must still be one item; it measured 44 per 48-slot crate in the wild");
        }

        // The slot grid has to be cleared too, not just the item list. If clear() emptied `items` while leaving
        // `slots` marked, the restored item would find its cell occupied and vanish -- a silent data-loss fix
        // that looks exactly like a working one from the item count alone.
        [Test]
        public void ARestoredItemLandsOnTheCellTheSaveNames()
        {
            var page = FreshlyLootedCrate();
            WorldSave.RestorePageForTest(page, SavedContents());
            var jar = page.getItem(4, 4);
            Assert.That(jar, Is.Not.Null, "the saved item must occupy the cell its save names, not be dropped");
            Assert.That(jar.item.id, Is.EqualTo(SteakId));
        }

        // The 0x0 guard: a save that does not know its own page size must not be allowed to shrink the page,
        // but after a clear() its slot grid still has to be reset or the restore silently drops everything.
        // This is the case the else-branch in RestorePage exists for.
        [Test]
        public void ASaveWithUnknownSizeStillRestoresIntoTheExistingGrid()
        {
            var page = FreshlyLootedCrate();
            var ps = SavedContents();
            ps.Width = 0; ps.Height = 0;   // "the save does not know", not "the page is empty"

            WorldSave.RestorePageForTest(page, ps);

            Assert.That(page.width, Is.EqualTo(8), "a 0x0 save must not shrink the page");
            Assert.That(page.height, Is.EqualTo(6));
            Assert.That(page.getItemCount(), Is.EqualTo(1), "and the saved item must still land");
        }
    }
}
