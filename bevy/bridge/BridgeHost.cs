using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SDG.NetTransport.Mem;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

public sealed partial class BridgeHost : IDisposable
{
    readonly Catalog _catalog = new();
    MemNetwork _network;
    NetWorldServer _server;
    NetWorldClient _client;
    PlayerMovementSim _movement = new();
    Vector3 _position, _velocity;
    float _yaw, _pitch;
    byte _recov;
    ushort _held;
    bool _grounded = true;
    bool _movingInput;
    float _footNoiseTimer;
    bool _allowCheats = true;
    /// WorldBuilder.ZombiesDisabled: the menu's Zombies row arrives as init
    /// `zombies` (ZombiesOverride); without it the source env switches apply.
    bool _zombiesDisabled;
    public static bool ZombiesDisabledFor(JsonElement r) =>
        r.TryGetProperty("zombies", out var z) && z.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? !z.GetBoolean()
            : Environment.GetEnvironmentVariable("UG_NOZOMBIES") == "1"
              || Environment.GetEnvironmentVariable("UG_DEDICATED_NOZOMBIES") == "1";
    string _name = "Survivor", _map = "bevy";
    int _correctionVersion;
    bool _ownerReady;
    Vector3 _listenSpawn;
    readonly List<BridgeEvent> _events = new();
    long _eventSequence;
    readonly WorldGeometry _geometry = new();
    readonly Dictionary<int, InfiniteTerrain> _terrains = new();
    PlayerStanceSim _stance = new();
    RetailLootStreamer _loot;
    WorldEnvironment _environment;
    WorldServices _services;
    VehicleServices _vehicles;
    VehicleLightingServices _vehicleLights;
    VehicleMaintenanceServices _vehicleMaintenance;
    VehicleDoorServices _vehicleDoors;
    VehicleHitchServices _vehicleHitches;
    VehicleTowServices _vehicleTows;
    BodyCombatServices _bodies;
    WeaponServices _weapons;
    CosmeticBulletServices _cosmetic;
    WarheadServices _warheads;
    MountedWeaponServices _mounted;
    NpcHeliServices _npcHelis;
    PlayerAppearanceNetSync _appearance;
    readonly Dictionary<uint, byte> _vehicleRecov = new();
    readonly Dictionary<uint, VehicleRecovEvent> _vehicleRecovery = new();
    readonly List<JsonElement> _serviceSpecs = new();
    WildlifeServices _wildlife;
    ZombieServices _zombies;
    int _seed = 1337;
    bool _worldChanged;
    sealed record BridgeSave(int Seed, RetailLootStreamer.SaveData Loot, JsonElement[] Services, ResourceServices.Saved[] Resources = null, WorldServices.PropDoorSaved[] ObjectDoors = null, BodyCombatServices.Saved[] Bodies = null, VehicleServices.PartsSaved[] VehicleParts = null, double OriginX = 0, double OriginZ = 0, SavedProjectile[] Projectiles = null, WildlifeServices.Saved Wildlife = null, ZombieServices.Saved Zombies = null, WeaponServices.Saved[] Weapons = null, CosmeticBulletServices.WaterSaved[] CosmeticWater = null, WarheadServices.Saved[] StuckProjectiles = null, string Holiday = null, MountedWeaponServices.Saved[] VehicleMounts = null, NpcHeliServices.Saved NpcHelis = null, VehicleLightingServices.Saved[] VehicleLights = null, VehicleMaintenanceServices.Saved[] VehicleMaintenance = null, VehicleDoorServices.Saved[] VehicleDoors = null, VehicleHitchServices.Saved[] VehicleHitches = null);
    public sealed record BridgeEvent(long Seq, string Kind, object Data, long Tick);
    void Emit(string kind, object data) => _events.Add(new(++_eventSequence, kind, data, _server?.Session.CurrentTick ?? _client?.Applier.LastAppliedServerTick ?? 0));
    static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, PropertyNameCaseInsensitive = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    static readonly Dictionary<string, MethodInfo> Actions = typeof(NetWorldClient).GetMethods()
        .Where(m => m.Name.StartsWith("Send") && m.IsPublic && !m.Name.Contains("ForTest"))
        .ToDictionary(m => char.ToLowerInvariant(m.Name[4]) + m.Name[5..], StringComparer.OrdinalIgnoreCase);

