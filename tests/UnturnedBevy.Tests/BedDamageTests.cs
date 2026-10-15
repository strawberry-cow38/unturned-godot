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
public sealed class BedDamageTests
{
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
    static JsonElement Call(BridgeHost host, string op, object args = null) => JsonSerializer.SerializeToElement(
        host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), JsonOptions);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), JsonOptions);
    static void Action(BridgeHost host, string name, object args = null) => Call(host, "action", new { name, args = args ?? new { } });
    static NetWorldServer Server(BridgeHost host) => (NetWorldServer)typeof(BridgeHost)
        .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
    static void Pump(BridgeHost host, int count = 8, BridgeHost remote = null)
    {
        for (int i = 0; i < count; i++) { if (remote != null) Call(remote, "tick"); Call(host, "tick"); if (remote != null) Thread.Sleep(1); }
    }
    static object Spec => new {
        beds = new[] { new { netId = 1, position = new[] { 0f, 0f, 0f }, yaw = 0, geometryKey = "bed-one", seatNetId = 1 } },
        seats = new[] { new { netId = 1, position = new[] { 0f, .45f, 1f } } },
    };
    static readonly float[][] Vertices = [
        [-.5f,0,-1], [.5f,0,-1], [.5f,.45f,-1], [-.5f,.45f,-1],
        [-.5f,0,1], [.5f,0,1], [.5f,.45f,1], [-.5f,.45f,1],
    ];
    static readonly int[] Indices = [0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5];
    static void Geometry(BridgeHost host) => Call(host, "worldDynamicGeometry", new {
        key = "bed-one", meshes = new[] { new { vertices = Vertices, indices = Indices } },
    });
    static BridgeHost Init()
    {
        var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Root(), spawn = new[] { 0, 0, 3 } });
        Call(host, "worldServices", Spec); Geometry(host); Pump(host);
        return host;
    }
    static bool Ray(BridgeHost host) => Call(host, "worldRay", new {
        from = new[] { 0f, .2f, 3f }, to = new[] { 0f, .2f, -3f },
    }).GetProperty("hit").GetBoolean();

    [Test]
    public void SourceTwoHundredHealthRetiresClaimAndChildSeatAndLateUploadsCannotReviveIt()
    {
        using var host = Init();
        var server = Server(host);
        Action(host, "claimBed", new { netId = 1 }); Pump(host);
        Action(host, "sitSeat", new { netId = 1 }); Pump(host);
        ushort player = State(host).GetProperty("playerId").GetUInt16();
        Assert.That(server.Interactables.TryGetSpawn(player, out _, out _), Is.True);
        Assert.That(server.Interactables.SeatOccupant(1), Is.EqualTo(player));
        bool Damage(float amount) => server.Combat.DamageBarricadeAlong(new(0,.2f,3), new(0,.2f,-3), amount, server.Session.CurrentTick);
        Assert.That(Damage(-5), Is.True);
        Assert.That(Damage(199), Is.True); Assert.That(server.Interactables.BedCount, Is.EqualTo(1));
        Call(host, "worldServices", Spec); // Does not heal the fixture.
        Assert.That(Damage(1), Is.True);
        Assert.That(server.Interactables.BedCount, Is.Zero);
        Assert.That(server.Interactables.SeatCount, Is.Zero);
        Assert.That(server.Interactables.TryGetSpawn(player, out _, out _), Is.False);
        Assert.That(Ray(host), Is.False, "the host collider retires before the next native update");
        Call(host, "worldServices", Spec); Geometry(host);
        Assert.That(server.Interactables.BedCount, Is.Zero);
        Assert.That(server.Interactables.SeatCount, Is.Zero);
        Assert.That(Ray(host), Is.False, "an in-flight native upload cannot resurrect a destroyed fixture");
        Pump(host);
        Assert.That(State(host).GetProperty("interactables").GetProperty("beds").GetArrayLength(), Is.Zero);
        Assert.That(State(host).GetProperty("interactables").GetProperty("seats").GetArrayLength(), Is.Zero);
    }

    [Test]
    public void FirstStaticOrOtherDynamicColliderBlocksBedDamageAndRebasePreservesItsIdentity()
    {
        using var host = Init(); var server = Server(host);
        var wall = new[] { new[] { -2f,-1f,2f }, new[] { 2f,-1f,2f }, new[] { 0f,3f,2f } };
        Call(host, "worldGeometry", new { meshes = new[] { new { vertices = wall } } }); Geometry(host);
        Assert.That(server.Combat.DamageBarricadeAlong(new(0,.2f,3),new(0,.2f,-3),500,0), Is.False);
        Assert.That(server.Interactables.BedCount, Is.EqualTo(1));
        Call(host, "worldGeometry", new { replace = true, meshes = Array.Empty<object>() }); Geometry(host);
        Call(host, "worldDynamicGeometry", new { key = "opaque-wall", meshes = new[] { new { vertices = wall } } });
        Assert.That(server.Combat.DamageBarricadeAlong(new(0,.2f,3),new(0,.2f,-3),500,0), Is.False);
        Call(host, "worldDynamicGeometry", new { key = "opaque-wall", meshes = Array.Empty<object>() });
        Call(host, "rebase", new { shift = new[] { 256, 0, 0 }, expectedEpoch = 0 });
        Assert.That(server.Combat.DamageBarricadeAlong(new(-256,.2f,3),new(-256,.2f,-3),200,0), Is.True);
        Assert.That(server.Interactables.BedCount, Is.Zero);
    }

    [Test]
    public void ActualRemoteAcceptedGunfireUsesVehicleDamageAndReplicatesBedAndSeatRemoval()
    {
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback,0));
        ushort port = (ushort)((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Close();
        using var host = new BridgeHost(); using var remote = new BridgeHost();
        Call(host,"init",new{contentRoot=Root(),spawn=new[]{0,0,3},listenPort=port,mapId="Fixture",allowCheats=true});
        Call(host,"worldServices",Spec);Geometry(host);
        Call(remote,"init",new{contentRoot=Root(),connectHost="127.0.0.1",connectPort=port,mapId="Fixture"});Pump(host,20,remote);
        Action(host,"console",new{text="teleport 3 0 0"});Pump(host,10,remote);
        Action(host,"claimBed",new{netId=1});Action(host,"sitSeat",new{netId=1});Pump(host,10,remote);
        // The seated host really occupies the bed's end now. Shoot from the side
        // so its authoritative player capsule does not intercept the bed shot.
        Action(remote,"console",new{text="teleport 3 0 0"});Pump(host,10,remote);
        Action(remote,"console",new{text="give 4"});Pump(host,10,remote);
        var page = State(remote).GetProperty("inventory").GetProperty("pages").EnumerateArray()
            .First(p=>p.GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("id").GetInt32()==4));
        var item=page.GetProperty("items").EnumerateArray().First(i=>i.GetProperty("id").GetInt32()==4);
        Action(remote,"equipAndHold",new{page=page.GetProperty("index").GetInt32(),x=item.GetProperty("x").GetInt32(),y=item.GetProperty("y").GetInt32()});Pump(host,80,remote);
        var direction = new Vector3(-3, .225f-1.75f, 0).normalized;
        for(int shot=1;shot<=6;shot++)
        {
            Action(remote,"fire",new{origin=new[]{3f,1.75f,0f},dir=new[]{direction.x,direction.y,direction.z}});Pump(host,12,remote);
            Assert.That(Server(host).Interactables.BedCount,Is.EqualTo(shot<6?1:0),"Eaglefire Vehicle_Damage35 against Bed.Health200");
        }
        Assert.That(State(remote).GetProperty("weapon").GetProperty("ammo").GetInt32(),Is.EqualTo(24));
        Assert.That(State(remote).GetProperty("interactables").GetProperty("beds").GetArrayLength(),Is.Zero);
        Assert.That(State(remote).GetProperty("interactables").GetProperty("seats").GetArrayLength(),Is.Zero);
        Assert.That(Server(host).Interactables.TryGetSpawn(1,out _,out _),Is.False);
        Assert.That(Ray(host),Is.False);
    }
}
