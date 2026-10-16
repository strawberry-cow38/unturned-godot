using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

/// <summary>The LootField streaming adapter: unchanged source parsers/weighted rolls,
/// authoritative entity lifecycle in place of Godot node spawn/QueueFree.</summary>
public sealed class RetailLootStreamer
{
    readonly (int Index, ushort ItemId, byte Table, float X, float Y, float Z)[] _points;
    readonly Dictionary<int, uint> _live = new();
    readonly HashSet<int> _taken = new();
    readonly NetWorldServer _server;
    readonly WorldGeometry _geometry;
    readonly bool _generated;
    long _nextTick;
    public int PointCount => _points.Length;
    public int LiveCount => _live.Count;
    public string MapRoot { get; }
    public sealed record LivePoint(int Index, ushort ItemId, float X, float Y, float Z);
    public sealed record SavedPoint(int Index, ushort ItemId, byte Table, float X, float Y, float Z);
    public sealed record SaveData(string MapRoot, int[] Taken, LivePoint[] Live, SavedPoint[] GeneratedPoints = null);
    public RetailLootStreamer(string mapRoot, NetWorldServer server, WorldGeometry geometry)
    {
        if (!File.Exists(Path.Combine(mapRoot, "Spawns", "Items.dat")) || !File.Exists(Path.Combine(mapRoot, "Spawns", "Jars.dat")))
            throw new FileNotFoundException("Retail map requires Spawns/Items.dat and Spawns/Jars.dat");
        MapRoot = Path.GetFullPath(mapRoot);
        var source = new RetailLootData(); source.LoadFromPei(MapRoot); _points = source.Points().ToArray();
        _server = server; _geometry = geometry;
    }
    public RetailLootStreamer(string mapRoot, IReadOnlyList<(Godot.Vector3 Pos, byte Table)> points,
        NetWorldServer server, WorldGeometry geometry)
    {
        if (!File.Exists(Path.Combine(mapRoot,"Spawns","Items.dat")))
            throw new FileNotFoundException("Generated loot requires original Spawns/Items.dat tables");
        MapRoot = Path.GetFullPath(mapRoot);
        var source = new RetailLootData(); source.LoadGenerated(MapRoot,points);
        _points = source.Points().ToArray(); _generated = true;
        _server = server; _geometry = geometry;
    }
    RetailLootStreamer(SaveData saved, NetWorldServer server, WorldGeometry geometry)
    {
        MapRoot = saved.MapRoot; _generated = true; _server = server; _geometry = geometry;
        _points = saved.GeneratedPoints.Select(p => (p.Index,p.ItemId,p.Table,p.X,p.Y,p.Z)).ToArray();
    }
    public static RetailLootStreamer FromSave(SaveData saved, NetWorldServer server,
        WorldGeometry geometry, Vector3 origin)
    {
        // Generated rows are already in the saved local frame and preserve the
        // original rolls, including points not yet visited. Never read retail
        // Jars.dat or rerun the island generator to restore this field.
        var result = saved.GeneratedPoints != null ? new RetailLootStreamer(saved,server,geometry)
            : new RetailLootStreamer(saved.MapRoot,server,geometry);
        if (saved.GeneratedPoints == null) result.TranslateWorld(-origin);
        result.Restore(saved);
        return result;
    }
    public void TranslateWorld(Vector3 d)
    {
        for (int i = 0; i < _points.Length; i++)
        { var p = _points[i]; _points[i] = (p.Index, p.ItemId, p.Table, p.X + d.x, p.Y + d.y, p.Z + d.z); }
    }
    public SaveData Capture()
    {
        ObservePickedUp();
        return new(MapRoot, _taken.Order().ToArray(), _live.Select(pair => {
            _server.WorldItems.TryGet(pair.Value, out var e);
            return new LivePoint(pair.Key, e.ItemId, e.Pos.x, e.Pos.y, e.Pos.z);
        }).ToArray(), _generated ? _points.Select(p =>
            new SavedPoint(p.Index,p.ItemId,p.Table,p.X,p.Y,p.Z)).ToArray() : null);
    }
    public void Restore(SaveData saved)
    {
        _taken.UnionWith(saved.Taken ?? []);
        var restored = _server.WorldItems.All.ToList();
        foreach (var point in saved.Live ?? [])
        {
            int at = restored.FindIndex(e => e.ItemId == point.ItemId && MathF.Abs(e.Pos.x - point.X) < .01f
                && MathF.Abs(e.Pos.y - point.Y) < .01f && MathF.Abs(e.Pos.z - point.Z) < .01f);
            if (at < 0) { _taken.Add(point.Index); continue; }
            var entity = restored[at]; restored.RemoveAt(at);
            _server.WorldItems.ServerSettle(entity.NetIdValue, entity.Pos, _server.Session.CurrentTick);
            _live[point.Index] = entity.NetIdValue;
        }
    }
    void ObservePickedUp()
    {
        foreach (var pair in _live.ToArray())
            if (!_server.WorldItems.TryGet(pair.Value, out _)) { _taken.Add(pair.Key); _live.Remove(pair.Key); }
    }
    public void Step(bool force = false)
    {
        long tick = _server.Session.CurrentTick;
        if (!force && tick < _nextTick) return;
        _nextTick = tick + 18; // first 50 Hz boundary at or after the original 0.35 second interval
        var players = _server.Players.All.Select(p => p.Pos).ToArray();
        bool Near(float x, float z, float radius) => players.Any(p => (p.x - x) * (p.x - x) + (p.z - z) * (p.z - z) <= radius * radius);
        ObservePickedUp();
        foreach (var pair in _live.ToArray())
        {
            _server.WorldItems.TryGet(pair.Value, out var item);
            if (!Near(item.Pos.x, item.Pos.z, 125f)) { _server.WorldItems.ServerRemove(pair.Value, tick); _live.Remove(pair.Key); }
        }
        foreach (var p in _points)
        {
            if (_live.Count >= 600) break;
            if (_live.ContainsKey(p.Index) || _taken.Contains(p.Index) || !Near(p.X, p.Z, 95f)) continue;
            var item = Assets.makeLoot(p.ItemId, p.Table);
            float y = MathF.Max(p.Y, _geometry.GroundHeight(p.X, p.Z)) + .05f;
            var e = _server.WorldItems.ServerSpawn(_server.Ids.Mint(), item, new(p.X, y, p.Z), tick);
            _server.WorldItems.ServerSettle(e.NetIdValue, e.Pos, tick);
            _live[p.Index] = e.NetIdValue;
        }
    }
}
