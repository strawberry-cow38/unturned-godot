using System.Text.Json;
using SDG.NetTransport;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

// Hybrid port of game/DedicatedServer.cs (+ WorldSaveDriver timing and the
// BanStore wiring). The bridge owns the authoritative NetWorldServer over UDP
// with NO local client: no player slot, no body, no owner snapshot. The
// headless native host (`unturned-bevy --dedicated`) keeps the parts that need
// physics -- world collision, vehicle bodies, zombie HOT bodies, NPC flight --
// and publishes them through the same world* operations a listen host uses.
//
// Boot order follows the source: the world is built and registered first, then
// `dedicatedServe` loads the save (the existing `load` path) and only then does
// the session start ticking, so no client ever sees the pre-restore world.
public sealed partial class BridgeHost
{
    /// <summary>Source DedicatedServer.Replicate: a 10 s heartbeat (500 ticks).</summary>
    const long DedicatedHeartbeatTicks = 500;

    bool _dedicated, _serving, _stopped, _netlog, _wipedSinceBoot;
    string _dedicatedMapRoot;
    bool _pvp = true;
    int _maxPing;
    byte[] _icon;
    bool _announceDeaths = true;
    DeathMessageOptions _deathMessages;
    long _lastHeartbeatTick;
    Vector3 _dedicatedFallbackSpawn;

    /// <summary>True while this process hosts a dedicated world; Program.cs then
    /// leaves SIGTERM/SIGINT to the native parent, which asks for the final save.</summary>
    public bool DedicatedActive => _dedicated && _server != null && !_stopped;

    static string Env(string key) => Environment.GetEnvironmentVariable(key);

    void ConfigureDedicated(JsonElement request)
    {
        _dedicated = Bool(request, "dedicated");
        _serving = false; _stopped = false;
        if (!_dedicated) return;
        if (IsRemote) throw new ArgumentException("dedicated and connectHost are mutually exclusive");
        if (_listenPort == 0) throw new ArgumentException("A dedicated server requires listenPort");
        if (Bool(request, "starterKit")) throw new ArgumentException("starterKit needs a local player; a dedicated server has none");
        if (request.TryGetProperty("finiteDirectory", out _)) throw new ArgumentException("Finite islands are not hosted by the dedicated server yet");
        _dedicatedMapRoot = Text(request, "mapRoot");

        // Source DedicatedServer._Ready reads the operator's UG_* environment once
        // at boot. An explicit request value (the native CLI) wins over it.
        if (!request.TryGetProperty("serverName", out _)) _serverName = Env("UG_NAME") is { Length: > 0 } name ? name : "";
        if (!request.TryGetProperty("motd", out _)) _motd = Env("UG_MOTD") is { Length: > 0 } motd ? motd : "";
        if (!request.TryGetProperty("maxPlayers", out _))
        {
            // There is no local player to count, so every seat is a remote one.
            _maxPeers = int.TryParse(Env("UG_MAXPLAYERS"), out int seats) && seats > 0 ? seats : 24;
            if (_maxPeers > 32)
            {
                // The session cap is 32 (source NetServerSession maxPeers): clamp, do not crash-loop.
                Log.Err($"[DEDICATED] UG_MAXPLAYERS={_maxPeers} exceeds the 32-peer session cap; using 32");
                _maxPeers = 32;
            }
        }
        // UG_PVE only changes what the browser advertises: source DedicatedServer
        // never touches Combat.PvPEnabled outside the arena match gate.
        _pvp = Env("UG_PVE") != "1";
        _maxPing = int.TryParse(Env("UG_MAXPING"), out int ping) && ping > 0 ? ping : 0;
        _icon = null;
        if (Env("UG_ICON") is { Length: > 0 } icon)
        {
            // Read once at boot: a status responder that touches the disk is one a stranger can make touch the disk.
            try
            {
                byte[] png = File.ReadAllBytes(icon);
                if (png.Length > SDG.NetTransport.Udp.UdpServerTransport.IconMaxBytes)
                    Log.Err($"[DEDICATED] server icon {icon} is {png.Length} B, over the {SDG.NetTransport.Udp.UdpServerTransport.IconMaxBytes} B limit -- not published");
                else { _icon = png; Log.Print($"[DEDICATED] server icon published ({png.Length} B)"); }
            }
            catch (Exception e) { Log.Err($"[DEDICATED] could not read server icon {icon}: {e.Message}"); }
        }
        _announceDeaths = Env("UG_DEATHMSG") != "0";
        _deathMessages = new DeathMessageOptions
        {
            ShowKiller = Env("UG_DEATH_KILLER") == "1",
            ShowWeapon = Env("UG_DEATH_WEAPON") == "1",
            ShowDistance = Env("UG_DEATH_DISTANCE") == "1",
            ShowLocation = Env("UG_DEATH_LOCATION") == "1",
        };
        _netlog = Bool(request, "netlog") || Env("UG_NETLOG") == "1";
    }

