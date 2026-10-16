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
        // v77: the ledger also holds ground (command88) requests, so the stored
        // command is the exact boxed request value of whichever route sent it.
        readonly Dictionary<ushort, (uint RequestId, object Command, MagazineTransferEvent Result)> _magazineRequests = new();
        internal void ForgetMagazineTransfers(ushort owner) => _magazineRequests.Remove(owner);

        void OnMagazineTransfer(ushort sender, MagazineTransferCommand cmd) => ProcessMagazineTransfer(sender,
            new MagazineRequest(cmd.RequestId, ScopedInventoryAddress.Owned(cmd.Magazine, cmd.MagazineToken),
                cmd.Unloading ? default : ScopedInventoryAddress.Owned(cmd.Round, cmd.RoundToken), cmd.Unloading,
                cmd.Unloading ? cmd.Round.Id : (ushort)0));

        void ProcessMagazineTransfer(ushort sender, MagazineRequest cmd) => ProcessMagazineRequest(sender, cmd.RequestId,
            cmd.Magazine.Token, cmd, () =>
            {
                Vector3? position = TryGetSenderPos(sender, out var at) ? at : null;
                if (SenderInventory(sender) == null) return (MagazineTransferResult.MissingInventory, 0);
                if (!_combat.IsAlive(sender)) return (MagazineTransferResult.Dead, 0);
                if (_inventories.ServerResolveScoped(sender, cmd.Magazine, position) == null) return (MagazineTransferResult.StaleMagazine, 0);
                if (!cmd.Unloading && _inventories.ServerResolveScoped(sender, cmd.Round, position) == null) return (MagazineTransferResult.StaleRound, 0);
                var result = ApplyMagazineTransfer(sender, cmd.Legacy, out ushort amount);
                if (result == MagazineTransferResult.Applied)
                {
                    _inventories.ServerMarkDirty(sender);
                    // Direct amount/cartridge writes have no grid event. Repaint
                    // all viewers after the complete shared MagRules mutation.
                    if (cmd.Magazine.CrateId != 0) _inventories.ServerRepaintCrateViewers(cmd.Magazine.CrateId);
                    if (cmd.Round.CrateId != 0 && cmd.Round.CrateId != cmd.Magazine.CrateId)
                        _inventories.ServerRepaintCrateViewers(cmd.Round.CrateId);
                }
                return (result, amount);
            });

        /// <summary>The shared owner-only completion: one monotonic replay ledger
        /// per connection across commands 81, 85 and 88; exact replay resends the stored result.</summary>
        internal void ProcessMagazineRequest(ushort sender, uint requestId, ulong magazineToken, object command,
            System.Func<(MagazineTransferResult Result, ushort Amount)> apply)
        {
            void Send(MagazineTransferEvent result) => _sendTo(sender,
                NetMessagePak.Pack(ReplicationIds.EventMagazineTransfer, result.Write));
            var reply = new MagazineTransferEvent { RequestId = requestId, MagazineToken = magazineToken, Tick = (uint)_tick() };
            if (_magazineRequests.TryGetValue(sender, out var old) && unchecked((int)(requestId - old.RequestId)) <= 0)
            {
                if (command.Equals(old.Command)) Send(old.Result);
                else { reply.Result = MagazineTransferResult.StaleRequest; Send(reply); }
                return;
            }
            (reply.Result, reply.Amount) = apply();
            if (reply.Applied) Diag.MagLoadsApplied++;
            else { reply.Amount = 0; Diag.MagLoadsRejected++; }
            _magazineRequests[sender] = (requestId, command, reply);
            Send(reply);
        }
    }
}
