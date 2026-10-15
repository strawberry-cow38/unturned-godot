using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>Coordinate-frame changes for an integrated host. This is NOT a teleport:
    /// IDs, occupancy, sequence windows, velocity, inventory and simulation deadlines stay intact.
    /// The caller must first drain all peer transports/reliable messages at the old frame, translate
    /// its own physics/geometry in the same transaction, then publish a full snapshot before resuming.
    /// Remote peers require a negotiated frame barrier; never call this on a live UDP world alone.</summary>
    public static class WorldRebase
    {
        /// <summary>Retain authoritative distant objects outside the wire's local position window.
        /// In-range storage is byte-identical. Wire encoding remains bounded; hosts must rebase the
        /// active play area before reaching that boundary. Disabled by default on all original hosts.</summary>
        public static void EnableUnboundedStorage(NetWorldServer server)
        {
            server.Players.UnboundedPositionStorage = true;
            server.Zombies.UnboundedPositionStorage = true;
            server.Animals.UnboundedPositionStorage = true;
            server.Projectiles.UnboundedPositionStorage = true;
            server.Vehicles.UnboundedPositionStorage = true;
            server.Deployables.UnboundedPositionStorage = true;
            server.Deployables.Pipes.UnboundedPositionStorage = true;
            server.WorldItems.UnboundedPositionStorage = true;
            server.Crops.UnboundedPositionStorage = true;
            server.Containers.UnboundedPositionStorage = true;
        }

        internal static Vector3 Store(Vector3 p, bool unbounded)
        {
            if (!unbounded) return PlayerReplication.Quantize(p);
            // Clamped wire quantization floors to its fixed-point grid. Retain that exact rounding
            // outside the window too, without destroying a far-away object's actual coordinate.
            float Grid(float n, int integerBits, int fractionBits)
            {
                float range = 1 << (integerBits - 1);
                return n >= -range && n < range ? NetQuantization.QuantizeClampedFloat(n, integerBits, fractionBits)
                    : MathF.Floor(n * (1 << fractionBits)) / (1 << fractionBits);
            }
            return new Vector3(Grid(p.x, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits),
                Grid(p.y, NetQuantization.PositionYIntBits, NetQuantization.PositionYFracBits),
                Grid(p.z, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits));
        }

        public static void Translate(NetWorldServer server, NetWorldClient client, Vector3 delta)
        {
            if (!float.IsFinite(delta.x) || !float.IsFinite(delta.y) || !float.IsFinite(delta.z))
                throw new ArgumentException("World translation must be finite", nameof(delta));
            long stamp = server.Session.CurrentTick + 1;
            Translate(server.Players, server.CombatState, server.Zombies, server.Animals, server.Projectiles,
                server.Vehicles, server.Deployables, server.WorldItems, server.Crops, server.Containers, server.Inventories, delta, stamp);
            server.PlayerHost.TranslateWorld(delta);
            server.VehicleHost.TranslateWorld(delta);
            server.Combat.TranslateWorld(delta);
            server.Interactables.TranslateWorld(delta);
            server.Deadzones.TranslateWorld(delta);
            server.ForageHost.TranslateWorld(delta);
            server.SeaLevelY += delta.y;
            if (server.PendingSave != null) TranslateSavedPlayers(server.PendingSave, delta);
            foreach (var peer in server.Session.Peers) server.Composer.ForgetClient(peer.PlayerId);
            if (client == null) return;
            Translate(client.Players, client.CombatState, client.Zombies, client.Animals, client.Projectiles,
                client.Vehicles, client.Deployables, client.WorldItems, client.Crops, client.Containers, client.Inventories, delta, stamp);
            client.TranslateWorldContext(delta);
        }

        static void Translate(PlayerReplication players, PlayerCombatReplication combat, ZombieReplication zombies,
            AnimalReplication animals, ProjectileReplication projectiles, VehicleReplication vehicles,
            DeployableReplication deployables, WorldItemReplication items, CropReplication crops,
            ContainerReplication containers, InventoryReplication inventory, Vector3 d, long stamp)
        {
            foreach (var e in players.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in combat.All) e.SpawnPos += d;
            foreach (var e in zombies.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in animals.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in projectiles.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in vehicles.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in deployables.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in deployables.Pipes.All)
            { for (int i = 0; i < e.Path.Length; i++) e.Path[i] += d; e.LastChangedTick = stamp; }
            foreach (var e in items.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in crops.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in containers.All) { e.Pos += d; e.LastChangedTick = stamp; }
            foreach (var e in inventory.Crates) e.Pos += d;
        }

        // Only offline players survive from PendingSave into Capture; world tables are captured fresh.
        static void TranslateSavedPlayers(WorldSave save, Vector3 delta)
        { foreach (var p in save.Players) { p.X += delta.x; p.Y += delta.y; p.Z += delta.z; } }
    }

    public sealed partial class ServerPlayerAuthority
    {
        internal void TranslateWorld(Vector3 d)
        { foreach (var state in _driven.Values) if (state.HasAdopted) state.Adopted.Pos += d; }
    }
    public sealed partial class ServerVehicles
    {
        internal void TranslateWorld(Vector3 d)
        { foreach (var state in _driven.Values) if (state.HasAdopted) state.Adopted.Pos += d; }
    }
    public sealed partial class ServerCombat
    {
        internal void TranslateWorld(Vector3 d)
        { foreach (var b in _bullets) b.Pos += d; foreach (var g in _grenades) g.Pos += d; }
    }
    public sealed partial class ServerInteractables
    {
        internal void TranslateWorld(Vector3 d)
        {
            foreach (uint id in new List<uint>(_doors.Keys)) { var v = _doors[id]; v.Pos += d; _doors[id] = v; }
            foreach (uint id in new List<uint>(_objectDoors.Keys)) { var v = _objectDoors[id]; v.Pos += d; _objectDoors[id] = v; }
            foreach (uint id in new List<uint>(_seats.Keys)) { var v = _seats[id]; v.Pos += d; _seats[id] = v; }
            _beds.TranslateWorld(d); Stamp();
        }
    }
    public sealed partial class ServerDeadzones
    {
        internal void TranslateWorld(Vector3 d)
        { for (int i = 0; i < _volumes.Count; i++) { var v = _volumes[i]; v.Center += d; _volumes[i] = v; } }
    }
    public sealed partial class ServerForage
    {
        internal void TranslateWorld(Vector3 d)
        { for (int i = 0; i < _pos.Length; i++) _pos[i] += d; }
    }
    public sealed partial class PredictionReconciler
    {
        internal void TranslateWorld(Vector3 d)
        { for (int i = 0; i < _ring.Length; i++) if (_ring[i].Used) _ring[i].Pos += d; }
    }
    public sealed partial class NetWorldClient
    {
        internal void TranslateWorldContext(Vector3 d)
        {
            Prediction.Pos += d; Prediction.Reconciler.TranslateWorld(d);
            // An unacknowledged carried event may already have executed. Translate its redundant copy;
            // keep its sequence, so it is still deduplicated rather than firing a second time.
            for (int i = 0; i < _combatRingCount; i++)
            {
                if (_combatRing[i].Kind == CombatEventKind.Fire) _combatRing[i].Fire.Origin += d;
                if (_combatRing[i].Kind == CombatEventKind.Grenade) _combatRing[i].Grenade.Origin += d;
            }
        }
    }
    public sealed partial class PlayerReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class ZombieReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class ProjectileReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class AnimalReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class VehicleReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class DeployableReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class WorldItemReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class CropReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class ContainerReplication
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
    public sealed partial class ItemPipeGraph
    {
        public bool UnboundedPositionStorage { get; set; }
        Vector3 StorePosition(Vector3 p) => WorldRebase.Store(p, UnboundedPositionStorage);
    }
}
