using System.Text.Json;
using System.Text.Json.Nodes;
using SDG.NetPak;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Read-only source map metadata joined to actual client fixtures.
/// Authored door/seat counters follow the same full manifest as the host;
/// minted fixture/container IDs are never inferred from registration order.</summary>
sealed class RemoteMapBindings
{
    readonly MapServices.Parsed _parsed;
    readonly string _map, _holiday;
    long _revision;
    string _signature;
    object _result;
    readonly record struct Key(ushort Kind, Vector3 Position, float Yaw);

    public RemoteMapBindings(JsonElement request, string map, string holiday)
    {
        _map = map; _holiday = holiday;
        if (!Text(request, "mapName", "PEI").Equals(map, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Map bindings must use the session's selected map");
        _parsed = MapServices.Parse(request, holiday);
    }

    static Key KeyFor(ushort kind, Vector3 pos, float yaw) =>
        new(kind, PlayerReplication.Quantize(pos), NetQuantization.QuantizeDegrees(yaw, NetQuantization.YawBits));
    static JsonElement[] Entries(JsonElement root, string name) => root.GetProperty(name).EnumerateArray().ToArray();

    public object Read(NetWorldClient client, string map, string holiday)
    {
        if (_map != map || _holiday != holiday) throw new InvalidOperationException("A different map/holiday requires fresh bindings");
        var containers = client.Containers.All.ToArray();
        var fixtures = client.Deployables.All.Where(e => DeployableDef.ById(e.DefId) is { Fixture: not FixtureKind.None }).ToArray();
        // Container fixtures are immutable poses; deployable graph version also
        // invalidates reconstructed/full snapshots. This only caches a query.
        string signature = string.Join(',', containers.Select(e => e.NetIdValue)) + "|" + client.Deployables.GraphVersion;
        if (_result != null && signature == _signature) return _result;
        _signature = signature;
        var containerGroups = containers.GroupBy(e => KeyFor(e.KindId, e.Pos, e.YawDegrees)).ToDictionary(g => g.Key, g => g.ToArray());
        var fixtureGroups = fixtures.GroupBy(e => KeyFor(e.DefId, e.Pos, e.YawDegrees)).ToDictionary(g => g.Key, g => g.ToArray());
        var sourceContainers = Entries(_parsed.Spec, "containers");
        var sourceFixtures = Entries(_parsed.Spec, "fixtures");
        var mappings = JsonSerializer.Deserialize<MapServices.ObjectMapping[]>(JsonSerializer.Serialize(_parsed.Objects));
        var containerIds = new Dictionary<int, uint>();
        int boundContainers = 0, boundFixtures = 0, ambiguousContainers = 0, ambiguousFixtures = 0;
        foreach (var m in mappings)
        {
            if (m.ContainerOrdinal is int c)
            {
                var source = sourceContainers[c];
                ushort kind = ContainerSchema.KindFor(Text(source, "mesh"), Bool(source, "display"), Text(source, "label"));
                var key = KeyFor(kind, Vec(source, "position"), Float(source, "yaw"));
                if (containerGroups.TryGetValue(key, out var candidates))
                {
                    if (candidates.Length == 1) { m.ContainerNetId = candidates[0].NetIdValue; containerIds[c] = candidates[0].NetIdValue; boundContainers++; }
                    else ambiguousContainers++;
                }
            }
            if (m.FixtureOrdinal is int f)
            {
                var source = sourceFixtures[f];
                var key = KeyFor(checked((ushort)Int(source, "defId")), Vec(source, "position"), Float(source, "yaw"));
                if (fixtureGroups.TryGetValue(key, out var candidates))
                {
                    if (candidates.Length == 1) { m.FixtureNetId = candidates[0].NetIdValue; boundFixtures++; }
                    else ambiguousFixtures++;
                }
            }
        }
        var doors = Entries(_parsed.Spec, "containerDoors").Where(d => containerIds.ContainsKey(Int(d, "containerOrdinal"))).Select(d => {
            var node = JsonNode.Parse(d.GetRawText()); node["netId"] = containerIds[Int(d, "containerOrdinal")]; return node;
        }).ToArray();
        return _result = new {
            revision = ++_revision, holiday, readOnly = true,
            complete = boundContainers == sourceContainers.Length && boundFixtures == sourceFixtures.Length,
            unboundContainers = sourceContainers.Length - boundContainers, ambiguousContainers,
            unboundFixtures = sourceFixtures.Length - boundFixtures, ambiguousFixtures,
            objects = mappings, resources = _parsed.Resources,
            objectDoors = Entries(_parsed.Spec, "objectDoors"), containerDoors = doors,
            seats = Entries(_parsed.Spec, "seats"), npcs = Entries(_parsed.Spec, "npcs"),
            resourceCount = Int(_parsed.Spec, "resourceCount"), destructibleCount = Int(_parsed.Spec, "destructibleCount"),
            containerCount = sourceContainers.Length, fixtureCount = sourceFixtures.Length,
            deadzoneCount = Entries(_parsed.Spec, "deadzones").Length,
        };
    }
}

public sealed partial class BridgeHost
{
    RemoteMapBindings _mapBindings;
    object ReadMapBindings(JsonElement request)
    {
        if (_client.State != NetSessionState.Connected)
            throw new InvalidOperationException("Map bindings require the server's accepted holiday");
        if (!Text(request, "mapName", "PEI").Equals(_map, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Map bindings must use the session's selected map");
        _mapBindings ??= new(request, _map, _client.ServerHoliday);
        return _mapBindings.Read(_client, _map, _client.ServerHoliday);
    }
}
