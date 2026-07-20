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
    // HELD-GUN id is DEFERRED: it needs PlayerStateCommand.HeldItemId (a separate v11 gap the protocol note
    // reserves) -- until that lands, HeldId stays 0 (a joiner's avatar shows the clothing, not a held weapon).
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
                    changed |= SetB(ref ce.Stance, (byte)mi.Stance);

                if (changed) _server.CombatState.MarkDirty(ce, tick);
            }
        }

        static ushort Id(Item it) => it?.id ?? (ushort)0;
        static bool SetU(ref ushort field, ushort val) { if (field == val) return false; field = val; return true; }
        static bool SetB(ref byte field, byte val) { if (field == val) return false; field = val; return true; }
    }
}
