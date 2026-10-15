using System.Text.Json;
using SDG.NetPak;
using SDG.Unturned;
using SDG.NetTransport;
using SDG.NetTransport.Mem;
using SDG.NetTransport.Udp;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    ushort _listenPort;
    int _maxPeers = 24;
    string _serverName = "Bevy world", _motd = "";
    UdpServerTransport _udpTransport;

    void ConfigureNetwork(JsonElement request)
    {
        int port = Int(request, "listenPort");
        if (port < 0 || port > ushort.MaxValue)
            throw new ArgumentException("listenPort must be zero (local only) or 1..65535");
        int peers = Int(request, "maxPlayers", 24);
        if (peers < 1 || peers > 32)
            throw new ArgumentException("maxPlayers must be 1..32, including the local player");
        _listenPort = (ushort)port;
        _maxPeers = peers;
        _serverName = Text(request, "serverName", "Bevy world");
        _motd = Text(request, "motd");
    }

    IServerTransport ServerTransport()
    {
        if (_listenPort == 0) return new MemServerTransport(_network);
        // Same query responder, content identity and session protocol as the
        // original dedicated server. The local player also traverses UDP.
        return _udpTransport = new UdpServerTransport(_listenPort) {
            StatusVersion = NetContent.Hash,
            StatusProtocol = NetProtocol.Version,
            StatusMotd = _motd,
            StatusMap = _map,
            StatusGamemode = "Survival",
            StatusMaxPlayers = _maxPeers,
            StatusPvp = true,
        };
    }

    IClientTransport LocalTransport() => _listenPort == 0
        ? new MemClientTransport(_network)
        : new UdpClientTransport("127.0.0.1", _listenPort);

    void CheckNetworkOrigin(double x, double z)
    {
        if (_listenPort != 0 && (x != 0 || z != 0))
            throw new InvalidOperationException("UDP worlds require the shared zero origin; remote origin negotiation is not implemented");
    }

    void CheckNetworkSpawn(UnityEngine.Vector3 spawn)
    {
        if (_listenPort != 0 && (MathF.Abs(spawn.x) >= NetQuantization.PositionXZRange
            || MathF.Abs(spawn.z) >= NetQuantization.PositionXZRange || MathF.Abs(spawn.y) >= NetQuantization.PositionYRange))
            throw new ArgumentException("UDP spawn must fit the original shared world-position wire range");
    }

    object NetworkState() => new {
        mode = _listenPort == 0 ? "local" : "listen",
        port = _listenPort,
        serverName = _client.Session.ServerName,
        maxPlayers = _client.Session.ServerMaxPlayers,
        state = _client.State.ToString(),
        reason = _client.Session.DisconnectReason.ToString(),
        peers = _server.Session.Peers.Count,
        protocol = NetProtocol.Version,
    };

    object[] PlayerReplicas() => _client.Players.All.Select(player => {
        _client.CombatState.TryGet(player.OwnerPlayerId, out var combat);
        _client.Profiles.TryGet(player.OwnerPlayerId, out var profile);
        return (object)new {
            playerId = player.OwnerPlayerId, position = Array(player.Pos), yaw = player.YawDegrees,
            profile = profile == null ? null : new { name = profile.Name, face = profile.Face, avatarHash = profile.AvatarHash },
            alive = combat?.Alive ?? true, health = combat?.Health ?? 100,
            // Transform wire stance (0 stand .. 4 furniture sitting), rather
            // than the distinct EPlayerStance enum in appearance publication.
            heldItemId = combat?.HeldId ?? 0, stance = player.Stance, gesture = combat?.Gesture ?? 0,
            worn = new { hat = combat?.WornHat ?? 0, glasses = combat?.WornGlasses ?? 0,
                mask = combat?.WornMask ?? 0, shirt = combat?.WornShirt ?? 0, vest = combat?.WornVest ?? 0,
                backpack = combat?.WornBackpack ?? 0, pants = combat?.WornPants ?? 0 },
            wornQuality = new {
                hat = Durability.UnpackWorn(combat?.WornCond ?? 0, 0), glasses = Durability.UnpackWorn(combat?.WornCond ?? 0, 1),
                mask = Durability.UnpackWorn(combat?.WornCond ?? 0, 2), shirt = Durability.UnpackWorn(combat?.WornCond ?? 0, 3),
                vest = Durability.UnpackWorn(combat?.WornCond ?? 0, 4), backpack = Durability.UnpackWorn(combat?.WornCond ?? 0, 5),
                pants = Durability.UnpackWorn(combat?.WornCond ?? 0, 6),
            },
            attachments = new { sightId = combat?.HeldSight ?? 0, magazineId = combat?.HeldMagazine ?? 0, barrelId = combat?.HeldBarrel ?? 0 },
            lights = new { worn = combat?.WornLightOn ?? false, held = combat?.HeldLightOn ?? false },
        };
    }).ToArray();
}
