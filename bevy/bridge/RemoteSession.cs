using System.Text.Json;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    string _connectHost;
    ushort _connectPort = 47872;
    bool _remoteSpawnAdopted, _remoteProfileSent;
    RemoteWeaponView _remoteWeapon;
    RemoteEnvironment _remoteEnvironment;
    bool IsRemote => _connectHost != null;

    void ConfigureRemote(JsonElement request)
    {
        string host = Text(request, "connectHost").Trim();
        _connectHost = host.Length == 0 ? null : host;
        int port = Int(request, "connectPort", 47872);
        if (port < 1 || port > ushort.MaxValue) throw new ArgumentException("connectPort must be 1..65535");
        _connectPort = (ushort)port;
        if (!IsRemote) return;
        if (_listenPort != 0) throw new ArgumentException("connectHost and listenPort are mutually exclusive");
        if (Bool(request, "starterKit")) throw new ArgumentException("starterKit is a host-only initializer");
        if (Text(request, "password").Length > 0)
            throw new ArgumentException("Password authentication is not implemented by the original handshake protocol");
    }

    string[] Capabilities() => _dedicated
        ? ["dedicated", "udpListen", "save", "autosave", "moderation", "catalog", "worldGeometry", "worldLoot", "worldWildlife", "worldZombies", "worldClock", "weather", "eventAck"]
        : IsRemote
        ? ["movement", "udpClient", "inventory", "vitals", "combat", "crafting", "catalog", "terrain", "worldClock", "eventAck", "clientReplicas", "mapBindings"]
        : ["movement", "memSession", "udpListen", "inventory", "vitals", "combat", "crafting", "save", "terrain", "catalog", "worldGeometry", "worldLoot", "worldWildlife", "worldZombies", "worldClock", "weather", "eventAck", "rebase"];

    /// <summary>Host enrichments the remote still lacks and that the source client HAD (protocol follow-ups).
    /// v80 closed repairTool (Command101), weather/dayNumber (System21) and vehiclePartHealth (System9 tail).</summary>
    static readonly string[] RemoteWireGaps = [
        "heldCosmeticBullets", "heldWarheadEffects",
    ];

    /// <summary>Host data a remote lacks because the Godot source never sent it to a joined client either --
    /// not gaps to port, recorded so nobody invents them (row 15A, each checked against game/):
    /// explicitRespawn -- PlayerController.Die leaves the death-screen buttons dead on a remote server
    ///   (`_serverOwnedRespawn &amp;&amp; NetRequestRespawn == null`); only the automatic server respawn runs.
    /// acceptedImpactDetails -- ClientWorldSession.ImpactFx renders position + flesh/not-flesh only.
    /// stuckProjectilePose, projectileLifetime -- ProjectileReplicaView mirrors in-flight System4 entities
    ///   only; it frees the node when the server retires the entity and has no pose/expiry of its own.
    /// wildlifeHostState -- AnimalPuppets interpolate transform + anim byte; no health or corpse clock.
    /// zombieHostState -- ClientWorldSession removed the ZombiePuppets renderer with the old zombie system and
    ///   DedicatedServer removed ZombieNetSync; the current ZombieChunkField is built only for the local SP/
    ///   listen player (WorldBuilder Player = player), never published, and has no attack on players at all.
    /// password -- the original handshake has no password field (init refuses a nonempty one).
    /// acceptedShotTick -- PlayerFiredEvent carries origin/dir/gun only.</summary>
    static readonly string[] RemoteSourceAbsent = [
        "explicitRespawn", "acceptedImpactDetails", "stuckProjectilePose", "projectileLifetime",
        "wildlifeHostState", "zombieHostState", "password", "acceptedShotTick",
    ];

    long _toolSentAt = -1;
    bool _toolSentActive;
    /// <summary>v80 Command101. Native repeats Tool(true) every frame while the trigger is held; the host
    /// window is five ticks (BodyCombatServices.ToolUse), so one command per bridge tick is enough and a
    /// change of state always goes out at once.</summary>
    bool SendRemoteToolUse(bool active)
    {
        long now = _network.CurrentTick;
        if (active && _toolSentActive && _toolSentAt == now) return true;
        if (!_client.SendToolUse(active)) return false;
        _toolSentAt = now; _toolSentActive = active;
        return true;
    }

    void CheckRemoteOperation(string operation)
    {
        if (!IsRemote || operation is "init" or "disconnect" or "shutdown" or "simulate" or "tick" or "snapshot"
            or "catalog" or "powerView" or "worldSpawns" or "npcCatalog" or "npcView" or "diagnostics" or "action" or "terrain" or "reconnect"
            or "serverProbeStart" or "serverProbePoll" or "serverProbeCancel" or "mapBindings" or "profileAvatar" or "profileRules" or "localProfile" or "setLocalFace" or "attachmentOptions" or "craftingView" or "inventoryView"
            or "bugReport" or "bugReportRetry" or "bugReportPoll") return;
        throw new InvalidOperationException($"{operation} requires world authority; this session is a remote client");
    }

    void StartRemote()
    {
        _remoteSpawnAdopted = false; _remoteProfileSent = false; _toolSentAt = -1; _toolSentActive = false;
        _position = Vector3.zero; _velocity = Vector3.zero; _held = 0;
        CreateClient(new UdpClientTransport(_connectHost, _connectPort));
        _remoteWeapon = new RemoteWeaponView(_client, _catalog, Emit);
        _remoteEnvironment = new RemoteEnvironment(_client.Environment);
        _remoteMounted = new RemoteMountedFlights(_catalog.Mounted,
            (kind,data,tick)=>_events.Add(new(++_eventSequence,kind,data,tick)));
        // Handshake is intentionally asynchronous. The native frame loop keeps
        // polling while connecting; a fast local spin cannot advance a remote server.
    }

    object ReconnectRemote()
    {
        RequireSession();
        if (!IsRemote) throw new InvalidOperationException("reconnect requires a remote client");
        if (_remoteSpawnAdopted)
            throw new InvalidOperationException("A previously joined session must be replaced with init to clear old replicas");
        if (_client.State != NetSessionState.Disconnected
            || _client.Session.DisconnectReason != NetDisconnectReason.Rejected
            || !NetRejectText.IsRetryable(_client.Session.RejectReason))
            return new { queued = false };
        _remoteProfileSent = false;
        _client.Connect(); // Source ClientWorldSession retry path; native owns retry count/policy.
        return new { queued = true };
    }

    void TickRemote()
    {
        _network.Tick();
        _client.Tick();
        if (_client.State != NetSessionState.Connected) { _remoteMounted.Clear(); return; }
        _remoteMounted.Step(unchecked((uint)_client.Applier.LastAppliedServerTick));
        if (!_remoteProfileSent)
        {
            SendConfiguredProfile();
            _remoteProfileSent = true;
        }
        if (!_remoteSpawnAdopted)
        {
            // Source ClientWorldSession creates its one owner body from the first
            // actual owner entity. Never publish a map hint over the server spawn.
            if (!_client.Players.TryGetByOwner(_client.PlayerId, out var owner)) return;
            _position = owner.Pos; _yaw = owner.YawDegrees; _velocity = Vector3.zero;
            _movement = new(); _stance = new(); _correctionVersion++;
            _remoteSpawnAdopted = true;
        }
        _remoteWeapon.Step(_held);
        if (_remoteWeapon.TakeSelectionChange() is ushort selected) _held = selected;
        StepSteady();
        TemperatureTick();
        _client.SendPlayerState(_position, _yaw, _pitch, _velocity,
            (byte)((StanceCode(_movement.Stance) << 1) | (_scopeSteady.Steadying ? MoveInput.ButtonSteady : 0)),
            _grounded, _recov, _held, TemperatureBand);
    }

    object RemoteAction(string name, JsonElement args)
    {
        if (name == "requestGesture") return RequestGesture(args);
        if (name == "holdItem")
        {
            bool queued = _remoteWeapon.RequestSelect(Int(args, "page", -1), Int(args, "x", -1), Int(args, "y", -1), false);
            return new { queued, pending = queued, heldItemId = _held };
        }
        if (name == "equipAndHold")
        {
            bool queued = _remoteWeapon.RequestSelect(Int(args, "page", -1), Int(args, "x", -1), Int(args, "y", -1), true);
            return new { queued, pending = queued, heldItemId = _held };
        }
        if (name == "setFiremode") return new { queued = _remoteWeapon.SetFiremode(Int(args, "mode", -1)) };
        if (name is "installAttachment" or "removeAttachment" or "chooseShellType" or "unloadShells" or "rackGun")
            return new { queued = _remoteWeapon.RequestAttachment(name,args) };
        if (name == "toolUse") return new { queued = SendRemoteToolUse(Bool(args, "active")) };
        if (name == "respawn")
        {
            string reason = $"{name} has no corresponding complete remote request in the current protocol";
            Emit("actionRejected", new { text = reason });
            return new { queued = false, reason };
        }
        // DevConsole `plant` on a joined client: no CropManager, so it sends
        // PlantCrop with the resolved seed (the server spends one).
        if (name == "console" && CropConsole(args, remote: true)) return new { queued = true };
        // Original Send* methods use connection identity. No console/repair/
        // damage shortcut can accidentally run a local authority in this mode.
        return SendAction(name, args);
    }

    object ClockSnapshot()
    {
        long tick = _server?.Session.CurrentTick ?? _client.Applier.LastAppliedServerTick;
        return new { day = _environment != null ? _environment.DayAt(_client.Clock, tick) : _remoteEnvironment?.DayAt(_client.Clock, tick),
            timeOfDay = _client.Clock.TimeOfDayAt(tick), dayLengthSeconds = _client.Clock.DayLengthSeconds };
    }

    object[] RemoteAnimals() => _client.Animals.All.Select(e => {
        var kind = AnimalCatalog.Get(e.Species);
        return (object)new { netId = e.NetIdValue, species = e.Species, animalId = kind.AnimalId,
            position = Array(e.Pos), yaw = e.YawDegrees, animState = e.AnimState, dead = e.IsDead,
            remote = true, clip = AnimalPuppetData.ClipFor(e.AnimState), bodyHeight = WildlifeServices.BodyHeightFor(kind.AnimalId),
            radius = AnimalBrain.RadiusFor(WildlifeServices.BodyHeightFor(kind.AnimalId)),
            rig = $"{kind.Rig}_rig.json", texture = $"objects/{kind.Tex}", rigYawFix = AnimalBrain.RigYawFix };
    }).ToArray();

    object[] RemoteZombies() => _client.Zombies.All.Select(e => (object)new {
        netId = e.NetIdValue, position = Array(e.Pos), yaw = e.YawDegrees, animState = e.AnimState,
        speciality = e.Speciality, dead = e.IsDead, remote = true, hasBody = false,
    }).ToArray();
}
