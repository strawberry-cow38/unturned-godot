using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>The host side of original TreeTrunk/OreRock. Geometry supplies an
/// authoritative resource index; original reward functions run only on death.
/// Scene timers become deterministic sim deadlines, retained across saves.</summary>
public sealed class ResourceServices
{
    readonly NetWorldServer _server;
    readonly Catalog _catalog;
    readonly Action<string, object> _emit;
    readonly Dictionary<int, ResourceNode> _nodes = new();
    public sealed record Saved(int Index, float Health, long RegrowTicks, long DropTicks, float[] Direction);
    public ResourceServices(NetWorldServer server, Catalog catalog, Action<string, object> emit)
    {
        _server = server; _catalog = catalog; _emit = emit;
        server.Combat.DamageResource = Damage;
    }
    public void Clear() => _nodes.Clear();
    public void TranslateWorld(Vector3 d)
    { foreach (var node in _nodes.Values) node.Position = new(node.Position.X + d.x, node.Position.Y + d.y, node.Position.Z + d.z); }
    public void Register(JsonElement r)
    {
        string kind = Text(r, "kind");
        if (kind is not ("tree" or "ore")) return;
        int index = Int(r, "index");
        if (index < 0 || index > ushort.MaxValue) throw new ArgumentException("Resource index exceeds its wire budget");
        string name = Text(r, "name");
        var tree = ResourceRules.Tree(name);
        bool ore = kind == "ore";
        float health = Float(r, "health", ore ? ResourceRules.OreHealth : tree.Health);
        float reset = Float(r, "resetSeconds", ore ? ResourceRules.OreReset : tree.Reset);
        if (!float.IsFinite(health) || health <= 0 || !float.IsFinite(reset) || reset <= 0) throw new ArgumentException("Invalid resource health/reset");
        float trunkLength = Float(r, "trunkLength", 8);
        if (!float.IsFinite(trunkLength) || trunkLength <= 0) throw new ArgumentException("Invalid resource trunk length");
        var p = Vec(r, "position");
        if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) throw new ArgumentException("Invalid resource position");
        var node = new ResourceNode { Index = index, Name = name, Ore = ore, Position = new(p.x, p.y, p.z),
            Health = health, MaxHealth = health, ResetTicks = Ticks(reset), TrunkLength = trunkLength,
            LogItem = tree.Log, RewardMin = ore ? ResourceRules.OreRewardMin : tree.Min,
            RewardMax = ore ? ResourceRules.OreRewardMax : tree.Max };
        node.Spawn = (item, pos) => _server.WorldItems.ServerSpawn(_server.Ids.Mint(), item, new(pos.X, pos.Y, pos.Z), _server.Session.CurrentTick);
        _nodes[index] = node;
    }
    void Damage(CombatResourceHit hit)
    {
        if (!_nodes.TryGetValue(hit.Index, out var n) || !_server.Resources.IsAlive(hit.Index)) return;
        float damage = hit.Damage;
        if (n.Ore)
        {
            // Original MeleeTree gates on pickaxe and uses Resource_Damage
            // unchanged by strong-swing, skill or player damage multipliers.
            if (!hit.Melee || hit.Weapon != "axe_pick") damage = 0;
            else damage = _catalog.MeleeDefs.Values.FirstOrDefault(m => m.Name == hit.Weapon)?.ResourceDamage ?? hit.Damage;
        }
        if (!float.IsFinite(damage) || damage < 0
            || !float.IsFinite(hit.Point.x) || !float.IsFinite(hit.Point.y) || !float.IsFinite(hit.Point.z)
            || !float.IsFinite(hit.Direction.x) || !float.IsFinite(hit.Direction.y) || !float.IsFinite(hit.Direction.z)) return;
        n.Health = Math.Max(0, n.Health - damage);
        Publish(n, new ResourceContactEvent { Kind = ResourceContactKind.Hit, Position = hit.Point,
            Tick = unchecked((uint)hit.Tick), Health = n.Health, Damage = damage });
        if (n.Health > 0) return;
        _server.Transactions.SetResourceAlive(n.Index, false);
        n.RegrowAt = hit.Tick + n.ResetTicks;
        var dir = new Vector3(hit.Direction.x, 0, hit.Direction.z);
        dir = dir.sqrMagnitude > .01f ? dir.normalized : new(0, 0, -1);
        n.FallDirection = new(dir.x, dir.y, dir.z);
        if (n.Ore) n.Reward();
        else n.DropAt = hit.Tick + Ticks(ResourceRules.DebrisLife);
        Publish(n, new ResourceContactEvent { Kind = ResourceContactKind.Felled, Tick = unchecked((uint)hit.Tick),
            Position = new(n.Position.X, n.Position.Y, n.Position.Z), Direction = dir,
            TrunkLength = n.TrunkLength, DebrisSeconds = n.Ore ? 0 : (float)ResourceRules.DebrisLife });
    }
    void Publish(ResourceNode node, ResourceContactEvent value)
    {
        value.Index = checked((ushort)node.Index);
        value.Material = node.Ore ? ResourceContactMaterial.Ore : ResourceContactMaterial.Tree;
        var message = NetMessagePak.Pack(ReplicationIds.EventResourceContact, value.Write);
        _emit(ResourceEvents.Name(value), ResourceEvents.Data(value, node.Name));
        _server.BroadcastEvent(message);
    }
    public void Step()
    {
        long tick = _server.Session.CurrentTick;
        foreach (var n in _nodes.Values)
        {
            if (n.DropAt >= 0 && tick >= n.DropAt) { n.Reward(); n.DropAt = -1; }
            if (n.RegrowAt >= 0 && tick >= n.RegrowAt)
            { n.Health = n.MaxHealth; n.RegrowAt = -1; _server.Transactions.SetResourceAlive(n.Index, true); }
        }
    }
    public Saved[] Capture()
    {
        long tick = _server.Session.CurrentTick;
        return _nodes.Values.Where(n => n.Health < n.MaxHealth || n.RegrowAt >= 0 || n.DropAt >= 0)
            .Select(n => new Saved(n.Index, n.Health, n.RegrowAt < 0 ? -1 : Math.Max(0, n.RegrowAt - tick),
                n.DropAt < 0 ? -1 : Math.Max(0, n.DropAt - tick), new[] { n.FallDirection.X, n.FallDirection.Y, n.FallDirection.Z })).ToArray();
    }
    public void Restore(Saved[] saved)
    {
        long tick = _server.Session.CurrentTick;
        foreach (var s in saved ?? []) if (_nodes.TryGetValue(s.Index, out var n))
        {
            n.Health = Math.Clamp(s.Health, 0, n.MaxHealth);
            n.RegrowAt = s.RegrowTicks < 0 ? -1 : tick + s.RegrowTicks;
            n.DropAt = s.DropTicks < 0 ? -1 : tick + s.DropTicks;
            if (s.Direction?.Length == 3) n.FallDirection = new(s.Direction[0], s.Direction[1], s.Direction[2]);
        }
    }
    static long Ticks(double seconds) => Math.Max(1, (long)Math.Ceiling(seconds / SimClock.FixedDelta));
}

public sealed partial class ResourceNode
{
    public int Index;
    public string Name;
    public bool Ore;
    public float Health, MaxHealth;
    public long ResetTicks, RegrowAt = -1, DropAt = -1;
    public int RewardMin, RewardMax;
    public ushort LogItem;
    ushort StickItem => (ushort)(LogItem + 1);
    const ushort ScrapItem = 67;
    public Godot.Vector3 Position;
    Godot.Vector3 _fallDir = new(0, 0, -1);
    float _trunkLen = 8;
    public Godot.Vector3 FallDirection { get => _fallDir; set => _fallDir = value; }
    public float TrunkLength { get => _trunkLen; set => _trunkLen = value; }
    public Action<Item, Godot.Vector3> Spawn;
    public void Reward() { if (Ore) DropScrap(); else DropRewards(); }
}
