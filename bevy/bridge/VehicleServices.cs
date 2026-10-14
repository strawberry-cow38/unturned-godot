using System.Text.Json;
using UnityEngine;
using UnturnedGodot.Net;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>The host node adapter around the original server vehicle authority.
/// Rapier owns each physical body; the managed server owns seats, reach checks,
/// driver envelopes, recovery, storage and inventory transactions.</summary>
public sealed class VehicleServices
{
    readonly NetWorldServer _server;
    readonly Dictionary<uint, VehicleStorageShape> _storage = new();
    readonly Dictionary<(uint, int), (long tick, bool clear, Vector3 pos)> _exits = new();
    readonly HashSet<uint> _withDoors = new();
    readonly Dictionary<string, uint> _keys = new();
    readonly Dictionary<uint, float> _fuel = new();
    public float FuelFor(uint id, float fallback) => _fuel.GetValueOrDefault(id, fallback);
    public void SaveFuel(WorldSave save)
    {
        var vehicles = _server.Vehicles.All.ToArray();
        foreach (var v in save.Vehicles) if (v.SpawnIndex >= 0 && v.SpawnIndex < vehicles.Length)
            v.Fuel = FuelFor(vehicles[v.SpawnIndex].NetIdValue, v.Fuel);
    }
    public void RestoreFuel(WorldSave save)
    {
        if (save == null) return;
        var vehicles = _server.Vehicles.All.ToArray();
        foreach (var v in save.Vehicles) if (v.SpawnIndex >= 0 && v.SpawnIndex < vehicles.Length && vehicles[v.SpawnIndex].TypeId == v.TypeId)
            _fuel[vehicles[v.SpawnIndex].NetIdValue] = v.Fuel;
    }
    public VehicleServices(NetWorldServer server, WorldGeometry geometry, Action<string, object> emit)
    {
        _server = server;
        server.VehicleStorage.ShapeOf = id => _storage.GetValueOrDefault(id);
        server.VehicleHost.AdjustExitSpot = p => new(p.x, Math.Max(p.y, geometry.GroundHeight(p.x, p.z) + .05f), p.z);
        server.VehicleHost.ResolveExit = (id, seat) => {
            if (!_withDoors.Contains(id)) return null;
            if (_exits.TryGetValue((id, seat), out var exit) && server.Session.CurrentTick - exit.tick <= 10)
                return (exit.clear, exit.pos);
            return (false, Vector3.zero); // a real door must have a current host physics clearance result
        };
        server.VehicleHost.ApplyCarjackForce = (id, sender) => emit("carjack", new { netId = id, playerId = sender });
    }
    public object Register(JsonElement request)
    {
        long tick = _server.Session.CurrentTick;
        if (Bool(request, "replace", true))
        {
            foreach (var vehicle in _server.Vehicles.All.ToArray()) _server.Vehicles.ServerRemove(new(vehicle.NetIdValue), tick);
            _storage.Clear(); _exits.Clear(); _withDoors.Clear(); _keys.Clear(); _fuel.Clear();
        }
        var result = new List<object>();
        foreach (var spec in request.GetProperty("vehicles").EnumerateArray())
        {
            string key = Text(spec, "key");
            if (key.Length > 0 && _keys.TryGetValue(key, out uint existing) && _server.Vehicles.TryGet(existing, out var previous))
            { result.Add(new { netId = existing, typeId = previous.TypeId, key }); continue; }
            var id = _server.Ids.Mint();
            byte type = checked((byte)Int(spec, "typeId")), variant = checked((byte)Int(spec, "variant"));
            var pos = Vec(spec, "position");
            var e = _server.Vehicles.ServerSpawn(id, type, variant, pos, tick,
                Float(spec, "speedMax"), Float(spec, "climbMax"), Float(spec, "fallMax"));
            int seats = spec.TryGetProperty("seatOffsets", out var offsets) ? offsets.GetArrayLength() : Int(spec, "seatCount", 1);
            _server.Vehicles.ServerSetSeatCount(id, seats);
            if (Bool(spec, "hasDoors")) _withDoors.Add(id.Value);
            var rotation = Vec(spec, "eulerDegrees", new(0, Float(spec, "yaw"), 0));
            _server.Vehicles.ServerPublish(id, pos, rotation, Vector3.zero, Vector3.zero, 0,
                Float(spec, "fuel"), Float(spec, "health"), Float(spec, "battery"), checked((byte)Int(spec, "flags")), tick);
            if (spec.TryGetProperty("storage", out var storage))
            {
                var shape = new VehicleStorageShape {
                    TrunkWidth = Byte(storage, "trunkWidth"), TrunkHeight = Byte(storage, "trunkHeight"),
                    GloveboxWidth = Byte(storage, "gloveboxWidth"), GloveboxHeight = Byte(storage, "gloveboxHeight") };
                if (storage.TryGetProperty("seats", out var pockets)) shape.Seats = pockets.EnumerateArray()
                    .Select(p => (Byte(p, "width"), Byte(p, "height"), Text(p, "label"))).ToArray();
                _storage[id.Value] = shape;
            }
            if (key.Length > 0) _keys[key] = id.Value;
            _fuel[id.Value] = Float(spec, "fuel");
            result.Add(new { netId = id.Value, typeId = type, key });
        }
        return new { vehicles = result };
    }
    public object Publish(JsonElement request)
    {
        int count = 0; long tick = _server.Session.CurrentTick;
        foreach (var state in request.GetProperty("vehicles").EnumerateArray())
        {
            uint id = checked((uint)Int(state, "netId"));
            if (!_server.Vehicles.TryGet(id, out var e)) throw new ArgumentException($"Unknown vehicle {id}");
            float fuel = Float(state, "fuel", FuelFor(id, e.Fuel)), health = Float(state, "health", e.Health), battery = Float(state, "battery", e.Battery);
            byte flags = checked((byte)Int(state, "flags", e.Flags));
            _fuel[id] = fuel;
            if (e.DriverPlayerId == 0)
                _server.Vehicles.ServerPublish(new(id), Vec(state, "position", e.Pos), Vec(state, "eulerDegrees", new(e.PitchDegrees, e.YawDegrees, e.RollDegrees)),
                    Vec(state, "linVel", e.LinVel), Vec(state, "angVel", e.AngVel), Float(state, "steerDegrees", e.SteerDegrees), fuel, health, battery, flags, tick);
            else _server.Vehicles.ServerPublishVitals(new(id), fuel, health, battery, (flags & VehicleReplication.FlagExploded) != 0, tick);
            count++;
        }
        return new { updated = count };
    }
    public object ExitProbe(JsonElement request)
    {
        uint id = checked((uint)Int(request, "netId")); int seat = Int(request, "seat");
        if (!_server.Vehicles.TryGet(id, out var e) || seat < 0 || seat >= e.SeatCount) throw new ArgumentException("Unknown vehicle seat");
        _exits[(id, seat)] = (_server.Session.CurrentTick, Bool(request, "clear"), Vec(request, "position"));
        return new { registered = true };
    }
    static byte Byte(JsonElement e, string key) => checked((byte)Int(e, key));
}
