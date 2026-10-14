using System.Reflection;
using System.Text.Json;
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
    readonly List<object> _events = new();
    static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
    static readonly Dictionary<string, MethodInfo> Actions = typeof(NetWorldClient).GetMethods()
        .Where(m => m.Name.StartsWith("Send") && m.IsPublic && !m.Name.Contains("ForTest"))
        .ToDictionary(m => char.ToLowerInvariant(m.Name[4]) + m.Name[5..], StringComparer.OrdinalIgnoreCase);

    public object Handle(string op, JsonElement r)
    {
        switch (op)
        {
            case "init":
                Dispose();
                _catalog.Load(Text(r, "contentRoot", Path.GetFullPath("game/content")));
                _name = Text(r, "name", "Survivor"); _map = Text(r, "mapId", "bevy");
                _position = Vec(r, "spawn", new(0, 1, 0));
                Start();
                if (Bool(r, "starterKit")) SeedStarterKit();
                for (int n = 0; n < 6; n++) Tick();
                return new { protocolVersion = 1, fixedDelta = SimClock.FixedDelta, catalogItems = _catalog.Count,
                    recipes = _catalog.Recipes.Count, capabilities = new[] { "movement", "memSession", "inventory", "vitals", "combat", "crafting", "save", "terrain" },
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
            case "action":
                RequireSession();
                return Action(Text(r, "name"), r.TryGetProperty("args", out var args) ? args : default);
            case "save":
                RequireSession();
                var save = WorldSave.Capture(_server, _map, 0, 0.35f, 3600f, _server.PendingSave);
                string path = Path.GetFullPath(Text(r, "path"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", save.ToJson()); File.Move(path + ".tmp", path, true);
                return new { path, version = save.Version };
            case "load":
                RequireSession();
                if (!WorldSave.TryParse(File.ReadAllText(Text(r, "path")), _map, out var loaded, out var error)) throw new InvalidDataException(error);
                Dispose(); Start(loaded);
                for (int n = 0; n < 6; n++) Tick();
                return new { version = loaded.Version };
            case "terrain":
                var terrain = new InfiniteTerrain(Int(r, "seed", 1337));
                var coord = new RegionCoord(Int(r, "x"), Int(r, "z"));
                var region = terrain.Generate(coord, Math.Clamp(Int(r, "lod"), 0, 6));
                return new { origin_x = coord.MinX, origin_z = coord.MinZ, cells = region.Cells, spacing = region.Spacing,
                    heights = region.Heights, normals = region.Normals, layers = region.Layers.Select(b => (int)b).ToArray(),
                    trees = region.Trees, roads = region.Roads, foliage = region.Foliage, boulders = region.Boulders,
                    seaLevel = InfiniteTerrain.SeaLevel };
            case "shutdown": return null;
            default: throw new ArgumentException($"Unknown bridge operation: {op}");
        }
    }

    void Start(WorldSave save = null)
    {
        _network = new MemNetwork(); _movement = new PlayerMovementSim(); _recov = 0; _events.Clear();
        _server = new NetWorldServer(new MemServerTransport(_network));
        _server.SpawnProvider = _ => _position;
        _server.Transactions.Blueprints = _catalog.Recipes;
        _server.PendingSave = save;
        save?.ApplyWorld(_server, 0);
        _client = new NetWorldClient(new MemClientTransport(_network), _name);
        _client.PlayerRecov += e => { _position = e.Pos; _velocity = e.Vel; _recov = e.RecovCounter; _movement.Velocity = WorldToLocal(_velocity, _yaw); _correctionVersion++; };
        _client.ConsoleResult += e => _events.Add(new { kind = "console", data = e });
        _client.HitConfirmed += e => _events.Add(new { kind = "hit", data = e });
        _client.CraftQueue_ += e => _events.Add(new { kind = "craftQueue", data = e });
        _client.PlayerFired += e => _events.Add(new { kind = "fire", data = e });
        _client.Connect();
        for (int n = 0; n < 100 && _client.State != NetSessionState.Connected; n++) Tick();
        if (_client.State != NetSessionState.Connected) throw new InvalidOperationException("Local server handshake did not complete");
        for (int n = 0; n < 4; n++) Tick();
        if (save != null && _server.Players.TryGetByOwner(_client.PlayerId, out var player)) _position = player.Pos;
    }

    object Simulate(JsonElement input)
    {
        _yaw = Float(input, "yaw", _yaw); _pitch = Float(input, "pitch", _pitch);
        _grounded = Bool(input, "grounded", _grounded);
        _held = checked((ushort)Int(input, "heldItemId", _held));
        _movement.Stance = Stance(Text(input, "stance", "stand"));
        _movement.SpeedMultiplier = PlayerMovementSim.SpeedMultiplierForHeld(_held);
        _movement.GravityMultiplier = Float(input, "gravityMultiplier", 1);
        if (input.TryGetProperty("velocity", out _)) _movement.Velocity = WorldToLocal(Vec(input, "velocity"), _yaw);
        _velocity = LocalToWorld(_movement.Step(new(Float(input, "moveX"), Float(input, "moveY")), Bool(input, "jump"), _grounded, (float)SimClock.FixedDelta), _yaw);
        return new { velocity = Array(_velocity), height = PlayerMovementDef.HeightForStance(_movement.Stance), jumpLocked = _movement.JumpLocked };
    }

    void Tick()
    {
        if (_client.State == NetSessionState.Connected)
            _client.SendPlayerState(_position, _yaw, _pitch, _velocity, (byte)(StanceCode(_movement.Stance) << 1), _grounded, _recov, _held);
        _network.Tick(); _client.Tick(); _server.TickSimulation(); _server.TickReplication();
        if (_client.Players.TryGetByOwner(_client.PlayerId, out var own)) _client.AckCombat(own.LastProcessedCombatSeq);
        if (_catalog.Guns.TryGetValue(_held, out var gun)) _server.Combat.SetGunProfile(_client.PlayerId, gun);
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

    public object State()
    {
        if (_client == null) return null;
        _client.CombatState.TryGet(_client.PlayerId, out var combat);
        _client.Vitals.TryGet(_client.PlayerId, out var vitals);
        _client.Inventories.TryGet(_client.PlayerId, out var entry);
        _client.Skills.TryGet(_client.PlayerId, out var skills);
        var inv = entry?.Inventory;
        var events = _events.ToArray(); _events.Clear();
        return new { tick = _network.CurrentTick, connected = _client.State == NetSessionState.Connected, playerId = _client.PlayerId,
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
            recipes = _catalog.Recipes.Select((bp, i) => Recipe(bp, i, inv, skills?.Skills)).ToArray(),
            skills = new { experience = skills?.Skills.experience ?? 0, groups = skills?.Skills.skills },
            worldItems = _client.WorldItems.All.Select(e => new { netId = e.NetIdValue, itemId = e.ItemId, amount = e.Amount,
                quality = e.Quality, position = Array(e.Pos), settled = e.Settled }).ToArray(),
            zombies = _client.Zombies.All.Select(e => new { netId = e.NetIdValue, position = Array(e.Pos), yaw = e.YawDegrees, animState = e.AnimState, speciality = e.Speciality }).ToArray(),
            events };
    }

    static object Recipe(BlueprintDef bp, int i, PlayerInventory inv, PlayerSkills skills)
    {
        string reason = "Inventory unavailable";
        bool can = inv != null && Crafting.CanCraft(bp, new Crafting.PlayerInvAdapter(inv), skills, out reason);
        if (bp.RequiresStation) { can = false; reason = "Requires crafting station"; }
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
    void RequireSession() { if (_server == null) throw new InvalidOperationException("Send init first"); }
    public void Dispose() { _client?.Disconnect(); _server?.TearDown(); _client = null; _server = null; }
}
