using System.Text.Json;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public sealed partial class WorldServices
{
    sealed class BedBody(uint id, uint seat, string key)
    {
        public readonly uint Id = id, Seat = seat;
        public readonly string Key = key;
        public float Health = 200f; // Bed.cs Health/HealthMax.
    }
    readonly Dictionary<string, BedBody> _bedBodies = new();
    readonly HashSet<uint> _retiredBedSeats = new();

    /// <summary>Original InteractableNetSync.DamageAlong: only the first world
    /// collider can take the accepted bullet/melee's VehicleDamage. No client
    /// hit/damage operation exists; core invokes this after its own world ray.</summary>
    public void AttachBarricadeDamage(WorldGeometry geometry)
    {
        geometry.DynamicAlive = key => (!_bedBodies.TryGetValue(key, out var bed) || bed.Health > 0)
            && (!_doorBodies.TryGetValue(key, out var door) || door.Health > 0);
        _server.Combat.DamageBarricadeAlong = (from, to, amount, tick) => Damage(0, from, to, amount, tick);
        _server.Combat.DamageMountedBarricadeAlong = Damage;
        bool Damage(uint firingVehicle, Vector3 from, Vector3 to, float amount, long tick)
        {
            if (!geometry.RayDynamicKey(from, to, out var key, firingVehicle) || key == null) return false;
            if (DamageDoor(key, amount, geometry)) return true;
            if (!_bedBodies.TryGetValue(key, out var bed) || bed.Health <= 0) return false;
            if (!(amount > 0) || !float.IsFinite(amount)) return true;
            bed.Health = Math.Max(0, bed.Health - amount);
            if (bed.Health > 0) return true;
            // Bed.QueueFree removes the claim and its child PropSeat; the source
            // InteractableNetSync retire sweep then publishes both absences.
            _server.Interactables.RemoveBed(bed.Id);
            if (bed.Seat != 0)
            {
                _retiredBedSeats.Add(bed.Seat);
                _server.Interactables.RemoveSeat(bed.Seat);
            }
            geometry.RemoveDynamic(bed.Key);
            return true;
        }
    }

    void RegisterBedBody(JsonElement spec)
    {
        uint id = checked((uint)Int(spec, "netId"));
        string key = Text(spec, "geometryKey");
        if (!string.IsNullOrWhiteSpace(key))
        {
            uint seat = checked((uint)Int(spec, "seatNetId"));
            if (_bedBodies.TryGetValue(key, out var existing))
            {
                if (existing.Id != id || existing.Seat != seat)
                    throw new ArgumentException("Bed geometry key cannot change fixture identity");
                return; // Re-registration must not heal or revive a broken bed.
            }
            _bedBodies.Add(key, new BedBody(id, seat, key));
        }
        _server.Interactables.RegisterBed(id, Vec(spec, "position"), Float(spec, "yaw"));
    }
}
