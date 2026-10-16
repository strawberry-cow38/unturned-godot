using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;
namespace UnturnedBevyTests;
/// <summary>The native held-deployable place intent (Command 7 naming the held
/// jar) over actual UDP: the managed peer is a remote client of the listen host,
/// and only the host spends the item and creates the body.</summary>
[TestFixture, NonParallelizable]
public sealed class DeployablePlacementTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    const ushort Generator = 458;
    BridgeHost _host, _peer; int _port;
    static string Content() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return Path.Combine(d.FullName, "game/content"); throw new DirectoryNotFoundException(); }
    static JsonElement J(object v) => JsonSerializer.SerializeToElement(v, Options);
    static JsonElement Call(BridgeHost h, string op, object args = null) => J(h.Handle(op, J(args ?? new { })));
    static JsonElement State(BridgeHost h) => J(h.State());
    static void Action(BridgeHost h, string name, object args) => Call(h, "action", new { name, args });
    void Pump(int n = 12) { for (int i = 0; i < n; i++) { Call(_peer, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_host)!;
    ushort PeerId => (ushort)State(_peer).GetProperty("playerId").GetUInt32();
    PlayerInventory PeerInventory { get { Server.Inventories.TryGet(PeerId, out var e); return e.Inventory; } }
    JsonElement[] Rows(BridgeHost h) => State(h).GetProperty("deployables").EnumerateArray().ToArray();
    Vector3 Feet => State(_peer).GetProperty("player").GetProperty("position") is var p ? new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()) : default;
    (byte page, byte x, byte y)[] Jars(ushort id)
    {
        var inv = PeerInventory; var found = new List<(byte, byte, byte)>();
        for (byte page = 0; page < PlayerInventory.OWNPAGES; page++)
            foreach (var jar in inv.items[page].items) if (jar.item.id == id) found.Add((page, jar.x, jar.y));
        return found.ToArray();
    }
    void Place((byte page, byte x, byte y) jar, Vector3 at) =>
        Action(_peer, "placeDeployable", new { defId = Generator, pos = new[] { at.x, at.y, at.z }, yawDegrees = 90f, page = jar.page, x = jar.x, y = jar.y });

    [SetUp]
    public void Setup()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _port = ((IPEndPoint)socket.LocalEndPoint).Port; }
        _host = new(); _peer = new();
        Call(_host, "init", new { contentRoot = Content(), listenPort = _port, allowCheats = true, spawn = new[] { 0, 0, 0 } });
        Call(_peer, "init", new { contentRoot = Content(), connectHost = "127.0.0.1", connectPort = _port }); Pump(24);
        Action(_peer, "console", new { text = $"give {Generator}" }); Pump();
        Action(_peer, "console", new { text = $"give {Generator}" }); Pump();
        Assert.That(Jars(Generator), Has.Length.EqualTo(2), "two generator jars in the peer's bag");
    }
    [TearDown] public void Cleanup() { _peer.Dispose(); _host.Dispose(); }

    [Test]
    public void ActualUdpPlaceSpendsTheNamedJarAndCarriesItsConditionToTheAcceptedBody()
    {
        var jars = Jars(Generator);
        // The named jar is the worn one; the id search would have taken the other.
        var inv = PeerInventory;
        var named = inv.items[jars[1].page].getItem(inv.items[jars[1].page].getIndex(jars[1].x, jars[1].y)).item;
        named.quality = 50;
        var at = Feet + new Vector3(0, 0, -2.5f);
        Place(jars[1], at); Pump();
        var rows = Rows(_peer);
        Assert.That(rows, Has.Length.EqualTo(1), "the accepted body replicated to the owner");
        Assert.That(rows[0].GetProperty("defId").GetInt32(), Is.EqualTo(Generator));
        Assert.That(rows[0].GetProperty("yaw").GetSingle(), Is.EqualTo(90f).Within(0.1f));
        var p = rows[0].GetProperty("position");
        Assert.That(new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()) - at, Has.Property("magnitude").LessThan(0.02f));
        Assert.That(rows[0].GetProperty("health").GetSingle(), Is.EqualTo(225f).Within(0.5f), "450 HP at the named jar's 50%");
        var left = Jars(Generator);
        Assert.That(left, Has.Length.EqualTo(1));
        Assert.That(left[0], Is.EqualTo(jars[0]), "the other jar is untouched");
        Assert.That(Rows(_host), Has.Length.EqualTo(1), "one host body");
    }

    [Test]
    public void ActualUdpRefusesOutOfRangeSeatedAndDeadPlacersWithoutSpending()
    {
        var jars = Jars(Generator);
        Place(jars[0], Feet + new Vector3(0, 0, -30)); Pump();
        Assert.That(Rows(_peer), Is.Empty, "beyond Range + slack");
        Assert.That(Jars(Generator), Has.Length.EqualTo(2), "nothing spent out of range");
        var car = Server.Vehicles.ServerSpawn(Server.Ids.Mint(), 3, 0, Feet + new Vector3(2, 0, 0), Server.Session.CurrentTick);
        Server.Vehicles.ServerSetSeatCount(new NetId(car.NetIdValue), 4);
        Pump();
        Action(_peer, "enterVehicle", new { netId = car.NetIdValue, seat = 1 }); Pump();
        Assert.That(Server.VehicleHost.IsSeated(PeerId), Is.True, "peer took a passenger seat");
        Place(jars[0], Feet + new Vector3(0, 0, -2.5f)); Pump();
        Assert.That(Rows(_peer), Is.Empty, "a seated hand never reaches the ghost");
        Assert.That(Jars(Generator), Has.Length.EqualTo(2), "nothing spent from the seat");
        Action(_peer, "exitVehicle", new { }); Pump();
        Assert.That(Server.VehicleHost.IsSeated(PeerId), Is.False);
        Server.Combat.DamagePlayerExternal(PeerId, 1000f); Pump();
        Assert.That(Server.CombatState.IsAlive(PeerId), Is.False, "the placer died");
        var corpse = PeerInventory;
        corpse.tryAddItemAuto(new Item(Generator, 1), out _); Pump();
        var held = Jars(Generator);
        Assert.That(held, Is.Not.Empty);
        Place(held[0], Feet + new Vector3(0, 0, -2.5f)); Pump();
        Assert.That(Rows(_peer), Is.Empty, "TryPlaceDeployable returns on _dead");
        Assert.That(Jars(Generator), Has.Length.EqualTo(held.Length), "nothing spent");
    }
}