    /// <summary>After WorldPersistence configured the per-map file: a dedicated host
    /// must have one, and (having no other entry restore) owes its restore whenever
    /// the file exists at boot. The ban list defaults beside it.</summary>
    void ConfigureDedicatedSave(JsonElement request)
    {
        if (!_dedicated) return;
        if (_savePath == null) throw new ArgumentException("A dedicated server requires savePath");
        if (!request.TryGetProperty("restorePending", out _)) _restorePending = File.Exists(_savePath);
        // BanStore resolves UG_BANS_FILE / UG_SAVE_DIR itself; its last resort is
        // Godot's user://, which this process has not got: keep it beside the save.
        string bans = Text(request, "bansPath");
        if (bans.Length == 0) bans = Env("UG_BANS_FILE") ?? "";
        BanStore.Path = bans.Length > 0 ? Path.GetFullPath(bans) : Path.Combine(Path.GetDirectoryName(_savePath)!, "bans.tsv");
        _wipedSinceBoot = false;
    }

    /// <summary>Source DedicatedServer._Ready, after the shared host services
    /// were installed by StartSession. Runs again after a save load.</summary>
    void StartDedicated()
    {
        // stdout is the JSONL protocol channel; NetLog lands on stderr (journald).
        NetLog.Sink = s => Log.Print(s);
        NetLog.ErrorSink = s => Log.Err(s);
        NetLog.Enabled = _netlog;
        _server.AnnounceDeaths = _announceDeaths;
        _server.DeathMessages = _deathMessages;
        _server.EnableSyncCheck();   // hardening Part C: 1 Hz StateHash block, clients self-check
        if (_udpTransport != null)
        {
            _udpTransport.StatusPvp = _pvp;
            _udpTransport.StatusMaxPing = _maxPing;
            _udpTransport.StatusIcon = _icon;
            if (!string.IsNullOrEmpty(_dedicatedMapRoot))
                _udpTransport.StatusMap = Path.GetFileName(_dedicatedMapRoot.TrimEnd('/', '\\'));
        }
        _server.Session.PeerConnected += peer => Log.Print($"[DEDICATED] player {peer.PlayerId} '{peer.Name}' joined ({_server.Session.Peers.Count} online)");
        _server.Session.PeerDisconnected += (peer, reason) => Log.Print($"[DEDICATED] player {peer.PlayerId} left ({reason})");

        // C2 real spawns: Players.dat points spread by player id at ground + 1.5 m.
        _dedicatedFallbackSpawn = _position;
        var spawns = string.IsNullOrEmpty(_dedicatedMapRoot) ? new() : LevelSpawns.PlayerSpawns(_dedicatedMapRoot);
        if (spawns.Count > 0)
            _server.SpawnProvider = playerId =>
            {
                var pick = spawns[(playerId - 1) % spawns.Count];
                // A point outside the registered terrain has no ground to stand on;
                // fall back to the native host's map spawn rather than y=0.
                return _geometry.TryGroundHeight(pick.x, pick.z, out float ground)
                    ? new Vector3(pick.x, ground + 1.5f, pick.z) : _dedicatedFallbackSpawn;
            };
        else _server.SpawnProvider = _ => _dedicatedFallbackSpawn;

        // Bans load at boot and are rewritten on every change; separate from the
        // world save so `wipe` never unbans anyone (BanStore.cs, linked verbatim).
        BanStore.Load(_server.Transactions.Moderation);
        _server.Transactions.BansChanged = () => BanStore.Save(_server.Transactions.Moderation);
        // `save`/`wipe` and the autosave are WorldPersistence's, installed by StartSession.
        // A wipe promises "restart the server for a fresh one": remember it, so the
        // shutdown save (which the source never had) cannot write the world back.
        var wipe = _server.Transactions.WipeSaveHandler;
        if (wipe != null) _server.Transactions.WipeSaveHandler = () => { _wipedSinceBoot = true; return wipe(); };
        // Source DedicatedServer: world items stream on a 128 m ring around each player.
        _server.WorldItems.Interest = new InterestPolicy { RingRadius = 128f, CellOf = (Vector3 _) => -1 };
        _lastHeartbeatTick = _server.Session.CurrentTick;
    }

    void DedicatedConnectionDropped(ITransportConnection connection, string reason, bool isError)
        => Log.Print($"[DEDICATED] connection dropped ({connection.GetAddressString(true)}): {reason}");

