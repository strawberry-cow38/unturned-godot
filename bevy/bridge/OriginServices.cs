using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SDG.Unturned;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    double _originX, _originZ;
    long _originEpoch;
    object Origin => new { epoch = _originEpoch, x = _originX, z = _originZ };

    object Rebase(JsonElement request)
    {
        RequireSession();
        long expected = request.GetProperty("expectedEpoch").GetInt64();
        if (expected != _originEpoch) throw new InvalidOperationException($"Origin epoch mismatch: expected {_originEpoch}, received {expected}");
        var shift = Vec(request, "shift");
        if (shift.y != 0 || shift == Vector3.zero || shift.x % InfiniteTerrain.RegionSize != 0 || shift.z % InfiniteTerrain.RegionSize != 0)
            throw new ArgumentException("Rebase shift must be a nonzero horizontal multiple of the 256m region size");
        // A local teleport before the barrier could still be waiting as a reliable fact. Drain it first,
        // then translate that accepted pose, never overwrite it with the request's pre-drain position.
        DrainNetwork();
        var delta = -shift;
        WorldRebase.Translate(_server, _client, delta);
        _position += delta;
        _geometry.TranslateWorld(delta);
        _services.TranslateWorld(delta);
        _vehicles.TranslateWorld(delta);
        _loot?.TranslateWorld(delta);
        _wildlife.TranslateWorld(delta);
        _zombies.TranslateWorld(delta);
        for (int i = 0; i < _serviceSpecs.Count; i++) _serviceSpecs[i] = CoordinateJson.ServiceSpec(_serviceSpecs[i], delta);
        for (int i = 0; i < _events.Count; i++)
        {
            var e = _events[i];
            _events[i] = e with { Data = CoordinateJson.Event(e.Data, delta) };
        }
        _originX += shift.x; _originZ += shift.z; _originEpoch++;
        // Full snapshots establish new baselines at the SAME tick. No TickSimulation, recov event,
        // join, ID mint or timer reset takes place across this coordinate change.
        PublishFullSnapshot();
        DrainNetwork();
        return new { shift = Array(shift), origin = Origin };
    }

    void PrepareRespawnFrame()
    {
        if (!_server.CombatState.TryGet(_client.PlayerId, out var combat) || combat.Alive) return;
        var target = _server.Combat.RespawnPositionOf?.Invoke(_client.PlayerId) ?? combat.SpawnPos;
        float margin = NetQuantization.PositionXZRange - InfiniteTerrain.RegionSize;
        if (MathF.Abs(target.x) < margin && MathF.Abs(target.z) < margin) return;
        var shift = new Vector3(MathF.Floor(target.x / InfiniteTerrain.RegionSize) * InfiniteTerrain.RegionSize, 0,
            MathF.Floor(target.z / InfiniteTerrain.RegionSize) * InfiniteTerrain.RegionSize);
        Rebase(JsonSerializer.SerializeToElement(new { expectedEpoch = _originEpoch, shift = Array(shift) }));
    }

    void DrainNetwork()
    {
        // This bridge owns a zero-latency/loss MemNetwork. A remote/faulted network needs an explicit
        // negotiated epoch protocol; refusing an undrainable barrier is safer than dropping messages.
        _client.PollNetwork();
        for (int i = 0; i < 1024; i++)
        {
            _server.PollNetwork();
            _client.PollNetwork(false);
            bool reliable = _client.Session.Session.InFlightMessageCount != 0 || _client.Session.Session.PendingSendCount != 0
                || _server.Session.Peers.Any(p => p.Session.InFlightMessageCount != 0 || p.Session.PendingSendCount != 0);
            if (_network.PendingDatagramCount == 0 && !reliable) return;
        }
        throw new InvalidOperationException("Cannot rebase while old-frame network traffic is pending");
    }

    void PublishFullSnapshot()
    {
        foreach (var peer in _server.Session.Peers)
        {
            _server.Composer.ForgetClient(peer.PlayerId);
            var at = _server.Players.TryGetByOwner(peer.PlayerId, out var player) ? player.Pos : Vector3.zero;
            byte[] snapshot = _server.Composer.Compose(_server.Session.CurrentTick, peer.PlayerId, at,
                maxBytes: NetProtocol.MaxReliableMessageBytes / 2);
            peer.SendReliable(NetMessagePak.Pack(ReplicationIds.EventJoinSnapshot, w => {
                w.WriteUInt32((uint)snapshot.Length); w.WriteBytes(snapshot, 0, snapshot.Length);
            }, bufferSize: snapshot.Length + 8));
        }
        _worldChanged = false;
    }
}

/// <summary>Only fields representing WORLD positions are translated. Model pivots, extents,
/// directions and vehicle hit regions remain local vectors. This also keeps saved registration
/// manifests in the same frame as WorldSave rather than rebuilding live services on each rebase.</summary>
internal static class CoordinateJson
{
    static readonly JsonSerializerOptions Options = new() {
        IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new VectorJson(), new ByteArrayJson(), new JsonStringEnumConverter() }
    };
    static void Move(JsonObject obj, string key, Vector3 d)
    {
        // Persisted ObjectMapping predates the wire's camel-case policy. Preserve its
        // property spelling while translating either serialized representation.
        key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
        if (obj[key] is JsonArray a && a.Count == 3)
        { a[0] = a[0].GetValue<float>() + d.x; a[1] = a[1].GetValue<float>() + d.y; a[2] = a[2].GetValue<float>() + d.z; }
        else if (obj[key] is JsonObject v && v.ContainsKey("x") && v.ContainsKey("y") && v.ContainsKey("z"))
        { v["x"] = v["x"].GetValue<float>() + d.x; v["y"] = v["y"].GetValue<float>() + d.y; v["z"] = v["z"].GetValue<float>() + d.z; }
    }
    static void Entry(JsonObject obj, Vector3 d) { Move(obj, "position", d); Move(obj, "interactionPosition", d); }
    public static JsonElement Entry(JsonElement element, Vector3 d)
    {
        var node = JsonNode.Parse(element.GetRawText()).AsObject(); Entry(node, d);
        return JsonSerializer.SerializeToElement(node);
    }
    public static JsonElement Window(JsonElement element, Vector3 d)
    {
        var node=JsonNode.Parse(element.GetRawText()).AsObject();Move(node,"windowPosition",d);
        return JsonSerializer.SerializeToElement(node);
    }
    public static JsonElement ServiceSpec(JsonElement element, Vector3 d)
    {
        var node = JsonNode.Parse(element.GetRawText()).AsObject();
        foreach (string key in new[] { "objects", "mapObjects", "mapResources", "resources", "destructibles", "doors", "beds", "objectDoors", "containerDoors", "seats", "npcs", "taps", "fixtures", "containers", "vehicles" })
            if (node[key] is JsonArray entries) foreach (var e in entries.OfType<JsonObject>()) Entry(e, d);
        if (node["deadzones"] is JsonArray zones) foreach (var e in zones.OfType<JsonObject>()) Move(e, "center", d);
        if (node["seaLevel"] != null && d.y != 0) node["seaLevel"] = node["seaLevel"].GetValue<float>() + d.y;
        return JsonSerializer.SerializeToElement(node);
    }
    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, Options);
    public static JsonElement Event(object data, Vector3 d)
    {
        var node = JsonSerializer.SerializeToNode(data, Options);
        if (node is JsonObject obj)
            foreach (string key in new[] { "pos", "position", "point", "sourcePos", "origin" }) Move(obj, key, d);
        return JsonSerializer.SerializeToElement(node);
    }
}
