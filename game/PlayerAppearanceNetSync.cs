using UnturnedGodot.Net;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Server side of B10 (SP/MP-unify): publishes each player's APPEARANCE (worn clothing + stance) from the
    // server-authoritative state into the combat block (PlayerCombatReplication), so a joiner's RemotePlayers
    // puppets dress correctly. Worn slots come from the server-side per-player inventory (Inventories); stance
    // from the player's held MoveInput (Players). Dirty-only + low cadence (appearance changes slowly), so it
    // costs no delta bytes between changes. Ticked on the world's SimRoot before net.server.replicate.
    //
    // HELD item id: v22 MoveInput.HeldItemId (the client reports what is in its hands with every input) -> ce.HeldId,
    // so a joiner's avatar shows the gun/melee too (RemotePlayers attaches it like the local 3P body does).
    public sealed class PlayerAppearanceNetSync
    {
        public const int PublishDivisorTicks = 10;   // 5 Hz -- clothing/stance change slowly; dirty-only anyway

        readonly NetWorldServer _server;

        public PlayerAppearanceNetSync(NetWorldServer server) { _server = server; }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            if (tick % PublishDivisorTicks != 0) return;

            foreach (var ce in _server.CombatState.All)
            {
                ushort pid = ce.OwnerPlayerId;
                bool changed = false;

                SDG.Unturned.PlayerInventory heldFrom = null;
                if (_server.Inventories.TryGet(pid, out var inv))
                {
                    var pi = inv.Inventory;
                    heldFrom = pi;
                    changed |= SetU(ref ce.WornShirt, Id(pi.wornShirt));
                    changed |= SetU(ref ce.WornPants, Id(pi.wornPants));
                    changed |= SetU(ref ce.WornHat, Id(pi.wornHat));
                    changed |= SetU(ref ce.WornVest, Id(pi.wornVest));
                    changed |= SetU(ref ce.WornMask, Id(pi.wornMask));
                    changed |= SetU(ref ce.WornGlasses, Id(pi.wornGlasses));
                    changed |= SetU(ref ce.WornBackpack, Id(pi.wornBackpack));
                }
                if (_server.Players.TryGetHeldInput(pid, out var mi))
                {
                    changed |= SetB(ref ce.Stance, (byte)mi.Stance);
                    changed |= SetU(ref ce.HeldId, mi.HeldItemId);   // v22: what the player is holding -> other clients' puppets draw the gun/melee
                    // ...and what is bolted to it. Derived SERVER-SIDE from the server's own copy of the player's
                    // inventory rather than asking the client to report three more ids on every input packet: the
                    // attachment state already lives here, and an equipped weapon is in a holster page by
                    // construction (EquipToHandSlot moves it into one), so the id identifies it there.
                    var heldGun = FindEquipped(heldFrom, mi.HeldItemId);
                    changed |= SetU(ref ce.HeldSight, AttId(heldGun, "Sight"));
                    changed |= SetU(ref ce.HeldMagazine, AttId(heldGun, "Magazine"));
                    changed |= SetU(ref ce.HeldBarrel, AttId(heldGun, "Barrel"));
                }

                if (changed) _server.CombatState.MarkDirty(ce, tick);
            }
        }

        static ushort Id(Item it) => it?.id ?? (ushort)0;

        /// <summary>The equipped weapon, found in the holster pages by the id the client reports holding. Null when
        /// it is not in one (nothing equipped, or something held straight out of the bag) -- the puppet then shows
        /// the gun's factory fittings, which is what it did before this existed.</summary>
        static Item FindEquipped(PlayerInventory pi, ushort heldId)
        {
            if (pi == null || heldId == 0) return null;
            for (byte pg = 0; pg < PlayerInventory.SLOTS; pg++)
            {
                var page = pi.items[pg];
                if (page == null) continue;
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var it = page.getItem(i)?.item;
                    if (it != null && it.id == heldId) return it;
                }
            }
            return null;
        }

        /// <summary>The id fitted in a slot, as the wire carries it: 0 means NOTHING FITTED.
        ///
        /// The clamp is the whole function. InstalledId reports an empty slot as -1 (gunBarrelId and friends
        /// default to it), and these fields are ushort -- so a bare cast published 65535 for every slot a gun
        /// did not have filled, on every player, forever. It also feeds the appearance hash, so the wrong
        /// value was being mixed into the dirty check as well as sent.</summary>
        static ushort AttId(Item gun, string slot)
        {
            if (gun == null) return 0;
            int id = AttachmentFit.InstalledId(gun, slot);
            return id > 0 ? (ushort)id : (ushort)0;
        }
        static bool SetU(ref ushort field, ushort val) { if (field == val) return false; field = val; return true; }
        static bool SetB(ref byte field, byte val) { if (field == val) return false; field = val; return true; }
    }
}