    void TickDedicated()
    {
        // Nothing simulates or accepts before the save is restored (source: the
        // server is constructed after the world build, then loads, then ticks).
        if (!_serving || _stopped) return;
        _server.TickSimulation(); _weapons.Step(); _npcHelis.Step(); _mounted.Step(); _cosmetic.Step();
        _warheads.Prune();
        // A listen host's own client consumes these per-shot presentation facts
        // from PlayerFired/PlayerMeleed; with no client they would only grow.
        _weapons.DiscardPresentationFacts();
        StepWorldServices();
        _appearance.Tick();
        if (_worldChanged) PublishFullSnapshot();
        TickAutosave();   // source order: net.save.autosave, then net.server.replicate LAST
        _server.TickReplication();
        if (_udpTransport != null) _udpTransport.StatusPlayerCount = _server.Session.Peers.Count;
        if (_server.Session.CurrentTick - _lastHeartbeatTick >= DedicatedHeartbeatTicks)
        {
            _lastHeartbeatTick = _server.Session.CurrentTick;
            Log.Print($"[DEDICATED] tick {_server.Session.CurrentTick} | players {_server.Session.Peers.Count} | snapshots full={_server.Composer.Diag.FullSnapshotsComposed} delta={_server.Composer.Diag.DeltaSnapshotsComposed}");
        }
    }

    object DedicatedOperation(string op, JsonElement request)
    {
        if (!_dedicated || _server == null) throw new InvalidOperationException($"{op} requires an initialized dedicated server");
        if (op is "dedicatedStop" or "disconnect") return DedicatedStop();
        if (_stopped) throw new InvalidOperationException("The dedicated server has stopped");
        if (_serving) return new { serving = true, port = (int)_listenPort, save = (string)null };
        string line = LoadDedicatedSave();
        Log.Print("[SAVE] " + line);
        _serving = true; _lastHeartbeatTick = _server.Session.CurrentTick;
        Log.Print($"[DEDICATED] world up (terrain={(_geometry.HasTerrain ? "real map" : "fallback plane")}); listening on udp {_listenPort}");
        return new { serving = true, port = (int)_listenPort, save = line };
    }

    /// <summary>WorldSaveDriver.LoadIntoWorld through the shared `load` operation
    /// (which restores the season start and loot seed first). A save that does not
    /// parse is reported and moved aside to <c>.refused-UNIX</c> -- the source left it
    /// in place for its autosave to overwrite -- and the world starts fresh. A failure
    /// after the restore began changing the world fails the boot instead (the file is
    /// moved aside the same way, so systemd's restart comes up fresh).</summary>
    string LoadDedicatedSave()
    {
        if (!_restorePending) return "no save at " + _savePath + " -- fresh world";
        string error;
        try
        {
            if (WorldSave.TryParse(File.ReadAllText(_savePath), _map, out _, out error)) error = null;
        }
        catch (Exception e) { error = e.Message; }
        if (error != null)
        {
            string aside = MoveRefusedSave();
            Log.Err($"[save] refusing {_savePath}: {error}. Starting a FRESH world; the file is kept as {aside}.");
            _restorePending = false;
            return "save refused (" + error + ") -- fresh world, file kept as " + aside;
        }
        try
        {
            var result = JsonSerializer.SerializeToElement(Handle("load", JsonSerializer.SerializeToElement(new { path = _savePath })));
            return result.GetProperty("message").GetString();
        }
        catch (Exception e)
        {
            string aside = MoveRefusedSave();
            Log.Err($"[save] restoring {_savePath} failed part-way: {e.Message}. Moved to {aside}; refusing to serve a half-restored world.");
            throw new InvalidOperationException($"save restore failed part-way ({e.Message}); file kept as {aside}", e);
        }
    }

    string MoveRefusedSave()
    {
        string aside = $"{_savePath}.refused-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        try { File.Move(_savePath, aside); return aside; }
        catch (Exception) { return _savePath; }
    }

    /// <summary>Graceful stop (SIGTERM through the native parent, or the
    /// parent's pipe closing): write the world, then release the socket.</summary>
    object DedicatedStop()
    {
        if (_stopped) return new { saved = false, stopped = true, path = _savePath };
        // Beyond the source (Godot was SIGKILLed after ignoring SIGTERM): the stop
        // writes the world once more, unless its restore is still owed.
        // ...and never over a wipe the admin was told needs a restart (an autosave
        // after the wipe recreates the file, which the source would also keep).
        bool wiped = _wipedSinceBoot && !File.Exists(_savePath);
        bool saved = _serving && !_restorePending && !wiped && SaveNow();
        _stopped = true;
        if (_serving) Log.Print(saved ? $"[SAVE] shutdown save written to {_savePath}"
            : wiped ? $"[SAVE] wiped -- no shutdown save; the next boot is a fresh world"
            : $"[SAVE] shutdown save FAILED -- {_savePath}");
        Log.Print("[DEDICATED] shutting down");
        Dispose();
        return new { saved, stopped = true, path = _savePath };
    }

