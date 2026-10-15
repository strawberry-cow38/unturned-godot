using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // v69: source mechanics-panel repairs are free and indexed. They are distinct
    // from the existing spare-consuming FitTire command, which repairs first-flat.
    public enum VehicleMaintenanceAction : byte { RepairGlass, RepairTire, Horn }
    public struct VehicleMaintenanceCommand
    {
        public uint NetId;
        public VehicleMaintenanceAction Action;
        public byte Index;
        public bool IsValid => NetId != 0 && Action switch {
            VehicleMaintenanceAction.RepairGlass => Index < 18,
            VehicleMaintenanceAction.RepairTire => Index < 8,
            VehicleMaintenanceAction.Horn => Index == 0,
            _ => false };
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid vehicle maintenance intent");
            w.WriteUInt32(NetId); w.WriteUInt8((byte)Action); w.WriteUInt8(Index);
        }
        public static bool TryRead(NetPakReader r, out VehicleMaintenanceCommand value)
        {
            value = default; var next = new VehicleMaintenanceCommand();
            if (!r.ReadUInt32(out next.NetId) || !r.ReadUInt8(out byte kind) || !r.ReadUInt8(out next.Index)) return false;
            next.Action = (VehicleMaintenanceAction)kind;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }
    public enum VehicleMaintenanceKind : byte { GlassBroken, GlassRepaired, TireRepaired, ManualHorn }
    public struct VehicleMaintenanceEvent
    {
        public uint NetId, Tick;
        public VehicleMaintenanceKind Kind;
        public byte Index;
        public Vector3 Position, Normal, HalfSize;
        public bool IsValid => NetId != 0 && MountedWire.Finite(Position) && Kind switch {
            VehicleMaintenanceKind.GlassBroken => Index < 18 && MountedWire.Finite(Normal)
                && Math.Abs(Normal.sqrMagnitude - 1) < .002f && MountedWire.Finite(HalfSize)
                && HalfSize.x >= .2f && HalfSize.y >= .2f && HalfSize.z >= .2f
                && HalfSize.x <= 100 && HalfSize.y <= 100 && HalfSize.z <= 100,
            VehicleMaintenanceKind.GlassRepaired => Index < 18,
            VehicleMaintenanceKind.TireRepaired => Index < 8,
            VehicleMaintenanceKind.ManualHorn => Index == 255,
            _ => false };
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid accepted vehicle maintenance fact");
            w.WriteUInt32(NetId); w.WriteUInt32(Tick); w.WriteUInt8((byte)Kind); w.WriteUInt8(Index);
            MountedWire.WriteVector(w, Position);
            if (Kind == VehicleMaintenanceKind.GlassBroken) { MountedWire.WriteVector(w, Normal); MountedWire.WriteVector(w, HalfSize); }
        }
        public static bool TryRead(NetPakReader r, out VehicleMaintenanceEvent value)
        {
            value = default; var next = new VehicleMaintenanceEvent();
            if (!r.ReadUInt32(out next.NetId) || !r.ReadUInt32(out next.Tick) || !r.ReadUInt8(out byte kind)
                || !r.ReadUInt8(out next.Index) || !MountedWire.ReadVector(r, out next.Position)) return false;
            next.Kind = (VehicleMaintenanceKind)kind;
            if (next.Kind == VehicleMaintenanceKind.GlassBroken
                && (!MountedWire.ReadVector(r, out next.Normal) || !MountedWire.ReadVector(r, out next.HalfSize))) return false;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }
    public sealed partial class VehicleReplication
    {
        public const uint GlassBits = (1u << 18) - 1;
        public void ServerPublishMaintenance(NetId id, uint panes, uint broken, byte tires, long tick)
        {
            if (panes > GlassBits || (broken & ~panes) != 0) throw new ArgumentException("Invalid vehicle pane state");
            if (!_vehicles.TryGet(id, out var e)) return;
            if (e.GlassMask == panes && e.BrokenGlassMask == broken && e.TireMask == tires) return;
            e.GlassMask = panes; e.BrokenGlassMask = broken; e.TireMask = tires; e.LastChangedTick = tick;
        }
    }
    public sealed partial class NetWorldServer
    {
        public Func<ushort, VehicleMaintenanceCommand, bool> VehicleMaintenanceAllowed;
        public Action<ushort, VehicleMaintenanceCommand> VehicleMaintenanceRequested;
        void RegisterVehicleMaintenanceCommands() => Commands.Register<VehicleMaintenanceCommand>(ReplicationIds.CommandVehicleMaintenance,
            VehicleMaintenanceCommand.TryRead, (owner, cmd) => VehicleMaintenanceRequested(owner, cmd),
            (owner, cmd) => VehicleMaintenanceRequested != null && CombatState.IsAlive(owner) && VehicleMaintenanceAllowed?.Invoke(owner, cmd) == true);
    }
    public sealed partial class NetWorldClient
    {
        public event Action<VehicleMaintenanceEvent> VehicleMaintenance;
        void RegisterVehicleMaintenanceEvents() => Events.Register<VehicleMaintenanceEvent>(ReplicationIds.EventVehicleMaintenance,
            VehicleMaintenanceEvent.TryRead, value => VehicleMaintenance?.Invoke(value));
        public bool SendVehicleMaintenance(VehicleMaintenanceCommand command)
        {
            if (State != NetSessionState.Connected || !command.IsValid) return false;
            return SendCommand(ReplicationIds.CommandVehicleMaintenance, command.Write);
        }
    }
}
