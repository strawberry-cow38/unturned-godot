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
                    // v42: GESTURES. Resolved HERE because this is where the stance and the held item already
                    // are -- ServerGestures parks the request and this decides it. Only LOOPING gestures reach
                    // the entity (a one-shot wave latched into a state block would leave the puppet waving
                    // forever), and the Broadcast filter keeps INVENTORY_START off the wire so nobody watches
                    // you rummage.
                    if (ServerGestures.TryTakePending(pid, out var wantGesture))
                        changed |= SetB(ref ce.Gesture,
                                        ServerGestures.Resolve(ce.Gesture, wantGesture,
                                                               (SDG.Unturned.EPlayerStance)mi.Stance,
                                                               mi.HeldItemId != 0));
                    changed |= SetBool(ref ce.WornLightOn, mi.WornLight);   // their lamps, so other clients can light the lens
                    changed |= SetBool(ref ce.HeldLightOn, mi.HeldLight);
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

        /// <summary>An installed attachment id for the wire, where 0 means NOTHING FITTED.
        ///
        /// ⚠ -1 IS THE SENTINEL, NOT 0. Item.gunSightId/gunBarrelId/gunGripId/gunTacticalId all default to -1
        /// (ItemAsset.cs:154), and this used to cast that straight to ushort -- so an unfitted slot went over the
        /// wire as 65535. That is not merely a wrong number: AttachmentFit.MountOn gates on `id > 0`, so 65535
        /// passes the gate, MeshFor(65535) finds nothing, and a remote player's gun renders with NO iron sight
        /// instead of falling back to its factory one. The LOCAL path never showed it, because there the int -1
        /// fails `> 0` correctly and only the cast to ushort turns it into a positive.
        ///
        /// Caught by mp.attachments_replicate on its first ever execution -- the test shipped in the same commit
        /// as the bug and had never run.</summary>
        static ushort AttId(Item gun, string slot)
        {
            if (gun == null) return 0;
            int id = AttachmentFit.InstalledId(gun, slot);
            return id > 0 ? (ushort)id : (ushort)0;
        }
        static bool SetU(ref ushort field, ushort val) { if (field == val) return false; field = val; return true; }
        static bool SetB(ref byte field, byte val) { if (field == val) return false; field = val; return true; }
        static bool SetBool(ref bool field, bool val) { if (field == val) return false; field = val; return true; }
    }
}
