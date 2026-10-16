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
/// <summary>Placed storage over actual UDP: the fridge (IsStorage, a
/// Refrigerator body) and the campfire (a Deployable with a DeployableCrate)
/// open through the existing storage command by the accepted NetId, and only
/// the host's crate holds what the owner stores.</summary>
[TestFixture, NonParallelizable]
public sealed class PlacedStorageTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    BridgeHost _host, _peer; int _port;
    static string Content() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return Path.Combine(d.FullName, "game/content"); throw new DirectoryNotFoundException(); }
    static JsonElement J(object v) => JsonSerializer.SerializeToElement(v, Options);
    static JsonElement Call(BridgeHost h, string op, object args = null) => J(h.Handle(op, J(args ?? new { })));
    static JsonElement State(BridgeHost h) => J(h.State());
    static void Action(BridgeHost h, string name, object args) => Call(h, "action", new { name, args });
    void Pump(int n = 12) { for (int i = 0; i < n; i++) { Call(_peer, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_host)!;
    Vector3 Feet => State(_peer).GetProperty("player").GetProperty("position") is var p ? new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()) : default;
    JsonElement Page(int index) => State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray().FirstOrDefault(p => p.GetProperty("index").GetInt32() == index);
    (int page, int x, int y) Cell(ushort id)
    {
        foreach (var p in State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray())
            foreach (var i in p.GetProperty("items").EnumerateArray())
                if (i.GetProperty("id").GetInt32() == id) return (p.GetProperty("index").GetInt32(), i.GetProperty("x").GetInt32(), i.GetProperty("y").GetInt32());
        throw new AssertionException($"no {id} in the peer bag");
    }
    uint Place(ushort id, Vector3 at)
    {
        Action(_peer, "console", new { text = $"give {id}" }); Pump();
        var c = Cell(id);
        Action(_peer, "placeDeployable", new { defId = id, pos = new[] { at.x, at.y, at.z }, yawDegrees = 0f, page = c.page, x = c.x, y = c.y }); Pump();
        return State(_peer).GetProperty("deployables").EnumerateArray().Single(r => r.GetProperty("defId").GetInt32() == id).GetProperty("netId").GetUInt32();
    }

    [SetUp]
    public void Setup()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _port = ((IPEndPoint)socket.LocalEndPoint).Port; }
        _host = new(); _peer = new();
        Call(_host, "init", new { contentRoot = Content(), listenPort = _port, allowCheats = true, spawn = new[] { 0, 0, 0 } });
        Call(_peer, "init", new { contentRoot = Content(), connectHost = "127.0.0.1", connectPort = _port }); Pump(24);
    }
    [TearDown] public void Cleanup() { _peer.Dispose(); _host.Dispose(); }

    [TestCase((ushort)9130, 5, 4, TestName = "ActualUdpPlacedFridgeOpensItsHostCrateAndKeepsStoredItems")]
    [TestCase((ushort)362, 3, 2, TestName = "ActualUdpPlacedCampfireOpensItsHostCrateAndKeepsStoredItems")]
    public void PlacedStorageOpensByAcceptedNetId(ushort def, int width, int height)
    {
        uint id = Place(def, Feet + new Vector3(0, 0, -1.5f));
        Action(_peer, "openStorage", new { netId = id }); Pump();
        var view = State(_peer).GetProperty("storage");
        Assert.That(view.GetProperty("netId").GetUInt32(), Is.EqualTo(id), "the accepted StorageOpened names the placed body");
        var storage = Page(PlayerInventory.STORAGE);
        Assert.That(storage.ValueKind, Is.EqualTo(JsonValueKind.Object), "the owner echo carries the storage page");
        Assert.That((storage.GetProperty("width").GetInt32(), storage.GetProperty("height").GetInt32()), Is.EqualTo((width, height)));
        Action(_peer, "console", new { text = "give 13" }); Pump();
        var beans = Cell(13);
        Action(_peer, "moveItem", new { page0 = beans.page, x0 = beans.x, y0 = beans.y, page1 = PlayerInventory.STORAGE, x1 = 0, y1 = 0, rot1 = 0 }); Pump();
        Assert.That(Page(PlayerInventory.STORAGE).GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetInt32() == 13), "moved into the open crate");
        Action(_peer, "closeStorage", new { }); Pump();
        Assert.That(State(_peer).GetProperty("storage").GetProperty("netId").GetUInt32(), Is.Zero);
        Assert.That(Server.Inventories.TryGetCrate(id, out var crate), Is.True, "the host crate is keyed by the deployable NetId");
        Assert.That(crate.Storage.getItemCount(), Is.EqualTo(1), "the host crate kept it");
        Action(_peer, "openStorage", new { netId = id }); Pump();
        Assert.That(Page(PlayerInventory.STORAGE).GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetInt32() == 13), "reopened with the stored item");
    }
}
