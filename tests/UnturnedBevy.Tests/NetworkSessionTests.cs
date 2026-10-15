using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text.Json;
using NUnit.Framework;
using SDG.NetTransport.Udp;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class NetworkSessionTests
{
    BridgeHost _host;
    readonly List<NetWorldClient> _peers = new();
    ushort _port;
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

    [SetUp]
    public void SetUp()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
        _host = new BridgeHost();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var peer in _peers) peer.Disconnect();
        _peers.Clear();
        _host.Dispose();
    }

    object Call(string op, object request = null) => _host.Handle(op, JsonSerializer.SerializeToElement(request ?? new { }));
    JsonElement State() => JsonSerializer.SerializeToElement(_host.State(), JsonOptions);
    void Listen(int maxPlayers = 4, bool allowCheats = false) => Call("init", new {
        contentRoot = ContentRoot(), listenPort = _port, spawn = new[] { 0, 0, 0 },
        serverName = "Parity host", mapId = "PEI", motd = "Source UDP", maxPlayers, allowCheats,
    });

    NetWorldClient Peer(string name = "remote", ulong? hash = null)
    {
        var client = new NetWorldClient(new UdpClientTransport("127.0.0.1", _port), name, contentHash: hash ?? NetContent.Hash);
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
            // A real UDP socket is involved; allow the OS to deliver locally
            // without tying the deterministic simulation to elapsed wall time.
            Thread.Sleep(1);
        }
    }

    [Test]
    public void RealUdpPeerJoinsMovesChatsLeavesAndReconnectsWithoutStaleReplica()
    {
        Listen();
        var peer = Peer();
        Pump();
        Assert.That(peer.State, Is.EqualTo(NetSessionState.Connected));
        Assert.That(peer.Session.ServerName, Is.EqualTo("Parity host"));
        Assert.That(peer.Session.ServerMaxPlayers, Is.EqualTo(4));
        Assert.That(State().GetProperty("players").GetArrayLength(), Is.EqualTo(2));
        for (int n = 0; n < 35; n++) { peer.SendMoveInput(0, 1, 0); Pump(1); }
        Pump();
        Assert.That(peer.Players.TryGetByOwner(peer.PlayerId, out var own), Is.True);
        Assert.That(own.Pos.sqrMagnitude, Is.GreaterThan(1));
        var replica = State().GetProperty("players").EnumerateArray().Single(p => p.GetProperty("playerId").GetUInt16() == peer.PlayerId);
        Assert.That(replica.GetProperty("position")[2].GetSingle(), Is.EqualTo(own.Pos.z).Within(0.01));
        peer.SendChat("UDP hello");
        Pump();
        Assert.That(State().GetProperty("events").EnumerateArray().Any(e =>
            e.GetProperty("kind").GetString() == "chat" && e.GetProperty("data").GetProperty("text").GetString() == "UDP hello"), Is.True);
        ushort oldId = peer.PlayerId;
        peer.Disconnect();
        _peers.Remove(peer);
        Pump();
        Assert.That(State().GetProperty("players").GetArrayLength(), Is.EqualTo(1));
        var next = Peer("rejoined");
        Pump();
        Assert.That(next.State, Is.EqualTo(NetSessionState.Connected));
        Assert.That(next.PlayerId, Is.Not.EqualTo(oldId));
        Assert.That(State().GetProperty("players").EnumerateArray().Any(p => p.GetProperty("playerId").GetUInt16() == oldId), Is.False);
    }

    [Test]
    public void ListenDefaultsRefuseRemoteCheatsAndKeepLocalInventoryAuthority()
    {
        Listen();
        var peer = Peer();
        string response = null;
        peer.ConsoleResult += e => response = e.Text;
        Pump();
        peer.SendConsole("give 13");
        Pump();
        Assert.That(response, Does.Contain("disabled"));
        Assert.That(peer.Inventories.TryGet(peer.PlayerId, out var bag), Is.True);
        Assert.That(bag.Inventory.getItemCount(13), Is.Zero);
        Assert.That(State().GetProperty("network").GetProperty("mode").GetString(), Is.EqualTo("listen"));
        Assert.That(State().GetProperty("connected").GetBoolean(), Is.True);
    }

    [Test]
    public void ContentMismatchAndCapacityUseOriginalHandshakeRejection()
    {
        Listen(2);
        var mismatch = Peer("wrong-content", NetContent.Hash ^ 1);
        Pump();
        Assert.That(mismatch.State, Is.EqualTo(NetSessionState.Disconnected));
        Assert.That(mismatch.Session.DisconnectReason, Is.EqualTo(NetDisconnectReason.Rejected));
        _peers.Remove(mismatch); mismatch.Disconnect();
        var accepted = Peer();
        Pump();
        Assert.That(accepted.State, Is.EqualTo(NetSessionState.Connected));
        var full = Peer("full");
        Pump();
        Assert.That(full.Session.DisconnectReason, Is.EqualTo(NetDisconnectReason.Rejected));
        Assert.That(State().GetProperty("network").GetProperty("peers").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public void UdpRejectsUnnegotiatedOriginAndReleasesPortOnSessionReplacement()
    {
        Listen();
        var before = State();
        Assert.Throws<InvalidOperationException>(() => Call("rebase", new { expectedEpoch = 0, shift = new[] { 256, 0, 0 } }));
        Assert.That(State().GetProperty("origin").GetRawText(), Is.EqualTo(before.GetProperty("origin").GetRawText()));
        Call("init", new { contentRoot = ContentRoot() });
        using var check = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        check.Bind(new IPEndPoint(IPAddress.Any, _port));
        Assert.That(State().GetProperty("network").GetProperty("mode").GetString(), Is.EqualTo("local"));
        Assert.Throws<InvalidOperationException>(() => Call("init", new { contentRoot = ContentRoot(), listenPort = _port, origin = new[] { 256, 0 } }));
    }

    [Test]
    public void EdgeOfWireSpawnRespawnsWithoutAttemptingTheLocalOriginBarrier()
    {
        float edge = NetQuantization.PositionXZRange - 128;
        Call("init", new { contentRoot = ContentRoot(), listenPort = _port,
            spawn = new[] { edge, 0, 0 }, allowCheats = true });
        Call("action", new { name = "console", args = new { text = "kill" } });
        Pump(4);
        Assert.That(State().GetProperty("player").GetProperty("alive").GetBoolean(), Is.False);
        Call("action", new { name = "respawn" });
        Pump(8);
        Assert.That(State().GetProperty("player").GetProperty("alive").GetBoolean(), Is.True);
        Assert.That(State().GetProperty("player").GetProperty("position")[0].GetSingle(), Is.EqualTo(edge).Within(0.01));
        Call("action", new { name = "console", args = new { text = "kill" } });
        Pump(ServerCombat.RespawnDelayTicks + 10);
        Assert.That(State().GetProperty("player").GetProperty("alive").GetBoolean(), Is.True);
        Assert.That(State().GetProperty("origin").GetProperty("epoch").GetInt64(), Is.Zero);
    }

    [Test]
    public void LateJoinUsesInitialSpawnInsteadOfTheHostsCurrentWalkPosition()
    {
        Listen();
        for (int n = 0; n < 40; n++)
            Call("tick", new { input = new { position = new[] { n * 0.05f, 0, 0 }, velocity = new[] { 2.5f, 0, 0 } } });
        Assert.That(State().GetProperty("player").GetProperty("position")[0].GetSingle(), Is.GreaterThan(1));
        var late = Peer("late");
        Pump();
        Assert.That(late.Players.TryGetByOwner(late.PlayerId, out var own), Is.True);
        Assert.That(own.Pos.x, Is.Zero.Within(0.01));
        Assert.Throws<ArgumentException>(() => Call("init", new { contentRoot = ContentRoot(), listenPort = _port, spawn = new[] { NetQuantization.PositionXZRange + 1, 0, 0 } }));
    }

    [Test]
    public void ExistingBrowserProtocolReportsLivePeerCountMapAndContentIdentity()
    {
        Listen();
        Peer();
        Pump();
        using var query = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        query.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var request = new byte[512];
        "UGSQ"u8.CopyTo(request);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 0x12345678);
        query.SendTo(request, new IPEndPoint(IPAddress.Loopback, _port));
        Pump(2);
        Assert.That(query.Poll(1_000_000, SelectMode.SelectRead), Is.True);
        var reply = new byte[512];
        int length = query.Receive(reply);
        Assert.That(length, Is.InRange(24, request.Length));
        Assert.That(System.Text.Encoding.ASCII.GetString(reply, 0, 4), Is.EqualTo("UGSR"));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)), Is.EqualTo(0x12345678));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(8)), Is.EqualTo(2));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(10)), Is.EqualTo(4));
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(12)), Is.EqualTo(NetContent.Hash));
        Assert.That(reply[20], Is.EqualTo(NetProtocol.Version));
        Assert.That(System.Text.Encoding.UTF8.GetString(reply, 25, reply[24]), Is.EqualTo("PEI"));
    }

    [Test]
    public void FailedBindDoesNotLeaveAHalfInitializedSessionAndCanRecover()
    {
        using (var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            occupied.Bind(new IPEndPoint(IPAddress.Any, _port));
            Assert.Throws<SocketException>(() => Listen());
            Assert.That(_host.State(), Is.Null);
        }
        Listen();
        Assert.That(State().GetProperty("connected").GetBoolean(), Is.True);
    }
}
