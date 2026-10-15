using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public enum VehicleLightingAction : byte { CycleHeadlights, ToggleSiren, SetPattern, RepairLamp }
    public struct VehicleLightingCommand
    {
        public uint NetId;
        public VehicleLightingAction Action;
        // SetPattern: 0..2 or255=off. RepairLamp: canonical label bit0..6.
        public byte Value;
        public bool IsValid => NetId != 0 && Action <= VehicleLightingAction.RepairLamp && Action switch {
            VehicleLightingAction.SetPattern => Value <= 2 || Value == 255,
            VehicleLightingAction.RepairLamp => Value < 7, _ => Value == 0 };
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid vehicle lighting intent");
            w.WriteUInt32(NetId); w.WriteUInt8((byte)Action); w.WriteUInt8(Value);
        }
        public static bool TryRead(NetPakReader r, out VehicleLightingCommand value)
        {
            value = default; var next = new VehicleLightingCommand();
            if (!r.ReadUInt32(out next.NetId) || !r.ReadUInt8(out byte action) || !r.ReadUInt8(out next.Value)) return false;
            next.Action = (VehicleLightingAction)action;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }
    public enum VehicleLightingKind : byte { AlarmHorn, LampBroken, LampRepaired }
    public struct VehicleLightingEvent
    {
        public uint NetId, Tick;
        public VehicleLightingKind Kind;
        public byte Lamp;
        public Vector3 Position;
        public bool IsValid => NetId != 0 && Kind <= VehicleLightingKind.LampRepaired
            && (Kind == VehicleLightingKind.AlarmHorn ? Lamp == 255 : Lamp < 7) && MountedWire.Finite(Position);
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid accepted vehicle lighting fact");
            w.WriteUInt32(NetId); w.WriteUInt32(Tick); w.WriteUInt8((byte)Kind); w.WriteUInt8(Lamp); MountedWire.WriteVector(w, Position);
        }
        public static bool TryRead(NetPakReader r, out VehicleLightingEvent value)
        {
            value = default; var next = new VehicleLightingEvent();
            if (!r.ReadUInt32(out next.NetId) || !r.ReadUInt32(out next.Tick) || !r.ReadUInt8(out byte kind)
                || !r.ReadUInt8(out next.Lamp) || !MountedWire.ReadVector(r, out next.Position)) return false;
            next.Kind = (VehicleLightingKind)kind;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }
    public sealed partial class VehicleReplication
    {
        public void ServerPublishLighting(NetId id, byte flags, float battery, bool highBeam, byte pattern, byte lamps, byte broken, long tick)
        {
            if (pattern > 2 || lamps > 127 || (broken & ~lamps) != 0 || (highBeam && (flags & FlagHeadlights) == 0)
                || !float.IsFinite(battery) || battery < 0) throw new ArgumentException("Invalid vehicle lighting state");
            if (!_vehicles.TryGet(id, out var e)) return;
            float stored = NetQuantization.QuantizeClampedFloat(battery, BatteryIntBits, BatteryFracBits);
            if (e.Flags == flags && e.Battery == stored && e.HighBeam == highBeam && e.LightbarPattern == pattern
                && e.LampMask == lamps && e.BrokenLampMask == broken) return;
            e.Flags = flags; e.Battery = stored; e.HighBeam = highBeam; e.LightbarPattern = pattern;
            e.LampMask = lamps; e.BrokenLampMask = broken; e.LastChangedTick = tick;
        }
    }
    public sealed partial class NetWorldServer
    {
        public Func<ushort, VehicleLightingCommand, bool> VehicleLightingAllowed;
        public Action<ushort, VehicleLightingCommand> VehicleLightingRequested;
        void RegisterVehicleLightingCommands() => Commands.Register<VehicleLightingCommand>(ReplicationIds.CommandVehicleLighting,
            VehicleLightingCommand.TryRead, (owner, cmd) => VehicleLightingRequested(owner, cmd),
            (owner, cmd) => VehicleLightingRequested != null && CombatState.IsAlive(owner) && VehicleLightingAllowed?.Invoke(owner, cmd) == true);
    }
    public sealed partial class NetWorldClient
    {
        public event Action<VehicleLightingEvent> VehicleLighting;
        void RegisterVehicleLightingEvents() => Events.Register<VehicleLightingEvent>(ReplicationIds.EventVehicleLighting,
            VehicleLightingEvent.TryRead, value => VehicleLighting?.Invoke(value));
        public bool SendVehicleLighting(VehicleLightingCommand command)
        {
            if (State != NetSessionState.Connected || !command.IsValid) return false;
            return SendCommand(ReplicationIds.CommandVehicleLighting, command.Write);
        }
    }
}
