using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>v80 (fishing): the server-side catch the source never had. PlayerController ran the whole
    /// UseableFisher loop (FishingSim) locally and GrantFish added the fish to its own copy of the bag, which the
    /// next owner-inventory echo overwrote, while the XP was overwritten by the per-tick skills adoption. As retail's
    /// UseableFisher does, the authority now owns the bite timer, the catch window, the catch-challenge minigame and
    /// the reward; the casting client keeps the gauge, the bobber's flight and the presentation.
    ///
    /// Command103 FishingCast: the bobber has landed in fishable water (ReceiveBobberInWaterConfirmation) at this
    /// point, cast at this gauge strength. Command104 FishingInput: primary pressed / released, or the rod put away.
    /// Event78 FishingState, to the caster only: where the server's line now is, and the fish it landed.</summary>
    public struct FishingCastCommand
    {
        public Vector3 Bobber;
        public float Strength;   // 0..1, a byte on the wire
        public void Write(NetPakWriter w)
        {
            NetWire.WritePos(w, Bobber);
            w.WriteUInt8((byte)Math.Round(Math.Clamp(Strength, 0f, 1f) * 255f));
        }
        public static bool TryRead(NetPakReader r, out FishingCastCommand cmd)
        {
            cmd = default;
            if (!NetWire.ReadPos(r, out var bobber) || !r.ReadUInt8(out byte strength)) return false;
            cmd = new FishingCastCommand { Bobber = bobber, Strength = strength / 255f };
            return true;
        }
    }

    public struct FishingInputCommand
    {
        public const byte Press = 0, Release = 1, Stop = 2;
        public byte Kind;
        public void Write(NetPakWriter w) => w.WriteUInt8(Kind);
        public static bool TryRead(NetPakReader r, out FishingInputCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte kind) || kind > Stop) return false;
            cmd = new FishingInputCommand { Kind = kind };
            return true;
        }
    }

    public struct FishingStateEvent
    {
        /// <summary>No line out (reeled in, refused, put away).</summary>
        public const byte Idle = 0;
        /// <summary>The bobber floats; the bite timer runs.</summary>
        public const byte Line = 1;
        /// <summary>A fish has taken the bait: the catch window opens WARNING_DURATION later.</summary>
        public const byte Bite = 2;
        /// <summary>Hooked: the catch-challenge minigame runs on the server.</summary>
        public const byte Challenge = 3;
        public byte State;
        public ushort CaughtItemId;   // the fish just landed (0 = none)
        public void Write(NetPakWriter w) { w.WriteUInt8(State); w.WriteUInt16(CaughtItemId); }
        public static bool TryRead(NetPakReader r, out FishingStateEvent e)
        {
            e = default;
            if (!r.ReadUInt8(out byte state) || state > Challenge || !r.ReadUInt16(out ushort item)) return false;
            e = new FishingStateEvent { State = state, CaughtItemId = item };
            return true;
        }
    }

    public sealed partial class NetWorldClient
    {
        public event Action<FishingStateEvent> FishingState;
        void RegisterFishingEvents() => Events.Register<FishingStateEvent>(ReplicationIds.EventFishingState,
            FishingStateEvent.TryRead, e => FishingState?.Invoke(e));
        public bool SendFishingCast(Vector3 bobber, float strength) =>
            SendCommand(ReplicationIds.CommandFishingCast, new FishingCastCommand { Bobber = bobber, Strength = strength }.Write);
        public bool SendFishingInput(byte kind) =>
            SendCommand(ReplicationIds.CommandFishingInput, new FishingInputCommand { Kind = kind }.Write);
    }
}
