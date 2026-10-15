using System.Text.Json;
using UnityEngine;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    FiniteSpawnPool _finiteSpawns;
    int _respawnLookVersion;
    float? _respawnLookYaw;

    // Persist only the original regular points with the service registrations.
    // Reopening a save must not rerun the generator or depend on edited files.
    static object SpawnRows(FiniteSpawnPool pool) => pool.Points.Select(p =>
        new { position = Array(p.Position), yaw = p.Yaw }).ToArray();

    void PrepareFiniteRespawns(JsonElement request)
    {
        string directory = Text(request, "finiteDirectory");
        if (directory.Length == 0) return;
        if (IsRemote) throw new InvalidOperationException("Remote clients cannot install a spawn pool");
        var files = FiniteIslandFiles.Open(Godot.ProjectSettings.ContentRoot, directory);
        if (_map != "Island_" + files.Info.Name)
            throw new ArgumentException("Finite spawn pool must match the selected source map");
        var spec = JsonSerializer.SerializeToElement(new {
            finitePlayerSpawns = SpawnRows(FiniteSpawnPool.Load(files.PlayersPath)) });
        if (_originX != 0 || _originZ != 0)
            spec = CoordinateJson.ServiceSpec(spec, new((float)-_originX, 0, (float)-_originZ));
        _serviceSpecs.Add(spec);
    }

    void AdoptFiniteRespawns(JsonElement spec)
    {
        if (_finiteSpawns != null || !spec.TryGetProperty("finitePlayerSpawns", out var rows)) return;
        // Saved rows are already local. Construct in source coordinates first
        // so the source origin-placeholder guard survives a later rebase/load.
        var origin = new Vector3((float)_originX, 0, (float)_originZ);
        _finiteSpawns = new(rows.EnumerateArray().Select(p =>
            new FiniteSpawnPool.Point(Vec(p, "position") + origin, Float(p, "yaw"))));
        _finiteSpawns.Translate(-origin);
        _server.Combat.RespawnPositionOf = owner => {
            if (!_server.CombatState.TryGet(owner, out var life)) return _position;
            Vector3? bed = _server.Interactables.TryGetSpawn(owner, out var at, out _) ? at : null;
            var current = _server.Players.TryGetByOwner(owner, out var player) ? player.Pos : life.SpawnPos;
            return _finiteSpawns.Resolve(owner, bed, life.SpawnPos, current);
        };
        _server.Combat.PlayerRespawned += (owner, tick) => {
            if (_finiteSpawns.Complete(owner) is float yaw)
                _server.Combat.PublishRespawnYaw(owner, yaw, tick);
        };
        _server.Session.PeerDisconnected += (peer, _) => _finiteSpawns?.Forget(peer.PlayerId);
    }
}
