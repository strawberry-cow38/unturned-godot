using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class PowerGraphViewTests
{
    static JsonElement Call(BridgeHost host, string operation, object value = null) =>
        JsonSerializer.SerializeToElement(host.Handle(operation, JsonSerializer.SerializeToElement(value ?? new { })));

    [Test]
    public void RealUdpSwitchToggleControlsAcceptedReadOnlyPowerGraphAndRefusesBurningSwitch()
    {
        string content = null;
        for (var p = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); p != null; p = p.Parent)
            if (Directory.Exists(Path.Combine(p.FullName, "game/content"))) { content = Path.Combine(p.FullName, "game/content"); break; }
        Assert.That(content, Is.Not.Null);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.LocalEndPoint).Port;
        socket.Close();
        using var host = new BridgeHost();
        using var peer = new BridgeHost();
        Call(host, "init", new { contentRoot = content, spawn = new[] { 0, 5, 2 }, listenPort = port });
        Call(peer, "init", new { contentRoot = content, connectHost = "127.0.0.1", connectPort = port });
        var server = (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var graph = server.Deployables;
        long tick = server.Session.CurrentTick;
        var gen = graph.ServerPlace(server.Ids.Mint(), 458, 0, new Vector3(-1, 5, 0), 0, tick);
        var gate = graph.ServerPlace(server.Ids.Mint(), 9105, 0, new Vector3(0, 5, 0), 0, tick);
        var lamp = graph.ServerPlace(server.Ids.Mint(), 459, 0, new Vector3(1, 5, 0), 0, tick);
        Assert.That(graph.Schema.TryGet(9105, out var def) && def.IsSwitch, Is.True, "actual original source schema must carry IsSwitch");
        byte Port(ushort id, DeployableDef.PortKind kind) => checked((byte)Array.FindIndex(DeployableDef.ById(id).Ports, p => p.Kind == kind));
        byte genOut = Port(458, DeployableDef.PortKind.Output), switchIn = Port(9105, DeployableDef.PortKind.Consumer);
        byte switchOut = Port(9105, DeployableDef.PortKind.Passthrough), lampIn = Port(459, DeployableDef.PortKind.Consumer);
        graph.ServerConnectWire(server.Ids.Mint(), gen.NetIdValue, genOut, gate.NetIdValue, switchIn, tick);
        graph.ServerConnectWire(server.Ids.Mint(), gate.NetIdValue, switchOut, lamp.NetIdValue, lampIn, tick);
        graph.ServerToggle(gen.NetIdValue, true, tick);
        void Pump()
        {
            for (int i = 0; i < 24; i++) { Call(peer, "tick"); Call(host, "tick"); Thread.Sleep(1); }
        }
        JsonElement Lamp(JsonElement view) => view.GetProperty("devices").EnumerateArray()
            .Single(d => d.GetProperty("netId").GetUInt32() == lamp.NetIdValue).GetProperty("ports")[lampIn];
        void Toggle(bool on) => Call(peer, "action", new { name = "toggleDeployable", args = new { netId = gate.NetIdValue, on } });
        Pump();
        var first = Call(peer, "powerView");
        Assert.That(first.GetProperty("version").GetInt32(), Is.EqualTo(1));
        Assert.That(first.GetProperty("readOnly").GetBoolean(), Is.True);
        Assert.That(first.GetProperty("stateHash").GetUInt64(), Is.EqualTo(
            JsonSerializer.SerializeToElement(peer.State()).GetProperty("powerStateHash").GetUInt64()),
            "native cache key must describe the accepted client, not host truth");
        Assert.That(first.GetProperty("wires").GetArrayLength(), Is.EqualTo(2));
        Assert.That(Lamp(first).GetProperty("powered").GetBoolean(), Is.True);
        ulong hash = graph.StateHash(); long before = server.Session.CurrentTick;
        Call(peer, "powerView"); Call(host, "powerView");
        Assert.That(graph.StateHash(), Is.EqualTo(hash), "view must never mutate authoritative scalars/topology");
        Assert.That(server.Session.CurrentTick, Is.EqualTo(before), "view must not tick the simulation");
        Toggle(false);
        Assert.That(gate.ToggledOn, Is.True, "request waits for authority");
        Pump();
        Assert.That(gate.ToggledOn, Is.False);
        Assert.That(Call(peer, "powerView").GetProperty("stateHash").GetUInt64(),
            Is.Not.EqualTo(first.GetProperty("stateHash").GetUInt64()), "accepted changes invalidate native power view");
        Assert.That(Lamp(Call(peer, "powerView")).GetProperty("powered").GetBoolean(), Is.False,
            "actual remote accepted circuit must go dark through the original solver");
        Toggle(true); Pump();
        Assert.That(Lamp(Call(peer, "powerView")).GetProperty("powered").GetBoolean(), Is.True);
        graph.ServerSetScalars(gate.NetIdValue, gate.Health, 0, true, server.Session.CurrentTick);
        Pump(); Toggle(false); Pump();
        Assert.That(gate.ToggledOn, Is.True, "server must reject toggling the burning switch");
        Assert.That(Lamp(Call(peer, "powerView")).GetProperty("powered").GetBoolean(), Is.False);
    }
}
