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
public sealed class ResourceContactTests
{
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };
    BridgeHost _host, _remote;
    ushort _port;
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
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);
    static JsonElement[] Contacts(BridgeHost host) => State(host).GetProperty("events").EnumerateArray()
        .Where(e => e.GetProperty("kind").GetString() is "resourceHit" or "resourceFelled").ToArray();
    void Pump(int count = 16)
    { for (int i = 0; i < count; i++) { Call(_remote, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    void Join(BridgeHost remote) => Call(remote, "init", new {
        contentRoot = Root(), connectHost = "127.0.0.1", connectPort = _port,
    });
    [SetUp] public void Setup()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port; }
        _host = new(); _remote = new();
        Call(_host, "init", new { contentRoot = Root(), listenPort = _port, starterKit = true, allowCheats = true, spawn = new[] { 0, 0, 0 } });
        Join(_remote); Pump();
        Action(_remote, "console", new { text = "teleport 10 0 0" }); Pump();
    }
    [TearDown] public void Cleanup() { _remote.Dispose(); _host.Dispose(); }

    [Test]
    public void RealBulletPublishesOneHitAndFallToRemoteAndLocalWithoutChangingRewardTiming()
    {
        Call(_host, "worldServices", new { resourceCount = 1, resources = new[] {
            new { index = 0, kind = "tree", name = "Birch_0", position = new[] { 0, 0, -2 }, health = 1, resetSeconds = 60, trunkLength = 8.25f }
        } });
        Call(_host, "worldGeometry", new { meshes = new[] { new { resourceIndex = 0,
            vertices = new[] { new[] { -2, 0, -2 }, new[] { 2, 0, -2 }, new[] { 0, 4, -2 } } } } });
        Pump();
        Action(_host, "fire"); Pump();
        var local = Contacts(_host); var remote = Contacts(_remote);
        Assert.That(local.Select(e => e.GetProperty("kind").GetString()), Is.EqualTo(new[] { "resourceHit", "resourceFelled" }), "The local wire echo must not duplicate direct accepted facts");
        Assert.That(remote.Select(e => e.GetProperty("kind").GetString()), Is.EqualTo(new[] { "resourceHit", "resourceFelled" }));
        var fall = remote[1].GetProperty("data");
        Assert.That(fall.GetProperty("index").GetInt32(), Is.Zero); Assert.That(fall.GetProperty("kind").GetString(), Is.EqualTo("tree"));
        Assert.That(fall.GetProperty("trunkLength").GetSingle(), Is.EqualTo(8.25f));
        Assert.That(fall.GetProperty("debrisSeconds").GetSingle(), Is.EqualTo(ResourceRules.DebrisLife));
        Assert.That(fall.GetProperty("acceptedTick").GetUInt32(), Is.EqualTo(local[1].GetProperty("data").GetProperty("acceptedTick").GetUInt32()));
        Assert.That(remote[1].GetProperty("tick").GetUInt32(), Is.EqualTo(fall.GetProperty("acceptedTick").GetUInt32()));
        Assert.That(fall.GetProperty("ageSeconds").GetDouble(), Is.GreaterThanOrEqualTo(0));
        Assert.That(fall.GetProperty("direction")[2].GetSingle(), Is.LessThan(-.99));
        Assert.That(State(_remote).GetProperty("resources").GetProperty("dead").EnumerateArray().Select(i => i.GetInt32()), Is.EqualTo(new[] { 0 }));
        Assert.That(State(_remote).GetProperty("worldItems").GetArrayLength(), Is.Zero, "The remote effect does not create rewards");

        using var late = new BridgeHost(); Join(late);
        for (int n = 0; n < 24; n++) { Call(late, "tick"); Pump(1); }
        Assert.That(State(late).GetProperty("resources").GetProperty("dead").GetArrayLength(), Is.EqualTo(1));
        Assert.That(Contacts(late), Is.Empty, "A late join has no historical fall direction or animation");
        Pump(550);
        Assert.That(State(_host).GetProperty("worldItems").GetArrayLength(), Is.EqualTo(7), "Original index-zero Birch rewards run once after the source timer");
        Assert.That(State(_remote).GetProperty("worldItems").GetArrayLength(), Is.EqualTo(7));
        Assert.That(Contacts(_remote).Length, Is.EqualTo(2)); Assert.That(Contacts(_host).Length, Is.EqualTo(2));
    }

    [Test]
    public void OreContactKeepsSourceToolGateAndHasNoInventedFallingLifetime()
    {
        Call(_host, "worldServices", new { resourceCount = 1, resources = new[] {
            new { index = 0, kind = "ore", name = "Metal_0", position = new[] { 0, 0, -2 }, health = 100, resetSeconds = 60 }
        } });
        void Damage(string weapon, bool melee) => Server.Combat.DamageResource(new CombatResourceHit(0, 1, 999, 999, melee,
            weapon, new(0, 1, -2), new(0, 0, -1), Server.Session.CurrentTick));
        Damage("eaglefire", false); Pump();
        var hit = Contacts(_remote).Single().GetProperty("data");
        Assert.That(hit.GetProperty("damage").GetSingle(), Is.Zero); Assert.That(hit.GetProperty("health").GetSingle(), Is.EqualTo(100));
        Assert.That(Server.Resources.IsAlive(0), Is.True);
        Damage("axe_pick", true); Pump();
        var contacts = Contacts(_remote);
        Assert.That(contacts.Length, Is.EqualTo(3));
        Assert.That(contacts[1].GetProperty("data").GetProperty("damage").GetSingle(), Is.EqualTo(100), "Original Resource_Damage overrides the arbitrary caller amount");
        var fall = contacts[2].GetProperty("data");
        Assert.That(fall.GetProperty("kind").GetString(), Is.EqualTo("ore")); Assert.That(fall.GetProperty("debrisSeconds").GetSingle(), Is.Zero);
        Assert.That(State(_remote).GetProperty("worldItems").GetArrayLength(), Is.EqualTo(2));
        Assert.Throws<InvalidOperationException>(() => Call(_remote, "worldServices", new { resourceCount = 0 }));
    }

    [Test]
    public void AcceptedAgeHandlesSnapshotLagAndTickWrapWithoutRestartingOldFalls()
    {
        Assert.That(ResourceEvents.Age(100, 200), Is.EqualTo(2).Within(.00001));
        Assert.That(ResourceEvents.Age(200, 100), Is.Zero, "Reliable event may arrive before its snapshot");
        Assert.That(ResourceEvents.Age(uint.MaxValue - 10, 9), Is.EqualTo(.4).Within(.00001));
        Assert.That(ResourceEvents.Age(9, uint.MaxValue - 10), Is.Zero);
    }
}
