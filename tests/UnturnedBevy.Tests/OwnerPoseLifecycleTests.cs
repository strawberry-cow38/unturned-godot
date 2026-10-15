using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class OwnerPoseLifecycleTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() } };
    static string Root()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException("game/content");
    }
    static JsonElement Call(BridgeHost host, string op, object args = null) => JsonSerializer.SerializeToElement(
        host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), Options);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), Options);
    static void Action(BridgeHost host, string name, object args = null) => Call(host, "action", new { name, args = args ?? new { } });
    static NetWorldServer Server(BridgeHost host) => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
    static void Pump(BridgeHost host, int count = 12, object input = null)
    { for (int n = 0; n < count; n++) Call(host, "tick", input == null ? null : new { input }); }
    static float[] Position(BridgeHost host) => State(host).GetProperty("player").GetProperty("position").EnumerateArray().Select(v => v.GetSingle()).ToArray();
    static int Corrections(BridgeHost host) => State(host).GetProperty("player").GetProperty("correctionVersion").GetInt32();

    [TestCase(1.5f)]
    [TestCase(80f)]
    public void LoadedOwnerPoseArrivesBeforeAnyOldPositionCanBePublished(float distance)
    {
        using var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Root(), spawn = new[] { 10f, 3f, -4f }, yaw = 37f, starterKit = true });
        Assert.That(Position(host), Is.EqualTo(new[] { 10f, 3f, -4f }));
        Assert.That(State(host).GetProperty("player").GetProperty("yaw").GetSingle(), Is.EqualTo(37f));
        string path = Path.Combine(Path.GetTempPath(), $"bevy-owner-pose-{Guid.NewGuid():N}.json");
        try
        {
            Call(host, "save", new { path });
            Action(host, "console", new { text = $"teleport {10 + distance} 6 -4" }); Pump(host);
            Call(host, "simulate", new { input = new { yaw = 120f } }); Pump(host);
            Assert.That(Position(host)[0], Is.EqualTo(10 + distance).Within(.01));
            Call(host, "load", new { path });
            Assert.That(Position(host), Is.EqualTo(new[] { 10f, 3f, -4f }), "Stale pre-load state must not overwrite restored state during the join handshake");
            Assert.That(State(host).GetProperty("player").GetProperty("yaw").GetSingle(), Is.EqualTo(37f).Within(.1));
            int correction = Corrections(host); Pump(host, 30);
            Assert.That(Position(host), Is.EqualTo(new[] { 10f, 3f, -4f })); Assert.That(Corrections(host), Is.EqualTo(correction));
            Assert.That(State(host).GetProperty("inventory").GetProperty("pages")[0].GetProperty("items")[0].GetProperty("id").GetInt32(), Is.EqualTo(4));
        }
        finally { File.Delete(path); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AcceptedPropSeatOwnsItsSourceAnchorWithoutWalkingCorrectionLoop(bool recline)
    {
        using var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Root(), spawn = new[] { 0, 0, 0 }, starterKit = true }); Pump(host, 20);
        float foot = recline ? .6f : .6f - .422f;
        var position = new[] { 1f, foot, -2.02f };
        Call(host, "worldServices", new { seats = new[] {
            new { netId = 394, position = new[] { 1f, .6f, -2.02f }, face = new[] { -1, 0, 0 }, groundY = 0, recline }
        } });
        Action(host, "sitSeat", new { netId = 394 }); Pump(host);
        Assert.That(State(host).GetProperty("interactables").GetProperty("seats")[0].GetProperty("occupant").GetInt32(), Is.EqualTo(1));
        var input = new { position, velocity = new[] { 0, 0, 0 }, grounded = false, stance = "sitting", heldItemId = 4 };
        Pump(host, 12, input); int correction = Corrections(host); Pump(host, 80, input);
        Assert.That(Corrections(host), Is.EqualTo(correction), "An accepted 2.26m seat transition is not a walking claim");
        var server = Server(host); Assert.That(server.Players.TryGetByOwner(1, out var player), Is.True);
        Assert.That(player.Pos.x, Is.EqualTo(1).Within(.01)); Assert.That(player.Pos.y, Is.EqualTo(foot).Within(.01));
        Assert.That(player.Pos.z, Is.EqualTo(-2.02).Within(.01)); Assert.That(player.YawDegrees, Is.EqualTo(90).Within(.1));
        Action(host, "fire", new { origin = new[] { 1f, foot + 1.6f, -2.02f }, dir = new[] { -1, 0, 0 } }); Pump(host, 12, input);
        Assert.That(State(host).GetProperty("weapon").GetProperty("ammo").GetInt32(), Is.EqualTo(29), "Seat authority still carries accepted gunfire");
        Action(host, "sitSeat", new { netId = 0 }); Pump(host);
        var exit = new[] { -.1f, .1f, -2.02f };
        Pump(host, 20, new { position = exit, velocity = new[] { 0, 0, 0 }, grounded = true });
        Assert.That(player.Pos.x, Is.EqualTo(-.1).Within(.01)); Assert.That(player.Pos.y, Is.EqualTo(.1).Within(.01));
        Assert.That(Corrections(host), Is.EqualTo(correction), "Ordinary source stand-up resumes walking from the seat anchor");
    }

    [Test]
    public void AcceptedSeatAnchorMovesWithOriginAndDoesNotSurviveSessionReplacement()
    {
        using var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Root(), spawn = new[] { 1024, 0, 0 } });
        Call(host, "worldServices", new { seats = new[] {
            new { netId = 7, position = new[] { 1025f, .6f, -2f }, recline = true }
        } });
        Action(host, "sitSeat", new { netId = 7 }); Pump(host);
        int correction = Corrections(host);
        Call(host, "rebase", new { expectedEpoch = 0, shift = new[] { 1024, 0, 0 } }); Pump(host, 30);
        Assert.That(Position(host)[0], Is.EqualTo(1).Within(.01));
        Assert.That(Corrections(host), Is.EqualTo(correction));
        Assert.That(Server(host).Players.TryGetByOwner(1, out var owner), Is.True);
        Assert.That(owner.Pos.x, Is.EqualTo(1).Within(.01), "The authority callback uses the rebased anchor too");
        Call(host, "init", new { contentRoot = Root(), spawn = new[] { -8, 2, 3 }, yaw = 210f }); Pump(host);
        Assert.That(Position(host), Is.EqualTo(new[] { -8f, 2f, 3f }));
        Assert.That(State(host).GetProperty("interactables").GetProperty("seats").GetArrayLength(), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RemoteUdpOccupantReceivesOneSeatCorrectionAndBothObserversSeeSourcePose(bool recline)
    {
        ushort port;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        using var host = new BridgeHost(); using var remote = new BridgeHost();
        Call(host, "init", new { contentRoot = Root(), listenPort = port, spawn = new[] { 0, 0, 0 } });
        Call(host, "worldServices", new { seats = new[] {
            new { netId = 394, position = new[] { 1f, .6f, -2.02f }, face = new[] { -1, 0, 0 }, recline }
        } });
        Call(remote, "init", new { contentRoot = Root(), connectHost = "127.0.0.1", connectPort = port });
        void Step(int count) { for (int n = 0; n < count; n++) { Call(remote, "tick"); Call(host, "tick"); Thread.Sleep(1); } }
        Step(24); Action(remote, "sitSeat", new { netId = 394 }); Step(24);
        int correction = Corrections(remote); Step(100);
        Assert.That(correction, Is.GreaterThan(0)); Assert.That(Corrections(remote), Is.EqualTo(correction));
        float foot = recline ? .6f : .6f - BodyRules.PropSeatHipSeated;
        Assert.That(Position(remote)[0], Is.EqualTo(1).Within(.01)); Assert.That(Position(remote)[1], Is.EqualTo(foot).Within(.01));
        ushort id = State(remote).GetProperty("playerId").GetUInt16();
        var observed = State(host).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("playerId").GetUInt16() == id);
        Assert.That(observed.GetProperty("position")[0].GetSingle(), Is.EqualTo(1).Within(.01));
        Assert.That(observed.GetProperty("stance").GetInt32(), Is.EqualTo(MoveInput.WireStanceSitting));
        Action(remote, "sitSeat", new { netId = 0 }); Step(12);
        Call(remote, "tick", new { input = new { position = new[] { -.1f, .1f, -2.02f }, grounded = true } }); Step(20);
        Assert.That(Corrections(remote), Is.EqualTo(correction));
        Assert.That(Server(host).Players.TryGetByOwner(id, out var player), Is.True);
        Assert.That(player.Pos.x, Is.EqualTo(-.1).Within(.01));
    }

    [Test]
    public void DeathAndAcceptedVehicleBoardingReleaseFurnitureBeforeNextOwnerPublication()
    {
        using var host = new BridgeHost();
        Call(host, "init", new { contentRoot = Root(), spawn = new[] { 0, 0, 0 } });
        Call(host, "worldServices", new { seats = new[] {
            new { netId = 7, position = new[] { 1f, .6f, -2f }, recline = true }
        } });
        Action(host, "sitSeat", new { netId = 7 }); Pump(host);
        Assert.That(Server(host).Interactables.IsSeated(1), Is.True);
        Action(host, "console", new { text = "kill" }); Pump(host);
        Assert.That(Server(host).Interactables.IsSeated(1), Is.False, "Source death releases furniture without relying on a dead client's input");
        Assert.That(State(host).GetProperty("interactables").GetProperty("seats")[0].GetProperty("occupant").GetInt32(), Is.Zero);
        Action(host, "respawn"); Pump(host);
        Assert.That(Position(host)[0], Is.EqualTo(0).Within(.01));
        Action(host, "sitSeat", new { netId = 7 }); Pump(host);
        Assert.That(Position(host)[0], Is.EqualTo(1).Within(.01), "Re-entering the same ID after death gets a fresh correction");
        var registration = Call(host, "worldVehicles", new { vehicles = new[] {
            new { key = "seat-transfer", typeId = 1, position = new[] { 2f, .6f, -2f }, health = 300, healthMax = 300,
                seatOffsets = new[] { new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 1f } } }
        } });
        uint vehicle = registration.GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        Action(host, "enterVehicle", new { netId = vehicle, seat = 1 }); Pump(host);
        Assert.That(Server(host).VehicleHost.IsSeated(1), Is.True);
        Assert.That(Server(host).Interactables.IsSeated(1), Is.False, "Accepted vehicle occupancy retires the old furniture table entry");
        int correction = Corrections(host); Pump(host, 40);
        Assert.That(Corrections(host), Is.EqualTo(correction));
        Assert.That(Server(host).Players.TryGetByOwner(1, out var player), Is.True);
        Assert.That(player.Pos.x, Is.EqualTo(2).Within(.01));
    }
}