    /// <summary>Program.cs: the native parent's pipe closed without a stop.</summary>
    public void EndOfInput()
    {
        if (DedicatedActive) DedicatedStop();
    }

    static readonly string[] DedicatedOperations = [
        "init", "shutdown", "tick", "snapshot", "catalog", "diagnostics", "npcCatalog", "profileRules",
        "terrain", "worldGeometry", "worldDynamicGeometry", "worldRay", "worldSpawns", "worldSpawn", "worldServices",
        "mapServices", "worldLoot", "worldVehicles", "worldVehicleState", "worldVehicleExit", "worldVehicleDamage", "worldVehicleLighting",
        "worldVehicleGlazing", "worldNpcHelis", "worldMountedInput", "worldWildlife", "worldZombies", "worldZombieState",
        "worldZombieNoise", "worldCosmeticGeometry", "save", "load", "dedicatedServe", "dedicatedStop",
    ];

    void CheckDedicatedOperation(string op)
    {
        if (!_dedicated || System.Array.IndexOf(DedicatedOperations, op) >= 0) return;
        throw new InvalidOperationException($"{op} needs a local player; a dedicated server has none");
    }

    object DedicatedNetworkState() => new {
        mode = "dedicated", ownsWorld = _server != null, host = (string)null, port = (int)_listenPort,
        serverName = _serverName, holiday = _worldHoliday, maxPlayers = _maxPeers,
        state = _stopped ? "Stopped" : _serving ? "Serving" : "Loading",
        reason = "", rejectReason = "", rejectText = (string)null, retryable = false, serverProtocol = 0,
        contentHash = NetContent.Hash.ToString("X16"), peers = _server?.Session.Peers.Count,
        protocol = NetProtocol.Version, awaitingSpawn = false, wireGaps = System.Array.Empty<string>(),
        serving = _serving, savePath = _savePath, allowCheats = _allowCheats,
        bans = _server?.Transactions.Moderation.Count ?? 0,
        autosaveInSeconds = Math.Max(0, AutosaveSeconds - _sinceSave),
    };

    /// <summary>The host snapshot without an owner: the same world-list rows a
    /// listen host's client produces, read from the server registries. Owner
    /// sections (player, inventory, recipes...) are absent and playerId is the
    /// existing "no local player" 0. Presentation-only lists (dropped items,
    /// projectiles, cosmetic bullets, crops) are omitted.</summary>
    object DedicatedState()
    {
        long tick = _server.Session.CurrentTick;
        var interactables = _server.Interactables;
        return new {
            tick, origin = Origin, connected = !_stopped, playerId = 0,
            network = NetworkState(), persistence = PersistenceState(), players = PlayerReplicas(_server.Players, _server.CombatState, _server.Profiles),
            worldClock = new { day = _environment?.DayAt(_server.Clock, tick), timeOfDay = _server.Clock.TimeOfDayAt(tick), dayLengthSeconds = _server.Clock.DayLengthSeconds },
            weather = _environment?.State(),
            npcHelis = _npcHelis?.Snapshots() ?? [],
            vehicleMounts = VehicleMountRows(_server.VehicleMounts),
            animals = _wildlife?.Snapshots(_server.Animals),
            deployables = DeployableRows(_server.Deployables),
            vehicles = VehicleRows(_server.Vehicles),
            zombies = _zombies?.Snapshots(_server.Zombies), zombieField = _zombies?.Summary(),
            interactables = new {
                doors = interactables.Doors.Select(d => new { netId = d.NetId, open = d.State.IsOpen, locked = d.State.Locked }),
                objectDoors = interactables.ObjectDoors.Select(d => new { netId = d.Key, open = d.Value, reverse = interactables.IsObjectDoorReversed(d.Key) }),
                beds = interactables.BedOwners.Select(d => new { netId = d.Key, owner = (ushort)d.Value }),
                seats = interactables.SeatOccupants.Select(d => new { netId = d.Key, occupant = d.Value }) },
            resources = new { count = _server.Resources.Count, dead = Enumerable.Range(0, _server.Resources.Count).Where(i => !_server.Resources.IsAlive(i)).ToArray() },
            destructibles = new { count = _server.Destructibles.Count, dead = Enumerable.Range(0, _server.Destructibles.Count).Where(i => !_server.Destructibles.IsAlive(i)).ToArray() },
            containers = _server.Containers.All.Select(e => new { netId = e.NetIdValue, kindId = e.KindId, position = Array(e.Pos), yaw = e.YawDegrees, width = e.Width, height = e.Height, display = e.Display, doorsOpen = e.DoorsOpen, cookerOn = e.CookerOn }).ToArray(),
            events = _events.ToArray(),
        };
    }
}
