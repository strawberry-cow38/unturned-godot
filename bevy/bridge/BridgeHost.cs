using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

public sealed class BridgeHost : IDisposable
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
    string _name = "Survivor", _map = "bevy";
    int _correctionVersion;
    readonly List<BridgeEvent> _events = new();
    long _eventSequence;
    readonly WorldGeometry _geometry = new();
    readonly Dictionary<int, InfiniteTerrain> _terrains = new();
    PlayerStanceSim _stance = new();
    RetailLootStreamer _loot;
    WorldEnvironment _environment;
    int _seed = 1337;
    sealed record BridgeSave(int Seed, RetailLootStreamer.SaveData Loot);
    public sealed record BridgeEvent(long Seq, string Kind, object Data);
    void Emit(string kind, object data) => _events.Add(new(++_eventSequence, kind, data));
    static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, PropertyNameCaseInsensitive = true, Converters = { new VectorJson() } };
    static readonly Dictionary<string, MethodInfo> Actions = typeof(NetWorldClient).GetMethods()
        .Where(m => m.Name.StartsWith("Send") && m.IsPublic && !m.Name.Contains("ForTest"))
        .ToDictionary(m => char.ToLowerInvariant(m.Name[4]) + m.Name[5..], StringComparer.OrdinalIgnoreCase);

    public object Handle(string op, JsonElement r)
    {
        if (r.TryGetProperty("ackEvents", out var ack))
        {
            long last = ack.GetInt64();
            if (last > _eventSequence) throw new ArgumentException("Event acknowledgement exceeds emitted sequence");
            _events.RemoveAll(e => e.Seq <= last);
        }
        switch (op)
        {
            case "init":
                Dispose(); _geometry.Clear();
                _catalog.Load(Text(r, "contentRoot", Path.GetFullPath("game/content")));
                _name = Text(r, "name", "Survivor"); _map = Text(r, "mapId", "bevy");
                _seed = Int(r, "seed", 1337);
                _position = Vec(r, "spawn", new(0, 1, 0));
                _velocity = Vector3.zero; _held = 0; _yaw = Float(r, "yaw"); _pitch = 0; _grounded = true; _correctionVersion++;
                Start();
                if (Bool(r, "starterKit")) SeedStarterKit();
                for (int n = 0; n < 6; n++) Tick();
                return new { protocolVersion = 1, fixedDelta = SimClock.FixedDelta, catalogItems = _catalog.Count,
                    recipes = _catalog.Recipes.Count, movementHeights = new { stand = PlayerMovementDef.HEIGHT_STAND, crouch = PlayerMovementDef.HEIGHT_CROUCH, prone = PlayerMovementDef.HEIGHT_PRONE }, capabilities = new[] { "movement", "memSession", "inventory", "vitals", "combat", "crafting", "save", "terrain", "catalog", "worldGeometry", "worldLoot", "worldClock", "weather", "eventAck" },
                    actions = Actions.Keys.Order().ToArray() };
            case "simulate":
                RequireSession();
                return Simulate(r.TryGetProperty("input", out var input) ? input : r);
            case "tick":
                RequireSession();
                if (r.TryGetProperty("input", out var body))
                {
                    _position = Vec(body, "position", _position); _velocity = Vec(body, "velocity", _velocity);
                    _grounded = Bool(body, "grounded", _grounded);
                    _yaw = Float(body, "yaw", _yaw); _pitch = Float(body, "pitch", _pitch);
                    _held = checked((ushort)Int(body, "heldItemId", _held));
                    if (body.TryGetProperty("stance", out _)) _movement.Stance = Stance(Text(body, "stance", "stand"));
                    _movement.Velocity = WorldToLocal(_velocity, _yaw);
                }
                Tick(); return new { fixedDelta = SimClock.FixedDelta };
            case "snapshot": RequireSession(); return null;
            case "catalog": RequireSession(); return new { items = _catalog.Metadata(), deployables = DeployableDef.All };
            case "action":
                RequireSession();
                return Action(Text(r, "name"), r.TryGetProperty("args", out var args) ? args : default);
            case "save":
                RequireSession();
                var save = WorldSave.Capture(_server, _map, _environment.DayAt(_server.Clock, _server.Session.CurrentTick),
                    _server.Clock.TimeOfDayAt(_server.Session.CurrentTick), _server.Clock.DayLengthSeconds, _server.PendingSave);
                string path = Path.GetFullPath(Text(r, "path"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var saveJson = JsonNode.Parse(save.ToJson());
                saveJson["BevyBridge"] = JsonSerializer.SerializeToNode(new BridgeSave(_seed, _loot?.Capture()));
                File.WriteAllText(path + ".tmp", saveJson.ToJsonString()); File.Move(path + ".tmp", path, true);
                _server.PendingSave = save;
                return new { path, version = save.Version };
            case "load":
                RequireSession();
                string loadJson = File.ReadAllText(Text(r, "path"));
                if (!WorldSave.TryParse(loadJson, _map, out var loaded, out var error)) throw new InvalidDataException(error);
                var extra = JsonNode.Parse(loadJson)?["BevyBridge"]?.Deserialize<BridgeSave>();
                if (extra != null) _seed = extra.Seed;
                Dispose(); Start(loaded);
                if (extra?.Loot != null)
                {
                    _loot = new RetailLootStreamer(extra.Loot.MapRoot, _server, _geometry);
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
                    seaLevel = InfiniteTerrain.SeaLevel };
            case "worldGeometry":
                if (Bool(r, "replace", true)) _geometry.Clear();
                if (r.TryGetProperty("terrain", out var terrainSpec))
                    _geometry.AddTerrain(Terrain(Int(terrainSpec, "seed", 1337)).Generate(new RegionCoord(Int(terrainSpec, "x"), Int(terrainSpec, "z")), Math.Clamp(Int(terrainSpec, "lod"), 0, 6)));
                if (r.TryGetProperty("terrainPatches", out var patches)) foreach (var patch in patches.EnumerateArray())
                {
                    var origin = patch.GetProperty("origin");
                    _geometry.AddHeightfield(origin[0].GetSingle(), origin[1].GetSingle(), Int(patch, "cells"), Float(patch, "spacing"),
                        patch.GetProperty("heights").EnumerateArray().Select(h => h.GetSingle()).ToArray(),
                        patch.TryGetProperty("holes", out var holes) && holes.ValueKind == JsonValueKind.Array ? holes.EnumerateArray().Select(h => h.GetBoolean()).ToArray() : null);
                }
                if (r.TryGetProperty("meshes", out var meshes)) foreach (var mesh in meshes.EnumerateArray())
                    _geometry.AddMesh(mesh.GetProperty("vertices").EnumerateArray().Select(Vec).ToArray(),
                        mesh.TryGetProperty("indices", out var ix) ? ix.EnumerateArray().Select(v => v.GetInt32()).ToArray() : Enumerable.Range(0, mesh.GetProperty("vertices").GetArrayLength()).ToArray(), Int(mesh, "destructibleIndex", -1), Bool(mesh, "terrain"));
                if (Bool(r, "finalize", true)) _geometry.Build();
                return new { triangles = _geometry.Count };
            case "worldLoot":
                RequireSession();
                string mapRoot = Path.GetFullPath(Text(r, "mapRoot"));
                if (_loot == null || _loot.MapRoot != mapRoot) _loot = new RetailLootStreamer(mapRoot, _server, _geometry);
                _loot.Step(force: true);
                return new { points = _loot.PointCount, live = _loot.LiveCount };
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
            case "worldRay":
                bool hit = _geometry.Ray(Vec(r, "from"), Vec(r, "to"), out var point, out var destructible);
                return new { hit, point = Array(point), destructibleIndex = destructible };
            case "shutdown": return null;
            default: throw new ArgumentException($"Unknown bridge operation: {op}");
        }
    }

    void Start(WorldSave save = null)
    {
        _network = new MemNetwork(); _movement = new PlayerMovementSim(); _stance = new PlayerStanceSim(); _recov = 0; _events.Clear(); _velocity = Vector3.zero; _lastRecipeState = null; _cachedRecipes = null;
        _server = new NetWorldServer(new MemServerTransport(_network));
        _environment = new WorldEnvironment(_server, _seed, save);
        _server.SpawnProvider = _ => _position;
        _server.Combat.WorldRay = _geometry.Ray;
        _server.Combat.GroundHeight = _geometry.GroundHeight;
        _server.Combat.ResolveHeldGunProfile = owner =>
            _server.Players.TryGetHeldInput(owner, out var held) && _catalog.Guns.TryGetValue(held.HeldItemId, out var gun)
                && _server.Inventories.TryGet(owner, out var owned) && owned.Inventory.getItemCount(held.HeldItemId) > 0 ? gun : null;
        DeployableNetSchema.RegisterAll(_server.Deployables.Schema);
        _server.Transactions.Blueprints = _catalog.Recipes;
        _server.PendingSave = save;
        save?.ApplyWorld(_server, 0);
        _client = new NetWorldClient(new MemClientTransport(_network), _name);
        DeployableNetSchema.RegisterAll(_client.Deployables.Schema);
        _client.PlayerRecov += e => { _position = e.Pos; _velocity = e.Vel; _recov = e.RecovCounter; _movement.Velocity = WorldToLocal(_velocity, _yaw); _correctionVersion++; };
        _client.ConsoleResult += e => Emit("console", e);
        _client.HitConfirmed += e => Emit("hit", e);
        _client.CraftQueue_ += e => Emit("craftQueue", e);
        _client.PlayerFired += e => Emit("fire", e);
        _client.ChatMessage += e => Emit("chat", e);
        _client.ImpactFx += e => Emit("impact", e);
        _client.PlayerDied += e => Emit("death", e);
        _client.PlayerRespawned += e => Emit("respawn", e);
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
        if (_client.State == NetSessionState.Connected)
            _client.SendPlayerState(_position, _yaw, _pitch, _velocity, (byte)(StanceCode(_movement.Stance) << 1), _grounded, _recov, _held);
        _network.Tick(); _client.Tick(); _server.TickSimulation(); _loot?.Step(); _environment.Step(_server); _server.TickReplication();
        if (_client.Players.TryGetByOwner(_client.PlayerId, out var own)) _client.AckCombat(own.LastProcessedCombatSeq);
    }

    object Action(string name, JsonElement args)
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
        var result = method.Invoke(_client, values);
        return new { queued = result is not bool boolean || boolean, value = result };
    }

    void SeedStarterKit()
    {
        if (!_server.Inventories.TryGet(_client.PlayerId, out var entry)) return;
        foreach (ushort id in new ushort[] { 4, 13, 14, 15 })
            if (Assets.find(id) != null) entry.Inventory.tryAddItem(new Item(id, 1));
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
        var inv = entry?.Inventory;
        var events = _events.ToArray();
        return new { tick = _network.CurrentTick, connected = _client.State == NetSessionState.Connected, playerId = _client.PlayerId,
            worldClock = new { day = _environment.DayAt(_client.Clock, _client.Applier.LastAppliedServerTick), timeOfDay = _client.Clock.TimeOfDayAt(_client.Applier.LastAppliedServerTick), dayLengthSeconds = _client.Clock.DayLengthSeconds },
            weather = _environment.State(),
            player = new { position = Array(_position), velocity = Array(_velocity), yaw = _yaw, pitch = _pitch,
                stance = _movement.Stance.ToString().ToLowerInvariant(), hp = combat?.Health ?? 100, alive = combat?.Alive ?? true,
                food = vitals?.Sim.Food ?? 1, water = vitals?.Sim.Water ?? 1, stamina = vitals?.Sim.Stamina ?? 1,
                infection = vitals?.Sim.Infection ?? 0, oxygen = vitals?.Sim.Oxygen ?? 1,
                ammo = _server.Combat.AmmoOf(_client.PlayerId), heldItemId = _held, correctionVersion = _correctionVersion },
            inventory = new { pages = inv?.items.Select(p => new { index = p.page, width = p.width, height = p.height,
                items = p.items.Select(j => new { id = j.item.id, name = Assets.find(j.item.id)?.itemName ?? $"Item {j.item.id}",
                    x = j.x, y = j.y, rot = j.rot, width = j.size_x, height = j.size_y, amount = j.item.amount,
                    quality = j.item.quality, icon = Assets.find(j.item.id)?.iconPath, gunAmmo = j.item.gunAmmo }).ToArray() }).ToArray(),
                worn = new { hat = inv?.wornHat?.id ?? 0, glasses = inv?.wornGlasses?.id ?? 0, mask = inv?.wornMask?.id ?? 0,
                    shirt = inv?.wornShirt?.id ?? 0, vest = inv?.wornVest?.id ?? 0, backpack = inv?.wornBackpack?.id ?? 0, pants = inv?.wornPants?.id ?? 0 } },
            recipes = Recipes(inv, skills?.Skills, includeRecipes),
            skills = new { experience = skills?.Skills.experience ?? 0, groups = skills?.Skills.skills },
            worldItems = _client.WorldItems.All.Select(e => new { netId = e.NetIdValue, itemId = e.ItemId, amount = e.Amount,
                quality = e.Quality, position = Array(e.Pos), settled = e.Settled }).ToArray(),
            deployables = _client.Deployables.All.Select(e => new { netId = e.NetIdValue, defId = e.DefId, position = Array(e.Pos), yaw = e.YawDegrees, health = e.Health, fuel = e.Fuel, toggledOn = e.ToggledOn }).ToArray(),
            vehicles = _client.Vehicles.All.Select(e => new { netId = e.NetIdValue, position = Array(e.Pos), yaw = e.YawDegrees, typeId = e.TypeId }).ToArray(),
            zombies = _client.Zombies.All.Select(e => new { netId = e.NetIdValue, position = Array(e.Pos), yaw = e.YawDegrees, animState = e.AnimState, speciality = e.Speciality }).ToArray(),
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
    static float[] Array(Vector3 v) => [v.x, v.y, v.z];
    static string Text(JsonElement r, string key, string fallback = "") => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    static int Int(JsonElement r, string key, int fallback = 0) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
    static float Float(JsonElement r, string key, float fallback = 0) { var f = r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetSingle() : fallback; return float.IsFinite(f) ? f : throw new ArgumentException($"{key} must be finite"); }
    static bool Bool(JsonElement r, string key, bool fallback = false) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
    static Vector3 Vec(JsonElement r, string key, Vector3 fallback = default) => r.TryGetProperty(key, out var v) ? Vec(v) : fallback;
    static Vector3 Vec(JsonElement v) { if (v.GetArrayLength() != 3) throw new ArgumentException("Expected [x,y,z]"); var a = v.EnumerateArray().Select(e => e.GetSingle()).ToArray(); if (a.Any(f => !float.IsFinite(f))) throw new ArgumentException("Vector must be finite"); return new(a[0], a[1], a[2]); }
    InfiniteTerrain Terrain(int seed) { if (!_terrains.TryGetValue(seed, out var source)) _terrains[seed] = source = new InfiniteTerrain(seed); return source; }
    void RequireSession() { if (_server == null) throw new InvalidOperationException("Send init first"); }
    public void Dispose() { _loot = null; _client?.Disconnect(); _server?.TearDown(); _client = null; _server = null; }
}