    public object Handle(string op, JsonElement r)
    {
        if (op != "init" && r.TryGetProperty("originEpoch", out var frame) && frame.GetInt64() != _originEpoch)
            throw new InvalidOperationException($"Origin epoch mismatch: expected {_originEpoch}, received {frame.GetInt64()}");
        if (r.TryGetProperty("ackEvents", out var ack))
        {
            long last = ack.GetInt64();
            if (last > _eventSequence) throw new ArgumentException("Event acknowledgement exceeds emitted sequence");
            _events.RemoveAll(e => e.Seq <= last);
        }
        CheckRemoteOperation(op);
        switch (op)
        {
            case "serverProbeStart": return _serverProbe.Start(Text(r, "host"), Int(r, "port", 47872));
            case "serverProbePoll": return _serverProbe.Poll(r.GetProperty("token").GetInt64());
            case "serverProbeCancel": return _serverProbe.Cancel(r.GetProperty("token").GetInt64());
            case "localProfile": return ReadLocalProfile(r);
            case "bugReport": return _bugReports.Write(r);
            case "bugReportRetry": return _bugReports.Retry(r);
            case "bugReportPoll": return BugReportPoll();
            case "finiteMap": return ReadFiniteMap(r);
            case "buildingView": return ReadBuildingView(r);
            case "finitePopulation": RequireSession(); return RegisterFinitePopulation(r);
            case "setLocalFace": return SetLocalFace(r);
            case "profileRules": return ReadProfileRules(r);
            case "profileAvatar": RequireSession(); return ReadProfileAvatar(r);
            case "reconnect": return ReconnectRemote();
            case "init":
                Dispose(); _geometry.Clear(); _serviceSpecs.Clear(); _originX = _originZ = 0; _originEpoch = 0;
                ConfigureNetwork(r);
                ConfigurePersistence(r);
                _catalog.Load(Text(r, "contentRoot", Path.GetFullPath("game/content")));
                ConfigureLocalProfile(r); _map = Text(r, "mapId", "bevy");
                _seed = Int(r, "seed", 1337);
                _allowCheats = Bool(r, "allowCheats", _listenPort == 0 && Environment.GetEnvironmentVariable("UG_DEDICATED_NOCHEATS") != "1");
                _zombiesDisabled = ZombiesDisabledFor(r);
                if (r.TryGetProperty("origin", out var initialOrigin))
                {
                    if (initialOrigin.GetArrayLength() != 2) throw new ArgumentException("origin must be [doubleX,doubleZ]");
                    _originX = initialOrigin[0].GetDouble(); _originZ = initialOrigin[1].GetDouble();
                    if (!double.IsFinite(_originX) || !double.IsFinite(_originZ)) throw new ArgumentException("origin must be finite");
                }
                CheckNetworkOrigin(_originX, _originZ);
                _position = Vec(r, "spawn", new(0, 1, 0));
                CheckNetworkSpawn(_position); _listenSpawn = _position;
                _velocity = Vector3.zero; _held = 0; _yaw = Float(r, "yaw"); _pitch = 0; _grounded = true; _correctionVersion++;
                PrepareFiniteRespawns(r);
                Start();
                if (Bool(r, "starterKit")) SeedStarterKit();
                for (int n = 0; n < 6; n++) Tick();
                return new { protocolVersion = 1, fixedDelta = SimClock.FixedDelta, catalogItems = _catalog.Count,
                    recipes = _catalog.Recipes.Count, movementHeights = new { stand = PlayerMovementDef.HEIGHT_STAND, crouch = PlayerMovementDef.HEIGHT_CROUCH, prone = PlayerMovementDef.HEIGHT_PRONE }, network = NetworkState(), capabilities = Capabilities(),
                    actions = Actions.Keys.Append("respawn").Append("setFiremode").Append("holdItem").Append("equipAndHold").Order().ToArray() };
            case "rebase": return Rebase(r);
            case "craftingView": RequireSession(); return ReadCrafting(r);
            case "inventoryView": RequireSession(); return ReadInventory(r);
            case "simulate":
                RequireSession();
                return Simulate(r.TryGetProperty("input", out var input) ? input : r);
            case "tick":
                RequireSession();
                if (r.TryGetProperty("input", out var body))
                {
                    ReadSteady(body); ReadTemperatureProbe(body); ReadLookEnd(body);
                    _position = Vec(body, "position", _position); _velocity = Vec(body, "velocity", _velocity);
                    _grounded = Bool(body, "grounded", _grounded);
                    _yaw = Float(body, "yaw", _yaw); _pitch = Float(body, "pitch", _pitch);
                    _held = checked((ushort)Int(body, "heldItemId", _held));
                    if (body.TryGetProperty("stance", out _)) _movement.Stance = Stance(Text(body, "stance", "stand"));
                    _movement.Velocity = WorldToLocal(_velocity, _yaw);
                }
                Tick(); return new { fixedDelta = SimClock.FixedDelta };
            case "snapshot": RequireSession(); return null;
            case "disconnect":
                RequireSession();
                if (_server != null) Dispose(); else { _client.Disconnect(); _remoteMounted?.Clear(); }
                return new { disconnected = true, ownsWorld = false };
            case "diagnostics": RequireSession(); return new { host = _server != null, liveBullets = _server?.Combat.LiveBullets, stuckProjectilePoses = _warheads?.LivePoseCount,
                // CommandTakeFromStorage verdicts (ServerTransactionsDiagnostics), host only.
                shelfTakes = _server == null ? null : new { applied = _server.Transactions.Diag.ShelfTakesApplied, rejected = _server.Transactions.Diag.ShelfTakesRejected } };
            case "catalog": RequireSession(); return new { items = _catalog.Metadata(), wildlife = WildlifeServices.Catalog(), gestures = GestureMetadata(), profileRules = ProfileMetadata(), bulletVisuals = CosmeticBulletServices.Metadata(), mountedWeapons = _catalog.Mounted.GunMetadata(), npcCrew = NpcHeliCrewBody.Metadata(), vehicleLighting = VehicleLightingServices.Metadata(), vehicleMaintenance = VehicleMaintenanceServices.Metadata(), throwMotion = new {
                speed = HandRules.ThrowSpeed, lift = HandRules.ThrowLift, weakScale = HandRules.WeakThrowScale,
                releaseFraction = HandRules.ThrowReleaseFraction, cooldown = HandRules.ThrowCooldown,
                clearProbe = HandRules.ThrowClearProbe, clearGap = HandRules.ThrowClearGap,
                fuseSeconds = Throwables.FuseSeconds, useSeconds = _catalog.Clip("", "TU_0") }, deployables = DeployableDef.All, containers = ContainerSchema.All,
                crops = FarmRegistry.All.Select(d => new { seedId = d.Id, growthSeconds = d.Growth, yieldItemId = d.Grow, name = CropRegistry.TryBySeed(d.Id, out string name) ? name : null }).ToArray() };
            case "attachmentOptions": RequireSession(); return IsRemote ? _remoteWeapon.AttachmentOptions() : _weapons.AttachmentOptions(_client.PlayerId);
            case "powerView": RequireSession(); return ReadPowerGraph();
            case "npcView": RequireSession(); return ReadNpcView(r);
            case "npcCatalog": RequireSession(); return new { characters = NpcCatalog.Characters.Select(NpcMetadata.Character).ToArray(), dialogues = NpcCatalog.Dialogues, vendors = NpcCatalog.Vendors, quests = NpcCatalog.Quests };
            case "worldVehicles":
                RequireSession();
                var fleet = _vehicles.Register(r); _mounted.SyncFleet(); _serviceSpecs.Add(r.Clone()); _worldChanged = true; return fleet;
            case "worldNpcHelis": RequireSession(); return _npcHelis.Configure(r);
            case "worldMountedInput": RequireSession(); return _mounted.HostInput(r);
            case "worldVehicleLighting": RequireSession(); return _vehicleLights.RegisterGeometry(r);
            case "worldVehicleGlazing": RequireSession(); return _vehicleMaintenance.Register(r);
            case "worldVehicleState": RequireSession(); return _vehicles.Publish(r);
            case "worldVehicleDamage": RequireSession(); return _bodies.HostVehicleDamage(r);
            case "worldVehicleExit": RequireSession(); return _vehicles.ExitProbe(r);
            case "mapBindings": RequireSession(); return ReadMapBindings(r);
            case "finiteServices":
            case "mapServices":
                RequireSession();
                bool finiteServices = op == "finiteServices";
                string mapKey = finiteServices ? "finite|" + Path.GetFullPath(Text(r, "directory"))
                    : Text(r, "mapName", "PEI") + "|" + Text(r, "mapRoot");
                if (_services.MapKey == mapKey) return _services.MapResult;
                if (_services.MapKey != null) throw new InvalidOperationException("A different map requires init");
                var parsed = finiteServices ? MapServices.ParseFinite(r, _worldHoliday) : MapServices.Parse(r, _worldHoliday);
                var mapSpec = JsonNode.Parse(parsed.Spec.GetRawText());
                mapSpec["mapKey"] = mapKey;
                mapSpec["mapObjects"] = JsonSerializer.SerializeToNode(parsed.Objects);
                mapSpec["mapResources"] = JsonSerializer.SerializeToNode(parsed.Resources);
                var persistedSpec = JsonSerializer.SerializeToElement(mapSpec);
                if (_originX != 0 || _originZ != 0) persistedSpec = CoordinateJson.ServiceSpec(persistedSpec, new((float)-_originX, 0, (float)-_originZ));
                var mapResult = _services.Register(persistedSpec); AdoptFiniteRespawns(persistedSpec); _serviceSpecs.Add(persistedSpec); _worldChanged = true; return mapResult;
            case "worldServices":
                RequireSession();
                var registration = _services.Register(r); _serviceSpecs.Add(r.Clone()); _worldChanged = true; return registration;
            case "action":
                RequireSession();
                return Action(Text(r, "name"), r.TryGetProperty("args", out var args) ? args : default);
            case "save":
                RequireSession();
                string path = WritablePathFor(r);
                var save = WriteWorldSave(path);
                return new { path, version = save.Version };
            case "load":
                RequireSession();
                string loadPath = SavePathFor(r);
                // Any load attempt ends the entry hold: a refused save leaves the fresh world
                // running and autosaving, as WorldSaveDriver.LoadIntoWorld does.
                _restorePending = false;
                string loadJson = File.ReadAllText(loadPath);
                if (!WorldSave.TryParse(loadJson, _map, out var loaded, out var error)) throw new InvalidDataException(error);
                var extra = JsonNode.Parse(loadJson)?["BevyBridge"]?.Deserialize<BridgeSave>();
                double loadedX = extra?.OriginX ?? 0, loadedZ = extra?.OriginZ ?? 0;
                CheckNetworkOrigin(loadedX, loadedZ);
                if (extra != null) _seed = extra.Seed;
                var loadDelta = new Vector3((float)(_originX - loadedX), 0, (float)(_originZ - loadedZ));
                _geometry.TranslateWorld(loadDelta); _position += loadDelta;
                _originX = loadedX; _originZ = loadedZ; _originEpoch++;
                _serviceSpecs.Clear(); if (extra?.Services != null) _serviceSpecs.AddRange(extra.Services);
                _worldHoliday = extra?.Holiday ?? _worldHoliday;
                AdoptSavedWorldStatics(loaded);
                Dispose(); Start(loaded, extra?.VehicleLights, extra?.VehicleMaintenance, extra?.VehicleDoors, extra?.VehicleHitches);
                RestoreProjectiles(extra?.Projectiles);
                _wildlife.Restore(extra?.Wildlife);
                _zombies.Restore(extra?.Zombies);
                _weapons.Restore(extra?.Weapons);
                _cosmetic.RestoreWater(extra?.CosmeticWater);
                _warheads.Restore(extra?.StuckProjectiles);
                _mounted.Restore(extra?.VehicleMounts);
                _npcHelis.Restore(extra?.NpcHelis);
                _services.Harvesting.Restore(extra?.Resources);
                _services.RestorePropDoors(extra?.ObjectDoors); _bodies.Restore(extra?.Bodies); _vehicles.RestoreParts(extra?.VehicleParts);
                if (extra?.Loot != null)
                {
                    _loot = RetailLootStreamer.FromSave(extra.Loot, _server, _geometry,
                        new((float)_originX, 0, (float)_originZ));
                    _loot.Step(force: true);
                }
                for (int n = 0; n < 6; n++) Tick();
                return new { version = loaded.Version, path = loadPath, message = LoadedLine(loadPath, loaded) };
            case "terrain":
                var terrain = Terrain(Int(r, "seed", 1337));
                var coord = new RegionCoord(Int(r, "x"), Int(r, "z"));
                var region = terrain.Generate(coord, Math.Clamp(Int(r, "lod"), 0, 6));
                return new { origin_x = coord.MinX, origin_z = coord.MinZ, cells = region.Cells, spacing = region.Spacing,
                    heights = region.Heights, normals = region.Normals, layers = region.Layers.Select(b => (int)b).ToArray(),
                    trees = region.Trees, roads = region.Roads, foliage = region.Foliage, boulders = region.Boulders,
                    roadClear = region.RoadClear, roadPatches = region.RoadPatches, roadMarks = region.RoadMarks, stopSigns = region.StopSigns,
                    poles = region.Poles, pylons = region.Pylons?.Select(p => new { x = p.X, z = p.Z, h = p.H, dirX = p.DirX, dirZ = p.DirZ, wired = p.Wired?.Select(w => new { x = w.X, z = w.Z, h = w.H, dirX = w.DirX, dirZ = w.DirZ }) }), bridges = region.Bridges, rails = region.Rails, levelCrossings = region.LevelCrossings,
                    crossingSigns = region.CrossingSigns, tunnels = region.Tunnels, tunnelMouths = region.TunnelMouths, holes = region.Holes,
                    overTunnel = region.OverTunnel, tunnelRocks = region.TunnelRocks,
                    tunnelStyles = Enum.GetValues<RoadKind>().Select(k => new { kind = (int)k,
                        lateral = InfiniteRoads.TunnelLateralOf(k), vertical = InfiniteRoads.TunnelVerticalOf(k), feet = InfiniteRoads.TunnelFloorDropOf(k),
                        reach = InfiniteRoads.ShellReachOf(k), boreReach = InfiniteRoads.BoreReachOf(k), bedBelow = InfiniteRoads.TunnelBedBelow(k),
                        tubeOffsets = Enumerable.Range(0, InfiniteRoads.TunnelTubes(k)).Select(i => InfiniteRoads.TubeOffset(k, i)),
                        shellProfile = Enumerable.Range(0, (int)Math.Ceiling(InfiniteRoads.ShellReachOf(k) * 8) + 1).Select(i => {
                            float lateral = -InfiniteRoads.ShellReachOf(k) + Math.Min(i * .25f, InfiniteRoads.ShellReachOf(k) * 2);
                            float top = InfiniteRoads.ShellTopOf(k, lateral); return new { lateral, top = float.IsFinite(top) ? (float?)top : null };
                        }) }),
                    treeModels = Enum.GetValues<InfiniteTerrain.TreeKind>().Select(k => Enumerable.Range(0, InfiniteTerrain.TreeVariants(k)).Select(v => InfiniteTerrain.TreeModel(k, v)).ToArray()).ToArray(),
                    boulderModels = InfiniteTerrain.BoulderKit.Select(b => new { name = b.Name, radius = b.Radius, bottom = b.Bottom, height = b.Height }), seaLevel = InfiniteTerrain.SeaLevel };
            case "worldGeometry":
                if (Bool(r, "replace", true)) _geometry.Clear();
                if (r.TryGetProperty("terrain", out var terrainSpec))
                    _geometry.AddTerrain(Terrain(Int(terrainSpec, "seed", 1337)).Generate(new RegionCoord(Int(terrainSpec, "x"), Int(terrainSpec, "z")), Math.Clamp(Int(terrainSpec, "lod"), 0, 6)), _originX, _originZ);
                if (r.TryGetProperty("terrainPatches", out var patches)) foreach (var patch in patches.EnumerateArray())
                {
                    var origin = patch.GetProperty("origin");
                    _geometry.AddHeightfield(origin[0].GetSingle(), origin[1].GetSingle(), Int(patch, "cells"), Float(patch, "spacing"),
                        patch.GetProperty("heights").EnumerateArray().Select(h => h.GetSingle()).ToArray(),
                        patch.TryGetProperty("holes", out var holes) && holes.ValueKind == JsonValueKind.Array ? holes.EnumerateArray().Select(h => h.GetBoolean()).ToArray() : null);
                }
                if (r.TryGetProperty("terrainLayers", out var layerPatches)) foreach (var patch in layerPatches.EnumerateArray()) _geometry.AddLayers(patch);
                if (r.TryGetProperty("meshes", out var meshes)) foreach (var mesh in meshes.EnumerateArray())
                    _geometry.AddMesh(mesh.GetProperty("vertices").EnumerateArray().Select(Vec).ToArray(),
                        mesh.TryGetProperty("indices", out var ix) ? ix.EnumerateArray().Select(v => v.GetInt32()).ToArray() : Enumerable.Range(0, mesh.GetProperty("vertices").GetArrayLength()).ToArray(), Int(mesh, "destructibleIndex", -1), Bool(mesh, "terrain"), Int(mesh, "resourceIndex", -1), vehicle: checked((uint)Int(mesh, "vehicleNetId")), deployable: checked((uint)Int(mesh, "deployableNetId")), bodyPart: Text(mesh, "bodyPart", "hull"), thin: Bool(mesh,"thin"), water: Bool(mesh,"water"),
                        sourceWorld: mesh.TryGetProperty("sourceWorld", out var worldLayerStatic) ? worldLayerStatic.GetBoolean() : null, combat: Bool(mesh,"combat",true));
                if (Bool(r, "finalize", true)) _geometry.Build();
                return new { triangles = _geometry.Count };
            case "worldLoot":
                RequireSession();
                string mapRoot = Path.GetFullPath(Text(r, "mapRoot"));
                if (_loot == null || _loot.MapRoot != mapRoot)
                {
                    _loot = new RetailLootStreamer(mapRoot, _server, _geometry);
                    _loot.TranslateWorld(new((float)-_originX, 0, (float)-_originZ));
                }
                _loot.Step(force: true);
                return new { points = _loot.PointCount, live = _loot.LiveCount };
            case "worldWildlife": RequireSession(); return _wildlife.Register(r, _originX, _originZ);
            case "worldCosmeticGeometry": RequireSession(); return _cosmetic.RegisterGeometry(r);
            // A spawn the world never makes cannot be streamed, ticked or replicated.
            case "worldZombies": RequireSession(); return _zombiesDisabled ? new { disabled = true, chunks = 0, points = 0 } : _zombies.Register(r, _originX, _originZ);
            case "worldZombieState": RequireSession(); return _zombies.Publish(r);
            case "worldZombieNoise": RequireSession(); _zombies.Noise(Vec(r,"position"),Float(r,"radius")); return new{queued=true};
            case "worldSpawns":
                return new { players = LevelSpawns.PlayerSpawns(Text(r, "mapRoot")).Select(s => new { x = s.x, z = s.z, yaw = s.yaw }).ToArray() };
            case "worldSpawn":
                RequireSession();
                var spawned = new List<object>();
                if (r.TryGetProperty("items", out var items)) foreach (var item in items.EnumerateArray())
                {
                    ushort itemId = checked((ushort)Int(item, "itemId"));
                    if (Assets.find(itemId) == null) throw new ArgumentException($"Unknown item {itemId}");
                    var instance = new Item(itemId, checked((ushort)Int(item, "amount", 1)), checked((byte)Int(item, "quality", 100)));
                    if (item.TryGetProperty("fuelLevel", out var fuel)) instance.fuelLevel = fuel.GetSingle();   // fixture seam: a filled ground can
                    var entity = _server.WorldItems.ServerSpawn(_server.Ids.Mint(), instance, Vec(item, "position"), _server.Session.CurrentTick);
                    _server.WorldItems.ServerSettle(entity.NetIdValue, entity.Pos, _server.Session.CurrentTick);
                    spawned.Add(new { netId = entity.NetIdValue, itemId });
                }
                return new { entities = spawned };
            case "worldDynamicGeometry":
                RequireSession();
                var dynamicGeometry = new WorldGeometry();
                foreach (var mesh in r.GetProperty("meshes").EnumerateArray())
                {
                    var verts = mesh.GetProperty("vertices").EnumerateArray().Select(Vec).ToArray();
                    dynamicGeometry.AddMesh(verts, mesh.TryGetProperty("indices", out var dynamicIx)
                        ? dynamicIx.EnumerateArray().Select(v => v.GetInt32()).ToArray() : Enumerable.Range(0, verts.Length).ToArray(),
                        Int(mesh, "destructibleIndex", -1), resource: Int(mesh, "resourceIndex", -1), vehicle: checked((uint)Int(mesh, "vehicleNetId")), deployable: checked((uint)Int(mesh, "deployableNetId")), bodyPart: Text(mesh, "bodyPart", "hull"), thin: Bool(mesh,"thin"), water: Bool(mesh,"water"),
                        sourceWorld: mesh.TryGetProperty("sourceWorld", out var worldLayerDynamic) ? worldLayerDynamic.GetBoolean() : null, combat: Bool(mesh,"combat",true));
                }
                if (r.TryGetProperty("capsules", out var capsules)) foreach (var capsule in capsules.EnumerateArray())
                    dynamicGeometry.AddCapsule(Vec(capsule, "foot"), Float(capsule, "radius"), Float(capsule, "height"));
                dynamicGeometry.Build(); _geometry.SetDynamic(Text(r, "key"), dynamicGeometry);
                return new { key = Text(r, "key"), triangles = dynamicGeometry.Count };
            case "worldRay":
                bool hit = _geometry.RayDetailed(Vec(r, "from"), Vec(r, "to"), out var rayHit);
                return new { hit, point = Array(rayHit.Point), destructibleIndex = rayHit.DestructibleIndex, resourceIndex = rayHit.ResourceIndex, vehicleNetId = rayHit.VehicleNetId, deployableNetId = rayHit.DeployableNetId, bodyPart = rayHit.BodyPart };
            case "shutdown": return null;
            default: throw new ArgumentException($"Unknown bridge operation: {op}");
        }
    }

