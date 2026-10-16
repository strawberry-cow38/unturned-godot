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

    static readonly string[] RemoteWireGaps = [
        "repairTool",
        "explicitRespawn", "weather", "dayNumber", "heldCosmeticBullets", "acceptedImpactDetails",
        "heldWarheadEffects", "stuckProjectilePose", "projectileLifetime", "vehiclePartHealth",
        "wildlifeHostState", "zombieHostState", "password", "acceptedShotTick",
    ];

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
        _remoteSpawnAdopted = false; _remoteProfileSent = false;
        _position = Vector3.zero; _velocity = Vector3.zero; _held = 0;
        CreateClient(new UdpClientTransport(_connectHost, _connectPort));
        _remoteWeapon = new RemoteWeaponView(_client, _catalog, Emit);
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
        if (name is "respawn" or "toolUse")
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
        return new { day = _environment == null ? (int?)null : _environment.DayAt(_client.Clock, tick),
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
