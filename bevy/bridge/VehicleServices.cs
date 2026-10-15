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
    public VehicleLightingServices Lighting;
    public VehicleMaintenanceServices Maintenance;
    public bool HasEngineHealth(uint id) => _parts.TryGetValue(id, out var parts) && parts.EngineMax > 0;
    readonly Dictionary<uint, VehicleStorageShape> _storage = new();
    readonly Dictionary<(uint, int), (long tick, bool clear, Vector3 pos)> _exits = new();
    readonly HashSet<uint> _withDoors = new();
    readonly Dictionary<string, uint> _keys = new();
    readonly Dictionary<uint, float> _fuel = new();
    readonly Dictionary<uint, JsonElement> _bodySpecs = new();
    readonly Dictionary<uint, Parts> _parts = new();
    sealed class Parts
    {
        public float Engine, EngineMax, Main, MainMax, Tail, TailMax;
        public bool Drowned;
    }
    public sealed record PartsSaved(int Ordinal, float Engine, float Main, float Tail, bool Drowned);
    public PartsSaved[] CaptureParts()
    {
        var fleet = _server.Vehicles.All.ToArray();
        return fleet.Select((v, i) => _parts.TryGetValue(v.NetIdValue, out var p) ? new PartsSaved(i, p.Engine, p.Main, p.Tail, p.Drowned) : null).Where(p => p != null).ToArray();
    }
    public void RestoreParts(PartsSaved[] saved)
    {
        var fleet = _server.Vehicles.All.ToArray();
        foreach (var s in saved ?? []) if (s.Ordinal >= 0 && s.Ordinal < fleet.Length && _parts.TryGetValue(fleet[s.Ordinal].NetIdValue, out var p))
        { p.Engine = Math.Clamp(s.Engine, 0, p.EngineMax); p.Main = Math.Clamp(s.Main, 0, p.MainMax); p.Tail = Math.Clamp(s.Tail, 0, p.TailMax); p.Drowned = s.Drowned; }
    }
    public float EngineHealth(uint id) => _parts.TryGetValue(id, out var p) ? p.Engine : 0;
    public bool IsHelicopter(uint id) => _bodySpecs.TryGetValue(id, out var spec) && Bool(spec, "isHeli");
    public float MainRotorMax(uint id) => _parts.TryGetValue(id, out var p) ? p.MainMax : 0;
    public float TailRotorMax(uint id) => _parts.TryGetValue(id, out var p) ? p.TailMax : 0;
    public float MainRotorHealth(uint id) => _parts.TryGetValue(id, out var p) ? p.Main : 0;
    public float TailRotorHealth(uint id) => _parts.TryGetValue(id, out var p) ? p.Tail : 0;
    public bool EngineDrowned(uint id) => _parts.TryGetValue(id, out var p) && p.Drowned;
    public void DamageParts(uint id, float amount, float engineShare, string part)
    {
        if (!_parts.TryGetValue(id, out var p)) return;
        if (part?.StartsWith("tire:") == true && int.TryParse(part.AsSpan(5), out int tire) && tire is >= 0 and < 8 && _server.Vehicles.TryGet(id, out var e))
            _server.Vehicles.ServerSetPoppedTires(new(id), (byte)(e.PoppedTireMask | (1 << tire)), _server.Session.CurrentTick + 1);
        p.Engine = Math.Max(0, p.Engine - amount * (part == "engine" ? 1 : engineShare));
        if (part == "mainRotor") p.Main = Math.Max(0, p.Main - amount);
        if (part == "tailRotor") p.Tail = Math.Max(0, p.Tail - amount);
    }
    public void RepairParts(uint id, float amount, float maximum)
    {
        if (!_parts.TryGetValue(id, out var p) || maximum <= 0) return;
        float fraction = amount / maximum;
        if (!p.Drowned) p.Engine = Math.Min(p.EngineMax, p.Engine + p.EngineMax * fraction);
        p.Main = Math.Min(p.MainMax, p.Main + p.MainMax * fraction);
        p.Tail = Math.Min(p.TailMax, p.Tail + p.TailMax * fraction);
    }
    public float HealthMax(uint id) => _bodySpecs.TryGetValue(id, out var spec) ? Float(spec, "healthMax", Float(spec, "health")) : 0;
    public string HitPart(uint id, Vector3 point, string reported)
    {
        if (!_bodySpecs.TryGetValue(id, out var spec) || !_server.Vehicles.TryGet(id, out var e)) return reported ?? "hull";
        if (Maintenance?.ResolveGlass(id, point) is int pane && pane >= 0) return "glass:" + pane;
        if (Lighting?.ResolveLamp(id, point) is int lamp && lamp >= 0) return "lamp:" + lamp;
        if (reported?.StartsWith("tire:") == true && int.TryParse(reported.AsSpan(5), out int tire) && tire is >= 0 and < 8 && (e.PoppedTireMask & (1 << tire)) == 0) return reported;
        var rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(e.YawDegrees * Mathf.Deg2Rad, e.PitchDegrees * Mathf.Deg2Rad, e.RollDegrees * Mathf.Deg2Rad);
        var p = System.Numerics.Vector3.Transform(new(point.x - e.Pos.x, point.y - e.Pos.y, point.z - e.Pos.z), System.Numerics.Quaternion.Inverse(rotation));
        if (spec.TryGetProperty("hitRegions", out var regions)) foreach (var region in regions.EnumerateArray())
        {
            var center = Vec(region, "center"); var size = Vec(region, "size");
            if (size.x > 0 && Math.Abs(p.X - center.x) <= size.x * .5f && Math.Abs(p.Y - center.y) <= size.y * .5f && Math.Abs(p.Z - center.z) <= size.z * .5f)
                return Text(region, "bodyPart", "hull");
        }
        if (spec.TryGetProperty("engineRegion", out var engine) && (Bool(engine, "rear") ? p.Z > Float(engine, "threshold") : p.Z < Float(engine, "threshold"))) return "engine";
        return "hull";
    }
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
            _storage.Clear(); _exits.Clear(); _withDoors.Clear(); _keys.Clear(); _fuel.Clear(); _bodySpecs.Clear(); _parts.Clear();
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
            _bodySpecs[id.Value] = spec.Clone();
            var parts = new Parts();
            if (spec.TryGetProperty("engineRegion", out var engine)) parts.Engine = parts.EngineMax = Float(engine, "health");
            if (spec.TryGetProperty("hitRegions", out var regions)) foreach (var region in regions.EnumerateArray())
                switch (Text(region, "bodyPart"))
                {
                    case "mainRotor": parts.Main = parts.MainMax = Float(region, "health"); break;
                    case "tailRotor": parts.Tail = parts.TailMax = Float(region, "health"); break;
                }
            _parts[id.Value] = parts;
            Lighting?.Registered(id.Value, spec);
            result.Add(new { netId = id.Value, typeId = type, key });
        }
        return new { vehicles = result };
    }
    public object Publish(JsonElement request)
    {
        int count = 0; long tick = _server.Session.CurrentTick + 1;
        foreach (var state in request.GetProperty("vehicles").EnumerateArray())
        {
            uint id = checked((uint)Int(state, "netId"));
            if (!_server.Vehicles.TryGet(id, out var e)) throw new ArgumentException($"Unknown vehicle {id}");
            float fuel = Float(state, "fuel", FuelFor(id, e.Fuel)), health = Float(state, "health", e.Health), battery = Float(state, "battery", e.Battery);
            byte flags = checked((byte)Int(state, "flags", e.Flags));
            // Body telemetry must not resurrect a vehicle destroyed by server
            // combat after the host's last replica. Damage is an explicit fact.
            flags = (byte)((flags & ~VehicleReplication.FlagExploded) | (e.Flags & VehicleReplication.FlagExploded));
            _fuel[id] = fuel;
            if (Bool(state, "engineDrowned") && _parts.TryGetValue(id, out var parts)) { parts.Drowned = true; parts.Engine = 0; }
            Lighting?.BodyState(id, state);
            if (Lighting != null) { flags = Lighting.NormalizeFlags(id, flags); battery = Lighting.BatteryFor(id, battery); }
            if (e.DriverPlayerId == 0)
                _server.Vehicles.ServerPublish(new(id), Vec(state, "position", e.Pos), Vec(state, "eulerDegrees", new(e.PitchDegrees, e.YawDegrees, e.RollDegrees)),
                    Vec(state, "linVel", e.LinVel), Vec(state, "angVel", e.AngVel), Float(state, "steerDegrees", e.SteerDegrees), fuel, health, battery, flags, tick);
            else _server.Vehicles.ServerPublishVitals(new(id), fuel, health, battery, (flags & VehicleReplication.FlagExploded) != 0, tick);
            Lighting?.PublishCurrent(id);
            count++;
        }
        return new { updated = count };
    }
    public void TranslateWorld(Vector3 delta)
    {
        foreach (var key in _exits.Keys.ToArray()) { var v = _exits[key]; _exits[key] = (v.tick, v.clear, v.pos + delta); }
        foreach (var key in _bodySpecs.Keys.ToArray()) _bodySpecs[key] = CoordinateJson.Entry(_bodySpecs[key], delta);
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
