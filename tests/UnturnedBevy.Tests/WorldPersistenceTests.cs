using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

/// <summary>Original game/WorldSaveDriver.cs lifecycle on the bridge host: 60 s simulation-clock
/// autosave, load into the reopened world, offline-player carry-over, console save/wipe, and the
/// source fact that leaving writes nothing.</summary>
[TestFixture, NonParallelizable]
public sealed class WorldPersistenceTests
{
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };
    string _dir;
    readonly List<BridgeHost> _hosts = new();
    readonly List<NetWorldClient> _peers = new();

    static string Root()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException("game/content");
    }

    [SetUp] public void SetUp() { _dir = Path.Combine(Path.GetTempPath(), $"bevy-persistence-{Guid.NewGuid():N}"); }

    [TearDown]
    public void TearDown()
    {
        foreach (var peer in _peers) peer.Disconnect();
        _peers.Clear();
        foreach (var host in _hosts) host.Dispose();
        _hosts.Clear();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    string SavePath => Path.Combine(_dir, "pei.json");
    static JsonElement Call(BridgeHost host, string op, object args = null) =>
        JsonSerializer.SerializeToElement(host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), JsonOptions);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), JsonOptions);
    static T Private<T>(BridgeHost host, string name) =>
        (T)typeof(BridgeHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
    static void SetPrivate(BridgeHost host, string name, object value) =>
        typeof(BridgeHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);

    BridgeHost Host(object init)
    {
        var host = new BridgeHost(); _hosts.Add(host);
        Call(host, "init", init);
        return host;
    }
    BridgeHost Local(string savePath = null, bool starterKit = true) => Host(new {
        contentRoot = Root(), mapId = "PEI", name = "Survivor", spawn = new[] { 10f, 0f, 20f }, starterKit, savePath });

    void Pump(BridgeHost host, int count, object input = null)
    {
        for (int n = 0; n < count; n++)
        {
            foreach (var peer in _peers) peer.Tick();
            if (input == null) Call(host, "tick"); else Call(host, "tick", new { input });
            if (_peers.Count > 0) Thread.Sleep(1);
        }
    }

    static WorldSave ReadSave(string path)
    {
        Assert.That(WorldSave.TryParse(File.ReadAllText(path), "PEI", out var save, out var error), Is.True, error);
        return save;
    }

    [Test]
    public void AutosaveFiresOnTheSourceSixtySecondSimulationClockAndEstablishesTheLootSeedOnce()
    {
        var host = Local(SavePath);
        SetPrivate(host, "_sinceSave", 0.0);
        Pump(host, 2999);
        Assert.That(File.Exists(SavePath), Is.False, "WorldSaveDriver.AutosaveSeconds is 60 simulated seconds (3000 ticks)");
        Pump(host, 1);
        Assert.That(File.Exists(SavePath), Is.True, "the 3000th 0.02 s step reaches 60 s and writes");
        var persistence = State(host).GetProperty("persistence");
        Assert.That(persistence.GetProperty("autosaves").GetInt32(), Is.EqualTo(1));
        Assert.That(persistence.GetProperty("path").GetString(), Is.EqualTo(SavePath));
        Assert.That(persistence.GetProperty("sinceSave").GetDouble(), Is.Zero);
        var first = ReadSave(SavePath);
        Assert.That(first.LootSeedValue, Is.Not.Zero, "the first save establishes the world's loot seed");
        Assert.That(first.Players.Single().Name, Is.EqualTo("Survivor"));
        Assert.That(File.Exists(SavePath + ".tmp"), Is.False, "temp-and-swap leaves no partial file");
        Pump(host, 3000);
        Assert.That(State(host).GetProperty("persistence").GetProperty("autosaves").GetInt32(), Is.EqualTo(2));
        Assert.That(ReadSave(SavePath).LootSeedValue, Is.EqualTo(first.LootSeedValue), "EnsureEstablished is idempotent");
    }

    [Test]
    public void ReopenRestoresTheLastAutosaveAndLeavingWritesNothing()
    {
        var first = Local(SavePath);
        var server = Private<NetWorldServer>(first, "_server");
        // Change something the player would notice: walk, eat into the bag, drop an item.
        Call(first, "action", new { name = "console", args = new { text = "teleport 42 3 -17" } });
        Pump(first, 20);
        Assert.That(State(first).GetProperty("player").GetProperty("position")[0].GetSingle(), Is.EqualTo(42f).Within(0.05f));
        ushort owner = Private<NetWorldClient>(first, "_client").PlayerId;
        Assert.That(server.Inventories.TryGet(owner, out var bag), Is.True);
        int before = bag.Inventory.getItemCount(13);
        Call(first, "action", new { name = "console", args = new { text = "give 13" } });
        Pump(first, 5);
        int carried = bag.Inventory.getItemCount(13);
        Assert.That(carried, Is.EqualTo(before + 1));
        Call(first, "worldSpawn", new { items = new[] { new { itemId = 13, amount = 1, position = new[] { 44f, 3f, -17f } } } });
        int dropped = server.WorldItems.All.Count();
        WorldTemperature.StartDayOfYear = 40;   // a saved season the reopened world must adopt
        SetPrivate(first, "_sinceSave", AutosaveTickRemainder());
        Pump(first, 1);
        Assert.That(File.Exists(SavePath), Is.True);
        byte[] autosaved = File.ReadAllBytes(SavePath);
        ulong seed = LootSeed.World;

        // Later changes, then leave: the source writes nothing on exit/quit.
        Call(first, "action", new { name = "console", args = new { text = "teleport 90 3 90" } });
        Pump(first, 20);
        first.Dispose();
        Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(autosaved), "MpLoopback/DedicatedServer _ExitTree only tear down");

        // Relaunch: a fresh world, then the entry restore the native client performs once its map is registered.
        var second = Local(SavePath, starterKit: false);
        Assert.That(WorldTemperature.StartDayOfYear, Is.EqualTo(WorldTemperature.DefaultStartDayOfYear), "init is a fresh world");
        Assert.That(LootSeed.Established, Is.False);
        var loaded = Call(second, "load");
        Assert.That(loaded.GetProperty("message").GetString(), Does.StartWith($"loaded {SavePath}: day "));
        var state = State(second).GetProperty("player");
        Assert.That(state.GetProperty("position")[0].GetSingle(), Is.EqualTo(42f).Within(0.05f));
        Assert.That(state.GetProperty("position")[2].GetSingle(), Is.EqualTo(-17f).Within(0.05f));
        var reopened = Private<NetWorldServer>(second, "_server");
        ushort again = Private<NetWorldClient>(second, "_client").PlayerId;
        Assert.That(reopened.Inventories.TryGet(again, out var restored), Is.True);
        Assert.That(restored.Inventory.getItemCount(13), Is.EqualTo(carried), "the reopened bag is the autosaved one");
        Assert.That(reopened.WorldItems.All.Count(), Is.EqualTo(dropped));
        Assert.That(WorldTemperature.StartDayOfYear, Is.EqualTo(40), "LoadIntoWorld restores the season start");
        Assert.That(LootSeed.World, Is.EqualTo(seed), "and adopts the world's loot seed");
        Assert.That(State(second).GetProperty("persistence").GetProperty("sinceSave").GetDouble(), Is.LessThan(1), "a load restarts the autosave clock");
    }

    static double AutosaveTickRemainder() => BridgeHost.AutosaveSeconds - SimClock.FixedDelta / 2;

    [Test]
    public void AFreshWorldAwaitingItsEntryRestoreNeverAutosavesOverTheFile()
    {
        var first = Local(SavePath);
        Call(first, "action", new { name = "console", args = new { text = "teleport 42 3 -17" } });
        Pump(first, 20);
        Call(first, "save");
        first.Dispose();
        byte[] saved = File.ReadAllBytes(SavePath);

        var second = Host(new { contentRoot = Root(), mapId = "PEI", name = "Survivor", spawn = new[] { 10f, 0f, 20f }, savePath = SavePath, restorePending = true });
        Assert.That(State(second).GetProperty("persistence").GetProperty("restorePending").GetBoolean(), Is.True);
        Pump(second, 3100);
        Assert.Throws<InvalidOperationException>(() => Call(second, "save"));
        Call(second, "action", new { name = "console", args = new { text = "save" } });
        Pump(second, 4);
        Assert.That(ConsoleTexts(second), Has.Some.Contains("has not been restored into this world yet"));
        Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(saved), "the placeholder world never overwrites the save it is waiting for");
        Call(second, "load");
        Assert.That(State(second).GetProperty("player").GetProperty("position")[0].GetSingle(), Is.EqualTo(42f).Within(0.05f));
        Pump(second, 3000);
        Assert.That(State(second).GetProperty("persistence").GetProperty("autosaves").GetInt32(), Is.EqualTo(1), "autosave resumes after the restore");
    }

    [Test]
    public void ConsoleSaveAndWipeRouteToTheConfiguredHostFile()
    {
        var unconfigured = Local();
        Call(unconfigured, "action", new { name = "console", args = new { text = "save" } });
        Pump(unconfigured, 4);
        Assert.That(ConsoleTexts(unconfigured), Has.Some.EqualTo("no save is configured on this server"));
        Assert.That(State(unconfigured).GetProperty("persistence").ValueKind, Is.EqualTo(JsonValueKind.Null));

        var host = Local(SavePath);
        Call(host, "action", new { name = "console", args = new { text = "save" } });
        Pump(host, 4);
        Assert.That(File.Exists(SavePath), Is.True);
        Assert.That(ConsoleTexts(host), Has.Some.EqualTo($"saved to {SavePath}"));
        Call(host, "action", new { name = "console", args = new { text = "wipe" } });
        Pump(host, 4);
        Assert.That(File.Exists(SavePath), Is.False);
        Assert.That(Private<NetWorldServer>(host, "_server").PendingSave, Is.Null, "wipe forgets the carry-over");
        Assert.That(ConsoleTexts(host), Has.Some.StartsWith("save deleted (").And.Contains("The running world is UNCHANGED"));
    }

    static IEnumerable<string> ConsoleTexts(BridgeHost host) => State(host).GetProperty("events").EnumerateArray()
        .Where(e => e.GetProperty("data").TryGetProperty("text", out _))
        .Select(e => e.GetProperty("data").GetProperty("text").GetString());

    [Test]
    public void OfflinePlayersRideAlongInEveryLaterSaveUntilWiped()
    {
        ushort port;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
        }
        var host = Host(new { contentRoot = Root(), mapId = "PEI", listenPort = port, spawn = new[] { 0, 0, 0 }, savePath = SavePath, allowCheats = true });
        var visitor = new NetWorldClient(new UdpClientTransport("127.0.0.1", port), "Visitor", contentHash: NetContent.Hash);
        DeployableNetSchema.RegisterAll(visitor.Deployables.Schema);
        _peers.Add(visitor); visitor.Connect();
        Pump(host, 20);
        Assert.That(visitor.State, Is.EqualTo(NetSessionState.Connected));
        for (int n = 0; n < 40; n++) { visitor.SendMoveInput(0, 1, 0); Pump(host, 1); }
        Pump(host, 10);
        SetPrivate(host, "_sinceSave", AutosaveTickRemainder()); Pump(host, 1);
        var online = ReadSave(SavePath).Players.Single(p => p.Name == "Visitor");
        Assert.That(Math.Abs(online.Z) + Math.Abs(online.X), Is.GreaterThan(1f), "the online visitor is captured where they walked");

        visitor.Disconnect(); _peers.Remove(visitor);
        Pump(host, 20);
        Assert.That(State(host).GetProperty("players").GetArrayLength(), Is.EqualTo(1));
        SetPrivate(host, "_sinceSave", AutosaveTickRemainder()); Pump(host, 1);
        var carried = ReadSave(SavePath).Players.Single(p => p.Name == "Visitor");
        Assert.That((carried.X, carried.Y, carried.Z), Is.EqualTo((online.X, online.Y, online.Z)), "an offline player rides along untouched");

        Call(host, "action", new { name = "console", args = new { text = "wipe" } });
        Pump(host, 4);
        SetPrivate(host, "_sinceSave", AutosaveTickRemainder()); Pump(host, 1);
        Assert.That(ReadSave(SavePath).Players.Any(p => p.Name == "Visitor"), Is.False, "wipe drops the carry-over chain");
    }

    [Test]
    public void RemoteClientsHaveNoPersistenceAndAFailedWriteNeverThrowsIntoTheTick()
    {
        // A path whose parent is a file cannot be created: the autosave reports, the tick continues.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var host = Local(Path.Combine(blocker, "pei.json"));
        SetPrivate(host, "_sinceSave", AutosaveTickRemainder());
        Assert.DoesNotThrow(() => Pump(host, 2));
        var persistence = State(host).GetProperty("persistence");
        Assert.That(persistence.GetProperty("autosaves").GetInt32(), Is.Zero);
        Assert.That(persistence.GetProperty("lastResult").GetString(), Does.StartWith("write failed:"));
    }
}
