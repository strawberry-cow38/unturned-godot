using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using SDG.NetTransport.Udp;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

/// <summary>The hybrid `--dedicated` host's managed half: a bridge session that owns the
/// authoritative world over real UDP with no local player (source DedicatedServer.cs),
/// its save lifecycle (WorldSaveDriver) and the console moderation surface (BanStore).</summary>
[TestFixture, NonParallelizable]
public sealed class DedicatedServerTests
{
    BridgeHost _host;
    readonly List<NetWorldClient> _peers = new();
    ushort _port;
    string _dir;
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };

    static string ContentRoot()
    {
        for (var at = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); at != null; at = at.Parent)
            if (Directory.Exists(Path.Combine(at.FullName, "game", "content")))
                return Path.Combine(at.FullName, "game", "content");
        throw new DirectoryNotFoundException("Cannot find repository game/content");
    }

    string SavePath => Path.Combine(_dir, "PEI.json");
    string BansPath => Path.Combine(_dir, "bans.tsv");

    [SetUp]
    public void SetUp()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
        _dir = Path.Combine(Path.GetTempPath(), "ug-dedicated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _host = new BridgeHost();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var peer in _peers) peer.Disconnect();
        _peers.Clear();
        _host.Dispose();
        Environment.SetEnvironmentVariable("UG_DEDICATED_NOCHEATS", null);
        Environment.SetEnvironmentVariable("UG_MAXPLAYERS", null);
        Environment.SetEnvironmentVariable("UG_NAME", null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    object Call(string op, object request = null) => _host.Handle(op, JsonSerializer.SerializeToElement(request ?? new { }));
    JsonElement State() => JsonSerializer.SerializeToElement(_host.State(), JsonOptions);
    JsonElement Network() => State().GetProperty("network");

    void Boot(bool serve = true, object extra = null)
    {
        var request = new Dictionary<string, object> {
            ["contentRoot"] = ContentRoot(), ["dedicated"] = true, ["listenPort"] = _port, ["spawn"] = new[] { 0, 1, 0 },
            ["mapId"] = "PEI", ["savePath"] = SavePath, ["bansPath"] = BansPath,
        };
        if (extra != null)
            foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject()) request[p.Name] = p.Value;
        Call("init", request);
        if (serve) Call("dedicatedServe");
    }

    void Restart()
    {
        foreach (var peer in _peers) peer.Disconnect();
        _peers.Clear();
        Call("dedicatedStop");
        _host.Dispose();
        _host = new BridgeHost();
        Boot();
    }

    NetWorldClient Peer(string name)
    {
        var client = new NetWorldClient(new UdpClientTransport("127.0.0.1", _port), name, contentHash: NetContent.Hash);
        DeployableNetSchema.RegisterAll(client.Deployables.Schema);
        _peers.Add(client);
        client.Connect();
        return client;
    }

    void Pump(int count = 12)
    {
        for (int n = 0; n < count; n++)
        {
            foreach (var peer in _peers) peer.Tick();
            Call("tick");
            Thread.Sleep(1);   // let the OS deliver loopback datagrams
        }
    }

    string Run(NetWorldClient peer, string text)
    {
        string reply = null;
        void Take(ConsoleResultEvent e) => reply = e.Text;
        peer.ConsoleResult += Take;
        peer.SendConsole(text);
        for (int n = 0; n < 40 && reply == null; n++) Pump(1);
        peer.ConsoleResult -= Take;
        return reply;
    }

    [Test]
    public void HostHasNoLocalPlayerAndAcceptsNobodyBeforeTheSaveIsRestored()
    {
        Boot(serve: false);
        Assert.That(Network().GetProperty("mode").GetString(), Is.EqualTo("dedicated"));
        Assert.That(Network().GetProperty("state").GetString(), Is.EqualTo("Loading"));
        Assert.That(State().GetProperty("playerId").GetInt32(), Is.Zero);
        Assert.That(State().GetProperty("players").GetArrayLength(), Is.Zero, "no player slot or body for the host");
        var early = Peer("early");
        Pump(20);
        Assert.That(early.State, Is.Not.EqualTo(NetSessionState.Connected), "nothing is accepted before dedicatedServe");
        Call("dedicatedServe");
        Pump(20);
        Assert.That(early.State, Is.EqualTo(NetSessionState.Connected));
        Assert.That(Network().GetProperty("peers").GetInt32(), Is.EqualTo(1));
        var players = State().GetProperty("players");
        Assert.That(players.GetArrayLength(), Is.EqualTo(1));
        Assert.That(players[0].GetProperty("playerId").GetUInt16(), Is.EqualTo(early.PlayerId));
        Assert.Throws<InvalidOperationException>(() => Call("simulate", new { input = new { moveY = 1 } }));
        Assert.Throws<InvalidOperationException>(() => Call("action", new { name = "console", args = new { text = "give 13" } }));
        Assert.Throws<InvalidOperationException>(() => Call("inventoryView"));
        Assert.Throws<InvalidOperationException>(() => Call("rebase", new { expectedEpoch = 0, shift = new[] { 256, 0, 0 } }));
    }

    [Test]
    public void BrowserStatusCountsOnlyRemotePeersAndSyncCheckRuns()
    {
        Environment.SetEnvironmentVariable("UG_MAXPLAYERS", "6");
        Environment.SetEnvironmentVariable("UG_NAME", "Claw test");
        Boot(extra: new { mapRoot = "/nonexistent/Maps/PEI" });
        var peer = Peer("one");
        Pump(80);   // > 1 s of ticks: at least one StateHash block
        Assert.That(peer.State, Is.EqualTo(NetSessionState.Connected));
        Assert.That(peer.Session.ServerName, Is.EqualTo("Claw test"));
        Assert.That(peer.Session.ServerMaxPlayers, Is.EqualTo(6));
        Assert.That(peer.Applier.Diag.SyncChecksPassed, Is.GreaterThan(0), "EnableSyncCheck is on, as on the source dedicated server");
        Assert.That(peer.Applier.Diag.SyncChecksFailed, Is.Zero);
        using var query = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        query.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var request = new byte[512];
        "UGSQ"u8.CopyTo(request);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 0x0BADF00D);
        query.SendTo(request, new IPEndPoint(IPAddress.Loopback, _port));
        Pump(2);
        Assert.That(query.Poll(1_000_000, SelectMode.SelectRead), Is.True);
        var reply = new byte[512];
        query.Receive(reply);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(8)), Is.EqualTo(1), "the host itself is not a player");
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(10)), Is.EqualTo(6));
        Assert.That(System.Text.Encoding.UTF8.GetString(reply, 25, reply[24]), Is.EqualTo("PEI"));
    }

    [Test]
    public void ConsoleBanIsPersistedToTheTsvAndRefusesTheHandleAfterRestart()
    {
        Boot();
        var griefer = Peer("griefer");
        var admin = Peer("admin");
        Pump();
        Assert.That(griefer.State, Is.EqualTo(NetSessionState.Connected));
        string reply = Run(admin, "ban griefer 2h stop building there");
        Assert.That(reply, Does.Contain("griefer was banned"));
        Pump();
        Assert.That(griefer.State, Is.EqualTo(NetSessionState.Disconnected));
        // BanStore format: ipv4 TAB expiresUnix TAB kind TAB name TAB reason TAB steamid.
        var row = File.ReadAllLines(BansPath).Single(l => !l.StartsWith('#')).Split('\t');
        Assert.That(row[0], Is.EqualTo("2130706433"), "127.0.0.1");
        Assert.That(long.Parse(row[1]), Is.GreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7000));
        Assert.That(row[2], Is.EqualTo("Ban"));
        Assert.That(row[3], Is.EqualTo("griefer"));
        Assert.That(row[4], Is.EqualTo("stop building there"));
        Assert.That(Run(admin, "bans"), Does.Contain("griefer").And.Contain("stop building there"));

        Restart();
        Assert.That(Network().GetProperty("bans").GetInt32(), Is.EqualTo(1), "loaded from the TSV at boot");
        var again = Peer("griefer");
        Pump(20);
        Assert.That(again.State, Is.EqualTo(NetSessionState.Disconnected));
        Assert.That(again.Session.RejectReason, Is.EqualTo(NetRejectReason.Banned));
        Assert.That(Network().GetProperty("peers").GetInt32(), Is.Zero);
    }

    [Test]
    public void UnbanRewritesTheListAndSurvivesRestart()
    {
        Boot();
        var griefer = Peer("griefer");
        var admin = Peer("admin");
        Pump();
        Run(admin, "kick griefer 10m flood");
        Assert.That(File.ReadAllLines(BansPath).Count(l => !l.StartsWith('#')), Is.EqualTo(1), "a timed kick is recorded");
        Assert.That(Run(admin, "unban 127.0.0.1"), Does.Contain("lifted 1"));
        Assert.That(File.ReadAllLines(BansPath).Count(l => !l.StartsWith('#')), Is.Zero);
        Restart();
        var back = Peer("griefer");
        Pump(20);
        Assert.That(back.State, Is.EqualTo(NetSessionState.Connected));
    }

    [Test]
    public void ConsoleSaveAndRestartRestoreWorldItemsAndThePlayersBag()
    {
        Boot();
        Call("worldSpawn", new { items = new[] { new { itemId = 13, amount = 1, position = new[] { 3f, 1f, 3f } } } });
        var peer = Peer("keeper");
        Pump();
        Assert.That(Run(peer, "give 15"), Does.Contain("gave"));
        Pump();
        Assert.That(peer.Inventories.TryGet(peer.PlayerId, out var bag) && bag.Inventory.getItemCount(15) == 1, Is.True);
        Assert.That(Run(peer, "save"), Is.EqualTo("saved to " + SavePath));
        Assert.That(File.Exists(SavePath), Is.True);

        Restart();
        Assert.That(Network().GetProperty("serving").GetBoolean(), Is.True);
        var back = Peer("keeper");
        Pump(30);
        Assert.That(back.State, Is.EqualTo(NetSessionState.Connected));
        Assert.That(back.WorldItems.All.Any(e => e.ItemId == 13), Is.True, "dropped item restored from the save");
        Assert.That(back.Inventories.TryGet(back.PlayerId, out var restored) && restored.Inventory.getItemCount(15) == 1,
            Is.True, "the returning player's bag restores by name");
    }

    [Test]
    public void AutosaveWritesEverySixtySecondsOfSimulation()
    {
        Boot();
        int ticks = (int)(BridgeHost.AutosaveSeconds / SimClock.FixedDelta);
        for (int n = 0; n < ticks - 2; n++) Call("tick");
        Assert.That(File.Exists(SavePath), Is.False);
        for (int n = 0; n < 4; n++) Call("tick");
        Assert.That(File.Exists(SavePath), Is.True, "WorldSaveDriver autosave cadence");
        Assert.That(WorldSave.TryParse(File.ReadAllText(SavePath), "PEI", out _, out string error), Is.True, error);
    }

    [Test]
    public void StopWritesTheFinalSaveAndReleasesThePort()
    {
        Boot();
        Call("worldSpawn", new { items = new[] { new { itemId = 13, amount = 1, position = new[] { 2f, 1f, 2f } } } });
        Pump(2);
        var result = JsonSerializer.SerializeToElement(Call("dedicatedStop"));
        Assert.That(result.GetProperty("saved").GetBoolean(), Is.True);
        Assert.That(File.Exists(SavePath), Is.True);
        Assert.That(_host.State(), Is.Null);
        Assert.That(_host.DedicatedActive, Is.False);
        using var check = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        check.Bind(new IPEndPoint(IPAddress.Any, _port));
    }

    [Test]
    public void ClosedParentPipeStillSavesOnce()
    {
        Boot();
        Pump(2);
        Assert.That(_host.DedicatedActive, Is.True);
        _host.EndOfInput();
        Assert.That(File.Exists(SavePath), Is.True);
        File.Delete(SavePath);
        _host.EndOfInput();
        Assert.That(File.Exists(SavePath), Is.False, "a stopped host does not save again");
    }

    [Test]
    public void WipeDeletesTheSaveButLeavesBansAndTheLiveWorld()
    {
        Boot();
        var admin = Peer("admin");
        var other = Peer("other");
        Pump();
        Run(admin, "save");
        Run(admin, "ban other");
        Assert.That(File.Exists(SavePath) && File.Exists(BansPath), Is.True);
        Assert.That(Run(admin, "wipe"), Does.StartWith("save deleted").And.Contain("UNCHANGED"));
        Assert.That(File.Exists(SavePath), Is.False);
        Assert.That(File.ReadAllLines(BansPath).Count(l => !l.StartsWith('#')), Is.EqualTo(1), "bans outlive worlds");
        Assert.That(Run(admin, "wipe"), Is.EqualTo("no save file to delete; the running world is unchanged"));
        Assert.That(admin.State, Is.EqualTo(NetSessionState.Connected));
    }

    [Test]
    public void WipeThenGracefulStopBootsAFreshWorld()
    {
        Boot();
        var admin = Peer("admin");
        Pump();
        Call("worldSpawn", new { items = new[] { new { itemId = 13, amount = 1, position = new[] { 2f, 1f, 2f } } } });
        Run(admin, "save");
        Assert.That(Run(admin, "wipe"), Does.StartWith("save deleted"));
        var stopped = JsonSerializer.SerializeToElement(Call("dedicatedStop"));
        Assert.That(stopped.GetProperty("saved").GetBoolean(), Is.False, "the shutdown save must not undo the wipe");
        Assert.That(File.Exists(SavePath), Is.False);
        _peers.Clear();
        _host.Dispose();
        _host = new BridgeHost();
        Boot();
        Assert.That(State().GetProperty("persistence").GetProperty("restorePending").GetBoolean(), Is.False);
        var back = Peer("admin");
        Pump(30);
        Assert.That(back.State, Is.EqualTo(NetSessionState.Connected));
        Assert.That(back.WorldItems.All.Any(e => e.ItemId == 13), Is.False, "the wiped world's item is gone");
    }

    [Test]
    public void WorldItemsStreamOnTheSourceInterestRing()
    {
        Boot();
        var peer = Peer("near");
        Pump();
        Assert.That(peer.State, Is.EqualTo(NetSessionState.Connected));
        Call("worldSpawn", new { items = new[] {
            new { itemId = 13, amount = 1, position = new[] { 2f, 1f, 2f } },
            new { itemId = 13, amount = 1, position = new[] { 400f, 1f, 400f } } } });
        Pump(30);
        Assert.That(peer.WorldItems.Count, Is.EqualTo(1), "only items inside the 128 m ring around the player replicate");
    }

    [Test]
    public void OversizedMaxPlayersIsClampedToTheSessionCap()
    {
        Environment.SetEnvironmentVariable("UG_MAXPLAYERS", "64");
        Boot();
        var peer = Peer("one");
        Pump(20);
        Assert.That(peer.Session.ServerMaxPlayers, Is.EqualTo(32));
    }

    [Test]
    public void NoCheatsLocksGivesAndModerationOnTheConsole()
    {
        Environment.SetEnvironmentVariable("UG_DEDICATED_NOCHEATS", "1");
        Boot();
        var peer = Peer("player");
        var target = Peer("target");
        Pump();
        Assert.That(Network().GetProperty("allowCheats").GetBoolean(), Is.False);
        Assert.That(Run(peer, "give 13"), Does.Contain("disabled"));
        Assert.That(Run(peer, "kick target"), Does.Contain("disabled"), "source gates moderation on AllowCheats");
        Assert.That(target.State, Is.EqualTo(NetSessionState.Connected));
        Assert.That(peer.Inventories.TryGet(peer.PlayerId, out var bag) && bag.Inventory.getItemCount(13) == 0, Is.True);
    }

    [Test]
    public void DefaultTestServerAllowsConsoleCheatsLikeTheSource()
    {
        Boot();
        var peer = Peer("player");
        Pump();
        Assert.That(Network().GetProperty("allowCheats").GetBoolean(), Is.True);
        Assert.That(Run(peer, "give 13"), Does.Contain("gave"));
    }

    [Test]
    public void UnreadableSaveIsReportedKeptAndTheWorldStartsFresh()
    {
        File.WriteAllText(SavePath, "{ not a save");
        Boot(serve: false);
        var served = JsonSerializer.SerializeToElement(Call("dedicatedServe"));
        Assert.That(served.GetProperty("save").GetString(), Does.StartWith("save refused"));
        var aside = Directory.GetFiles(_dir, Path.GetFileName(SavePath) + ".refused-*");
        Assert.That(aside, Has.Length.EqualTo(1), "the file is moved aside for recovery");
        Assert.That(File.ReadAllText(aside[0]), Is.EqualTo("{ not a save"));
        Assert.That(Network().GetProperty("serving").GetBoolean(), Is.True);
        var peer = Peer("fresh");
        Pump(20);
        Assert.That(peer.State, Is.EqualTo(NetSessionState.Connected));
        Run(peer, "save");
        Assert.That(File.ReadAllText(aside[0]), Is.EqualTo("{ not a save"), "a later save never overwrites the refused file");
    }

    [Test]
    public void InvalidDedicatedConfigurationsFailBeforeBinding()
    {
        Assert.Throws<ArgumentException>(() => Call("init", new { contentRoot = ContentRoot(), dedicated = true, savePath = SavePath }));
        Assert.Throws<ArgumentException>(() => Call("init", new { contentRoot = ContentRoot(), dedicated = true, listenPort = _port }));
        Assert.Throws<ArgumentException>(() => Call("init", new { contentRoot = ContentRoot(), dedicated = true, listenPort = _port,
            savePath = SavePath, starterKit = true }));
        Assert.Throws<InvalidOperationException>(() => Call("dedicatedServe"));
        using var check = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        check.Bind(new IPEndPoint(IPAddress.Any, _port));
    }
}
