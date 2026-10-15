using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    public sealed partial class ServerTransactions
    {
        // One bounded entry per connected owner. ReliableOrdered already rejects
        // network duplicates; this also makes deliberate repeated request IDs
        // idempotent and prevents a delayed UI request spending a second round.
        readonly Dictionary<ushort, (MagazineTransferCommand Command, MagazineTransferEvent Result)> _magazineRequests = new();
        internal void ForgetMagazineTransfers(ushort owner) => _magazineRequests.Remove(owner);

        void OnMagazineTransfer(ushort sender, MagazineTransferCommand cmd)
        {
            void Send(MagazineTransferEvent result) => _sendTo(sender,
                NetMessagePak.Pack(ReplicationIds.EventMagazineTransfer, result.Write));
            var reply = new MagazineTransferEvent { RequestId = cmd.RequestId, MagazineToken = cmd.MagazineToken,
                Tick = (uint)_tick() };
            if (_magazineRequests.TryGetValue(sender, out var old) && unchecked((int)(cmd.RequestId - old.Command.RequestId)) <= 0)
            {
                if (cmd.Equals(old.Command)) Send(old.Result);
                else { reply.Result = MagazineTransferResult.StaleRequest; Send(reply); }
                return;
            }
            var inventory = SenderInventory(sender);
            Item Exact(EquipmentAddress address, ulong token)
            {
                var page = inventory.items[address.Page]; byte index = page.getIndex(address.X, address.Y);
                var jar = index == byte.MaxValue ? null : page.getItem(index);
                return jar?.item?.id == address.Id && jar.x == address.X && jar.y == address.Y
                    && _inventories.ItemIdentityProvider?.Invoke(jar.item) == token ? jar.item : null;
            }
            if (inventory == null) reply.Result = MagazineTransferResult.MissingInventory;
            else if (!_combat.IsAlive(sender)) reply.Result = MagazineTransferResult.Dead;
            else if (Exact(cmd.Magazine, cmd.MagazineToken) == null) reply.Result = MagazineTransferResult.StaleMagazine;
            else if (!cmd.Unloading && Exact(cmd.Round, cmd.RoundToken) == null) reply.Result = MagazineTransferResult.StaleRound;
            else reply.Result = ApplyMagazineTransfer(sender, cmd.Legacy, out reply.Amount);
            if (reply.Applied) { Diag.MagLoadsApplied++; _inventories.ServerMarkDirty(sender); }
            else { reply.Amount = 0; Diag.MagLoadsRejected++; }
            _magazineRequests[sender] = (cmd, reply);
            Send(reply);
        }
    }
}
