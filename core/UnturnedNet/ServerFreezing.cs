using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>Drives Item.frozen everywhere in the world except inside a running cooker (ServerCooking owns
    /// that case, because food being actively heated thaws much faster).
    ///
    /// SERVER-OWNED, and the reason is stronger than it was for cooking: food at 100 % frozen never spoils at
    /// all, so a client permitted to write `frozen` is a client permitted to make its entire stockpile immortal.
    ///
    /// THE RULE IS POSITIONAL, which is what makes it cheap: an item's temperature is decided entirely by which
    /// grid it is sitting in. A freezer compartment freezes, everything else thaws. There is no per-item timer
    /// to keep, no event when something is moved -- dragging a steak out of the freezer changes its fate on the
    /// next tick simply because the sweep now finds it somewhere else.</summary>
    public sealed class ServerFreezing
    {
        readonly InventoryReplication _inventories;

        /// <summary>Is this fridge's compartment actually running? Injected like ServerCooking.HasPower for the
        /// same reason -- the power grid is a game-layer thing. Null reads as powered so a bare test harness
        /// freezes normally rather than silently doing nothing.</summary>
        public System.Func<uint, bool> HasPower;

        public ServerFreezing(InventoryReplication inventories) { _inventories = inventories; }

        /// <summary>One step. Returns the owner ids whose view changed, so the caller can mark them dirty --
        /// mutating a byte on an Item raises no dirty flag by itself (Items.onStateUpdated fires on add / remove
        /// / resize only), the same trap ServerCooking documents at its publish step.</summary>
        public List<ushort> Step(float dt)
        {
            List<ushort> touched = null;
            void Note(ushort owner) { if (owner != 0) (touched ??= new List<ushort>()).Add(owner); }

            // (1) CONTAINERS. A freezer compartment freezes what is in it; a fridge body and every plain crate
            // thaw. Note that a crate the player has OPEN is aliased into their page by CopyPage (same Item
            // references, not clones), so freezing the crate's copy moves the open view too -- see the CopyPage
            // note in ServerCooking for why that aliasing is load-bearing rather than incidental.
            foreach (var crate in _inventories.Crates)
            {
                bool powered = HasPower == null || HasPower(crate.NetIdValue);
                if (crate.HasFreezer && powered && Sweep(crate.Freezer, Freezing.FreezePerSecond, dt)) Note(crate.OpenBy);
                if (crate.HasFreezer && !powered && Sweep(crate.Freezer, -Freezing.ThawPerSecond, dt)) Note(crate.OpenBy);
                // THE BODY: normally the slow-spoil half of a fridge, so it thaws. An ice box has no warm half
                // -- the whole container is the freezer -- so its body freezes instead, on the same power gate
                // as a compartment. Unpowered it thaws like anything else, which is what makes cutting the
                // grid to a stocked freezer a real loss rather than a cosmetic one.
                float bodyRate = crate.BodyFreezes && powered ? Freezing.FreezePerSecond : -Freezing.ThawPerSecond;
                if (Sweep(crate.Storage, bodyRate, dt)) Note(crate.OpenBy);
            }

            // (2) WHAT PLAYERS ARE CARRYING. A frozen steak in a backpack thaws; that is the cost of taking it
            // with you. Only the player's OWN pages -- the STORAGE and FREEZER pages are views onto a container
            // already swept above, and sweeping them again would thaw a freezer's contents at double rate while
            // somebody had the door open.
            foreach (var e in _inventories.Owners)
            {
                bool any = false;
                for (byte p = 0; p < PlayerInventory.OWNPAGES; p++)
                    any |= Sweep(e.Inventory.items[p], -Freezing.ThawPerSecond, dt);
                if (any) Note(e.OwnerPlayerId);
            }

            if (touched != null)
                foreach (var o in touched) _inventories.ServerMarkDirty(o);
            return touched ?? Empty;
        }

        static readonly List<ushort> Empty = new List<ushort>();

        /// <summary>Move every freezable item in one grid toward frozen (positive rate) or thawed (negative).
        /// Returns whether anything actually changed, so a still fridge costs no replication.</summary>
        static bool Sweep(Items page, float perSecond, float dt)
        {
            if (page == null) return false;
            bool changed = false;
            for (byte i = 0; i < page.getItemCount(); i++)
            {
                var item = page.getItem(i)?.item;
                if (item == null) continue;
                // Thawing an already-thawed item is the overwhelmingly common case -- every bullet in every
                // crate in the world -- so it exits before touching the asset table.
                if (perSecond < 0f && item.frozen == 0) continue;
                if (!Freezing.Freezable(Assets.find(item.id))) continue;
                byte before = item.frozen;
                Freezing.AdvanceCarried(item, perSecond, dt);   // carries the sub-percent remainder -- see the note there
                if (item.frozen != before) changed = true;
            }
            return changed;
        }
    }
}
