using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SWAPPING, NOT STACKING (master: "do not allow attaching multiple of the same attachment on a gun, should swap
    // between the ones we select. with magazines, show the icon vertically, show the rounds in each mag").
    //
    // Two bugs sat behind that sentence, and both DESTROY PLAYER ITEMS silently:
    //
    //  1. The install path returned the outgoing attachment to the bag only when the incoming one had a DIFFERENT id
    //     (`prev != a.id`). Swapping one magazine for another of the same type therefore consumed one from the bag
    //     and gave nothing back -- a magazine deleted per swap, with no message and no way to notice except counting.
    //  2. The ring drew one icon per physical magazine, but consuming took ANY item of that id. So clicking your
    //     30/30 could hand the gun your 12/30, which reads in game as "the ammo counter is wrong" rather than as an
    //     inventory bug.
    //
    // Both are invisible in a screenshot and neither throws. The counting is the test.
    public sealed class AttachmentSwapTests : GameTest
    {
        public override string Name => "attach.swap_not_stack";

        // Two magazines of the SAME id holding different rounds -- the case that separates "an item" from "an id".
        static PlayerInventory BagWith(params (ushort Id, byte Amount)[] items)
        {
            var inv = new PlayerInventory();
            foreach (var (id, amt) in items) inv.tryAddItem(new Item(id) { amount = amt });
            return inv;
        }

        static int CountOf(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (pg.getItem(i)?.item?.id == id) n++;
            }
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // Assets.find returns nothing without it, and every check below then passes vacuously on a stub

            // A real magazine id, so Fits() sees a genuine MAGAZINE asset with a real magCaliber rather than a stub
            // that would pass the filter for the wrong reason.
            ushort magId = 0;
            int cal = 0;
            for (ushort id = 1; id < 2000 && magId == 0; id++)
            {
                var a = Assets.find(id);
                if (a != null && a.IsMagazine && a.type == EItemType.MAGAZINE && a.magCaliber > 0) { magId = id; cal = a.magCaliber; }
            }
            T.Check($"found a real magazine asset to test with (id {magId}, caliber {cal})", magId != 0);
            if (magId == 0) yield break;

            // ---- PER-INSTANCE, NOT PER-ID. Two of the same magazine with different rounds must surface as two
            // entries carrying their own amounts -- otherwise the ring is offering a choice the player cannot make.
            var inv = BagWith((magId, 30), (magId, 12));
            var inst = AttachmentFit.InBagInstances(inv, "Magazine", cal);
            T.Check($"two magazines of one id surface as TWO entries ({inst.Count})", inst.Count == 2);
            var amounts = new List<int>();
            foreach (var e in inst) amounts.Add(e.Item.amount);
            amounts.Sort();
            T.Check($"...each carrying its OWN round count ({string.Join(", ", amounts)})",
                amounts.Count == 2 && amounts[0] == 12 && amounts[1] == 30);
            // The old collapsing view is what this replaces -- kept so the difference is stated, not implied.
            var collapsed = AttachmentFit.InBag(inv, "Magazine", cal);
            T.Check($"...where the by-id view sees only one kind ({collapsed.Count} entry, count {(collapsed.Count > 0 ? collapsed[0].Count : 0)})",
                collapsed.Count == 1 && collapsed[0].Count == 2);

            // ---- THE SWAP, DRIVEN THROUGH THE REAL CODE.
            //
            // This used to re-implement the menu's sequence inline, under a comment saying "the swap as the menu
            // performs it". That is a mirror, not a test: it agrees with the production path by construction and
            // cannot disagree with it, which is why it sat green over a DUPLICATION bug for as long as that bug
            // existed. AttachmentMenu.FitAttachmentTo is now callable, so these checks drive the thing that ships.
            var gun = new Item(1);
            AttachmentFit.SetInstalledId(gun, "Magazine", magId);       // one already fitted
            int before = CountOf(inv, magId);
            var clicked = AttachmentFit.InBagInstances(inv, "Magazine", cal)[0].Item;
            bool fitted = AttachmentMenu.FitAttachmentTo(gun, inv, "Magazine", magId, clicked, out _);
            int after = CountOf(inv, magId);
            T.Check($"a same-id swap succeeds and keeps the count level ({before} -> {after})",
                fitted && after == before);
            T.Check("...with the clicked magazine now on the gun",
                AttachmentFit.InstalledId(gun, "Magazine") == magId);

            // ---- THE DUPE. The consume can fail legitimately: the clicked instance is gone because the bag
            // changed under the ring (moving ANY item is enough). The old order gave the outgoing attachment back
            // BEFORE that consume, then returned early -- leaving it on the gun AND in the bag. One item, two
            // places, and the next inventory refresh drew the extra copy. Reported by strawberry as "things
            // reappear when I move another item around".
            var inv3 = BagWith((magId, 30));
            var gun3 = new Item(1);
            AttachmentFit.SetInstalledId(gun3, "Magazine", magId);
            var stale = new Item(magId) { amount = 30 };   // NOT in the bag: exactly what a stale ring hands over
            int b3 = CountOf(inv3, magId);
            bool ok3 = AttachmentMenu.FitAttachmentTo(gun3, inv3, "Magazine", magId, stale, out _);
            int a3 = CountOf(inv3, magId);
            T.Check($"a swap whose clicked item vanished is REFUSED (returned {ok3})", !ok3);
            T.Check($"...and gives nothing back, so nothing is duplicated ({b3} -> {a3})", a3 == b3);
            T.Check("...leaving the gun exactly as it was",
                AttachmentFit.InstalledId(gun3, "Magazine") == magId);

            // ---- ROUNDS ARE PRESERVED PER OBJECT. tryAddItem must not flatten amount to 1, or every magazine
            // returned from a gun comes back empty and the player is quietly robbed of ammo instead of a magazine.
            var inv2 = BagWith((magId, 17));
            var got = AttachmentFit.InBagInstances(inv2, "Magazine", cal);
            T.Check($"a magazine keeps its rounds through the bag ({(got.Count > 0 ? got[0].Item.amount : -1)})",
                got.Count == 1 && got[0].Item.amount == 17);

            // ---- ONLY MAGAZINES GET THE VERTICAL/ROUNDS TREATMENT. A sight has no rounds; showing "1" on it would
            // be nonsense, and Item.amount defaults to 1 on everything so it is exactly the sort of field that leaks.
            T.Check("magazine is the only slot that reports rounds", AttachmentFit.TypeFor("Magazine") == EItemType.MAGAZINE);
            T.Check("...a sight is not a magazine", AttachmentFit.TypeFor("Sight") != EItemType.MAGAZINE);

            yield break;
        }
    }
}
