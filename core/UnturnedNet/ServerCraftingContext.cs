using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public sealed partial class NetWorldServer
    {
        /// <summary>Host scene adapter for original eye-radius and line-of-sight
        /// station tags. Null preserves the old no-station host boundary.</summary>
        public Func<ushort, Vector3, ICollection<string>> CraftingStationsAt;
        /// <summary>Host camera-origin envelope for source vehicle chase views.
        /// Null uses the existing bounded on-foot eye admission.</summary>
        public Func<ushort, Vector3, bool> CraftingEyeAllowed;
        readonly Dictionary<ushort, (CraftingContextCommand Request, CraftingContextEvent Reply)> _craftingContexts = new();
        void ForgetCraftingContext(ushort owner) => _craftingContexts.Remove(owner);
        void RegisterCraftingContextCommands() => Commands.Register<CraftingContextCommand>(ReplicationIds.CommandCraftingContext,
            CraftingContextCommand.TryRead, HandleCraftingContext);
        void HandleCraftingContext(ushort owner, CraftingContextCommand command)
        {
            void Send(CraftingContextEvent reply) => SendEventTo(owner,
                NetMessagePak.Pack(ReplicationIds.EventCraftingContext, reply.Write));
            var result = new CraftingContextEvent { RequestId = command.RequestId, Tick = (uint)Session.CurrentTick };
            if (_craftingContexts.TryGetValue(owner, out var old)
                && unchecked((int)(command.RequestId - old.Request.RequestId)) <= 0)
            {
                if (command.Equals(old.Request)) Send(old.Reply);
                else { result.Result = CraftingContextResult.StaleRequest; Send(result); }
                return;
            }
            if (!CombatState.IsAlive(owner)) result.Result = CraftingContextResult.Dead;
            else if (!Players.TryGetByOwner(owner, out var player)
                || !(CraftingEyeAllowed?.Invoke(owner,command.Eye)
                    ?? (command.Eye-player.Pos).sqrMagnitude <= Combat.DefaultGun.MaxAimOriginOffset*Combat.DefaultGun.MaxAimOriginOffset))
                result.Result = CraftingContextResult.InvalidEye;
            else
            {
                var tags = CraftingStationsAt?.Invoke(owner, command.Eye);
                if (tags == null) result.Result = CraftingContextResult.Unavailable;
                else
                {
                    result.Tags = tags.Distinct(StringComparer.Ordinal).OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
                    result.Result = CraftingContextResult.Ready;
                    if (!result.IsValid) { result.Tags = null; result.Result = CraftingContextResult.Unavailable; }
                    else if (command.Action == CraftingContextAction.Craft)
                        result.Result = Transactions.TryCraftWithStations(owner, command.BlueprintIndex, result.Tags)
                            ? CraftingContextResult.Crafted : CraftingContextResult.CraftRejected;
                }
            }
            _craftingContexts[owner] = (command, result);
            Send(result);
        }
    }
}
