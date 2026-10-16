using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public sealed partial class ServerTransactions
    {
        // Both exact routes share a single monotonic ledger. Switching between
        // an owned and viewed magazine cannot replay an already spent request.
        readonly record struct MagazineRequest(uint RequestId, ScopedInventoryAddress Magazine,
            ScopedInventoryAddress Round, bool Unloading, ushort UnloadRoundId)
        {
            public MagLoadCommand Legacy => new() { MagPage = Magazine.Page, MagX = Magazine.X, MagY = Magazine.Y,
                MagId = Magazine.Id, RoundPage = Round.Page, RoundX = Round.X, RoundY = Round.Y,
                RoundId = Unloading ? UnloadRoundId : Round.Id, Unloading = Unloading };
        }
        readonly Dictionary<ushort, (MagazineRequest Command, MagazineTransferEvent Result)> _magazineRequests = new();
        internal void ForgetMagazineTransfers(ushort owner) => _magazineRequests.Remove(owner);

        void OnMagazineTransfer(ushort sender, MagazineTransferCommand cmd) => ProcessMagazineTransfer(sender,
            new MagazineRequest(cmd.RequestId, ScopedInventoryAddress.Owned(cmd.Magazine, cmd.MagazineToken),
                cmd.Unloading ? default : ScopedInventoryAddress.Owned(cmd.Round, cmd.RoundToken), cmd.Unloading,
                cmd.Unloading ? cmd.Round.Id : (ushort)0));

        void ProcessMagazineTransfer(ushort sender, MagazineRequest cmd)
        {
            void Send(MagazineTransferEvent result) => _sendTo(sender,
                NetMessagePak.Pack(ReplicationIds.EventMagazineTransfer, result.Write));
            var reply = new MagazineTransferEvent { RequestId = cmd.RequestId, MagazineToken = cmd.Magazine.Token,
                Tick = (uint)_tick() };
            if (_magazineRequests.TryGetValue(sender, out var old) && unchecked((int)(cmd.RequestId - old.Command.RequestId)) <= 0)
            {
                if (cmd.Equals(old.Command)) Send(old.Result);
                else { reply.Result = MagazineTransferResult.StaleRequest; Send(reply); }
                return;
            }
            Vector3? position = TryGetSenderPos(sender, out var at) ? at : null;
            if (SenderInventory(sender) == null) reply.Result = MagazineTransferResult.MissingInventory;
            else if (!_combat.IsAlive(sender)) reply.Result = MagazineTransferResult.Dead;
            else if (_inventories.ServerResolveScoped(sender, cmd.Magazine, position) == null) reply.Result = MagazineTransferResult.StaleMagazine;
            else if (!cmd.Unloading && _inventories.ServerResolveScoped(sender, cmd.Round, position) == null) reply.Result = MagazineTransferResult.StaleRound;
            else reply.Result = ApplyMagazineTransfer(sender, cmd.Legacy, out reply.Amount);
            if (reply.Applied)
            {
                Diag.MagLoadsApplied++;
                _inventories.ServerMarkDirty(sender);
                // Direct amount/cartridge writes have no grid event. Repaint
                // all viewers after the complete shared MagRules mutation.
                if (cmd.Magazine.CrateId != 0) _inventories.ServerRepaintCrateViewers(cmd.Magazine.CrateId);
                if (cmd.Round.CrateId != 0 && cmd.Round.CrateId != cmd.Magazine.CrateId)
                    _inventories.ServerRepaintCrateViewers(cmd.Round.CrateId);
            }
            else { reply.Amount = 0; Diag.MagLoadsRejected++; }
            _magazineRequests[sender] = (cmd, reply);
            Send(reply);
        }
    }
}
