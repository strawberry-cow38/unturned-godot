using System;
using System.Collections.Generic;
using SDG.NetPak;
using UnturnedGodot;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public enum CraftingContextAction : byte { Probe, Craft }
    public enum CraftingContextResult : byte { Ready, Crafted, CraftRejected, Unavailable, Dead, InvalidEye, StaleRequest }

    /// <summary>Current source view camera, never a client assertion of station tags.
    /// The craft variant rechecks the same context inside its transaction.</summary>
    public struct CraftingContextCommand
    {
        public uint RequestId;
        public CraftingContextAction Action;
        public ushort BlueprintIndex;
        public Vector3 Eye;
        public bool IsValid => RequestId != 0 && Action <= CraftingContextAction.Craft
            && MountedWire.Finite(Eye) && (Action != CraftingContextAction.Probe || BlueprintIndex == 0);
        public void Write(NetPakWriter writer)
        {
            if (!IsValid) throw new ArgumentException("Invalid crafting context request");
            writer.WriteUInt32(RequestId); writer.WriteUInt8((byte)Action);
            writer.WriteUInt16(BlueprintIndex); MountedWire.WriteVector(writer, Eye);
        }
        public static bool TryRead(NetPakReader reader, out CraftingContextCommand value)
        {
            value = default;
            var next = new CraftingContextCommand();
            if (!reader.ReadUInt32(out next.RequestId) || !reader.ReadUInt8(out byte action)
                || !reader.ReadUInt16(out next.BlueprintIndex) || !MountedWire.ReadVector(reader, out next.Eye)) return false;
            next.Action = (CraftingContextAction)action;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }

    /// <summary>Owner-only access at a particular accepted host tick. Tags keep
    /// source ordinal identity; a refusal never carries usable station access.</summary>
    public struct CraftingContextEvent
    {
        public const int MaxTags = 64;
        public uint RequestId, Tick;
        public CraftingContextResult Result;
        public string[] Tags;
        public bool HasContext => Result <= CraftingContextResult.CraftRejected;
        public bool IsValid
        {
            get
            {
                if (RequestId == 0 || Result > CraftingContextResult.StaleRequest) return false;
                var tags = Tags ?? Array.Empty<string>();
                if (tags.Length > MaxTags || (!HasContext && tags.Length != 0)) return false;
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tag in tags) if (!BlueprintDef.IsValidKey(tag) || !unique.Add(tag)) return false;
                return true;
            }
        }
        public void Write(NetPakWriter writer)
        {
            if (!IsValid) throw new ArgumentException("Invalid crafting context result");
            writer.WriteUInt32(RequestId); writer.WriteUInt32(Tick); writer.WriteUInt8((byte)Result);
            var tags = Tags ?? Array.Empty<string>(); writer.WriteUInt8((byte)tags.Length);
            foreach (var tag in tags) writer.WriteString(tag);
        }
        public static bool TryRead(NetPakReader reader, out CraftingContextEvent value)
        {
            value = default;
            var next = new CraftingContextEvent();
            if (!reader.ReadUInt32(out next.RequestId) || !reader.ReadUInt32(out next.Tick)
                || !reader.ReadUInt8(out byte result) || !reader.ReadUInt8(out byte count) || count > MaxTags) return false;
            next.Result = (CraftingContextResult)result;
            next.Tags = new string[count];
            for (int n = 0; n < count; n++) if (!reader.ReadString(out next.Tags[n])) return false;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }

    public sealed partial class NetWorldClient
    {
        public event Action<CraftingContextEvent> CraftingContext;
        void RegisterCraftingContextEvents() => Events.Register<CraftingContextEvent>(ReplicationIds.EventCraftingContext,
            CraftingContextEvent.TryRead, value => CraftingContext?.Invoke(value));
        public bool SendCraftingContext(uint requestId, Vector3 eye, bool craft = false, ushort blueprintIndex = 0)
        {
            var command = new CraftingContextCommand { RequestId = requestId, Eye = eye,
                Action = craft ? CraftingContextAction.Craft : CraftingContextAction.Probe, BlueprintIndex = blueprintIndex };
            return command.IsValid && SendCommand(ReplicationIds.CommandCraftingContext, command.Write);
        }
    }
}
