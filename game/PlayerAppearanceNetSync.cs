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

                if (_server.Inventories.TryGet(pid, out var inv))
                {
                    var pi = inv.Inventory;
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
                }

                if (changed) _server.CombatState.MarkDirty(ce, tick);
            }
        }

        static ushort Id(Item it) => it?.id ?? (ushort)0;
        static bool SetU(ref ushort field, ushort val) { if (field == val) return false; field = val; return true; }
        static bool SetB(ref byte field, byte val) { if (field == val) return false; field = val; return true; }
    }
}