    void Start(WorldSave save = null, VehicleLightingServices.Saved[] lights = null, VehicleMaintenanceServices.Saved[] maintenance = null, VehicleDoorServices.Saved[] doors = null, VehicleHitchServices.Saved[] hitches = null)
    {
        try { StartSession(save, lights, maintenance, doors, hitches); }
        catch { Dispose(); throw; }
    }

    void StartSession(WorldSave save, VehicleLightingServices.Saved[] lights, VehicleMaintenanceServices.Saved[] maintenance, VehicleDoorServices.Saved[] doors, VehicleHitchServices.Saved[] hitches)
    {
        _scopeSteady.Reset(); _wantsSteady = false; _localProfileSent = false; _ownerReady = false; ResetTemperature();
        _worldChanged = false; _geometry.ClearDynamic();
        _network = new MemNetwork(); _movement = new PlayerMovementSim(); _stance = new PlayerStanceSim(); _recov = 0; _events.Clear(); _vehicleRecov.Clear(); _vehicleRecovery.Clear(); _velocity = Vector3.zero; _lastRecipeState = null; _cachedRecipes = null;
        if (IsRemote) { StartRemote(); return; }
        _server = new NetWorldServer(ServerTransport(), maxPeers: _maxPeers,
            contentHash: NetContent.Hash, activeHoliday: _worldHoliday, serverName: _serverName, maxPlayers: _maxPeers, gamemode: "Survival");
        _server.Transactions.AllowCheats = _allowCheats;
        InstallPersistenceHandlers();
        _server.PlayerHost.AcceptSeatedActions = true;
        _server.VehicleHost.RepositionExitedOwner = _server.PlayerHost.RepositionOwner;
        if (_listenPort == 0) WorldRebase.EnableUnboundedStorage(_server);
        _server.Transactions.RepositionOwner = _server.PlayerHost.RepositionOwner;
        _environment = new WorldEnvironment(_server, _seed, save);
        // WindField.SampleWind for the server's turbines: the source noise and weather floor on server time.
        // UG_WIND is the source's own TestWind override ("flag droop / turbine tests").
        var wind = new SourceWindField();
        float? forcedWind = float.TryParse(Environment.GetEnvironmentVariable("UG_WIND"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float forced) ? forced : null;
        _server.Power.WindAt = p => forcedWind ?? wind.Sample(p.x, p.z, (float)(_server.Session.CurrentTick * SimClock.FixedDelta), Math.Clamp(_environment.Sim.WindMain, 0f, 1f));
        // UDP late joins use the initial map spawn, including after a restore restart.
        var initialSpawn = _listenSpawn;
        _server.SpawnProvider = _listenPort == 0 ? _ => _position : _ => initialSpawn;
        _geometry.DestructibleAlive = i => i >= _server.Destructibles.Count || _server.Destructibles.IsAlive(i);
        _geometry.ResourceAlive = i => i >= _server.Resources.Count || _server.Resources.IsAlive(i);
        _server.Combat.WorldRayDetailed = _geometry.RayDetailed;
        _server.CraftingStationsAt = (_, eye) => CraftingStations.At(_server, _geometry, eye);
        _server.CraftingEyeAllowed = (owner, eye) => CraftingStations.EyeAllowed(_server,_geometry,owner,eye);
        _server.Combat.WorldRay = _geometry.Ray;
        _server.Combat.GroundHeight = _geometry.GroundHeight;
        _server.Combat.ResolveHeldGunProfile = owner =>
            _server.Players.TryGetHeldInput(owner, out var held) && _catalog.Guns.TryGetValue(held.HeldItemId, out var gun)
                && _server.Inventories.TryGet(owner, out var owned) && owned.Inventory.getItemCount(held.HeldItemId) > 0 ? gun : null;
        DeployableNetSchema.RegisterAll(_server.Deployables.Schema);
        _services = new WorldServices(_server, _catalog, Emit);
        _services.AttachBarricadeDamage(_geometry);
        _server.Npcs.ActiveHoliday = () => _worldHoliday;
        _vehicles = new VehicleServices(_server, _geometry, Emit); _vehicleRecov.Clear();
        _bodies = new BodyCombatServices(_server, _geometry, _vehicles, Emit);
        _vehicleLights = new VehicleLightingServices(_server, _catalog, _vehicles, _bodies, new Random(_seed).NextDouble); _vehicles.Lighting = _vehicleLights;
        _vehicleMaintenance = new VehicleMaintenanceServices(_server, _catalog, _vehicleLights, _geometry); _vehicles.Maintenance = _vehicleMaintenance;
        _vehicleDoors = new VehicleDoorServices(_server, _catalog, _geometry);
        _vehicleHitches = new VehicleHitchServices(_server, _catalog, _geometry);
        _vehicleTows = new VehicleTowServices(_server, _catalog, () => _client?.PlayerId ?? 0);
        _ = new VehicleHandToolServices(_server, Emit);
        _ = new VehicleFuelServices(_server, _catalog, _vehicles);
        _wildlife = new WildlifeServices(_server, _geometry, Emit);
        _zombies = new ZombieServices(_server, _geometry, Emit);
        _services.DoorNoise = _zombies.Noise;
        _vehicleLights.Noise = _zombies.Noise; _vehicleMaintenance.Noise = _zombies.Noise;
        _zombies.RainIntensity=()=>_environment.Sim.BlendAlpha*_environment.Severity;
        _weapons = new WeaponServices(_server, _catalog, Emit);
        _cosmetic = new CosmeticBulletServices(_server,_geometry,_weapons,Emit);
        _warheads = new WarheadServices(_server,_weapons,_cosmetic,Emit);
        _mounted = new MountedWeaponServices(_server,_catalog,_cosmetic);
        _server.Combat.MountedRayDetailed=MountedWorldRay;
        _npcHelis = new NpcHeliServices(_server,_catalog,_mounted,_geometry,_services.Harvesting.Canopies,MountedWorldRay,
            row=>{
                var request=JsonSerializer.SerializeToElement(new{replace=false,vehicles=new[]{row}});
                var result=JsonSerializer.SerializeToElement(_vehicles.Register(request));_mounted.SyncFleet();
                _serviceSpecs.Add(request);_worldChanged=true;return result.GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
            },new((float)-_originX,0,(float)-_originZ));
        _appearance = new PlayerAppearanceNetSync(_server);
        _weapons.Noise=(owner,radius)=>{if(_server.Players.TryGetByOwner(owner,out var p))_zombies.Noise(p.Pos,radius);};
        _movingInput=false;_footNoiseTimer=0;
        _bodies.RepairRate = _weapons.RepairRate;
        _bodies.RequestSalvage = id => _client.SendSalvageDeployable(id);
        _server.Combat.CanThrow = (owner, id) => _server.Inventories.TryGet(owner, out var bag) && bag.Inventory.getItemCount(id) > 0;
        _server.Combat.ThrowAccepted = (owner, id, netId, tick) => Emit("throwAccepted", new { playerId = owner, itemId = id, netId, tick });
        _server.Transactions.ConsumeApplied = (owner, cmd, id) => Emit("consumeAccepted", new { playerId = owner, itemId = id, page = cmd.Page, x = cmd.X, y = cmd.Y, tick = _server.Session.CurrentTick });
        _server.Vitals.SurvivalDrain = true;
        foreach (var spec in _serviceSpecs)
        {
            if (spec.TryGetProperty("vehicles", out _)) _vehicles.Register(spec); else _services.Register(spec);
            AdoptFiniteRespawns(spec);
            AdoptFinitePopulation(spec);
        }
        _server.Transactions.Blueprints = _catalog.Recipes;
        ConfigureInventoryTransactions();
        ConfigureNearby();
        _server.PendingSave = save;
        save?.ApplyWorld(_server, 0);
        _vehicles.RestoreFuel(save);
        _vehicleLights.AdoptSavedBatteries(save);
        _vehicleLights.Restore(lights); _vehicleMaintenance.Restore(maintenance); _vehicleDoors.Restore(doors); _vehicleHitches.Restore(hitches);
        _services.AdoptSavedBits();
        CreateClient(LocalTransport());
        // Accept precedes the reliable owner snapshot. Publishing the old
        // native pose in that interval can overwrite a nearby saved position
        // (and yaw) before we ever read the restored owner.
        for (int n = 0; n < 100 && (_client.State != NetSessionState.Connected
            || !_client.Players.TryGetByOwner(_client.PlayerId, out _)); n++) Tick();
        if (_client.State != NetSessionState.Connected || !_client.Players.TryGetByOwner(_client.PlayerId, out var player))
            throw new InvalidOperationException("Local server owner snapshot did not arrive");
        _position = player.Pos;
        if (save != null) { _yaw = player.YawDegrees; _correctionVersion++; }
        _ownerReady = true;
        for (int n = 0; n < 4; n++) Tick();
    }

    object Simulate(JsonElement input)
    {
        ReadSteady(input);
        _movingInput=Math.Abs(Float(input,"moveX"))>.01f||Math.Abs(Float(input,"moveY"))>.01f;
        _yaw = Float(input, "yaw", _yaw); _pitch = Float(input, "pitch", _pitch);
        _grounded = Bool(input, "grounded", _grounded);
        _held = checked((ushort)Int(input, "heldItemId", _held));
        var wanted = Stance(Text(input, "stance", "stand"));
        _client.Vitals.TryGet(_client.PlayerId, out var ownerVitals);
        _movement.Stance = _stance.Step(Bool(input, "crouchKey"), Bool(input, "proneKey"),
            Bool(input, "sprintKey", wanted == EPlayerStance.SPRINT), ownerVitals?.Sim.Stamina ?? 1f, ownerVitals?.Broken ?? false,
            input.TryGetProperty("stance", out _) ? wanted == EPlayerStance.SPRINT ? EPlayerStance.STAND : wanted : null,
            Float(input, "capsuleHeight", PlayerMovementDef.HeightForStance(_movement.Stance)), _ => Bool(input, "headroom", true), Bool(input, "equipmentAllowsSprint", true));
        _movement.SpeedMultiplier = PlayerMovementSim.SpeedMultiplierForHeld(_held);
        _movement.GravityMultiplier = Float(input, "gravityMultiplier", 1);
        if (input.TryGetProperty("velocity", out _)) _movement.Velocity = WorldToLocal(Vec(input, "velocity"), _yaw);
        _velocity = LocalToWorld(_movement.Step(new(Float(input, "moveX"), Float(input, "moveY")), Bool(input, "jump"), _grounded, (float)SimClock.FixedDelta), _yaw);
        return new { velocity = Array(_velocity), height = PlayerMovementDef.HeightForStance(_movement.Stance), eyeHeight = PlayerMovementDef.EyeHeightForStance(_movement.Stance), stance = _movement.Stance.ToString().ToLowerInvariant(), jumpLocked = _movement.JumpLocked };
    }

    void Tick()
    {
        if (IsRemote) { TickRemote(); return; }
        StepSteady();
        PublishLocalProfile();
        // Automatic death timer and explicit respawn use the same source target.
        // Make that target representable before core emits its bounded recovery packet.
        if (_server.CombatState.TryGet(_client.PlayerId, out var life) && !life.Alive
            && life.RespawnAtTick == _server.Session.CurrentTick + 1) PrepareRespawnFrame();
        if (_ownerReady) TemperatureTick();
        if (_ownerReady && _client.State == NetSessionState.Connected)
            _client.SendPlayerState(_position, _yaw, _pitch, _velocity,
                (byte)((StanceCode(_movement.Stance) << 1) | (_scopeSteady.Steadying ? MoveInput.ButtonSteady : 0)), _grounded, _recov, _held, TemperatureBand);
        _network.Tick(); _client.Tick(); _server.TickSimulation(); _weapons.Step(); _npcHelis.Step(); _mounted.Step(); _cosmetic.Step();
        _warheads.Prune();
        if (_weapons.TakeSelectionChange(_client.PlayerId) is ushort selected) _held = selected;
        if(_server.CombatState.TryGet(_client.PlayerId,out var living)&&living.Alive&&!_server.VehicleHost.IsSeated(_client.PlayerId))
        {
            _footNoiseTimer-=(float)SimClock.FixedDelta;
            if(_movingInput&&_footNoiseTimer<=0)
            {
                _footNoiseTimer=.4f;
                float noise=StealthDetection.Radius(_movement.Stance,true)*(_server.Skills.TryGet(_client.PlayerId,out var skills)?skills.Skills.SneakyBeakyNoiseMultiplier():1);
                if(noise>2)_zombies.Noise(_position,noise);
            }
        }
        _loot?.Step(); _services.Step(); _bodies.Step(); _vehicleHitches.Step(); _vehicleTows.Step(); StepFarmSpeed(); _vehicleLights.Step(); _vehicleMaintenance.Step(); _vehicleDoors.Step(); _wildlife.Step(); _zombies.Step(); _environment.Step(_server);
        _appearance.Tick();
        if (_worldChanged) PublishFullSnapshot();
        TickAutosave();
        _server.TickReplication();
        if (_udpTransport != null) _udpTransport.StatusPlayerCount = _server.Session.Peers.Count;
        if (_client.Players.TryGetByOwner(_client.PlayerId, out var own)) _client.AckCombat(own.LastProcessedCombatSeq);
    }

    object Action(string name, JsonElement args)
    {
        if (name == "nearbyInventory") return RequestNearby(args);
        if (name == "nearbyOperation") return RequestNearbyOperation(args);
        if (name == "nearbyMagazineTransfer") return RequestNearbyMagazineTransfer(args);
        if (name == "craftingContext") return RequestCraftingContext(args);
        if (name == "craft" && args.TryGetProperty("eye",out _)) return RequestCraftingContext(args,craft:true);
        if (name == "vehicleHitch") return new { queued = _client.SendVehicleHitch(VehicleHitchServices.ReadCommand(args)) };
        if (name == "vehicleDoor") return new { queued = _client.SendVehicleDoor(VehicleDoorServices.ReadCommand(args)) };
        if (name == "vehicleMaintenance") return new { queued = _client.SendVehicleMaintenance(VehicleMaintenanceServices.ReadCommand(args)) };
        if (name == "vehicleLighting") return new { queued = _client.SendVehicleLighting(VehicleLightingServices.ReadCommand(args)) };
        if (name == "mountInput") return new { queued = _client.SendMountedInput(MountedWeaponServices.ReadInput(args)) };
        if (name == "depositFuel")
        {
            // TryDepositFuel: the exact held fuel container, by its accepted identity token.
            byte page = checked((byte)Int(args, "page")), x = checked((byte)Int(args, "x")), y = checked((byte)Int(args, "y"));
            bool known = _client.Inventories.TryGetItemToken(_client.PlayerId, page, x, y, out ulong token);
            return new { queued = known && _client.SendDepositFuel(checked((uint)Int(args, "netId")), page, x, y, checked((ushort)Int(args, "id")), token) };
        }
        if (name is "pourVehicleFuel" or "siphonVehicleFuel")
        {
            // v79 TryDepositFuel/TryExtractFuel vehicle branches: the exact held can by its accepted token.
            byte page = checked((byte)Int(args, "page")), x = checked((byte)Int(args, "x")), y = checked((byte)Int(args, "y"));
            uint car = checked((uint)Int(args, "vehicleNetId")); ushort id = checked((ushort)Int(args, "id"));
            bool known = _client.Inventories.TryGetItemToken(_client.PlayerId, page, x, y, out ulong token);
            return new { queued = known && (name == "pourVehicleFuel"
                ? _client.SendPourVehicleFuel(car, page, x, y, id, token) : _client.SendSiphonVehicleFuel(car, page, x, y, id, token)) };
        }
        // TryExtractFuel: RMB a gas-pump fixture; the host picks the can and gates on the pump's wired power.
        if (name == "extractFuel") return new { queued = _client.SendExtractFuel(checked((uint)Int(args, "netId"))) };
        if (name == "drinkHeldFluid") return DrinkHeldFluid(args);
        if (name == "fillHeldFluidAtTap") return FillHeldFluidAtTap(args);
        if (IsRemote) return RemoteAction(name, args);
        if (name == "requestGesture") return RequestGesture(args);
        if (name == "console" && PlayerDebugConsole(Text(args, "text"))) return new { queued = true };
        if (name == "console" && CropConsole(args, remote: false)) return new { queued = true };
        if (name == "console" && WeatherConsole(Text(args, "text"))) return new { queued = true };
        if (name == "console" && RotorConsole(Text(args, "text"))) return new { queued = true };
        if (name == "console" && _wildlife.Console(_client.PlayerId, Text(args, "text"))) return new { queued = true };
        if (name is "installAttachment" or "removeAttachment" or "chooseShellType" or "unloadShells" or "rackGun") return _weapons.AttachmentAction(_client.PlayerId,name,args);
        if (name == "toolUse") return new { queued = _bodies.ToolUse(_client.PlayerId, Bool(args, "active")) };
        if (name == "equipAndHold")
        {
            var choice = _weapons.EquipAndHold(_client.PlayerId, _client, Int(args, "page", -1), Int(args, "x", -1), Int(args, "y", -1));
            if (choice.queued && !choice.pending) _held = choice.heldItemId;
            return new { queued = choice.queued, heldItemId = _held, pending = choice.pending };
        }
        if (name == "holdItem")
        {
            ushort id = _weapons.Select(_client.PlayerId, Int(args, "page", -1), Int(args, "x", -1), Int(args, "y", -1));
            if (id != 0) _held = id;
            return new { queued = id != 0, heldItemId = _held };
        }
        if (name == "setFiremode") return new { queued = _weapons.SetFiremode(_client.PlayerId, _client, Int(args, "mode", -1)) };
        if (name == "respawn")
        {
            PrepareRespawnFrame();
            return new { queued = _server.Combat.ServerRequestRespawn(_client.PlayerId, _server.Session.CurrentTick) };
        }
        return SendAction(name, args);
    }

    object SendAction(string name, JsonElement args)
    {
        if (!Actions.TryGetValue(name, out var method)) throw new ArgumentException($"Unknown client action: {name}");
        var parameters = method.GetParameters(); var values = new object[parameters.Length];
        for (int n = 0; n < parameters.Length; n++)
        {
            var p = parameters[n];
            string key = p.Name;
            if (name == "craft" && key == "blueprintIndex") key = "index";
            if (name == "equipItem" && key == "fromPage") key = "page";
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value))
                values[n] = p.ParameterType == typeof(Vector3) ? Vec(value) : value.Deserialize(p.ParameterType, JsonOptions);
            else if (p.HasDefaultValue) values[n] = p.DefaultValue;
            else if (name == "fire" && key == "origin") values[n] = new Vector3(_position.x, _position.y + 1.6f, _position.z);
            else if (name == "fire" && key == "dir") values[n] = new Vector3(-MathF.Sin(_yaw * Mathf.Deg2Rad), 0, -MathF.Cos(_yaw * Mathf.Deg2Rad));
            else throw new ArgumentException($"Action {name} requires {key}");
        }
        var visual = name == "fire" ? CosmeticBulletServices.ReadContext(args) : null;
        var result = method.Invoke(_client, values);
        if (name == "fire" && result is ushort sequence) _cosmetic?.Queue(_client.PlayerId,sequence,visual);
        return new { queued = result is not bool boolean || boolean, value = result };
    }

    void SeedStarterKit()
    {
        if (!_server.Inventories.TryGet(_client.PlayerId, out var entry)) return;
        _held = 4;
        foreach (ushort id in new ushort[] { 4, 13, 14, 15 })
            if (Assets.find(id) != null) entry.Inventory.tryAddItemAuto(new Item(id, 1), out _);
    }

    (ulong Inventory, ulong Skills, string Knowledge, string Stations)? _lastRecipeState;
    object[] _cachedRecipes;
    object[] Recipes(PlayerInventory inventory, PlayerSkills skills, bool force)
    {
        var key = (_client.Inventories.StateHashFor(_client.PlayerId), _client.Skills.StateHashFor(_client.PlayerId),
            string.Join('|', (_client.KnownBlueprints ?? new HashSet<string>()).Order()),
            string.Join('|', AcceptedCraftingStations));
        bool changed = _lastRecipeState != key;
        if (changed || _cachedRecipes == null)
        {
            _cachedRecipes = _catalog.Recipes.Select((bp, i) => Recipe(bp, i, inventory, skills)).ToArray();
            _lastRecipeState = key;
        }
        return force || changed ? _cachedRecipes : null;
    }

    public object State(bool includeRecipes = true)
    {
        if (_client == null) return null;
        _client.CombatState.TryGet(_client.PlayerId, out var combat);
        _client.Vitals.TryGet(_client.PlayerId, out var vitals);
        _client.Inventories.TryGet(_client.PlayerId, out var entry);
        _client.Skills.TryGet(_client.PlayerId, out var skills);
        _client.Profiles.TryGet(_client.PlayerId, out var profile);
        var inv = NearbyInventory(entry?.Inventory);
        var events = _events.ToArray();
        var occupied = _client.Vehicles.All.FirstOrDefault(v => v.DriverPlayerId == _client.PlayerId || (v.Passengers?.Contains(_client.PlayerId) ?? false));
        int seat = occupied == null ? -1 : occupied.DriverPlayerId == _client.PlayerId ? 0 : System.Array.IndexOf(occupied.Passengers, _client.PlayerId) + 1;
        return new { tick = _network.CurrentTick, origin = Origin, connected = _client.State == NetSessionState.Connected && (!IsRemote || _remoteSpawnAdopted), playerId = _client.PlayerId,
            network = NetworkState(), persistence = PersistenceState(), players = PlayerReplicas(), npc = _npcView.Snapshot(), storage = _storageView.Current, nearby = NearbyState(),
            worldClock = ClockSnapshot(),
            finiteVehicles = _finiteVehicles,
            weather = _environment?.State(),
            steady = SteadyState(),
            weapon = IsRemote ? _remoteWeapon.Snapshot() : _weapons.Snapshot(_client.PlayerId),
            player = new { position = Array(_position), velocity = Array(_velocity), yaw = _yaw, pitch = _pitch,
                respawnLook = new { version = _respawnLookVersion, yaw = _respawnLookYaw, pitch = 0f },
                profile = profile == null ? null : new { name = profile.Name, face = profile.Face, avatarHash = profile.AvatarHash },
                stance = _movement.Stance.ToString().ToLowerInvariant(), hp = combat?.Health ?? 100, alive = combat?.Alive ?? true, kills = combat?.Kills ?? 0,
                gesture = combat?.Gesture ?? 0, arrested = combat?.Gesture == (byte)EPlayerGesture.ARREST_START,
                food = vitals?.Sim.Food ?? 1, water = vitals?.Sim.Water ?? 1, stamina = vitals?.Sim.Stamina ?? 1,
                infection = vitals?.Sim.Infection ?? 0, oxygen = vitals?.Sim.Oxygen ?? 1,
                bleeding = vitals?.Bleeding ?? false, broken = vitals?.Broken ?? false, radiation = vitals?.Sim.Radiation ?? 0,
                temperature = TemperatureState(), inDeadzone = OwnerInDeadzone(), aimedTap = AimedTap(),
                vehicleNetId = occupied?.NetIdValue ?? 0, vehicleSeat = seat,
                ammo = IsRemote ? _remoteWeapon.Ammo : _server.Combat.AmmoOf(_client.PlayerId), heldItemId = _held, correctionVersion = _correctionVersion },
            inventory = new { pages = inv?.items.Select(p => new { index = p.page, width = p.width, height = p.height,
                items = p.items.Select(j => new { id = j.item.id, name = Assets.find(j.item.id)?.itemName ?? $"Item {j.item.id}",
                    x = j.x, y = j.y, rot = j.rot, width = j.size_x, height = j.size_y, amount = j.item.amount,
                    quality = j.item.quality, icon = Assets.find(j.item.id)?.iconPath, gunAmmo = j.item.gunAmmo,
                    frozen = j.item.frozen, cooked = j.item.cooked, cookStyle = j.item.cookStyle, fluid = FluidView(j.item) }).ToArray() }).ToArray(),
                worn = new { hat = inv?.wornHat?.id ?? 0, glasses = inv?.wornGlasses?.id ?? 0, mask = inv?.wornMask?.id ?? 0,
                    shirt = inv?.wornShirt?.id ?? 0, vest = inv?.wornVest?.id ?? 0, backpack = inv?.wornBackpack?.id ?? 0, pants = inv?.wornPants?.id ?? 0 },
                wornQuality = new { hat = inv?.wornHat?.quality ?? 0, glasses = inv?.wornGlasses?.quality ?? 0, mask = inv?.wornMask?.quality ?? 0,
                    shirt = inv?.wornShirt?.quality ?? 0, vest = inv?.wornVest?.quality ?? 0, backpack = inv?.wornBackpack?.quality ?? 0, pants = inv?.wornPants?.quality ?? 0 } },
            recipes = Recipes(inv, skills?.Skills, includeRecipes),
            skills = new { experience = skills?.Skills.experience ?? 0, groups = skills?.Skills.skills, tree = ReadSkillTree() },
            worldItems = _client.WorldItems.All.Select(e => new { netId = e.NetIdValue, itemId = e.ItemId, amount = e.Amount,
                quality = e.Quality, position = Array(e.Pos), settled = e.Settled,
                stuckDirection = _warheads?.PoseOf(e.NetIdValue)?.Direction, stuckNormal = _warheads?.PoseOf(e.NetIdValue)?.Normal,
                stuckEuler = _warheads?.PoseOf(e.NetIdValue)?.Euler }).ToArray(),
            projectiles = ProjectileSnapshots(),
            bullets = _cosmetic?.Snapshots() ?? _remoteMounted?.Snapshots(),
            npcHelis = _npcHelis?.Snapshots() ?? [],
            vehicleMounts = _client.VehicleMounts.All.Select(v => new { vehicleNetId = v.VehicleNetId, headOut = v.HeadOut, gunnerRise = v.GunnerRise,
                mounts = v.Mounts.Select((m,index) => new { index, yaw = m.Yaw, pitch = m.Pitch, ammo = m.Ammo, cooldownSeconds = m.CooldownSeconds, crewAlive = m.CrewAlive }).ToArray() }).ToArray(),
            animals = _wildlife?.Snapshots(_client) ?? RemoteAnimals(),
            powerStateHash = _client.Deployables.PowerViewHash(),
            deployables = _client.Deployables.All.Select(e => new { netId = e.NetIdValue, defId = e.DefId, position = Array(e.Pos), yaw = e.YawDegrees, mountUp = e.MountUp, health = e.Health, fuel = e.Fuel, energy = e.Energy, wind = e.Wind, onFire = e.OnFire, burnSeconds = _bodies?.BurnSeconds(false, e.NetIdValue), wreckSalvageable = _bodies?.Salvageable(false, e.NetIdValue), toggledOn = e.ToggledOn }).ToArray(),
            vehicleRecoveries = _vehicleRecovery.Values.Select(e => new { netId=e.NetId, pos=Array(e.Pos), linVel=Array(e.LinVel),
                yawDegrees=e.YawDegrees, pitchDegrees=e.PitchDegrees, rollDegrees=e.RollDegrees, recovCounter=e.RecovCounter,
                trailer=e.Trailer is {} t ? new { netId=t.NetId, pos=Array(t.Pos), linVel=Array(t.LinVel), angVel=Array(t.AngVel),
                    yawDegrees=t.YawDegrees, pitchDegrees=t.PitchDegrees, rollDegrees=t.RollDegrees } : null }).ToArray(),
            vehicles = _client.Vehicles.All.Select(e => new { netId = e.NetIdValue, typeId = e.TypeId, variant = e.Variant,
                position = Array(e.Pos), yaw = e.YawDegrees, eulerDegrees = Array(new(e.PitchDegrees, e.YawDegrees, e.RollDegrees)),
                linVel = Array(e.LinVel), angVel = Array(e.AngVel), steerDegrees = e.SteerSigned,
                fuel = _vehicles?.FuelFor(e.NetIdValue, e.Fuel) ?? e.Fuel, health = e.Health, battery = _vehicleLights?.BatteryFor(e.NetIdValue, e.Battery) ?? e.Battery, flags = e.Flags, highBeam = e.HighBeam, lightbarPattern = e.LightbarPattern, lampMask = e.LampMask, brokenLampMask = e.BrokenLampMask, glassMask = e.GlassMask, brokenGlassMask = e.BrokenGlassMask, tireMask = e.TireMask, hasBiFoldDoor = e.HasBiFoldDoor, doorOpen = e.DoorOpen, doorFold = e.DoorFold, engineHealth = _vehicles?.EngineHealth(e.NetIdValue),
                mainRotorHealth = _vehicles?.MainRotorHealth(e.NetIdValue), tailRotorHealth = _vehicles?.TailRotorHealth(e.NetIdValue), engineDrowned = _vehicles?.EngineDrowned(e.NetIdValue), onFire = _bodies?.OnFire(true, e.NetIdValue),
                burnSeconds = _bodies?.BurnSeconds(true, e.NetIdValue), wreckSalvageable = _bodies?.Salvageable(true, e.NetIdValue),
                driverPlayerId = e.DriverPlayerId, passengers = e.Passengers, seatCount = VehicleSeatCount(e),
                coupledTrailerNetId = e.CoupledTrailerNetId, towedNetId = e.TowedNetId, towRestLen = _vehicleTows?.RestLen(e.NetIdValue, e.TowRestLen) ?? e.TowRestLen, paintRgb = e.PaintRgb, poppedTireMask = e.PoppedTireMask,
                recovCounter = _vehicleRecov.GetValueOrDefault(e.NetIdValue) }).ToArray(),
            zombies = _zombies?.Snapshots(_client) ?? RemoteZombies(), zombieField = _zombies?.Summary(),
            crops = _client.Crops.All.Select(e => new { netId = e.NetIdValue, seedId = e.SeedId, position = Array(e.Pos), grown = _client.Crops.IsGrown(e, _client.Applier.LastAppliedServerTick), plantedAtTick = e.PlantedAtTick }).ToArray(),
            interactables = new {
                doors = _client.InteractableState.ReplicaDoors.Select(d => new { netId = d.Key, open = d.Value.Open, locked = d.Value.Locked }),
                objectDoors = _client.InteractableState.ReplicaObjectDoors.Select(d => new { netId = d.Key, open = d.Value, reverse = _client.InteractableState.ObjectDoorReversed(d.Key) }),
                beds = _client.InteractableState.ReplicaBeds.Select(d => new { netId = d.Key, owner = d.Value }),
                seats = _client.InteractableState.ReplicaSeats.Select(d => new { netId = d.Key, occupant = d.Value }) },
            resources = new { count = _client.Resources.Count, dead = Enumerable.Range(0, _client.Resources.Count).Where(i => !_client.Resources.IsAlive(i)).ToArray() },
            destructibles = new { count = _client.Destructibles.Count, dead = Enumerable.Range(0, _client.Destructibles.Count).Where(i => !_client.Destructibles.IsAlive(i)).ToArray() },
            containers = _client.Containers.All.Select(e => new { netId = e.NetIdValue, kindId = e.KindId, position = Array(e.Pos), yaw = e.YawDegrees, width = e.Width, height = e.Height, display = e.Display, doorsOpen = e.DoorsOpen, cookerOn = e.CookerOn }).ToArray(),
            events };
    }

    object Recipe(BlueprintDef bp, int i, PlayerInventory inv, PlayerSkills skills)
    {
        string reason = "Inventory unavailable";
        bool can = inv != null && Crafting.CanCraft(bp, new Crafting.PlayerInvAdapter(inv), skills, out reason);
        if (!Crafting.HasStations(bp, AcceptedCraftingStations)) { can = false; reason = "Requires crafting station"; }
        if (bp.Locked && !(_client.KnownBlueprints?.Contains(bp.Key) ?? false)) { can = false; reason = "Recipe not learned"; }
        return new { index = i, name = SourceCraftingMenu.Title(bp), operation = bp.Operation, craftable = can, reason, seconds = bp.Seconds,
            inputs = bp.Inputs.Select(Ingredient).ToArray(), outputs = bp.Outputs.Select(Ingredient).ToArray() };
    }
    static object Ingredient(BlueprintDef.Ingredient i) => new { id = Crafting.Resolve(i.Guid), name = Assets.findByGuid(i.Guid)?.itemName ?? i.Guid, amount = i.Amount, consume = i.Consume };
    static byte StanceCode(EPlayerStance s) => s switch { EPlayerStance.SPRINT => 1, EPlayerStance.CROUCH => 2, EPlayerStance.PRONE => 3, _ => 0 };
    static EPlayerStance Stance(string s) => Enum.TryParse<EPlayerStance>(s, true, out var stance) ? stance : EPlayerStance.STAND;
    static Vector3 LocalToWorld(Vector3 v, float yaw) { float c = MathF.Cos(yaw * Mathf.Deg2Rad), s = MathF.Sin(yaw * Mathf.Deg2Rad); return new(c * v.x - s * v.z, v.y, -s * v.x - c * v.z); }
    static Vector3 WorldToLocal(Vector3 v, float yaw) => LocalToWorld(v, yaw); // orthogonal reflection is its own inverse
    internal static float[] Array(Vector3 v) => [v.x, v.y, v.z];
    internal static string Text(JsonElement r, string key, string fallback = "") => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    internal static int Int(JsonElement r, string key, int fallback = 0) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
    internal static float Float(JsonElement r, string key, float fallback = 0) { var f = r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetSingle() : fallback; return float.IsFinite(f) ? f : throw new ArgumentException($"{key} must be finite"); }
    internal static bool Bool(JsonElement r, string key, bool fallback = false) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
    internal static Vector3 Vec(JsonElement r, string key, Vector3 fallback = default) => r.TryGetProperty(key, out var v) ? Vec(v) : fallback;
    internal static Vector3 Vec(JsonElement v) { if (v.GetArrayLength() != 3) throw new ArgumentException("Expected [x,y,z]"); var a = v.EnumerateArray().Select(e => e.GetSingle()).ToArray(); if (a.Any(f => !float.IsFinite(f))) throw new ArgumentException("Vector must be finite"); return new(a[0], a[1], a[2]); }
    InfiniteTerrain Terrain(int seed) { if (!_terrains.TryGetValue(seed, out var source)) _terrains[seed] = source = new InfiniteTerrain(seed); return source; }
    void RequireSession() { if (_client == null) throw new InvalidOperationException("Send init first"); }
    public void Dispose()
    {
        // The original request mailbox is static. Pending old-world gestures
        // must not be inherited by a newly connected owner with the same ID.
        if (_server != null) foreach (var player in _server.CombatState.All)
            ServerGestures.TryTakePending(player.OwnerPlayerId, out _);
        _serverProbe.Cancel();
        _loot = null; _finiteVehicles = null; _finiteTerrain = null; _client?.Disconnect(); _server?.TearDown(); _client = null; _server = null;
        _udpTransport?.TearDown(); _udpTransport = null;
        _environment = null; _services = null; _vehicles = null; _vehicleLights = null; _vehicleMaintenance = null; _vehicleDoors = null; _vehicleHitches = null; _vehicleTows = null; _bodies = null; _weapons = null;
        _cosmetic = null; _warheads = null; _mounted = null; _npcHelis = null; _appearance = null; _wildlife = null; _zombies = null;
        _remoteWeapon = null; _remoteMounted = null; _remoteSpawnAdopted = false; _remoteProfileSent = false;
        _mapBindings = null;
        if (_finiteSpawns != null) SDG.Unturned.LootCondition.Clear();
        _finiteSpawns = null; _respawnLookVersion = 0; _respawnLookYaw = null;
    }
}
