using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public enum MountedFlightKind : byte { Continue, Retire, Blast }
    public enum MountedFlightRetirement : byte { Impact, Expired }

    /// <summary>Accepted presentation only. Launch identity already arrives in
    /// MountedShot; these facts carry source collision/retirement and real blasts.
    /// There is deliberately no command or damage API on the receiving side.</summary>
    public struct MountedFlightEvent
    {
        public MountedFlightKind Kind;
        public uint LaunchTick, Tick;
        public ushort Sequence, RemainingSteps;
        public Vector3 Position, Velocity;
        public byte Pierced;
        public MountedFlightRetirement Reason;
        public PlayerFiredEvent Shot;
        public float Radius;
        public bool IsValid => Kind <= MountedFlightKind.Blast
            && unchecked((int)(Tick-LaunchTick)) is >= 0 and <= ushort.MaxValue
            && (Kind == MountedFlightKind.Blast
                ? MountedWire.ValidShot(Shot) && float.IsFinite(Radius) && Radius > 0 && Radius <= 10000
                : MountedWire.Finite(Position) && MountedWire.Finite(Velocity)
                    && (Kind == MountedFlightKind.Continue
                        ? RemainingSteps > 0 && Pierced is >= 1 and <= 2
                        : Reason <= MountedFlightRetirement.Expired));
        public void Write(NetPakWriter w)
        {
            if(!IsValid)throw new ArgumentException("Invalid mounted flight fact");
            w.WriteUInt8((byte)Kind);w.WriteUInt32(LaunchTick);w.WriteUInt32(Tick);w.WriteUInt16(Sequence);
            if(Kind==MountedFlightKind.Blast){Shot.Write(w);w.WriteFloat(Radius);return;}
            MountedWire.WriteVector(w,Position);MountedWire.WriteVector(w,Velocity);
            if(Kind==MountedFlightKind.Continue){w.WriteUInt16(RemainingSteps);w.WriteUInt8(Pierced);}
            else w.WriteUInt8((byte)Reason);
        }
        public static bool TryRead(NetPakReader r,out MountedFlightEvent value)
        {
            value=default;var next=new MountedFlightEvent();
            if(!r.ReadUInt8(out byte kind)||kind>(byte)MountedFlightKind.Blast||!r.ReadUInt32(out next.LaunchTick)
                ||!r.ReadUInt32(out next.Tick)||!r.ReadUInt16(out next.Sequence))return false;
            next.Kind=(MountedFlightKind)kind;
            if(next.Kind==MountedFlightKind.Blast)
            {if(!PlayerFiredEvent.TryRead(r,out next.Shot)||!r.ReadFloat(out next.Radius))return false;}
            else
            {
                if(!MountedWire.ReadVector(r,out next.Position)||!MountedWire.ReadVector(r,out next.Velocity))return false;
                if(next.Kind==MountedFlightKind.Continue)
                {if(!r.ReadUInt16(out next.RemainingSteps)||!r.ReadUInt8(out next.Pierced))return false;}
                else{if(!r.ReadUInt8(out byte reason))return false;next.Reason=(MountedFlightRetirement)reason;}
            }
            if(!next.IsValid)return false;value=next;return true;
        }
    }
    public sealed partial class NetWorldClient
    {
        public event Action<MountedFlightEvent> MountedFlight;
        void RegisterMountedFlightEvents()=>Events.Register<MountedFlightEvent>(ReplicationIds.EventMountedFlight,
            MountedFlightEvent.TryRead,value=>MountedFlight?.Invoke(value));
    }
}
