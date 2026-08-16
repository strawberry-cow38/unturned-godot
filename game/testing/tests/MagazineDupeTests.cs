using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // MAGAZINES ARE TRACKED TWICE, AND THE TWO RECORDS DISAGREE (strawberry 2026-08-16: "inv dupe still happens
    // with gun magazines").
    //
    // A magazine is the one attachment that is also a live ammo container, so it has two owners:
    //
    //   - the ATTACHMENT system stores "what is in the Magazine slot" on the gun item (AttachmentFit), which is
    //     what the fit menu writes and what the viewmodel draws;
    //   - the RELOAD system tracks `_loadedMagId` + Ammo, which is what a reload, a mag swap and RemoveMagazine
    //     all read, and which equipping a gun initialises to the gun's DEFAULT magazine id.
    //
    // Fitting a magazine through the menu updated only the first. The second still believed the factory magazine
    // was loaded -- so pulling the magazine afterwards handed the player that factory magazine, one they never
    // owned. Net +1 magazine per cycle, out of nothing.
    //
    // Counted, not inspected: "the bag looks right" is exactly what this bug defeats, since every individual
    // magazine in it is a legitimate object. Only the TOTAL is wrong.
    public sealed class MagazineDupeTests : GameTest
    {
        public override string Name => "attach.magazine_no_dupe";

        static int CountOf(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
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
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));   // spawns holding an eaglefire
            yield return Ticks(4);

            T.Check($"player is holding a gun ({p.HeldGunName})", p.HasGunOut);
            T.Check($"...with the factory magazine registered as loaded (HasMagLoaded {p.HasMagLoaded})", p.HasMagLoaded);

            ushort magId = (ushort)(p.Gun?.MagazineId ?? 0);
            T.Check($"the gun declares a default magazine ({magId})", magId != 0);
            if (magId == 0) yield break;

            // Give the player exactly ONE spare magazine and count everything from here.
            p.Inventory.tryAddItem(new Item(magId) { amount = 30 });
            int owned = CountOf(p.Inventory, magId);
            T.Check($"player owns exactly one spare magazine ({owned})", owned == 1);

            // Fit it through the same call the ring's click makes.
            var spare = AttachmentFit.InBagInstances(p.Inventory, "Magazine", Assets.find(magId)?.magCaliber ?? 0);
            T.Check($"the spare surfaces as a fittable magazine ({spare.Count})", spare.Count == 1);
            if (spare.Count == 0) yield break;
            bool fitted = AttachmentMenu.FitAttachmentTo(p.HeldItemForTest, p.Inventory, "Magazine", magId, spare[0].Item, out _);
            T.Check("fitting the spare magazine succeeds", fitted);
            T.Check($"...and it leaves the bag ({CountOf(p.Inventory, magId)})", CountOf(p.Inventory, magId) == 0);

            // NOW PULL IT BACK OUT. The player put one magazine in and takes one magazine out, so they must end
            // holding exactly one. The bug hands back the gun's FACTORY magazine as well, because the reload
            // system's record was never told about the fit.
            p.RemoveMagazine();
            yield return Ticks(2);
            int after = CountOf(p.Inventory, magId);
            T.Check($"one magazine in, one magazine out -- not two ({after})", after == 1);

            yield break;
        }
    }
}
