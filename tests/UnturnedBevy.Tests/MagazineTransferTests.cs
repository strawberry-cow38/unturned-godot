using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SDG.NetTransport.Udp;
using UnturnedGodot;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class MagazineTransferTests
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    BridgeHost _host, _peer;
    ushort _port;
    static JsonElement Call(BridgeHost h, string op, object args = null) =>
        JsonSerializer.SerializeToElement(h.Handle(op, JsonSerializer.SerializeToElement(args ?? new { }, Json)), Json);
    static JsonElement State(BridgeHost h) => JsonSerializer.SerializeToElement(h.State(), Json);
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);
    ushort Owner => State(_peer).GetProperty("playerId").GetUInt16();
    PlayerInventory Inventory => Server.Transactions.InventoryForTest(Owner);
    Item _mag, _rounds;
    ItemAsset _magAsset, _roundAsset;
    static string Content()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return Path.Combine(d.FullName, "game/content");
        throw new DirectoryNotFoundException();
    }
    void Pump(int count = 12) { for (int i = 0; i < count; i++) { Call(_peer, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    [SetUp] public void Setup()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)socket.LocalEndPoint!).Port; _port = (ushort)port; socket.Close();
        _host = new(); _peer = new();
        Call(_host, "init", new { contentRoot = Content(), listenPort = port, allowCheats = true });
        Call(_peer, "init", new { contentRoot = Content(), connectHost = "127.0.0.1", connectPort = port }); Pump(30);
        Inventory.items[2].resize(12, 12);
        _magAsset = Assets.find(6); _roundAsset = Assets.all().First(a => a.isAmmo && !a.IsMagazine && a.magRound == _magAsset.magRound);
        _mag = new(_magAsset.id, 0); _rounds = new(_roundAsset.id, 4);
        Inventory.items[2].addItem(0, 0, 0, _mag); Inventory.items[2].addItem(6, 0, 0, _rounds); Pump();
    }
    [TearDown] public void Cleanup() { _peer?.Dispose(); _host?.Dispose(); }
    JsonElement[] Facts(BridgeHost h) => State(h).GetProperty("events").EnumerateArray()
        .Where(e => e.GetProperty("kind").GetString() == "magazineTransfer").Select(e => e.GetProperty("data").Clone()).ToArray();
    void Send(uint request, bool unload = false, ulong? magToken = null, ulong? roundToken = null, ushort? roundId = null, byte magX = 0) =>
        Call(_peer, "action", new { name = "magazineTransfer", args = new {
            requestId = request, magPage = 2, magX, magY = 0, magId = _mag.id, magToken = magToken ?? WeaponState.ItemIdentity(_mag),
            roundPage = unload ? 0 : 2, roundX = unload ? 0 : 6, roundY = 0, roundId = roundId ?? _rounds.id,
            roundToken = unload ? 0UL : roundToken ?? WeaponState.ItemIdentity(_rounds), unloading = unload } });
    JsonElement Result(uint request, string expected)
    {
        Pump(); var result = Facts(_peer).Last(e => e.GetProperty("requestId").GetUInt32() == request);
        Assert.That(result.GetProperty("result").GetString(), Is.EqualTo(expected)); return result;
    }
    [Test]
    public void PublishedProtocol70PeerIsExplicitlyRejectedBeforeItCanSilentlyLoseMagazineReplies()
    {
        var old = new NetClientSession(new UdpClientTransport("127.0.0.1", _port), "prior-version",
            protocolVersion: 70, contentHash: NetContent.Hash);
        try {
            old.Connect();
            for (int i = 0; i < 100 && old.State != NetSessionState.Disconnected; i++) {
                old.Tick(); Call(_host, "tick"); Thread.Sleep(1);
            }
            Assert.That(old.State, Is.EqualTo(NetSessionState.Disconnected));
            Assert.That(old.RejectReason, Is.EqualTo(NetRejectReason.VersionMismatch));
            Assert.That(old.RejectServerVersion, Is.EqualTo(NetProtocol.Version));
        } finally { old.Disconnect(); }
    }
    [Test]
    public void ActualUdpLoadAndUnloadReturnCorrelatedOwnerFactAndOnlyReplicasChangeTheView()
    {
        Send(1); Assert.That(_mag.amount, Is.Zero, "intent is not a local mutation");
        var first = Result(1, "Applied"); Assert.That(first.GetProperty("amount").GetUInt16(), Is.EqualTo(1));
        Assert.That(first.GetProperty("magazineToken").GetUInt64(), Is.EqualTo(WeaponState.ItemIdentity(_mag)));
        Assert.That(_mag.amount, Is.EqualTo(1)); Assert.That(_rounds.amount, Is.EqualTo(3));
        var view = Call(_peer, "inventoryView");
        Assert.That(unchecked((int)(view.GetProperty("acceptedTick").GetUInt32() - first.GetProperty("acceptedTick").GetUInt32())), Is.GreaterThanOrEqualTo(0));
        var row = view.GetProperty("tiles").GetProperty("cells").EnumerateArray()
            .Single(e => e.GetProperty("page").GetByte() == 2 && e.GetProperty("x").GetByte() == 0);
        Assert.That(row.GetProperty("tile").GetProperty("amount").GetUInt16(), Is.EqualTo(1));
        Send(1); Result(1, "Applied"); Assert.That(_mag.amount, Is.EqualTo(1), "request replay does not load again");
        Send(2, unload: true); Result(2, "Applied"); Assert.That(_mag.amount, Is.Zero);
        Assert.That(Inventory.getItemCount(_roundAsset.id), Is.EqualTo(4)); Assert.That(Facts(_host), Is.Empty, "result is owner-only");
    }
    [Test]
    public void UdpRejectsStaleReplacedAndForeignInstancesDeadOwnerAndWrongCartridgeWithExplicitResults()
    {
        ulong old = WeaponState.ItemIdentity(_mag); var page = Inventory.items[2]; page.removeItem(page.getIndex(0, 0));
        _mag = new(_magAsset.id, 0); page.addItem(0, 0, 0, _mag); Pump();
        Send(1, magToken: old); Result(1, "StaleMagazine");
        var foreign = new Item(_magAsset.id, 0); var hostBag = Server.Transactions.InventoryForTest(State(_host).GetProperty("playerId").GetUInt16());
        hostBag.items[2].addItem(0, 0, 0, foreign);
        Send(2, magToken: WeaponState.ItemIdentity(foreign)); Result(2, "StaleMagazine");
        ulong staleRound = WeaponState.ItemIdentity(_rounds);
        page.removeItem(page.getIndex(6, 0)); _rounds = new(_roundAsset.id, 4); page.addItem(6, 0, 0, _rounds);
        Send(3, roundToken: staleRound); Result(3, "StaleRound");
        Server.CombatState.TryGet(Owner, out var life); life.Alive = false;
        Send(4); Result(4, "Dead"); life.Alive = true;
        // A magazine body with matching magRound is not loose ammunition on unload.
        _mag.amount = 2; _mag.magLoadedRound = _magAsset.magRound;
        Send(5, unload: true, roundId: _mag.id); Result(5, "RuleRefused");
        Assert.That(_mag.amount, Is.EqualTo(2)); Assert.That(_rounds.amount, Is.EqualTo(4)); Assert.That(foreign.amount, Is.Zero);
        // Selecting a subcell of the same multi-cell item cannot bypass exact address admission.
        if (_magAsset.size_x > 1) { Send(6, magX: 1); Result(6, "StaleMagazine"); }
        var wrong = Assets.all().First(a => a.isAmmo && !a.IsMagazine
            && MagRules.CheckLoad(_mag, _magAsset, a) != MagLoadResult.Ok);
        page.removeItem(page.getIndex(6, 0)); _rounds = new(wrong.id, 4); page.addItem(6, 0, 0, _rounds);
        Send(7); Result(7, "RuleRefused"); Assert.That(_rounds.amount, Is.EqualTo(4)); Assert.That(_mag.amount, Is.EqualTo(2));
        Assert.That(Facts(_host), Is.Empty);
    }
    [Test]
    public void UdpFullBagUnloadReportsNoRoomWithoutLosingARoundThenNewRequestSucceedsAfterSpaceAppears()
    {
        var page = Inventory.items[2]; page.removeItem(page.getIndex(6, 0));
        page.resize(_magAsset.size_x, _magAsset.size_y);
        for (byte i = 3; i < PlayerInventory.OWNPAGES; i++) Inventory.items[i].resize(0, 0);
        _mag.amount = 3; _mag.magLoadedRound = _magAsset.magRound; Pump();
        // The original39 command silently refuses this real full-bag case:
        // its snapshot stays identical and supplies no completion for a timer.
        Call(_peer, "action", new { name = "magLoad", args = new { magPage = 2, magX = 0, magY = 0,
            magId = _mag.id, roundPage = 0, roundX = 0, roundY = 0, roundId = _rounds.id, unloading = true } });
        Pump(); Assert.That(Facts(_peer), Is.Empty); Assert.That(_mag.amount, Is.EqualTo(3));
        Send(1, unload: true); Result(1, "NoRoom"); Assert.That(_mag.amount, Is.EqualTo(3));
        Assert.That(Inventory.getItemCount(_roundAsset.id), Is.Zero);
        page.resize(12, 12); Send(1, unload: true); Result(1, "NoRoom"); Assert.That(_mag.amount, Is.EqualTo(3));
        Send(2, unload: true); Result(2, "Applied"); Assert.That(_mag.amount, Is.EqualTo(2));
        Assert.That(Inventory.getItemCount(_roundAsset.id), Is.EqualTo(1));
    }
}
