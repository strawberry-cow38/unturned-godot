using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // DOES A GUN REMEMBER ITS ATTACHMENTS ACROSS A HOLSTER? (strawberry: "go through and make sure every gun works
    // with attachments, every gun REMEMBERS attachments, ammo count etc etc".)
    //
    // THE GAP THIS FILLS. gun.reequip_keeps_ammo drives the holster/re-equip round trip and measures AMMO -- it
    // asserts nothing about attachments. gun.attachment_fit asserts installed state on the ITEM (SetInstalledId ->
    // InstalledId, and two guns holding their own independently), which is the storage layer, not the round trip.
    // So the exact thing a player does -- put a scoped gun away, pull it out again -- was covered on one axis and
    // not the other, and the two look identical in a pass count.
    //
    // The failure mode is the same shape gun.reequip_keeps_ammo was written for: RestoreGunState early-outs on a
    // missing/blank backing item and leaves the gun on LoadGun's fresh defaults. For ammo that reads as a free
    // reload; for attachments it would read as the scope falling off in your pocket.
    public sealed class AttachmentPersistTests : GameTest
    {
        public override string Name => "gun.reequip_keeps_attachments";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            yield return Ticks(2);

            var gunItem = new Item(4);                 // eaglefire: hooks barrel + sight, and a real magazine
            p.Inventory.items[0].tryAddItem(gunItem);
            var stored = p.Inventory.items[0].getItem(0);
            T.Check("an eaglefire is in the primary slot", stored != null && stored.item != null);
            if (stored?.item == null) yield break;

            p.EquipHotbar(1);
            yield return Ticks(2);
            T.Check($"it equips ({p.HeldGunName})", p.HasGunOut);

            // Install a sight and a barrel ON THE BACKING ITEM, which is where installed state lives.
            // The EQUIP path must link the backing item by itself. Deliberately NOT calling DebugSetHeldItem:
            // forcing the link would be me supplying the very thing under test, and the render path reads
            // _heldItem, so a re-equip that fails to re-link renders a BARE gun while the item still holds a
            // scope -- which is the player-visible bug and is invisible if you only ever inspect the item.
            T.Check($"equipping links the backing item ({(p.HeldItemForTest != null ? "linked" : "null")})",
                p.HeldItemForTest != null);

            AttachmentFit.SetInstalledId(stored.item, "Sight", 5);    // eaglefire irons
            AttachmentFit.SetInstalledId(stored.item, "Barrel", 7);
            yield return Ticks(2);
            T.Check($"sight + barrel install (sight {AttachmentFit.InstalledId(stored.item, "Sight")}, barrel {AttachmentFit.InstalledId(stored.item, "Barrel")})",
                AttachmentFit.InstalledId(stored.item, "Sight") == 5 && AttachmentFit.InstalledId(stored.item, "Barrel") == 7);

            // HOLSTER by re-pressing the same hotbar key -- the exact path strawberry's ammo bug used.
            p.EquipHotbar(1);
            yield return Ticks(3);
            T.Check($"the gun is away ({p.HasGunOut})", !p.HasGunOut);

            // ...and back out.
            p.EquipHotbar(1);
            yield return Ticks(3);
            T.Check($"it comes back out ({p.HeldGunName})", p.HasGunOut);

            // THE CLAIM, read through the LIVE GUN rather than the item. Attachments live only on the item and
            // RestoreGunState never touches them, so "the item still holds what I wrote to it" is very nearly a
            // tautology -- it would pass even if re-equip dropped the link entirely. What the renderer actually
            // reads is _heldItem (MountBody3PAttachments), so that is what gets asserted.
            var live = p.HeldItemForTest;
            T.Check($"the re-equipped gun is linked to a backing item ({(live != null ? "linked" : "NULL -- renders bare")})",
                live != null);
            var after = live;
            T.Check($"sight SURVIVED the holster (id {(after != null ? AttachmentFit.InstalledId(after, "Sight") : -99)}, want 5)",
                after != null && AttachmentFit.InstalledId(after, "Sight") == 5);
            T.Check($"barrel SURVIVED the holster (id {(after != null ? AttachmentFit.InstalledId(after, "Barrel") : -99)}, want 7)",
                after != null && AttachmentFit.InstalledId(after, "Barrel") == 7);

            // THE CONTROL, and it is the reason this test can fail honestly. A slot that was NEVER filled must still
            // read empty afterwards -- if re-equip fabricated defaults into every slot, the two checks above would
            // pass for the wrong reason and this one catches it.
            T.Check($"...and a slot never filled is still empty (grip {(after != null ? AttachmentFit.InstalledId(after, "Grip") : -99)})",
                after != null && AttachmentFit.InstalledId(after, "Grip") == -1);

            yield break;
        }
    }
}
