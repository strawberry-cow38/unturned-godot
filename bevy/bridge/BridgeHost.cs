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
    string _name = "Survivor", _map = "bevy";
    int _correctionVersion;
    readonly List<BridgeEvent> _events = new();
    long _eventSequence;
    readonly WorldGeometry _geometry = new();
    readonly Dictionary<int, InfiniteTerrain> _terrains = new();
    PlayerStanceSim _stance = new();
    RetailLootStreamer _loot;
    WorldEnvironment _environment;
    WorldServices _services;
    VehicleServices _vehicles;
    BodyCombatServices _bodies;
    WeaponServices _weapons;
    CosmeticBulletServices _cosmetic;
    readonly Dictionary<uint, byte> _vehicleRecov = new();
    readonly List<JsonElement> _serviceSpecs = new();
    WildlifeServices _wildlife;
    ZombieServices _zombies;
    int _seed = 1337;
    bool _worldChanged;
    sealed record BridgeSave(int Seed, RetailLootStreamer.SaveData Loot, JsonElement[] Services, ResourceServices.Saved[] Resources = null, WorldServices.PropDoorSaved[] ObjectDoors = null, BodyCombatServices.Saved[] Bodies = null, VehicleServices.PartsSaved[] VehicleParts = null, double OriginX = 0, double OriginZ = 0, SavedProjectile[] Projectiles = null, WildlifeServices.Saved Wildlife = null, ZombieServices.Saved Zombies = null, WeaponServices.Saved[] Weapons = null, CosmeticBulletServices.WaterSaved[] CosmeticWater = null);
    public sealed record BridgeEvent(long Seq, string Kind, object Data, long Tick);
    void Emit(string kind, object data) => _events.Add(new(++_eventSequence, kind, data, _server?.Session.CurrentTick ?? 0));
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
        switch (op)
        {
            case "init":
                Dispose(); _geometry.Clear(); _serviceSpecs.Clear(); _originX = _originZ = 0; _originEpoch = 0;
                _catalog.Load(Text(r, "contentRoot", Path.GetFullPath("game/content")));
                _name = Text(r, "name", "Survivor"); _map = Text(r, "mapId", "bevy");
                _seed = Int(r, "seed", 1337);
                _allowCheats = Bool(r, "allowCheats", Environment.GetEnvironmentVariable("UG_DEDICATED_NOCHEATS") != "1");
                if (r.TryGetProperty("origin", out var initialOrigin))
                {
                    if (initialOrigin.GetArrayLength() != 2) throw new ArgumentException("origin must be [doubleX,doubleZ]");
                    _originX = initialOrigin[0].GetDouble(); _originZ = initialOrigin[1].GetDouble();
                    if (!double.IsFinite(_originX) || !double.IsFinite(_originZ)) throw new ArgumentException("origin must be finite");
                }
                _position = Vec(r, "spawn", new(0, 1, 0));
                _velocity = Vector3.zero; _held = 0; _yaw = Float(r, "yaw"); _pitch = 0; _grounded = true; _correctionVersion++;
                Start();
                if (Bool(r, "starterKit")) SeedStarterKit();
                for (int n = 0; n < 6; n++) Tick();
                return new { protocolVersion = 1, fixedDelta = SimClock.FixedDelta, catalogItems = _catalog.Count,
                    recipes = _catalog.Recipes.Count, movementHeights = new { stand = PlayerMovementDef.HEIGHT_STAND, crouch = PlayerMovementDef.HEIGHT_CROUCH, prone = PlayerMovementDef.HEIGHT_PRONE }, capabilities = new[] { "movement", "memSession", "inventory", "vitals", "combat", "crafting", "save", "terrain", "catalog", "worldGeometry", "worldLoot", "worldWildlife", "worldZombies", "worldClock", "weather", "eventAck", "rebase" },
                    actions = Actions.Keys.Append("respawn").Append("setFiremode").Append("holdItem").Append("equipAndHold").Order().ToArray() };
            case "rebase": return Rebase(r);
            case "simulate":
                RequireSession();
                return Simulate(r.TryGetProperty("input", out var input) ? input : r);
            case "tick":
                RequireSession();
                if (r.TryGetProperty("input", out var body))
                {
                    ReadSteady(body);
                    _position = Vec(body, "position", _position); _velocity = Vec(body, "velocity", _velocity);
                    _grounded = Bool(body, "grounded", _grounded);
                    _yaw = Float(body, "yaw", _yaw); _pitch = Float(body, "pitch", _pitch);
                    _held = checked((ushort)Int(body, "heldItemId", _held));
                    if (body.TryGetProperty("stance", out _)) _movement.Stance = Stance(Text(body, "stance", "stand"));
                    _movement.Velocity = WorldToLocal(_velocity, _yaw);
                }
                Tick(); return new { fixedDelta = SimClock.FixedDelta };
            case "snapshot": RequireSession(); return null;
            case "catalog": RequireSession(); return new { items = _catalog.Metadata(), wildlife = WildlifeServices.Catalog(), bulletVisuals = CosmeticBulletServices.Metadata(), throwMotion = new {
                speed = HandRules.ThrowSpeed, lift = HandRules.ThrowLift, weakScale = HandRules.WeakThrowScale,
                releaseFraction = HandRules.ThrowReleaseFraction, cooldown = HandRules.ThrowCooldown,
                clearProbe = HandRules.ThrowClearProbe, clearGap = HandRules.ThrowClearGap,
                fuseSeconds = Throwables.FuseSeconds, useSeconds = _catalog.Clip("", "TU_0") }, deployables = DeployableDef.All, containers = ContainerSchema.All,
                crops = FarmRegistry.All.Select(d => new { seedId = d.Id, growthSeconds = d.Growth, yieldItemId = d.Grow, name = CropRegistry.TryBySeed(d.Id, out string name) ? name : null }).ToArray() };
            case "attachmentOptions": RequireSession(); return _weapons.AttachmentOptions(_client.PlayerId);
            case "npcCatalog": RequireSession(); return new { characters = NpcCatalog.Characters, dialogues = NpcCatalog.Dialogues, vendors = NpcCatalog.Vendors, quests = NpcCatalog.Quests };
            case "worldVehicles":
                RequireSession();
                var fleet = _vehicles.Register(r); _serviceSpecs.Add(r.Clone()); _worldChanged = true; return fleet;
            case "worldVehicleState": RequireSession(); return _vehicles.Publish(r);
            case "worldVehicleDamage": RequireSession(); return _bodies.HostVehicleDamage(r);
            case "worldVehicleExit": RequireSession(); return _vehicles.ExitProbe(r);
            case "mapServices":
                RequireSession();
                string mapKey = Text(r, "mapName", "PEI") + "|" + Text(r, "mapRoot");
                if (_services.MapKey == mapKey) return _services.MapResult;
                if (_services.MapKey != null) throw new InvalidOperationException("A different map requires init");
                var parsed = MapServices.Parse(r);
                var mapSpec = JsonNode.Parse(parsed.Spec.GetRawText());
                mapSpec["mapKey"] = mapKey;
                mapSpec["mapObjects"] = JsonSerializer.SerializeToNode(parsed.Objects);
                mapSpec["mapResources"] = JsonSerializer.SerializeToNode(parsed.Resources);
                var persistedSpec = JsonSerializer.SerializeToElement(mapSpec);
                if (_originX != 0 || _originZ != 0) persistedSpec = CoordinateJson.ServiceSpec(persistedSpec, new((float)-_originX, 0, (float)-_originZ));
                var mapResult = _services.Register(persistedSpec); _serviceSpecs.Add(persistedSpec); _worldChanged = true; return mapResult;
            case "worldServices":
                RequireSession();
                var registration = _services.Register(r); _serviceSpecs.Add(r.Clone()); _worldChanged = true; return registration;
            case "action":
                RequireSession();
                return Action(Text(r, "name"), r.TryGetProperty("args", out var args) ? args : default);
            case "save":
                RequireSession();
                var save = WorldSave.Capture(_server, _map, _environment.DayAt(_server.Clock, _server.Session.CurrentTick),
                    _server.Clock.TimeOfDayAt(_server.Session.CurrentTick), _server.Clock.DayLengthSeconds, _server.PendingSave);
                _vehicles.SaveFuel(save);
                string path = Path.GetFullPath(Text(r, "path"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var saveJson = JsonNode.Parse(save.ToJson());
                saveJson["BevyBridge"] = JsonSerializer.SerializeToNode(new BridgeSave(_seed, _loot?.Capture(), _serviceSpecs.ToArray(), _services.Harvesting.Capture(), _services.CapturePropDoors(), _bodies.Capture(), _vehicles.CaptureParts(), _originX, _originZ, CaptureProjectiles(), _wildlife.Capture(), _zombies.Capture(), _weapons.Capture(), _cosmetic.CaptureWater()));
                File.WriteAllText(path + ".tmp", saveJson.ToJsonString()); File.Move(path + ".tmp", path, true);
                _server.PendingSave = save;
                return new { path, version = save.Version };
            case "load":
                RequireSession();
                string loadJson = File.ReadAllText(Text(r, "path"));
                if (!WorldSave.TryParse(loadJson, _map, out var loaded, out var error)) throw new InvalidDataException(error);
                var extra = JsonNode.Parse(loadJson)?["BevyBridge"]?.Deserialize<BridgeSave>();
                if (extra != null) _seed = extra.Seed;
                double loadedX = extra?.OriginX ?? 0, loadedZ = extra?.OriginZ ?? 0;
                var loadDelta = new Vector3((float)(_originX - loadedX), 0, (float)(_originZ - loadedZ));
                _geometry.TranslateWorld(loadDelta); _position += loadDelta;
                _originX = loadedX; _originZ = loadedZ; _originEpoch++;
                _serviceSpecs.Clear(); if (extra?.Services != null) _serviceSpecs.AddRange(extra.Services);
                Dispose(); Start(loaded);
                RestoreProjectiles(extra?.Projectiles);
                _wildlife.Restore(extra?.Wildlife);
                _zombies.Restore(extra?.Zombies);
                _weapons.Restore(extra?.Weapons);
                _cosmetic.RestoreWater(extra?.CosmeticWater);
                _services.Harvesting.Restore(extra?.Resources);
                _services.RestorePropDoors(extra?.ObjectDoors); _bodies.Restore(extra?.Bodies); _vehicles.RestoreParts(extra?.VehicleParts);
                if (extra?.Loot != null)
                {
                    _loot = new RetailLootStreamer(extra.Loot.MapRoot, _server, _geometry);
                    _loot.TranslateWorld(new((float)-_originX, 0, (float)-_originZ));
                    _loot.Restore(extra.Loot);
                    _loot.Step(force: true);
                }
                for (int n = 0; n < 6; n++) Tick();
                return new { version = loaded.Version };
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
                        mesh.TryGetProperty("indices", out var ix) ? ix.EnumerateArray().Select(v => v.GetInt32()).ToArray() : Enumerable.Range(0, mesh.GetProperty("vertices").GetArrayLength()).ToArray(), Int(mesh, "destructibleIndex", -1), Bool(mesh, "terrain"), Int(mesh, "resourceIndex", -1), vehicle: checked((uint)Int(mesh, "vehicleNetId")), deployable: checked((uint)Int(mesh, "deployableNetId")), bodyPart: Text(mesh, "bodyPart", "hull"), thin: Bool(mesh,"thin"), water: Bool(mesh,"water"));
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
            case "worldZombies": RequireSession(); return _zombies.Register(r, _originX, _originZ);
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
                        Int(mesh, "destructibleIndex", -1), resource: Int(mesh, "resourceIndex", -1), vehicle: checked((uint)Int(mesh, "vehicleNetId")), deployable: checked((uint)Int(mesh, "deployableNetId")), bodyPart: Text(mesh, "bodyPart", "hull"), thin: Bool(mesh,"thin"), water: Bool(mesh,"water"));
                }
                dynamicGeometry.Build(); _geometry.SetDynamic(Text(r, "key"), dynamicGeometry);
                return new { key = Text(r, "key"), triangles = dynamicGeometry.Count };
            case "worldRay":
                bool hit = _geometry.RayDetailed(Vec(r, "from"), Vec(r, "to"), out var rayHit);
                return new { hit, point = Array(rayHit.Point), destructibleIndex = rayHit.DestructibleIndex, resourceIndex = rayHit.ResourceIndex, vehicleNetId = rayHit.VehicleNetId, deployableNetId = rayHit.DeployableNetId, bodyPart = rayHit.BodyPart };
            case "shutdown": return null;
            default: throw new ArgumentException($"Unknown bridge operation: {op}");
        }
    }

    void Start(WorldSave save = null)
    {
        _scopeSteady.Reset(); _wantsSteady = false;
        _worldChanged = false; _geometry.ClearDynamic();
        _network = new MemNetwork(); _movement = new PlayerMovementSim(); _stance = new PlayerStanceSim(); _recov = 0; _events.Clear(); _velocity = Vector3.zero; _lastRecipeState = null; _cachedRecipes = null;
        _server = new NetWorldServer(new MemServerTransport(_network));
        _server.Transactions.AllowCheats = _allowCheats;
        _server.PlayerHost.AcceptSeatedActions = true;
        _server.VehicleHost.RepositionExitedOwner = _server.PlayerHost.RepositionOwner;
        WorldRebase.EnableUnboundedStorage(_server);
        _server.Transactions.RepositionOwner = _server.PlayerHost.RepositionOwner;
        _environment = new WorldEnvironment(_server, _seed, save);
        _server.SpawnProvider = _ => _position;
        _geometry.DestructibleAlive = i => i >= _server.Destructibles.Count || _server.Destructibles.IsAlive(i);
        _geometry.ResourceAlive = i => i >= _server.Resources.Count || _server.Resources.IsAlive(i);
        _server.Combat.WorldRayDetailed = _geometry.RayDetailed;
        _server.Combat.WorldRay = _geometry.Ray;
        _server.Combat.GroundHeight = _geometry.GroundHeight;
        _server.Combat.ResolveHeldGunProfile = owner =>
            _server.Players.TryGetHeldInput(owner, out var held) && _catalog.Guns.TryGetValue(held.HeldItemId, out var gun)
                && _server.Inventories.TryGet(owner, out var owned) && owned.Inventory.getItemCount(held.HeldItemId) > 0 ? gun : null;
        DeployableNetSchema.RegisterAll(_server.Deployables.Schema);
        _services = new WorldServices(_server, _catalog, Emit);
        _vehicles = new VehicleServices(_server, _geometry, Emit); _vehicleRecov.Clear();
        _bodies = new BodyCombatServices(_server, _geometry, _vehicles, Emit);
        _wildlife = new WildlifeServices(_server, _geometry, Emit);
        _zombies = new ZombieServices(_server, _geometry, Emit);
        _zombies.RainIntensity=()=>_environment.Sim.BlendAlpha*_environment.Severity;
        _weapons = new WeaponServices(_server, _catalog, Emit);
        _cosmetic = new CosmeticBulletServices(_server,_geometry,_weapons,Emit);
        _weapons.Noise=(owner,radius)=>{if(_server.Players.TryGetByOwner(owner,out var p))_zombies.Noise(p.Pos,radius);};
        _movingInput=false;_footNoiseTimer=0;
        _bodies.RepairRate = _weapons.RepairRate;
        _bodies.RequestSalvage = id => _client.SendSalvageDeployable(id);
        _server.Combat.CanThrow = (owner, id) => _server.Inventories.TryGet(owner, out var bag) && bag.Inventory.getItemCount(id) > 0;
        _server.Combat.ThrowAccepted = (owner, id, netId, tick) => Emit("throwAccepted", new { playerId = owner, itemId = id, netId, tick });
        _server.Transactions.ConsumeApplied = (owner, cmd, id) => Emit("consumeAccepted", new { playerId = owner, itemId = id, page = cmd.Page, x = cmd.X, y = cmd.Y, tick = _server.Session.CurrentTick });
        _server.Vitals.SurvivalDrain = true;
        foreach (var spec in _serviceSpecs)
            if (spec.TryGetProperty("vehicles", out _)) _vehicles.Register(spec); else _services.Register(spec);
        _server.Transactions.Blueprints = _catalog.Recipes;
        _server.PendingSave = save;
        save?.ApplyWorld(_server, 0);
        _vehicles.RestoreFuel(save);
        _services.AdoptSavedBits();
        _client = new NetWorldClient(new MemClientTransport(_network), _name);
        DeployableNetSchema.RegisterAll(_client.Deployables.Schema);
        WorldServices.RegisterCrops(_client.Crops.Schema);
        _client.PlayerRecov += e => { _position = e.Pos; _velocity = e.Vel; _recov = e.RecovCounter; _movement.Velocity = WorldToLocal(_velocity, _yaw); _correctionVersion++; };
        _client.ConsoleResult += e => Emit("console", e);
        _client.HitConfirmed += e => Emit("hit", e);
        _client.CraftQueue_ += e => Emit("craftQueue", e);
        _client.PlayerFired += e => {
            var facts=_weapons.TakeShot(e.PlayerId,e.Gun);
            Emit("fire",new{playerId=e.PlayerId,origin=e.Origin,dir=e.Dir,gun=e.Gun,
                itemId=facts?.ItemId,quality=facts?.Quality,handlingPenalty=facts?.HandlingPenalty,
                recoilMultiplier=facts?.RecoilMultiplier,selectionVersion=facts?.SelectionVersion,acceptedTick=facts?.Tick,
                barrelId=facts?.BarrelId,silenced=facts?.Silenced,barrelSilenced=facts?.BarrelSilenced,shootClip=facts?.ShootClip,shotVolume=facts?.ShotVolume,
                muzzleVelocity=facts?.MuzzleVelocity,barrelBroken=facts?.BarrelBroken});
        };
        _client.PlayerMeleed += e => {
            var facts=_weapons.TakeMelee(e.PlayerId,e.Strong);
            Emit("melee",new{playerId=e.PlayerId,strong=e.Strong,itemId=facts?.ItemId,repeated=facts?.Repeated,acceptedTick=facts?.Tick});
        };
        _client.PlayerGestured += e => Emit("gesture", e);
        _client.StorageClosed += e => Emit("storageClosed", e);
        _client.VehicleEntered += e => Emit("vehicleEntered", e);
        _client.VehicleExited += e => {
            if (e.PlayerId == _client.PlayerId) { _position = e.Pos; _velocity = Vector3.zero; _movement = new(); _correctionVersion++; }
            Emit("vehicleExited", e);
        };
        _client.VehicleExitRefused += e => Emit("vehicleExitRefused", e);
        _client.VehicleRecov += e => { _vehicleRecov[e.NetId] = e.RecovCounter; Emit("vehicleRecov", e); };
        _client.ChatMessage += e => Emit("chat", e);
        _client.ImpactFx += e => Emit("impact", e);
        _client.GrenadeExploded += e => Emit("grenadeExploded", e);
        _client.PlayerDied += e => { if (e.Victim == _client.PlayerId) _client.ClearCombatRing(); Emit("death", e); };
        _client.PlayerRespawned += e => {
            if (e.PlayerId == _client.PlayerId) { _client.ClearCombatRing(); _movement = new(); _stance = new(); _velocity = Vector3.zero; _held = 0; _correctionVersion++; }
            Emit("respawn", e);
        };
        _client.CropPlanted += e => Emit("cropPlanted", e);
        _client.CropHarvested += e => Emit("cropHarvested", e);
        _client.ResourceHarvested += e => Emit("resourceHarvested", e);
        _client.ResourceRespawned += e => Emit("resourceRespawned", e);
        _client.ObjectDestroyed += e => Emit("objectDestroyed", e);
        _client.ObjectRestored += e => Emit("objectRestored", e);
        _client.NpcState += e => Emit("npcState", e);
        _client.DoorStateChanged += e => Emit("doorState", e);
        _client.ObjectDoorState += e => Emit("objectDoorState", e);
        _client.BedClaimed += e => Emit("bedClaimed", e);
        _client.SeatOccupied += e => Emit("seatOccupied", e);
        _client.StorageOpened += e => Emit("storageOpened", e);
        _client.CookerState += e => Emit("cookerState", e);
        _client.ItemPickupDenied += e => Emit("pickupDenied", e);
        _client.PlayerHurt += e => Emit("hurt", e);
        _client.Connect();
        for (int n = 0; n < 100 && _client.State != NetSessionState.Connected; n++) Tick();
        if (_client.State != NetSessionState.Connected) throw new InvalidOperationException("Local server handshake did not complete");
        for (int n = 0; n < 4; n++) Tick();
        if (save != null && _server.Players.TryGetByOwner(_client.PlayerId, out var player))
        {
            _position = player.Pos; _yaw = player.YawDegrees; _correctionVersion++;
        }
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
        StepSteady();
        // Automatic death timer and explicit respawn use the same source target.
        // Make that target representable before core emits its bounded recovery packet.
        if (_server.CombatState.TryGet(_client.PlayerId, out var life) && !life.Alive
            && life.RespawnAtTick == _server.Session.CurrentTick + 1) PrepareRespawnFrame();
        if (_client.State == NetSessionState.Connected)
            _client.SendPlayerState(_position, _yaw, _pitch, _velocity,
                (byte)((StanceCode(_movement.Stance) << 1) | (_scopeSteady.Steadying ? MoveInput.ButtonSteady : 0)), _grounded, _recov, _held);
        _network.Tick(); _client.Tick(); _server.TickSimulation(); _weapons.Step(); _cosmetic.Step();
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
        _loot?.Step(); _services.Step(); _bodies.Step(); _wildlife.Step(); _zombies.Step(); _environment.Step(_server);
        if (_worldChanged) PublishFullSnapshot();
        _server.TickReplication();
        if (_client.Players.TryGetByOwner(_client.PlayerId, out var own)) _client.AckCombat(own.LastProcessedCombatSeq);
    }

    object Action(string name, JsonElement args)
    {
        if (name == "console" && PlayerDebugConsole(Text(args, "text"))) return new { queued = true };
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
        if (name == "fire" && result is ushort sequence) _cosmetic.Queue(_client.PlayerId,sequence,visual);
        return new { queued = result is not bool boolean || boolean, value = result };
    }

    void SeedStarterKit()
    {
        if (!_server.Inventories.TryGet(_client.PlayerId, out var entry)) return;
        _held = 4;
        foreach (ushort id in new ushort[] { 4, 13, 14, 15 })
            if (Assets.find(id) != null) entry.Inventory.tryAddItemAuto(new Item(id, 1), out _);
    }

    (ulong Inventory, ulong Skills, string Knowledge)? _lastRecipeState;
    object[] _cachedRecipes;
    object[] Recipes(PlayerInventory inventory, PlayerSkills skills, bool force)
    {
        var key = (_client.Inventories.StateHashFor(_client.PlayerId), _client.Skills.StateHashFor(_client.PlayerId),
            string.Join('|', (_client.KnownBlueprints ?? new HashSet<string>()).Order()));
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
        var inv = entry?.Inventory;
        var events = _events.ToArray();
        var occupied = _client.Vehicles.All.FirstOrDefault(v => v.DriverPlayerId == _client.PlayerId || (v.Passengers?.Contains(_client.PlayerId) ?? false));
        int seat = occupied == null ? -1 : occupied.DriverPlayerId == _client.PlayerId ? 0 : System.Array.IndexOf(occupied.Passengers, _client.PlayerId) + 1;
        return new { tick = _network.CurrentTick, origin = Origin, connected = _client.State == NetSessionState.Connected, playerId = _client.PlayerId,
            worldClock = new { day = _environment.DayAt(_client.Clock, _server.Session.CurrentTick), timeOfDay = _client.Clock.TimeOfDayAt(_server.Session.CurrentTick), dayLengthSeconds = _client.Clock.DayLengthSeconds },
            weather = _environment.State(),
            steady = SteadyState(),
            weapon = _weapons.Snapshot(_client.PlayerId),
            player = new { position = Array(_position), velocity = Array(_velocity), yaw = _yaw, pitch = _pitch,
                profile = profile == null ? null : new { name = profile.Name, face = profile.Face, avatarHash = profile.AvatarHash },
                stance = _movement.Stance.ToString().ToLowerInvariant(), hp = combat?.Health ?? 100, alive = combat?.Alive ?? true, kills = combat?.Kills ?? 0,
                food = vitals?.Sim.Food ?? 1, water = vitals?.Sim.Water ?? 1, stamina = vitals?.Sim.Stamina ?? 1,
                infection = vitals?.Sim.Infection ?? 0, oxygen = vitals?.Sim.Oxygen ?? 1,
                bleeding = vitals?.Bleeding ?? false, broken = vitals?.Broken ?? false, radiation = vitals?.Sim.Radiation ?? 0,
                vehicleNetId = occupied?.NetIdValue ?? 0, vehicleSeat = seat,
                ammo = _server.Combat.AmmoOf(_client.PlayerId), heldItemId = _held, correctionVersion = _correctionVersion },
            inventory = new { pages = inv?.items.Select(p => new { index = p.page, width = p.width, height = p.height,
                items = p.items.Select(j => new { id = j.item.id, name = Assets.find(j.item.id)?.itemName ?? $"Item {j.item.id}",
                    x = j.x, y = j.y, rot = j.rot, width = j.size_x, height = j.size_y, amount = j.item.amount,
                    quality = j.item.quality, icon = Assets.find(j.item.id)?.iconPath, gunAmmo = j.item.gunAmmo,
                    frozen = j.item.frozen, cooked = j.item.cooked, cookStyle = j.item.cookStyle }).ToArray() }).ToArray(),
                worn = new { hat = inv?.wornHat?.id ?? 0, glasses = inv?.wornGlasses?.id ?? 0, mask = inv?.wornMask?.id ?? 0,
                    shirt = inv?.wornShirt?.id ?? 0, vest = inv?.wornVest?.id ?? 0, backpack = inv?.wornBackpack?.id ?? 0, pants = inv?.wornPants?.id ?? 0 },
                wornQuality = new { hat = inv?.wornHat?.quality ?? 0, glasses = inv?.wornGlasses?.quality ?? 0, mask = inv?.wornMask?.quality ?? 0,
                    shirt = inv?.wornShirt?.quality ?? 0, vest = inv?.wornVest?.quality ?? 0, backpack = inv?.wornBackpack?.quality ?? 0, pants = inv?.wornPants?.quality ?? 0 } },
            recipes = Recipes(inv, skills?.Skills, includeRecipes),
            skills = new { experience = skills?.Skills.experience ?? 0, groups = skills?.Skills.skills },
            worldItems = _client.WorldItems.All.Select(e => new { netId = e.NetIdValue, itemId = e.ItemId, amount = e.Amount,
                quality = e.Quality, position = Array(e.Pos), settled = e.Settled }).ToArray(),
            projectiles = ProjectileSnapshots(),
            bullets = _cosmetic.Snapshots(),
            animals = _wildlife.Snapshots(_client),
            deployables = _client.Deployables.All.Select(e => new { netId = e.NetIdValue, defId = e.DefId, position = Array(e.Pos), yaw = e.YawDegrees, health = e.Health, fuel = e.Fuel, onFire = e.OnFire, burnSeconds = _bodies.BurnSeconds(false, e.NetIdValue), wreckSalvageable = _bodies.Salvageable(false, e.NetIdValue), toggledOn = e.ToggledOn }).ToArray(),
            vehicles = _client.Vehicles.All.Select(e => new { netId = e.NetIdValue, typeId = e.TypeId, variant = e.Variant,
                position = Array(e.Pos), yaw = e.YawDegrees, eulerDegrees = Array(new(e.PitchDegrees, e.YawDegrees, e.RollDegrees)),
                linVel = Array(e.LinVel), angVel = Array(e.AngVel), steerDegrees = e.SteerSigned,
                fuel = _vehicles.FuelFor(e.NetIdValue, e.Fuel), health = e.Health, battery = e.Battery, flags = e.Flags, engineHealth = _vehicles.EngineHealth(e.NetIdValue),
                mainRotorHealth = _vehicles.MainRotorHealth(e.NetIdValue), tailRotorHealth = _vehicles.TailRotorHealth(e.NetIdValue), engineDrowned = _vehicles.EngineDrowned(e.NetIdValue), onFire = _bodies.OnFire(true, e.NetIdValue),
                burnSeconds = _bodies.BurnSeconds(true, e.NetIdValue), wreckSalvageable = _bodies.Salvageable(true, e.NetIdValue),
                driverPlayerId = e.DriverPlayerId, passengers = e.Passengers, seatCount = e.SeatCount,
                towedNetId = e.TowedNetId, towRestLen = e.TowRestLen, paintRgb = e.PaintRgb, poppedTireMask = e.PoppedTireMask,
                recovCounter = _vehicleRecov.GetValueOrDefault(e.NetIdValue) }).ToArray(),
            zombies = _zombies.Snapshots(_client), zombieField = _zombies.Summary(),
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
        if (bp.RequiresStation) { can = false; reason = "Requires crafting station"; }
        if (bp.Locked && !(_client.KnownBlueprints?.Contains(bp.Key) ?? false)) { can = false; reason = "Recipe not learned"; }
        return new { index = i, name = bp.Name, operation = bp.Operation, craftable = can, reason, seconds = bp.Seconds,
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
    void RequireSession() { if (_server == null) throw new InvalidOperationException("Send init first"); }
    public void Dispose() { _loot = null; _client?.Disconnect(); _server?.TearDown(); _client = null; _server = null; }
}
