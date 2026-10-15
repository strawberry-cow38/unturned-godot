using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Headless equivalent of the original world's host registration and
/// publishers. All transactions, growth, forage and rubble rules live in core;
/// original ContainerNetSync owns stocked grids, cooking and freezing.</summary>
public sealed partial class WorldServices
{
    readonly NetWorldServer _server;
    readonly ContainerNetSync _containers;
    public ResourceServices Harvesting { get; }
    readonly GasStationServer _fuel = new();
    readonly List<int> _regrown = new();
    readonly Dictionary<int, JsonElement> _drops = new();
    readonly HashSet<int> _broken = new();
    readonly List<Tap> _taps = new();
    readonly Dictionary<uint, (Vector3 position, Vector3 swingSide)> _objectDoorMeta = new();
    public sealed record PropDoorSaved(uint NetId, bool Open, bool Reverse);
    public PropDoorSaved[] CapturePropDoors() => _server.Interactables.ObjectDoors
        .Select(d => new PropDoorSaved(d.Key, d.Value, _server.Interactables.IsObjectDoorReversed(d.Key))).ToArray();
    public void RestorePropDoors(PropDoorSaved[] saved)
    {
        foreach (var s in saved ?? []) if (_objectDoorMeta.TryGetValue(s.NetId, out var m))
        {
            _server.Interactables.ServerRestoreObjectDoor(s.NetId, false);
            // Existing toggle rule writes Reverse when opening from the chosen
            // side; this restores both open and closed double-acting states.
            _server.Interactables.ToggleObjectDoor(s.NetId, m.position + m.swingSide * (s.Reverse ? 1 : -1), out _, out _);
            if (!s.Open) _server.Interactables.ServerRestoreObjectDoor(s.NetId, false);
        }
    }
    sealed record Tap(System.Numerics.Matrix4x4 Transform, System.Numerics.Matrix4x4 Inverse, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)
    {
        public float Distance(Vector3 p)
        {
            var world = new System.Numerics.Vector3(p.x, p.y, p.z);
            var local = System.Numerics.Vector3.Transform(world, Inverse);
            var closest = System.Numerics.Vector3.Clamp(local, Min, Max);
            return System.Numerics.Vector3.Distance(world, System.Numerics.Vector3.Transform(closest, Transform));
        }
    }
    public object MapResult { get; private set; }
    public string MapKey { get; private set; }
    public WorldServices(NetWorldServer server, Catalog catalog, Action<string, object> emit)
    {
        _server = server; Harvesting = new(server, catalog, emit);
        BindStationarySeats();
        FarmRegistry.Load(); CropRegistry.Load(); NpcCatalog.Load();
        RegisterCrops(server.Crops.Schema);
        server.Transactions.FuelStations = _fuel;
        // Same affine-local AABB nearest point and reach as original WaterTap.DistanceTo.
        server.Transactions.RunningTapNear = p => _taps.Any(t => t.Distance(p) <= 2.5f);
        server.Npcs.DialogueOf = NpcCatalog.Dialogue;
        server.Npcs.QuestOfId = NpcCatalog.Quest;
        server.Npcs.VendorOf = NpcCatalog.Vendor;
        server.Npcs.ActiveHoliday = Main.ActiveHolidayNow;
        _containers = new ContainerNetSync(server, null);
    }
    public static void RegisterCrops(CropSchema schema)
    {
        foreach (var def in FarmRegistry.All)
            schema.Register(new CropNetDef { SeedId = def.Id, GrowthSeconds = def.Growth, YieldItemId = def.Grow });
    }
    public object Register(JsonElement spec)
    {
        long tick = _server.Session.CurrentTick;
        if (spec.TryGetProperty("hasWater", out _)) _server.HasWater = Bool(spec, "hasWater");
        if (spec.TryGetProperty("seaLevel", out _)) _server.SeaLevelY = Float(spec, "seaLevel");
        if (spec.TryGetProperty("survivalDrain", out _)) _server.Vitals.SurvivalDrain = Bool(spec, "survivalDrain");
        if (spec.TryGetProperty("resourceCount", out _))
        {
            int count = Count(spec, "resourceCount"); Harvesting.Clear(); _server.Resources.ServerInit(count, tick); _server.ForageHost.ServerInit(count);
        }
        foreach (var r in Entries(spec, "resources"))
        {
            int index = Int(r, "index");
            if (index < 0 || index >= _server.Resources.Count) throw new ArgumentException("Resource index exceeds registered count");
            Harvesting.Register(r);
            if (Int(r, "rewardItemId") == 0) continue;
            _server.ForageHost.SetMeta(index, checked((ushort)Int(r, "rewardItemId")), Vec(r, "position"), Ticks(Float(r, "resetSeconds", 1000)));
        }
        if (spec.TryGetProperty("destructibleCount", out _))
        {
            _server.DestructibleHost.ServerInit(Count(spec, "destructibleCount"), tick); _drops.Clear(); _broken.Clear();
        }
        foreach (var d in Entries(spec, "destructibles"))
        {
            int index = Int(d, "index");
            if (index < 0 || index >= _server.Destructibles.Count) throw new ArgumentException("Destructible index exceeds registered count");
            _server.DestructibleHost.SetMeta(index, Float(d, "health"), Ticks(Float(d, "resetSeconds")));
            if (Int(d, "dropItemId") > 0) _drops[index] = d.Clone();
        }
        foreach (var d in Entries(spec, "doors"))
            RegisterDoorBody(d);
        foreach (var d in Entries(spec, "beds"))
            RegisterBedBody(d);
        foreach (var d in Entries(spec, "objectDoors"))
        {
            uint id = checked((uint)Int(d, "netId")); var pos = Vec(d, "interactionPosition", Vec(d, "position")); var side = Vec(d, "swingSide");
            _server.Interactables.RegisterObjectDoor(id, pos, Bool(d, "open"), side);
            _objectDoorMeta[id] = (pos, side);
        }
        foreach (var d in Entries(spec, "seats")) RegisterStationarySeat(d);
        foreach (var d in Entries(spec, "deadzones"))
        {
            var kind = Enum.TryParse<DeadzoneKind>(Text(d, "kind", "Radiation"), true, out var k) ? k : throw new ArgumentException("Unknown deadzone kind");
            var shape = Enum.TryParse<DeadzoneShape>(Text(d, "shape", "Box"), true, out var s) ? s : throw new ArgumentException("Unknown deadzone shape");
            _server.Deadzones.AddVolume(Vec(d, "center"), Vec(d, "halfExtent"), shape, DeadzoneDef.Default(kind));
        }
        foreach (var tap in Entries(spec, "taps")) if (Bool(tap, "running", true))
        {
            var p = Vec(tap, "position"); var r = Vec(tap, "rotation"); var scale = Vec(tap, "scale", new(1, 1, 1));
            var matrix = MapServices.PlacementMatrix(new[] { r.x, r.y, r.z }, new[] { scale.x, scale.y, scale.z });
            matrix.Translation = new(p.x, p.y, p.z);
            if (!System.Numerics.Matrix4x4.Invert(matrix, out var inverse)) throw new ArgumentException("Degenerate tap transform");
            var lo = Vec(tap, "min"); var hi = Vec(tap, "max");
            _taps.Add(new(matrix, inverse, new(lo.x, lo.y, lo.z), new(hi.x, hi.y, hi.z)));
        }
        var entities = new List<object>();
        var fixtureIds = new List<uint>();
        foreach (var fixture in Entries(spec, "fixtures"))
        {
            ushort defId = checked((ushort)Int(fixture, "defId"));
            var def = DeployableDef.ById(defId);
            if (def == null || def.Fixture == FixtureKind.None) throw new ArgumentException($"Unknown map fixture {defId}");
            var pos = Vec(fixture, "position");
            var e = _server.Deployables.ServerPlace(_server.Ids.Mint(), defId, 0, pos, Float(fixture, "yaw"), tick);
            if (def.Fixture == FixtureKind.GasPump)
                _fuel.RegisterPump(e, Int(fixture, "stationId", StationFuel.StationIdFor(new(pos.x, pos.y, pos.z))), _server.Deployables, tick);
            else if (def.Fixture == FixtureKind.GridSource) _server.Deployables.ServerToggle(e.NetIdValue, true, tick);
            entities.Add(new { netId = e.NetIdValue, defId }); fixtureIds.Add(e.NetIdValue);
        }
        if (spec.TryGetProperty("mapRoot", out _)) LootTables.Load(Path.Combine(Text(spec, "mapRoot"), "Spawns", "Items.dat"));
        var containers = Entries(spec, "containers").Select(c => {
            var p = Vec(c, "position");
            return (Text(c, "mesh"), Int(c, "table"), Bool(c, "display"), Text(c, "label"), new Godot.Vector3(p.x, p.y, p.z), Float(c, "yaw"));
        }).ToList();
        var previousContainers = _server.Containers.All.Select(e => e.NetIdValue).ToHashSet();
        if (containers.Count > 0) _containers.RegisterAll(containers);
        if (spec.TryGetProperty("mapObjects", out var mapObjects))
        {
            var containerIds = _server.Containers.All.Where(e => !previousContainers.Contains(e.NetIdValue)).Select(e => e.NetIdValue).ToArray();
            var mappings = JsonSerializer.Deserialize<MapServices.ObjectMapping[]>(mapObjects.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var m in mappings)
            {
                if (m.FixtureOrdinal is int f) m.FixtureNetId = fixtureIds[f];
                if (m.ContainerOrdinal is int c) m.ContainerNetId = containerIds[c];
            }
            MapKey = Text(spec, "mapKey");
            return MapResult = new { objects = mappings, resources = spec.GetProperty("mapResources").Clone(),
                objectDoors = Entries(spec, "objectDoors").ToArray(),
                containerDoors = Entries(spec, "containerDoors").Select(d => {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(d.GetRawText());
                    node["netId"] = containerIds[Int(d, "containerOrdinal")]; return node;
                }).ToArray(), seats = Entries(spec, "seats").ToArray(), npcs = Entries(spec, "npcs").ToArray(),
                resourceCount = _server.Resources.Count, destructibleCount = _server.Destructibles.Count,
                containerCount = containers.Count, fixtureCount = fixturesCount(), deadzoneCount = _server.Deadzones.VolumeCount };
        }
        int fixturesCount() => fixtureIds.Count;
        return new { fixtures = entities, resources = _server.Resources.Count, destructibles = _server.Destructibles.Count,
            containers = _server.Containers.Count, objectDoors = _server.Interactables.ObjectDoorCount, seats = _server.Interactables.SeatCount, deadzones = _server.Deadzones.VolumeCount, hasWater = _server.HasWater, seaLevel = _server.SeaLevelY };
    }
    static long Ticks(float seconds) => (long)Math.Round(seconds / SimClock.FixedDelta);
    static int Count(JsonElement spec, string key)
    {
        int count = Int(spec, key); return count is >= 0 and <= ushort.MaxValue ? count : throw new ArgumentOutOfRangeException(key);
    }
    static IEnumerable<JsonElement> Entries(JsonElement spec, string key) => spec.TryGetProperty(key, out var array)
        ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();
    public void TranslateWorld(Vector3 delta)
    {
        Harvesting.TranslateWorld(delta);
        TranslateStationarySeats(delta);
        TranslateDoors(delta);
        foreach (var key in _drops.Keys.ToArray()) _drops[key] = CoordinateJson.Entry(_drops[key], delta);
        foreach (var key in _objectDoorMeta.Keys.ToArray())
        { var value = _objectDoorMeta[key]; _objectDoorMeta[key] = (value.position + delta, value.swingSide); }
        for (int i = 0; i < _taps.Count; i++)
        {
            var tap = _taps[i]; var matrix = tap.Transform;
            matrix.Translation += new System.Numerics.Vector3(delta.x, delta.y, delta.z);
            System.Numerics.Matrix4x4.Invert(matrix, out var inverse);
            _taps[i] = tap with { Transform = matrix, Inverse = inverse };
        }
        if (MapResult != null) MapResult = CoordinateJson.ServiceSpec(CoordinateJson.ToElement(MapResult), delta);
    }
    public void AdoptSavedBits()
    {
        _broken.Clear();
        for (int n = 0; n < _server.Destructibles.Count; n++) if (!_server.Destructibles.IsAlive(n)) _broken.Add(n);
    }
    public void Step()
    {
        long tick = _server.Session.CurrentTick;
        ReleaseInvalidStationaryOccupants(tick);
        StepDoors();
        _server.ItemMovers.Step((float)SimClock.FixedDelta);
        _containers.Tick(); Harvesting.Step();
        foreach (int i in Regrown(tick)) _server.Transactions.SetResourceAlive(i, true);
        _server.DestructibleHost.Tick(tick);
        for (int n = 0; n < _server.Destructibles.Count; n++)
        {
            if (_server.Destructibles.IsAlive(n)) _broken.Remove(n);
            else if (_broken.Add(n)) DropLoot(n);
        }
    }
    IEnumerable<int> Regrown(long tick) { _server.ForageHost.CollectRegrown(tick, _regrown); return _regrown; }
    bool TryGetDrop(int index, out ushort item, out byte min, out byte max)
    {
        item = 0; min = max = 0;
        if (!_drops.TryGetValue(index, out var d)) return false;
        item = checked((ushort)Int(d, "dropItemId")); min = checked((byte)Int(d, "dropMin")); max = checked((byte)Int(d, "dropMax"));
        return max >= min;
    }
    bool TryGetWorldPos(int index, out Godot.Vector3 pos)
    {
        pos = default; if (!_drops.TryGetValue(index, out var d)) return false;
        var p = Vec(d, "position"); pos = new(p.x, p.y, p.z); return true;
    }
}
