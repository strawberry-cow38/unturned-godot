using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class VehicleStorageViewTests
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() } };
    static JsonElement Call(BridgeHost h, string op, object args = null) => JsonSerializer.SerializeToElement(h.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), Json);
    static JsonElement State(BridgeHost h) => JsonSerializer.SerializeToElement(h.State(), Json);
    static void Action(BridgeHost h, string name, object args) => Call(h, "action", new { name, args });
    static void Pump(BridgeHost h, BridgeHost peer = null, int n = 12)
    { for (int i = 0; i < n; i++) { Call(h, "tick"); if (peer != null) { Call(peer, "tick"); Thread.Sleep(1); } } }
    static string Content()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game", "content"))) return Path.Combine(d.FullName, "game", "content");
        throw new DirectoryNotFoundException();
    }
    static uint Car(BridgeHost host) => Call(host, "worldVehicles", new { vehicles = new[] { new {
        typeId = 1, position = new[] { 0f, 0f, -1f }, seatOffsets = new[] { new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f } }, health = 6000,
        storage = new { trunkWidth = 6, trunkHeight = 4, gloveboxWidth = 3, gloveboxHeight = 2,
            seats = new[] { new { width = 4, height = 2, label = "Driver's seat" }, new { width = 4, height = 2, label = "Front passenger" } } }
    } } }).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();

    [Test]
    public void UdpAcceptedCabinLabelsRemainAfterEventAckAndCloseAfterExit()
    {
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)reservation.LocalEndPoint!).Port; reservation.Close();
        using var host = new BridgeHost(); using var remote = new BridgeHost();
        Call(host, "init", new { contentRoot = Content(), listenPort = port, spawn = new[] { 0, 0, 0 } });
        uint car = Car(host);
        Call(remote, "init", new { contentRoot = Content(), connectHost = "127.0.0.1", connectPort = port }); Pump(host, remote, 40);
        Action(remote, "openVehicleStorage", new { vehicleNetId = car, kind = 1 }); Pump(host, remote);
        Assert.That(State(remote).GetProperty("storage").GetProperty("revision").GetUInt64(), Is.Zero, "unseated request is not an accepted open");
        Action(remote, "enterVehicle", new { netId = car, seat = 1 }); Pump(host, remote);
        Action(remote, "openVehicleStorage", new { vehicleNetId = car, kind = 1 }); Pump(host, remote);
        var state = State(remote); var view = state.GetProperty("storage");
        Assert.That(view.GetProperty("label").GetString(), Is.EqualTo("Glovebox"));
        Assert.That(view.GetProperty("compartments")[1].GetProperty("label").GetString(), Is.EqualTo("Front passenger"));
        Assert.That(view.GetProperty("compartments")[1].GetProperty("page").GetByte(), Is.EqualTo(11));
        Assert.That(state.GetProperty("inventory").GetProperty("pages")[11].GetProperty("width").GetByte(), Is.EqualTo(4));
        Call(remote, "snapshot", new { ackEvents = state.GetProperty("events").EnumerateArray().Max(e => e.GetProperty("seq").GetInt64()) });
        Assert.That(State(remote).GetProperty("storage").GetProperty("netId").GetUInt32(), Is.GreaterThan(0));
        Assert.That(State(host).GetProperty("storage").GetProperty("netId").GetUInt32(), Is.Zero, "owner-only view");
        Action(remote, "exitVehicle", new { }); Pump(host, remote);
        state = State(remote);
        Assert.That(state.GetProperty("storage").GetProperty("netId").GetUInt32(), Is.Zero);
        Assert.That(state.GetProperty("storage").GetProperty("compartments").GetArrayLength(), Is.Zero);
        Assert.That(state.GetProperty("inventory").GetProperty("pages")[11].GetProperty("width").GetByte(), Is.Zero);
    }

    [Test]
    public void BridgeSaveLoadKeepsTrunkContentsWithoutLeakingAnOpenViewIntoTheOwner()
    {
        using var host = new BridgeHost(); Call(host, "init", new { contentRoot = Content(), spawn = new[] { 0, 0, 0 }, starterKit = true });
        uint car = Car(host);
        Action(host, "openVehicleStorage", new { vehicleNetId = car, kind = 0 }); Pump(host);
        Action(host, "moveItem", new { page0 = 0, x0 = 0, y0 = 0, page1 = 7, x1 = 0, y1 = 0, rot1 = 0 }); Pump(host);
        var original = State(host).GetProperty("inventory").GetProperty("pages")[7].GetProperty("items");
        Assert.That(original.GetArrayLength(), Is.EqualTo(1));
        string file = Path.Combine(Path.GetTempPath(), $"bevy-vehicle-storage-{Guid.NewGuid():N}.json");
        try
        {
            Call(host, "save", new { path = file }); Call(host, "load", new { path = file }); Pump(host);
            var state = State(host);
            Assert.That(state.GetProperty("storage").GetProperty("netId").GetUInt32(), Is.Zero);
            Assert.That(state.GetProperty("inventory").GetProperty("pages")[7].GetProperty("items").GetArrayLength(), Is.Zero);
            car = state.GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
            Action(host, "openVehicleStorage", new { vehicleNetId = car, kind = 0 }); Pump(host);
            var items = State(host).GetProperty("inventory").GetProperty("pages")[7].GetProperty("items");
            Assert.That(items.GetArrayLength(), Is.EqualTo(1));
            Assert.That(items[0].GetProperty("id").GetUInt16(), Is.EqualTo(original[0].GetProperty("id").GetUInt16()));
            Assert.That(items[0].GetProperty("gunAmmo").GetInt32(), Is.EqualTo(original[0].GetProperty("gunAmmo").GetInt32()));
        }
        finally { File.Delete(file); }
    }
}
