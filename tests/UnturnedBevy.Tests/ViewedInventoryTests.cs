using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using SDG.NetTransport.Udp;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class ViewedInventoryTests
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    BridgeHost _host, _peer;
    ushort _port;
    const uint CrateId = 65000;
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);
    static JsonElement Call(BridgeHost host, string op, object args = null) =>
        JsonSerializer.SerializeToElement(host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { }, Json)), Json);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), Json);
    static JsonElement Action(BridgeHost host, string name, object args) => Call(host, "action", new { name, args });
    ushort Owner(BridgeHost host) => State(host).GetProperty("playerId").GetUInt16();
    PlayerInventory Bag => Server.Transactions.InventoryForTest(Owner(_peer));
    InventoryReplication.CrateEntry Crate { get { Server.Inventories.TryGetCrate(CrateId, out var c); return c; } }
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
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        _host = new(); _peer = new();
        Call(_host, "init", new { contentRoot = Content(), listenPort = _port, allowCheats = true, spawn = new[] { 0, 0, 0 } });
        Call(_peer, "init", new { contentRoot = Content(), connectHost = "127.0.0.1", connectPort = _port }); Pump(30);
        Bag.items[2].resize(12, 12);
        Server.Inventories.ServerRegisterCrate(new NetId(CrateId), 12, 12, Vector3.zero);
        Server.Inventories.ServerAddFreezer(CrateId, 12, 12);
        Server.Inventories.ServerAddCompartments(CrateId, new[] { ((byte)12, (byte)12, "Seat") });
    }
    [TearDown] public void Cleanup() { _peer?.Dispose(); _host?.Dispose(); }
    void OpenBoth()
    {
        Action(_peer, "openStorage", new { netId = CrateId }); Action(_host, "openStorage", new { netId = CrateId }); Pump();
        foreach (var h in new[] { _host, _peer })
            foreach (var row in Call(h, "inventoryView").GetProperty("items").EnumerateArray())
            {
                var a = row.GetProperty("address");
                if (PlayerInventory.IsContainerView(a.GetProperty("page").GetByte()))
                    Assert.That(a.GetProperty("token").GetUInt64(), Is.Not.Zero, "received scoped item identity, never a server-only test token");
            }
    }
    JsonElement Address(BridgeHost h, byte page, byte x = 0) => Call(h, "inventoryView").GetProperty("items").EnumerateArray()
        .Single(i => i.GetProperty("address").GetProperty("page").GetByte() == page
            && i.GetProperty("address").GetProperty("x").GetByte() == x).GetProperty("address").Clone();
    JsonElement Tile(BridgeHost h, byte page, byte x = 0) => Call(h, "inventoryView").GetProperty("tiles").GetProperty("cells").EnumerateArray()
        .Single(i => i.GetProperty("page").GetByte() == page && i.GetProperty("x").GetByte() == x).GetProperty("tile");
    void Empty(Item item, byte page, byte x = 0, uint crate = CrateId, ulong? token = null) =>
        Action(_peer, "emptyViewedFuel", new { crateId = crate, page, x, y = 0, id = item.id, token = token ?? WeaponState.ItemIdentity(item) });
    JsonElement[] Facts(BridgeHost h) => State(h).GetProperty("events").EnumerateArray()
        .Where(e => e.GetProperty("kind").GetString() == "magazineTransfer").Select(e => e.GetProperty("data")).ToArray();
    void Send(uint request, Item mag, byte mp, Item round, byte rp, bool unload = false,
        uint mc = CrateId, uint rc = 0, byte mx = 0, byte rx = 6, ulong? mt = null) =>
        Action(_peer, "viewedMagazineTransfer", new { requestId = request, magCrateId = mc, magPage = mp, magX = mx, magY = 0,
            magId = mag.id, magToken = mt ?? WeaponState.ItemIdentity(mag), roundCrateId = unload ? 0 : rc,
            roundPage = unload ? 0 : rp, roundX = unload ? 0 : rx, roundY = 0, roundId = round.id,
            roundToken = unload ? 0UL : WeaponState.ItemIdentity(round), unloading = unload });
    JsonElement Result(uint request, string expected)
    { Pump(); var r = Facts(_peer).Last(e => e.GetProperty("requestId").GetUInt32() == request);
      Assert.That(r.GetProperty("result").GetString(), Is.EqualTo(expected)); return r; }
    (Item mag, Item round) Magazines()
    { var a = Assets.find(6); return (new(a.id, 0), new(Assets.all().First(a2 => a2.isAmmo && !a2.IsMagazine && a2.magRound == a.magRound).id, 4)); }

    [Test]
    public void ActualUdpEmptyAllContainerPageKindsPublishesEveryViewerWithoutReopening()
    {
        var fuel = Assets.all().First(a => a.IsFuelContainer);
        var pages = new[] { (PlayerInventory.STORAGE, Crate.Storage), (PlayerInventory.FREEZER, Crate.Freezer), (PlayerInventory.COMPARTMENT0, Crate.Compartments[0].Grid) };
        foreach (var (_, grid) in pages) grid.addItem(0, 0, 0, new Item(fuel.id) { fuelLevel = 250 });
        OpenBoth();
        foreach (var (page, grid) in pages)
        {
            var item = grid.getItem(0).item;
            Assert.That(Address(_peer, page).GetProperty("crateId").GetUInt32(), Is.EqualTo(CrateId));
            var received = Address(_peer, page);
            Action(_peer, "emptyViewedFuel", received);
            Assert.That(item.fuelLevel, Is.EqualTo(250), "no client mutation"); Pump();
            Assert.That(item.fuelLevel, Is.Zero);
            foreach (var h in new[] { _host, _peer }) Assert.That(Tile(h, page).GetProperty("fill").GetProperty("amount").GetSingle(), Is.Zero);
        }
        Assert.That(Crate.Viewers.Count, Is.EqualTo(2));
    }

    [Test]
    public void UdpEmptyRejectsClosedChangedViewStaleForeignDeadAndCurrentAccessRevocation()
    {
        var asset = Assets.all().First(a => a.IsFuelContainer); var item = new Item(asset.id) { fuelLevel = 250 };
        Crate.Storage.addItem(0, 0, 0, item); OpenBoth();
        Empty(item, 7, token: WeaponState.ItemIdentity(new Item(asset.id))); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(250));
        Empty(item, 7, crate: CrateId + 1); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(250));
        if (asset.size_x > 1) { Empty(item, 7, x: 1); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(250)); }
        Crate.Pos = new Vector3(20, 0, 0); Empty(item, 7); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(250), "reach is checked on action, not only open");
        Crate.Pos = Vector3.zero; Crate.Access = (_, _) => false; Empty(item, 7); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(250));
        Crate.Access = null; OpenBoth();
        Server.CombatState.TryGet(Owner(_peer), out var life); life.Alive = false;
        Empty(item, 7); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(250)); life.Alive = true;
        Action(_peer, "closeStorage", new { }); Pump(); Empty(item, 7); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(250));
        OpenBoth(); var oldToken = WeaponState.ItemIdentity(item); Crate.Storage.removeItem(0);
        item = new Item(asset.id) { fuelLevel = 200 }; Crate.Storage.addItem(0, 0, 0, item); Server.Inventories.ServerRepaintCrateViewers(CrateId);
        Empty(item, 7, token: oldToken); Pump(); Assert.That(item.fuelLevel, Is.EqualTo(200));
        Empty(item, 7); Pump(); Assert.That(item.fuelLevel, Is.Zero);
    }

    [Test]
    public void ActualUdpCrossScopeMagazineLoadRefreshesBothViewersAndSharesOwnedReplayLedger()
    {
        var (mag, rounds) = Magazines(); Crate.Storage.addItem(0, 0, 0, mag); Bag.items[2].addItem(6, 0, 0, rounds); OpenBoth();
        var received = Address(_peer, 7);
        Assert.That(received.GetProperty("token").GetUInt64(), Is.EqualTo(WeaponState.ItemIdentity(mag)));
        Send(1, mag, 7, rounds, 2, mt: received.GetProperty("token").GetUInt64());
        Assert.That(mag.amount, Is.Zero); Result(1, "Applied");
        Assert.That(mag.amount, Is.EqualTo(1)); Assert.That(rounds.amount, Is.EqualTo(3));
        foreach (var h in new[] { _host, _peer }) Assert.That(Tile(h, 7).GetProperty("amount").GetUInt16(), Is.EqualTo(1));
        Send(1, mag, 7, rounds, 2); Result(1, "Applied"); Assert.That(mag.amount, Is.EqualTo(1));
        var ownMag = new Item(mag.id, 0); Bag.items[2].addItem(0, 0, 0, ownMag);
        Action(_peer, "magazineTransfer", new { requestId = 1, magPage = 2, magX = 0, magY = 0, magId = ownMag.id,
            magToken = WeaponState.ItemIdentity(ownMag), roundPage = 2, roundX = 6, roundY = 0, roundId = rounds.id, roundToken = WeaponState.ItemIdentity(rounds), unloading = false });
        Result(1, "StaleRequest"); Assert.That(ownMag.amount, Is.Zero);
        var viewedRounds = new Item(rounds.id, 2); Crate.Freezer.addItem(6, 0, 0, viewedRounds); Server.Inventories.ServerRepaintCrateViewers(CrateId);
        Send(2, ownMag, 2, viewedRounds, PlayerInventory.FREEZER, mc: 0, rc: CrateId); Result(2, "Applied");
        Assert.That(ownMag.amount, Is.EqualTo(1)); Assert.That(viewedRounds.amount, Is.EqualTo(1));
        foreach (var h in new[] { _host, _peer }) Assert.That(Tile(h, PlayerInventory.FREEZER, 6).GetProperty("amount").GetUInt16(), Is.EqualTo(1));
        Assert.That(Facts(_host), Is.Empty, "completion is owner-only even for another viewer");
    }

    [Test]
    public void UdpMagazineRechecksBothInstancesAndDepletedViewedStackDisappearsForEveryViewer()
    {
        var (mag, rounds) = Magazines(); Crate.Storage.addItem(0, 0, 0, mag);
        rounds.amount = 1; Crate.Freezer.addItem(6, 0, 0, rounds); OpenBoth();
        Send(1, mag, 7, rounds, 9, rc: CrateId, mt: WeaponState.ItemIdentity(new Item(mag.id)));
        Result(1, "StaleMagazine"); Assert.That(rounds.amount, Is.EqualTo(1));
        Send(2, mag, 7, rounds, 9, rc: CrateId + 1); Result(2, "StaleRound");
        Crate.Access = (_, _) => false;
        Send(3, mag, 7, rounds, 9, rc: CrateId); Result(3, "StaleMagazine");
        Crate.Access = null; OpenBoth();
        Server.CombatState.TryGet(Owner(_peer), out var life); life.Alive = false;
        Send(4, mag, 7, rounds, 9, rc: CrateId); Result(4, "Dead"); life.Alive = true;
        Assert.That(mag.amount, Is.Zero); Assert.That(rounds.amount, Is.EqualTo(1));
        Send(5, mag, 7, rounds, 9, rc: CrateId); Result(5, "Applied");
        Assert.That(mag.amount, Is.EqualTo(1)); Assert.That(Crate.Freezer.getItemCount(), Is.Zero);
        foreach (var h in new[] { _host, _peer })
        {
            Assert.That(Tile(h, 7).GetProperty("amount").GetUInt16(), Is.EqualTo(1));
            Assert.That(Call(h, "inventoryView").GetProperty("items").EnumerateArray()
                .Any(i => i.GetProperty("address").GetProperty("page").GetByte() == 9), Is.False);
        }
        Action(_peer, "closeStorage", new { }); Pump(); OpenBoth();
        Assert.That(Crate.Freezer.getItemCount(), Is.Zero, "closing cannot restore a spent viewed stack");
    }

    [Test]
    public void UdpUnloadRefusalIsIdempotentAndClosedViewCannotContinueJobs()
    {
        var (mag, rounds) = Magazines(); mag.amount = 2; mag.magLoadedRound = mag.GetAsset().magRound;
        Crate.Compartments[0].Grid.addItem(0, 0, 0, mag); OpenBoth();
        for (byte p = 2; p < PlayerInventory.OWNPAGES; p++) Bag.items[p].resize(0, 0);
        Send(1, mag, 10, rounds, 0, unload: true); Result(1, "NoRoom"); Assert.That(mag.amount, Is.EqualTo(2));
        Bag.items[2].resize(12, 12); Send(1, mag, 10, rounds, 0, unload: true); Result(1, "NoRoom");
        Send(2, mag, 10, rounds, 0, unload: true); Result(2, "Applied");
        Assert.That(Bag.getItemCount(rounds.id), Is.EqualTo(1)); Assert.That(mag.amount, Is.EqualTo(1));
        foreach (var h in new[] { _host, _peer }) Assert.That(Tile(h, 10).GetProperty("amount").GetUInt16(), Is.EqualTo(1));
        Action(_peer, "closeStorage", new { }); Pump(); Send(3, mag, 10, rounds, 0, unload: true); Result(3, "StaleMagazine");
        Assert.That(mag.amount, Is.EqualTo(1)); OpenBoth();
        Server.Inventories.ServerRemoveCrate(CrateId, Server.Session.CurrentTick);
        Send(4, mag, 10, rounds, 0, unload: true); Result(4, "StaleMagazine"); Assert.That(mag.amount, Is.EqualTo(1));
    }

    [Test]
    public void Published74PeerCannotSilentlyDropViewedItemCommands()
    {
        var old = new NetClientSession(new UdpClientTransport("127.0.0.1", _port), "old", protocolVersion: 74, contentHash: NetContent.Hash);
        try { old.Connect(); for (int i = 0; i < 100 && old.State != NetSessionState.Disconnected; i++) { old.Tick(); Call(_host, "tick"); Thread.Sleep(1); }
            Assert.That(old.RejectReason, Is.EqualTo(NetRejectReason.VersionMismatch)); Assert.That(old.RejectServerVersion, Is.EqualTo(NetProtocol.Version)); }
        finally { old.Disconnect(); }
    }
}
