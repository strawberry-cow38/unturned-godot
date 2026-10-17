using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

/// <summary>Row 15A source parity for the zombie horde over actual UDP. The source builds
/// <c>ZombieChunkField{Player=player}</c> only for the SP/listen player (WorldBuilder.cs:2370/2869), the
/// dedicated server builds none and a joined client renders none. So on a listen host the horde anchors,
/// sees, hears and is hurt only through the listen owner, and its System3 rows reach only that owner's
/// client.</summary>
[TestFixture, NonParallelizable]
public sealed class ZombieParityTests
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() } };
    BridgeHost _host, _remote; ushort _port; string _map, _content;
    static JsonElement Call(BridgeHost h, string op, object request = null) =>
        JsonSerializer.SerializeToElement(h.Handle(op, JsonSerializer.SerializeToElement(request ?? new { }, Json)), Json);
    static JsonElement State(BridgeHost h) => JsonSerializer.SerializeToElement(h.State(false), Json);
    static void Action(BridgeHost h, string name, object args) => Call(h, "action", new { name, args });
    void Pump(int n = 12) { for (int i = 0; i < n; i++) { Call(_remote, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    T Field<T>(string name) => (T)typeof(BridgeHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);
    NetWorldServer Server => Field<NetWorldServer>("_server");
    ZombieServices Zombies => Field<ZombieServices>("_zombies");
    JsonElement Summary() => JsonSerializer.SerializeToElement(Zombies.Summary(), Json);

    [SetUp]
    public void Setup()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port; }
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "game/content"))) root = root.Parent;
        _content = Path.Combine(root!.FullName, "game/content");
        _map = Path.Combine(Path.GetTempPath(), "zombie-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_map);
        _host = new(); _remote = new();
        Call(_host, "init", new { contentRoot = _content, mapId = "Zombie_Parity", spawn = new[] { 20f, 2, 20 }, listenPort = _port, allowCheats = true });
        Call(_host, "worldGeometry", new { meshes = new[] { new { terrain = true, vertices = new[] {
            new[] { -2000f, 1, -2000 }, new[] { 2000f, 1, -2000 }, new[] { 2000f, 1, 2000 },
            new[] { -2000f, 1, -2000 }, new[] { 2000f, 1, 2000 }, new[] { -2000f, 1, 2000 } } } } });
        Call(_remote, "init", new { contentRoot = _content, connectHost = "127.0.0.1", connectPort = _port, name = "Remote" });
        Pump(24);
    }
    [TearDown]
    public void Cleanup() { _remote.Dispose(); _host.Dispose(); try { Directory.Delete(_map, true); } catch (IOException) { } }

    void Field(params float[][] points) => Call(_host, "worldZombies", new { mapRoot = _map, points });
    static int Rows(BridgeHost h) => State(h).TryGetProperty("zombies", out var z) && z.ValueKind == JsonValueKind.Array ? z.GetArrayLength() : 0;

    [Test]
    public void TheListenOwnersHordeIsUnchangedAndOnlyItsOwnClientReceivesIt()
    {
        Field([24, 2, 24], [26, 2, 22], [22, 2, 26]);
        Pump(120);
        Assert.That(Summary().GetProperty("bodies").GetInt32(), Is.GreaterThan(0), "the owner's nearby chunk is HOT");
        Assert.That(Rows(_host), Is.GreaterThan(0), "the listen owner's own client still receives System3");
        Assert.That(Rows(_remote), Is.Zero, "a joined client is sent no zombie rows (the source publishes none)");
    }

    [Test]
    public void TheHordeAnchorsOnTheListenOwnerNotOnRemotes()
    {
        Field([620, 2, 620], [622, 2, 618], [618, 2, 622]);
        Action(_remote, "console", new { text = "teleport 620 2 600" }); Pump(120);
        Assert.That(Server.Players.All.Any(p => (p.Pos - new UnityEngine.Vector3(620, 2, 600)).magnitude < 5), Is.True, "the remote stands in the field");
        Assert.That(Summary().GetProperty("bodies").GetInt32(), Is.Zero, "a remote player never streams the horde");
        Action(_host, "console", new { text = "teleport 620 2 600" }); Pump(120);
        Assert.That(Summary().GetProperty("bodies").GetInt32(), Is.GreaterThan(0), "the listen owner does");
    }

    [Test]
    public void ARemotesShotPassesWhereAZombieStandsAndItsGunfireDoesNotLure()
    {
        Field([900, 2, 900]);   // a registered field far away: the clock runs, no chunk is near
        Server.Combat.PvPEnabled = false;   // both players stand on the same spawn and fire the same line
        // A HOT body 3 m ahead of the spawn, and a vehicle panel behind it.
        var bodies = (Dictionary<uint, ZombieBodyState>)typeof(ZombieServices).GetField("_bodies", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Zombies)!;
        uint zombie = Server.Ids.Mint().Value;
        bodies.Add(zombie, new ZombieBodyState { NetId = zombie, GlobalPosition = new Godot.Vector3(20, 2, 17), Health = 100 });
        Server.Zombies.ServerSpawn(new NetId(zombie), 0, new UnityEngine.Vector3(20, 2, 17), Server.Session.CurrentTick);
        uint car = Call(_host, "worldVehicles", new { replace = false, vehicles = new[] { new { typeId = 3, position = new[] { 20f, 2, 14 },
            seatCount = 4, health = 900, healthMax = 1000, fuel = 1000, battery = 10000, speedMax = 30 } } })
            .GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        Call(_host, "worldDynamicGeometry", new { key = "car", meshes = new[] { new {
            vertices = new[] { new[] { 18f, 1f, 14f }, new[] { 22f, 1f, 14f }, new[] { 22f, 6f, 14f }, new[] { 18f, 6f, 14f } },
            indices = new[] { 0, 1, 2, 0, 2, 3 }, vehicleNetId = car } } });
        Pump();
        foreach (var h in new[] { _host, _remote })
        {
            Action(h, "console", new { text = "give 4" }); Pump();
            var at = State(h).GetProperty("inventory").GetProperty("pages").EnumerateArray()
                .SelectMany(p => p.GetProperty("items").EnumerateArray().Select(i => (page: p.GetProperty("index").GetInt32(), item: i)))
                .First(x => x.item.GetProperty("id").GetInt32() == 4);
            Action(h, "equipAndHold", new { page = at.page, x = at.item.GetProperty("x").GetInt32(), y = at.item.GetProperty("y").GetInt32() });
            Pump(24);
        }
        float Car() => Server.Vehicles.TryGet(car, out var v) ? v.Health : -1;
        float before = Car();
        Action(_remote, "fire", new { origin = new[] { 20f, 3.6f, 20f }, dir = new[] { 0f, 0f, -1f } }); Pump(24);
        Assert.That(bodies[zombie].Health, Is.EqualTo(100), "the remote's round is not absorbed by the owner's zombie");
        Assert.That(Car(), Is.LessThan(before), "it carries on into the car behind");
        Assert.That(Summary().GetProperty("alert").GetBoolean(), Is.False, "a remote's gunfire does not lure the horde");

        float afterRemote = Car();
        Action(_host, "fire", new { origin = new[] { 20f, 3.6f, 20f }, dir = new[] { 0f, 0f, -1f } }); Pump(24);
        Assert.That(bodies[zombie].Health, Is.LessThan(100), "the listen owner's round still hits it");
        Assert.That(Car(), Is.EqualTo(afterRemote), "and stops there");
        Assert.That(Summary().GetProperty("alert").GetBoolean(), Is.True, "the owner's gunfire is heard");
    }
}
