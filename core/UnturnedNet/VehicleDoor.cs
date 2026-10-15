using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // v70, command78: driver key or focused on-foot intent. Neither carries the
    // desired phase: the original authoritative toggle may reject a moving door.
    public struct VehicleDoorCommand
    {
        public uint NetId;
        public bool Driver;
        public Vector3 Origin, Direction;
        public bool IsValid => NetId != 0 && (Driver || (MountedWire.Finite(Origin)
            && MountedWire.Finite(Direction) && Math.Abs(Direction.sqrMagnitude - 1) < .002f));
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid folding door intent");
            w.WriteUInt32(NetId); w.WriteBit(Driver);
            if (!Driver) { MountedWire.WriteVector(w, Origin); MountedWire.WriteVector(w, Direction); }
        }
        public static bool TryRead(NetPakReader r, out VehicleDoorCommand value)
        {
            value = default; var next = new VehicleDoorCommand();
            if (!r.ReadUInt32(out next.NetId) || !r.ReadBit(out next.Driver)) return false;
            if (!next.Driver && (!MountedWire.ReadVector(r, out next.Origin) || !MountedWire.ReadVector(r, out next.Direction))) return false;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }
    public struct VehicleDoorEvent
    {
        public uint NetId, Tick;
        public bool Open;
        public Vector3 Position;
        public bool IsValid => NetId != 0 && MountedWire.Finite(Position);
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid folding door fact");
            w.WriteUInt32(NetId); w.WriteUInt32(Tick); w.WriteBit(Open); MountedWire.WriteVector(w, Position);
        }
        public static bool TryRead(NetPakReader r, out VehicleDoorEvent value)
        {
            value = default; var next = new VehicleDoorEvent();
            if (!r.ReadUInt32(out next.NetId) || !r.ReadUInt32(out next.Tick)
                || !r.ReadBit(out next.Open) || !MountedWire.ReadVector(r, out next.Position) || !next.IsValid) return false;
            value = next; return true;
        }
    }
    public sealed partial class VehicleReplication
    {
        public void ServerPublishDoor(NetId id, bool present, bool open, float fold, long tick)
        {
            if (!float.IsFinite(fold) || fold < 0 || fold > 1 || (!present && (open || fold != 0)))
                throw new ArgumentException("Invalid folding door state");
            if (!_vehicles.TryGet(id, out var e) || (e.HasBiFoldDoor == present && e.DoorOpen == open && e.DoorFold == fold)) return;
            e.HasBiFoldDoor = present; e.DoorOpen = open; e.DoorFold = fold; e.LastChangedTick = tick;
        }
    }
    public sealed partial class NetWorldServer
    {
        public Func<ushort, VehicleDoorCommand, bool> VehicleDoorAllowed;
        public Action<ushort, VehicleDoorCommand> VehicleDoorRequested;
        void RegisterVehicleDoorCommands() => Commands.Register<VehicleDoorCommand>(ReplicationIds.CommandVehicleDoor,
            VehicleDoorCommand.TryRead, (owner, command) => VehicleDoorRequested(owner, command),
            (owner, command) => VehicleDoorRequested != null && CombatState.IsAlive(owner) && VehicleDoorAllowed?.Invoke(owner, command) == true);
    }
    public sealed partial class NetWorldClient
    {
        public event Action<VehicleDoorEvent> VehicleDoor;
        void RegisterVehicleDoorEvents() => Events.Register<VehicleDoorEvent>(ReplicationIds.EventVehicleDoor,
            VehicleDoorEvent.TryRead, value => VehicleDoor?.Invoke(value));
        public bool SendVehicleDoor(VehicleDoorCommand command) => State == NetSessionState.Connected && command.IsValid
            && SendCommand(ReplicationIds.CommandVehicleDoor, command.Write);
    }
}
