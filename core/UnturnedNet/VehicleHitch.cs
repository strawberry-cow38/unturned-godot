using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // v74: fifth-wheel hitch intent, distinct from Command30/31's rope tow.
    // Cab selection is source registration order on the authority; never client-nominated.
    public struct VehicleHitchCommand
    {
        public uint TrailerNetId;
        public Vector3 Origin, Direction;
        public bool IsValid => TrailerNetId != 0 && MountedWire.Finite(Origin)
            && MountedWire.Finite(Direction) && Math.Abs(Direction.sqrMagnitude - 1) < .002f;
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid hitch intent");
            w.WriteUInt32(TrailerNetId); MountedWire.WriteVector(w, Origin); MountedWire.WriteVector(w, Direction);
        }
        public static bool TryRead(NetPakReader r, out VehicleHitchCommand value)
        {
            value = default; var next = new VehicleHitchCommand();
            if (!r.ReadUInt32(out next.TrailerNetId) || !MountedWire.ReadVector(r, out next.Origin)
                || !MountedWire.ReadVector(r, out next.Direction) || !next.IsValid) return false;
            value = next; return true;
        }
    }

    /// <summary>The second real body in one driver's point-joint pair. Optional
    /// tail of Command26 and Event29; the ordinary single vehicle packet keeps
    /// its original fields and gains only the absence bit.</summary>
    public struct VehiclePairPose
    {
        public uint NetId;
        public Vector3 Pos, LinVel, AngVel;
        public float YawDegrees, PitchDegrees, RollDegrees;
        public bool IsValid => NetId != 0 && MountedWire.Finite(Pos)
            && MountedWire.Finite(LinVel) && MountedWire.Finite(AngVel)
            && float.IsFinite(YawDegrees) && float.IsFinite(PitchDegrees) && float.IsFinite(RollDegrees);
        public static void WriteOptional(NetPakWriter w, VehiclePairPose? pose)
        {
            w.WriteBit(pose.HasValue);
            if (pose is not { } p) return;
            if (!p.IsValid) throw new ArgumentException("Invalid trailer pose");
            w.WriteUInt32(p.NetId); NetWire.WritePos(w, p.Pos);
            w.WriteDegrees(p.YawDegrees, NetQuantization.YawBits);
            w.WriteDegrees(p.PitchDegrees, NetQuantization.PitchBits);
            w.WriteDegrees(p.RollDegrees, NetQuantization.PitchBits);
            NetWire.WriteVel(w, p.LinVel); NetWire.WriteVel(w, p.AngVel);
        }
        public static bool ReadOptional(NetPakReader r, out VehiclePairPose? pose)
        {
            pose = null;
            if (!r.ReadBit(out bool present)) return false;
            if (!present) return true;
            var p = new VehiclePairPose();
            if (!r.ReadUInt32(out p.NetId) || !NetWire.ReadPos(r, out p.Pos)
                || !r.ReadDegrees(out p.YawDegrees, NetQuantization.YawBits)
                || !r.ReadDegrees(out p.PitchDegrees, NetQuantization.PitchBits)
                || !r.ReadDegrees(out p.RollDegrees, NetQuantization.PitchBits)
                || !NetWire.ReadVel(r, out p.LinVel) || !NetWire.ReadVel(r, out p.AngVel) || !p.IsValid) return false;
            pose = p; return true;
        }
        public static VehiclePairPose From(VehicleReplication.VehicleEntity e) => new VehiclePairPose
        {
            NetId = e.NetIdValue, Pos = e.Pos, LinVel = e.LinVel, AngVel = e.AngVel,
            YawDegrees = e.YawDegrees, PitchDegrees = e.PitchDegrees, RollDegrees = e.RollDegrees,
        };
    }

    public sealed partial class VehicleReplication
    {
        public bool TryGetCoupledCab(uint trailerId, out VehicleEntity cab)
        {
            foreach (var value in All) if (value.CoupledTrailerNetId == trailerId && trailerId != 0)
            { cab = value; return true; }
            cab = null; return false;
        }
        /// <summary>Host-only relationship writer. Game-side source type/focus
        /// validation precedes this; here links remain one-to-one and acyclic.</summary>
        public bool ServerSetCoupledTrailer(uint cabId, uint trailerId, long tick)
        {
            if (!_vehicles.TryGet(new NetId(cabId), out var cab)) return false;
            if (cab.CoupledTrailerNetId == trailerId) return true;
            if (trailerId != 0 && (cabId == trailerId || cab.Exploded || cab.CoupledTrailerNetId != 0
                || TryGetCoupledCab(cabId, out _) || !_vehicles.TryGet(new NetId(trailerId), out var trailer)
                || trailer.Exploded || trailer.CoupledTrailerNetId != 0 || TryGetCoupledCab(trailerId, out _))) return false;
            cab.CoupledTrailerNetId = trailerId; cab.CouplingRevision++; cab.LastChangedTick = tick; return true;
        }
    }

    public sealed partial class ServerVehicles
    {
        /// <summary>Original transformed kingpin/fifth-wheel distance check.
        /// Trailer SpeedMax is0: cab envelope + this bounded joint geometry
        /// replaces an independent trailer speed envelope. Null fails closed.</summary>
        public Func<VehicleReplication.VehicleEntity, VehicleReplication.VehicleEntity, VehicleStateCommand, bool> CoupledPoseAllowed;
        bool PairAllowed(VehicleReplication.VehicleEntity cab, VehicleStateCommand command)
        {
            if (cab.CoupledTrailerNetId == 0) return !command.Trailer.HasValue;
            return command.Trailer is { } trailer && trailer.NetId == cab.CoupledTrailerNetId
                && trailer.IsValid && _vehicles.TryGet(new NetId(trailer.NetId), out var body)
                && !body.Exploded && CoupledPoseAllowed?.Invoke(cab, body, command) == true;
        }
        VehiclePairPose? PairRecovery(VehicleReplication.VehicleEntity cab) =>
            cab.CoupledTrailerNetId != 0 && _vehicles.TryGet(new NetId(cab.CoupledTrailerNetId), out var trailer)
                ? VehiclePairPose.From(trailer) : null;
        void AdoptPair(VehicleStateCommand command, long tick)
        {
            if (command.Trailer is not { } trailer || !_vehicles.TryGet(new NetId(trailer.NetId), out var body)) return;
            _vehicles.ServerAdoptDriverState(new NetId(trailer.NetId), trailer.Pos,
                new Vector3(trailer.PitchDegrees, trailer.YawDegrees, trailer.RollDegrees),
                trailer.LinVel, trailer.AngVel, body.SteerDegrees, body.Flags, tick);
        }
    }

    public sealed partial class NetWorldServer
    {
        public Func<ushort, VehicleHitchCommand, bool> VehicleHitchAllowed;
        public Action<ushort, VehicleHitchCommand> VehicleHitchRequested;
        void RegisterVehicleHitchCommands() => Commands.Register<VehicleHitchCommand>(ReplicationIds.CommandVehicleHitch,
            VehicleHitchCommand.TryRead, (owner, command) => VehicleHitchRequested(owner, command),
            (owner, command) => VehicleHitchRequested != null && CombatState.IsAlive(owner)
                && VehicleHitchAllowed?.Invoke(owner, command) == true);
    }
    public sealed partial class NetWorldClient
    {
        public bool SendVehicleHitch(VehicleHitchCommand command) => State == NetSessionState.Connected && command.IsValid
            && SendCommand(ReplicationIds.CommandVehicleHitch, command.Write);
    }
}
