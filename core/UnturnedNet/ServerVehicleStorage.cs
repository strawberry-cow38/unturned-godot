using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>Which of a vehicle's two containers: the boot, reached from outside, or the cabin -- the glovebox and a
    /// compartment per seat -- reached from a seat.</summary>
    public enum VehicleStorageKind : byte { Trunk = 0, Cabin = 1 }

    /// <summary>v58 (id 68): "open this vehicle's trunk / cabin". Addressed by the VEHICLE, not by a container id: a
    /// car's containers are created on first open (a map holds hundreds of cars, almost none ever opened), so the
    /// client has no container id to name until the server has made one. The answer is the ordinary StorageOpened.</summary>
    public struct OpenVehicleStorageCommand
    {
        public uint VehicleNetId;
        public VehicleStorageKind Kind;
        public void Write(NetPakWriter w) { w.WriteUInt32(VehicleNetId); w.WriteUInt8((byte)Kind); }
        public static bool TryRead(NetPakReader r, out OpenVehicleStorageCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id) || !r.ReadUInt8(out byte kind)) return false;
            cmd = new OpenVehicleStorageCommand { VehicleNetId = id, Kind = (VehicleStorageKind)kind };
            return true;
        }
    }

    /// <summary>What containers a vehicle has, and how big. Decided by the GAME layer (it holds the vehicle's spec: its
    /// seats, whether the hull has a boot); null from ShapeOf = this vehicle has none.</summary>
    public sealed class VehicleStorageShape
    {
        public byte TrunkWidth, TrunkHeight;         // 0x0 = no trunk
        public byte GloveboxWidth, GloveboxHeight;   // 0x0 = no cabin storage at all
        public (byte w, byte h, string label)[] Seats = Array.Empty<(byte, byte, string)>();
        public bool HasTrunk => TrunkWidth > 0 && TrunkHeight > 0;
        public bool HasCabin => GloveboxWidth > 0 && GloveboxHeight > 0;
    }

    /// <summary>
    /// A VEHICLE'S CONTAINERS (strawberry 2026-10-07: "when opening the inventory in a car, add several storage
    /// 'containers' (like how we have both a fridge and freezer compartment on fridges) for each seat + glovebox").
    ///
    /// Two ordinary server containers per car, made on first open and registered with InventoryReplication like any
    /// fridge -- so every existing rule about containers (several viewers at once, edits written through, the owner
    /// echo) applies to them unchanged:
    ///   - the CABIN: Storage is the glovebox, and each seat is a compartment on its own view page;
    ///   - the TRUNK: one grid.
    ///
    /// What differs from a fridge is WHO may open them, because a car moves and a seat is not a distance. The cabin
    /// opens for the people sitting in that car and nobody else; the trunk for someone within TrunkReach of where the
    /// car is NOW. Step re-checks both every tick and shuts anyone who no longer qualifies -- a passenger who got
    /// out, a trunk left standing open as the car drove off.
    ///
    /// The trunk used to be a client-only grid, which stopped working the day the inventory became server-owned:
    /// the server had never opened anything, so every drag into it was refused and nothing could be stored.
    /// </summary>
    public sealed class ServerVehicleStorage
    {
        readonly InventoryReplication _inventories;
        readonly VehicleReplication _vehicles;
        readonly NetIdMinter _ids;

        /// <summary>The game layer's answer for one vehicle. Unset, or null for a vehicle = no containers.</summary>
        public Func<uint, VehicleStorageShape> ShapeOf;

        /// <summary>How far from the car's centre the trunk can be worked. The F zone a player aims at sits behind the
        /// rear seats, a few metres from centre on a long car; this is the server's slack on top of it.</summary>
        public const float TrunkReach = 6f;

        readonly Dictionary<(uint vehicle, VehicleStorageKind kind), uint> _crates = new Dictionary<(uint, VehicleStorageKind), uint>();
        readonly List<(uint vehicle, VehicleStorageKind kind)> _gone = new List<(uint, VehicleStorageKind)>();

        public int CrateCount => _crates.Count;

        public ServerVehicleStorage(InventoryReplication inventories, VehicleReplication vehicles, NetIdMinter ids)
        {
            _inventories = inventories; _vehicles = vehicles; _ids = ids;
        }

        /// <summary>The container of this kind on this vehicle, created on first use. 0 = the vehicle does not exist
        /// or has no such container.</summary>
        public uint CrateFor(uint vehicleNetId, VehicleStorageKind kind)
        {
            if (_crates.TryGetValue((vehicleNetId, kind), out uint existing)) return existing;
            if (!_vehicles.TryGet(vehicleNetId, out var veh)) return 0;
            var shape = ShapeOf?.Invoke(vehicleNetId);
            if (shape == null) return 0;

            NetId id = _ids.Mint();
            InventoryReplication.CrateEntry crate;
            if (kind == VehicleStorageKind.Trunk)
            {
                if (!shape.HasTrunk) return 0;
                crate = _inventories.ServerRegisterCrate(id, shape.TrunkWidth, shape.TrunkHeight, veh.Pos);
                crate.StorageLabel = "Trunk";
                crate.Access = (sender, at) => _vehicles.TryGet(vehicleNetId, out var v) && (v.Pos - at).magnitude <= TrunkReach;
            }
            else
            {
                if (!shape.HasCabin) return 0;
                crate = _inventories.ServerRegisterCrate(id, shape.GloveboxWidth, shape.GloveboxHeight, veh.Pos);
                crate.StorageLabel = "Glovebox";
                crate.Access = (sender, at) => IsSeatedIn(sender, vehicleNetId);
                _inventories.ServerAddCompartments(id.Value, shape.Seats);
            }
            crate.OnVehicle = true;
            _crates[(vehicleNetId, kind)] = id.Value;
            return id.Value;
        }

        /// <summary>Is this player in ANY seat of this vehicle -- driving, or a passenger.</summary>
        public bool IsSeatedIn(ushort player, uint vehicleNetId)
        {
            if (player == 0 || !_vehicles.TryGet(vehicleNetId, out var v)) return false;
            if (v.DriverPlayerId == player) return true;
            var pax = v.Passengers;
            if (pax != null) foreach (var p in pax) if (p == player) return true;
            return false;
        }

        /// <summary>Once per server tick: carry each container with its car, shut anyone who may no longer reach one,
        /// and drop the containers of a car that no longer exists.</summary>
        public void Step(long tick, Func<ushort, Vector3?> playerPos, Action<ushort, uint> closed = null)
        {
            _gone.Clear();
            foreach (var kv in _crates)
            {
                if (!_vehicles.TryGet(kv.Key.vehicle, out var v)) { _gone.Add(kv.Key); continue; }
                if (_inventories.TryGetCrate(kv.Value, out var crate)) crate.Pos = v.Pos;
            }
            // ⚠ The contents go with the car. That is what the old client-side trunk did too (it was a child node of
            // the vehicle), and it is noted as a gap rather than solved here: a despawned wreck spilling its boot onto
            // the road is a decision about loot, not about containers.
            foreach (var key in _gone)
            {
                _inventories.ServerRemoveCrate(_crates[key], tick);
                _crates.Remove(key);
            }
            _inventories.ServerRevalidateAccess(playerPos, tick, closed);
        }
    }
}
