using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class RemoteSessionTests
{
    BridgeHost _host, _remote;
    ushort _port;
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };
    static string Root()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException("game/content");
    }
    static JsonElement Call(BridgeHost host, string op, object request = null) => JsonSerializer.SerializeToElement(
        host.Handle(op, JsonSerializer.SerializeToElement(request ?? new { })), JsonOptions);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), JsonOptions);
    static JsonElement Action(BridgeHost host, string name, object args = null) => Call(host, "action", new { name, args = args ?? new { } });
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);

    [SetUp]
    public void Setup()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
        _host = new BridgeHost(); _remote = new BridgeHost();
    }
    [TearDown]
    public void Cleanup() { _remote.Dispose(); _host.Dispose(); }
    void Listen(bool cheats = true, int maxPlayers = 4) => Call(_host, "init", new {
        contentRoot = Root(), listenPort = _port, name = "Host", mapId = "PEI",
        spawn = new[] { 35, 8, -22 }, allowCheats = cheats, maxPlayers,
    });
    JsonElement Join() => Call(_remote, "init", new {
        contentRoot = Root(), connectHost = "127.0.0.1", connectPort = _port,
        name = "Remote", mapId = "PEI", spawn = new[] { -999, 300, 999 },
    });
    void Pump(int count = 16)
    {
        for (int i = 0; i < count; i++) { Call(_remote, "tick"); Call(_host, "tick"); Thread.Sleep(1); }
    }
    static int Count(JsonElement state, int id) => state.GetProperty("inventory").GetProperty("pages").EnumerateArray()
        .Sum(p => p.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("id").GetInt32() == id)
            .Sum(i => i.GetProperty("amount").GetInt32()));
    static (int Page, int X, int Y) Address(JsonElement state, int id)
    {
        foreach (var p in state.GetProperty("inventory").GetProperty("pages").EnumerateArray())
            foreach (var i in p.GetProperty("items").EnumerateArray())
                if (i.GetProperty("id").GetInt32() == id) return (p.GetProperty("index").GetInt32(), i.GetProperty("x").GetInt32(), i.GetProperty("y").GetInt32());
        throw new AssertionException($"No item{id}");
    }

    [Test]
    public void ClientHasNoServerAndAdoptsActualSpawnBeforeAnyPositionPublication()
    {
        Listen(); var init = Join();
        Assert.That(init.GetProperty("network").GetProperty("mode").GetString(), Is.EqualTo("client"));
        Assert.That(init.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "save"), Is.False);
        Assert.That(State(_remote).GetProperty("connected").GetBoolean(), Is.False);
        Assert.That(Call(_remote, "diagnostics").GetProperty("host").GetBoolean(), Is.False);
        // Native can have a map-spawn hint before handshake, but it must never
        // become the first claim and override the server-selected owner spawn.
        Call(_remote, "tick", new { input = new { position = new[] { 777, 888, 999 } } });
        Pump(); var state = State(_remote);
        Assert.That(state.GetProperty("connected").GetBoolean(), Is.True);
        Assert.That(state.GetProperty("player").GetProperty("position")[0].GetSingle(), Is.EqualTo(35).Within(.02));
        Assert.That(state.GetProperty("player").GetProperty("position")[1].GetSingle(), Is.EqualTo(8).Within(.02));
        Assert.That(state.GetProperty("player").GetProperty("position")[2].GetSingle(), Is.EqualTo(-22).Within(.02));
        Assert.That(state.GetProperty("network").GetProperty("ownsWorld").GetBoolean(), Is.False);
        Assert.That(State(_host).GetProperty("players").GetArrayLength(), Is.EqualTo(2));
        Assert.That(state.GetProperty("weather").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(state.GetProperty("network").GetProperty("wireGaps").EnumerateArray().Any(v => v.GetString() == "weather"), Is.True);
    }

    [Test]
    public void InventoryAndGunSelectionWaitForRealServerAcceptanceThenFireConsumesServerAmmo()
    {
        Listen(); Join(); Pump();
        Action(_remote, "console", new { text = "give 4" });
        Assert.That(Count(State(_remote), 4), Is.Zero, "request must not edit a client inventory");
        Pump(); var address = Address(State(_remote), 4);
        var equip = Action(_remote, "equipAndHold", new { page = address.Page, x = address.X, y = address.Y });
        Assert.That(equip.GetProperty("pending").GetBoolean(), Is.True);
        Assert.That(State(_remote).GetProperty("player").GetProperty("heldItemId").GetInt32(), Is.Zero);
        Pump(24); var state = State(_remote);
        Assert.That(state.GetProperty("weapon").GetProperty("held").GetProperty("page").GetInt32(), Is.Zero);
        Assert.That(state.GetProperty("player").GetProperty("heldItemId").GetInt32(), Is.EqualTo(4));
        int before = state.GetProperty("player").GetProperty("ammo").GetInt32();
        Action(_remote, "fire", new { origin = new[] { 35f, 9.6f, -22f }, dir = new[] { 0f, 0f, -1f } });
        Assert.That(State(_remote).GetProperty("player").GetProperty("ammo").GetInt32(), Is.EqualTo(before));
        Pump(24); state = State(_remote);
        Assert.That(state.GetProperty("player").GetProperty("ammo").GetInt32(), Is.EqualTo(before - 1));
        Assert.That(state.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "fire"), Is.True);
        Assert.That(Count(State(_host), 4), Is.Zero, "remote inventory is independent of listen owner");
    }

    [Test]
    public void DisabledRemoteCheatsAndHostOnlyOperationsCannotCreateLocalAuthority()
    {
        Listen(false); Join(); Pump();
        Action(_remote, "console", new { text = "give 13" }); Pump();
        Assert.That(Count(State(_remote), 13), Is.Zero);
        Assert.That(State(_remote).GetProperty("events").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "console"
            && e.GetProperty("data").GetProperty("text").GetString()!.Contains("disabled")), Is.True);
        foreach (string op in new[] { "worldSpawn", "worldVehicles", "worldVehicleDamage", "worldServices", "worldZombies", "worldGeometry", "save", "load", "rebase" })
            Assert.Throws<InvalidOperationException>(() => Call(_remote, op), op);
        Assert.That(Action(_remote, "respawn").GetProperty("queued").GetBoolean(), Is.False);
        Assert.That(Call(_remote, "diagnostics").GetProperty("host").GetBoolean(), Is.False);
    }

    [Test]
    public void RecoveryDeathAndAutomaticRespawnAdoptAcceptedFactsWithoutASecondBody()
    {
        Listen(); Join(); Pump();
        int correction = State(_remote).GetProperty("player").GetProperty("correctionVersion").GetInt32();
        Call(_remote, "tick", new { input = new { position = new[] { 1500, 8, 1500 }, grounded = true } });
        Pump(20);
        var player = State(_remote).GetProperty("player");
        Assert.That(player.GetProperty("position")[0].GetSingle(), Is.EqualTo(35).Within(.02));
        Assert.That(player.GetProperty("correctionVersion").GetInt32(), Is.GreaterThan(correction));
        ushort owner = State(_remote).GetProperty("playerId").GetUInt16();
        Server.Combat.DamagePlayerExternal(owner, 150);
        Pump(); Assert.That(State(_remote).GetProperty("player").GetProperty("alive").GetBoolean(), Is.False);
        Pump(300); player = State(_remote).GetProperty("player");
        Assert.That(player.GetProperty("alive").GetBoolean(), Is.True);
        Assert.That(player.GetProperty("position")[0].GetSingle(), Is.EqualTo(35).Within(.02));
        Assert.That(player.GetProperty("hp").GetInt32(), Is.EqualTo(100));
    }

    [Test]
    public void RemoteChatGestureDisconnectAndRejoinUseOriginalWireAndClearPriorReplicas()
    {
        Listen(); Join(); Pump();
        ushort previous = State(_remote).GetProperty("playerId").GetUInt16();
        Action(_remote, "chat", new { text = "remote bridge hello" });
        Action(_remote, "requestGesture", new { gesture = 6 });
        Pump(24);
        Assert.That(State(_host).GetProperty("events").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "chat"
            && e.GetProperty("data").GetProperty("text").GetString() == "remote bridge hello"), Is.True);
        Assert.That(State(_remote).GetProperty("player").GetProperty("gesture").GetInt32(), Is.EqualTo(6));
        Call(_remote, "disconnect"); Pump();
        Assert.That(State(_host).GetProperty("players").GetArrayLength(), Is.EqualTo(1));
        Join(); Pump();
        Assert.That(State(_remote).GetProperty("playerId").GetUInt16(), Is.Not.EqualTo(previous));
        Assert.That(State(_remote).GetProperty("player").GetProperty("gesture").GetInt32(), Is.Zero);
        Assert.That(State(_remote).GetProperty("events").GetArrayLength(), Is.LessThan(5));
    }

    [Test]
    public void RejectedJoinRemainsAClientAndCanBeReplacedByALocalSession()
    {
        Listen(maxPlayers: 1); Join(); Pump(30);
        Assert.That(State(_remote).GetProperty("connected").GetBoolean(), Is.False);
        Assert.That(State(_remote).GetProperty("network").GetProperty("reason").GetString(), Is.Not.EqualTo("None"));
        Assert.That(State(_remote).GetProperty("network").GetProperty("rejectReason").GetString(), Is.EqualTo("ServerFull"));
        Assert.That(State(_remote).GetProperty("network").GetProperty("rejectText").GetString(), Is.EqualTo("Server is full."));
        Assert.That(State(_remote).GetProperty("network").GetProperty("retryable").GetBoolean(), Is.True);
        Call(_remote, "init", new { contentRoot = Root(), name = "Local", spawn = new[] { 1, 2, 3 } });
        Assert.That(State(_remote).GetProperty("network").GetProperty("mode").GetString(), Is.EqualTo("local"));
        Assert.That(Call(_remote, "diagnostics").GetProperty("host").GetBoolean(), Is.True);
        Assert.That(State(_remote).GetProperty("connected").GetBoolean(), Is.True);
    }

    [Test]
    public void ReplacingIntegratedWorldWithRemoteClientCannotLeakItsOldServicesOrInventory()
    {
        Listen();
        Call(_remote, "init", new { contentRoot = Root(), starterKit = true, spawn = new[] { 1, 2, 3 } });
        Assert.That(Count(State(_remote), 4), Is.EqualTo(1));
        Assert.That(State(_remote).GetProperty("weather").ValueKind, Is.EqualTo(JsonValueKind.Object));
        Join(); Pump();
        Assert.That(Count(State(_remote), 4), Is.Zero);
        Assert.That(State(_remote).GetProperty("weather").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(State(_remote).GetProperty("weapon").GetProperty("held").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(State(_remote).GetProperty("worldItems").GetArrayLength(), Is.Zero);
        Assert.That(Call(_remote, "diagnostics").GetProperty("host").GetBoolean(), Is.False);
        Assert.Throws<InvalidOperationException>(() => Call(_remote, "worldServices"));
    }

    [Test]
    public void LeavingListenWorldReleasesPortAndAuthorityWhileRemoteRetainsTerminalStatus()
    {
        Listen(); Join(); Pump();
        var ended = Call(_host, "disconnect");
        Assert.That(ended.GetProperty("ownsWorld").GetBoolean(), Is.False);
        Assert.That(_host.State(), Is.Null);
        Assert.Throws<InvalidOperationException>(() => Call(_host, "tick"));
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            Assert.DoesNotThrow(() => socket.Bind(new IPEndPoint(IPAddress.Any, _port)), "listening socket must close on leave");
        for (int i = 0; i < 350; i++) Call(_remote, "tick");
        Assert.That(State(_remote).GetProperty("connected").GetBoolean(), Is.False);
        Assert.That(State(_remote).GetProperty("network").GetProperty("state").GetString(), Is.EqualTo("Disconnected"));
        Assert.That(State(_remote).GetProperty("network").GetProperty("ownsWorld").GetBoolean(), Is.False);
        Assert.DoesNotThrow(() => Call(_remote, "shutdown"), "JSONL runtime may close a remote process normally");
    }

    [Test]
    public void UnsupportedPasswordIsRefusedBeforeCreatingASession()
    {
        Assert.Throws<ArgumentException>(() => Call(_remote, "init", new {
            contentRoot = Root(), connectHost = "127.0.0.1", connectPort = _port, password = "unsupported",
        }));
        Assert.That(_remote.State(), Is.Null);
    }

    [Test]
    public void RetryableInitialRefusalCanReconnectAfterCapacityClearsWithoutLocalInit()
    {
        Listen(maxPlayers: 2);
        var blocker = new NetWorldClient(new SDG.NetTransport.Udp.UdpClientTransport("127.0.0.1", _port),
            "Blocker", contentHash: UnturnedGodot.NetContent.Hash);
        try
        {
            blocker.Connect();
            for (int i = 0; i < 20; i++) { blocker.Tick(); Call(_host, "tick"); Thread.Sleep(1); }
            Assert.That(blocker.State, Is.EqualTo(NetSessionState.Connected));
            Join(); Pump(20);
            Assert.That(State(_remote).GetProperty("network").GetProperty("rejectReason").GetString(), Is.EqualTo("ServerFull"));
            blocker.Disconnect();
            for (int i = 0; i < 8; i++) Call(_host, "tick");
            Assert.That(Call(_remote, "reconnect").GetProperty("queued").GetBoolean(), Is.True);
            Assert.That(State(_remote).GetProperty("connected").GetBoolean(), Is.False);
            Pump(30);
            Assert.That(State(_remote).GetProperty("connected").GetBoolean(), Is.True);
            Assert.That(State(_remote).GetProperty("player").GetProperty("position")[0].GetSingle(), Is.EqualTo(35).Within(.02));
            Assert.Throws<InvalidOperationException>(() => Call(_remote, "reconnect"), "Joined sessions require fresh replicas via init");
        }
        finally { blocker.Disconnect(); }
    }

    [Test]
    public void AsyncProbeReadsRealHostBeforeAnyClientWorldExists()
    {
        Listen();
        var start = Call(_remote, "serverProbeStart", new { host = "127.0.0.1", port = _port });
        long token = start.GetProperty("token").GetInt64();
        Assert.That(_remote.State(), Is.Null);
        JsonElement result = default;
        for (int i = 0; i < 500; i++)
        {
            Call(_host, "tick");
            result = Call(_remote, "serverProbePoll", new { token });
            if (!result.GetProperty("pending").GetBoolean()) break;
            Thread.Sleep(2);
        }
        Assert.That(result.GetProperty("pending").GetBoolean(), Is.False);
        var status = result.GetProperty("status");
        Assert.That(status.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(status.GetProperty("compatible").GetBoolean(), Is.True);
        Assert.That(status.GetProperty("map").GetString(), Is.EqualTo("PEI"));
        Assert.That(status.GetProperty("players").GetInt32(), Is.EqualTo(1));
        Assert.That(status.GetProperty("max").GetInt32(), Is.EqualTo(4));
        Assert.That(_remote.State(), Is.Null, "A status probe must never create a world or player");
    }

    [Test]
    public void RemoteAnimalMetadataUsesOriginalPuppetClipsAndStaticSpeciesDataOnly()
    {
        Listen();
        for (byte species = 0; species < 4; species++)
        {
            var id = Server.Ids.Mint();
            Server.Animals.ServerSpawn(id, species, new(36 + species, 8, -22), Server.Session.CurrentTick);
            Server.Animals.ServerPublish(id, new(36 + species, 8, -22), 90,
                species == 3 ? (byte)AnimalNetAnim.Dead : (byte)(species + 1), Server.Session.CurrentTick);
        }
        Join(); Pump(20);
        var animals = State(_remote).GetProperty("animals").EnumerateArray().ToArray();
        Assert.That(animals.Length, Is.EqualTo(4));
        string[] clips = ["Walk", "Eat", "Glance_0", "Idle"];
        float[] heights = [1.3f, .75f, 1.45f, 1.81f];
        foreach (var animal in animals)
        {
            int species = animal.GetProperty("species").GetInt32();
            Assert.That(animal.GetProperty("clip").GetString(), Is.EqualTo(clips[species]));
            Assert.That(animal.GetProperty("bodyHeight").GetSingle(), Is.EqualTo(heights[species]));
            Assert.That(animal.GetProperty("radius").GetSingle(), Is.EqualTo(AnimalBrain.RadiusFor(heights[species])));
            Assert.That(animal.TryGetProperty("health", out _), Is.False);
            Assert.That(animal.TryGetProperty("corpseRemainingTicks", out _), Is.False);
            Assert.That(animal.GetProperty("remote").GetBoolean(), Is.True);
        }
    }
}
