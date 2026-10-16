using System.Text.Json;
using System.Text.Json.Nodes;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Original <c>game/WorldSaveDriver.cs</c> lifecycle on the host authority: one configured
/// world file per map, a 60-second autosave on the 50 Hz simulation clock, the console's
/// <c>save</c>/<c>wipe</c> verbs, the first save establishing the loot seed, and a load restoring the
/// season start and loot seed before the world restarts. The FORMAT and the state walk stay in core
/// <c>WorldSave</c>; this only owns the path and the timing, as the source driver does.
///
/// Source facts kept on purpose: leaving or quitting does NOT write (MpLoopback/DedicatedServer
/// _ExitTree only tear down), so the newest autosave or explicit save is what reopens; a remote
/// client has no persistence at all.</summary>
public sealed partial class BridgeHost
{
    /// <summary>WorldSaveDriver.AutosaveSeconds.</summary>
    public const double AutosaveSeconds = 60.0;

    string _savePath;
    /// <summary>The native host restores the saved world once the map is registered; until that
    /// load runs, the fresh placeholder world must never autosave over the file (the source loads
    /// before its first simulation step, so it has no such window).</summary>
    bool _restorePending;
    double _sinceSave;
    int _autosaves;
    string _lastSaveResult;

    void ConfigurePersistence(JsonElement request)
    {
        string path = Text(request, "savePath");
        _savePath = IsRemote || path.Length == 0 ? null : Path.GetFullPath(path);
        _restorePending = _savePath != null && Bool(request, "restorePending");
        _sinceSave = 0; _autosaves = 0; _lastSaveResult = null;
        // Each init is a new world: start from the original process defaults. Only a
        // loaded save sets these (WorldSaveDriver.LoadIntoWorld); a fresh world rolls
        // its own loot seed at its first save.
        WorldTemperature.StartDayOfYear = WorldTemperature.DefaultStartDayOfYear;
        LootSeed.Clear();
    }

    /// <summary>Installed with the host server, like MpLoopback/DedicatedServer install
    /// WipeSaveHandler/SaveNowHandler; null without a configured path, so the verbs answer
    /// "no save is configured on this server".</summary>
    void InstallPersistenceHandlers()
    {
        if (_savePath == null) return;
        _server.Transactions.SaveNowHandler = SaveNowReport;
        _server.Transactions.WipeSaveHandler = WipeSave;
    }

    /// <summary>WorldSaveDriver.Tick: one fixed step of the 50 Hz simulation. Registered in the
    /// source immediately before net.server.replicate, after every mutation.</summary>
    void TickAutosave()
    {
        if (_savePath == null || _restorePending) return;
        _sinceSave += SimClock.FixedDelta;
        if (_sinceSave < AutosaveSeconds) return;
        _sinceSave = 0.0;
        if (SaveNow()) _autosaves++;
    }

    /// <summary>WorldSaveDriver.SaveNow: never throws into a game tick.</summary>
    bool SaveNow()
    {
        try
        {
            WriteWorldSave(_savePath);
            _lastSaveResult = "saved to " + _savePath;
            return true;
        }
        catch (Exception ex)
        {
            _lastSaveResult = $"write failed: {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine("[save] " + _lastSaveResult);
            return false;
        }
    }

    string SaveNowReport()
    {
        // Native-only window (see _restorePending): nothing may write the placeholder world over the save.
        if (_restorePending) return $"save refused -- {_savePath} has not been restored into this world yet";
        return SaveNow() ? $"saved to {_savePath}" : $"save FAILED (see the log) -- {_savePath}";
    }

    /// <summary>WorldSaveDriver.Wipe: forget the carry-over and delete the file; the LIVE world is
    /// deliberately left standing and the reply says so.</summary>
    string WipeSave()
    {
        _server.PendingSave = null;
        _sinceSave = 0.0;
        if (!File.Exists(_savePath)) return "no save file to delete; the running world is unchanged";
        try { File.Delete(_savePath); }
        catch (Exception) { return "could not delete " + _savePath; }
        return "save deleted (" + _savePath + "). The running world is UNCHANGED -- restart the server for a fresh one.";
    }

    string SavePathFor(JsonElement request)
    {
        string path = Text(request, "path");
        if (path.Length > 0) return Path.GetFullPath(path);
        return _savePath ?? throw new InvalidOperationException("No world save path is configured for this session");
    }

    /// <summary>The `save` op target: never the configured file while its restore is still owed.</summary>
    string WritablePathFor(JsonElement request)
    {
        string path = SavePathFor(request);
        if (_restorePending && path == _savePath)
            throw new InvalidOperationException($"{path} has not been restored into this world yet");
        return path;
    }

    WorldSave WriteWorldSave(string path)
    {
        // FIRST SAVE ESTABLISHES THE WORLD'S LOOT SEED (WorldSaveDriver.SaveNow). Idempotent: every
        // later save finds it set, so a world's loot never moves under it.
        LootSeed.EnsureEstablished(() => unchecked((ulong)Random.Shared.NextInt64(long.MinValue, long.MaxValue)));
        var save = WorldSave.Capture(_server, _map, _environment.DayAt(_server.Clock, _server.Session.CurrentTick),
            _server.Clock.TimeOfDayAt(_server.Session.CurrentTick), _server.Clock.DayLengthSeconds, _server.PendingSave);
        _vehicles.SaveFuel(save);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saveJson = JsonNode.Parse(save.ToJson());
        saveJson["BevyBridge"] = JsonSerializer.SerializeToNode(new BridgeSave(_seed, _loot?.Capture(), _serviceSpecs.ToArray(), _services.Harvesting.Capture(), _services.CapturePropDoors(), _bodies.Capture(), _vehicles.CaptureParts(), _originX, _originZ, CaptureProjectiles(), _wildlife.Capture(), _zombies.Capture(), _weapons.Capture(), _cosmetic.CaptureWater(), _warheads.Capture(), _worldHoliday, _mounted.Capture(), _npcHelis.Capture(), _vehicleLights.Capture(), _vehicleMaintenance.Capture(), _vehicleDoors.Capture(), _vehicleHitches.Capture()));
        // Temp file and swap: the file is either the old save or the new one, never half of either.
        File.WriteAllText(path + ".tmp", saveJson.ToJsonString()); File.Move(path + ".tmp", path, true);
        // The written state is the carry-over source for the next capture, so an offline player
        // stays in the chain (WorldSaveDriver._lastLoaded).
        _server.PendingSave = save;
        return save;
    }

    /// <summary>WorldSaveDriver.LoadIntoWorld's static restores, applied before the world restarts so
    /// the season is read from the save even by the weather constructed with it.</summary>
    void AdoptSavedWorldStatics(WorldSave save)
    {
        WorldTemperature.StartDayOfYear = save.StartDayOfYear;
        LootSeed.Set(save.LootSeedValue);   // adopt the world's established seed, 0 included
        _sinceSave = 0.0;
    }

    /// <summary>The source's LoadIntoWorld log line, returned to the caller.</summary>
    static string LoadedLine(string path, WorldSave save) =>
        $"loaded {path}: day {save.Day}, {save.Players.Count} player(s), {save.Deployables.Count} deployable(s), {save.WorldItems.Count} dropped item(s)";

    object PersistenceState() => _savePath == null ? null : new {
        path = _savePath, restorePending = _restorePending, sinceSave = _sinceSave, autosaveSeconds = AutosaveSeconds,
        autosaves = _autosaves, lastResult = _lastSaveResult };
}
