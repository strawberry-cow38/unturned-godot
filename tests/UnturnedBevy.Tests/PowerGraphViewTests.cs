using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;
using SDG.Unturned;
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

    [Test]
    public void RealUdpPeerRendersHostAcceptedTurbineWindBatteryChargeAndGeneratorFuel()
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
        Assert.That(graph.Schema.TryGet(9106, out var turbineDef) && turbineDef.IsWindTurbine, Is.True, "original schema carries the turbine family");
        Assert.That(graph.Schema.TryGet(1450, out var batteryDef) && batteryDef.IsBattery && batteryDef.EnergyMax == 600f * 3600f
            && batteryDef.ChargeWatts == 600f, Is.True, "original battery capacity and charge rate");
        Assert.That(graph.Schema.TryGet(458, out var genDef) && genDef.Ports.Count(p => p.Role != 0) == 2, Is.True, "generator side triggers");
        float wind = 0.6f;
        server.Power.WindAt = _ => wind;   // the host's sampler, held fixed; the client never samples wind
        long tick = server.Session.CurrentTick;
        byte Port(ushort id, DeployableDef.PortKind kind) => checked((byte)Array.FindLastIndex(DeployableDef.ById(id).Ports, p => p.Kind == kind));
        var turbine = graph.ServerPlace(server.Ids.Mint(), 9106, 0, new Vector3(-4, 5, 0), 0, tick);
        var lampA = graph.ServerPlace(server.Ids.Mint(), 459, 0, new Vector3(-4, 5, 3), 0, tick);
        var gen = graph.ServerPlace(server.Ids.Mint(), 458, 0, new Vector3(0, 5, 0), 0, tick);
        var battery = graph.ServerPlace(server.Ids.Mint(), 1450, 0, new Vector3(2, 5, 0), 0, tick);
        var lampB = graph.ServerPlace(server.Ids.Mint(), 459, 0, new Vector3(4, 5, 0), 0, tick);
        var dry = graph.ServerPlace(server.Ids.Mint(), 458, 0, new Vector3(0, 5, 6), 0, tick, fuel: 1f);
        Assert.That(gen.Fuel, Is.EqualTo(150_000f), "source tank is carried unclipped");
        byte lampIn = Port(459, DeployableDef.PortKind.Consumer);
        graph.ServerConnectWire(server.Ids.Mint(), turbine.NetIdValue, Port(9106, DeployableDef.PortKind.Output), lampA.NetIdValue, lampIn, tick);
        graph.ServerConnectWire(server.Ids.Mint(), gen.NetIdValue, Port(458, DeployableDef.PortKind.Output), battery.NetIdValue, (byte)Array.FindIndex(DeployableDef.Battery.Ports, p => p.Kind == DeployableDef.PortKind.Consumer), tick);
        graph.ServerConnectWire(server.Ids.Mint(), battery.NetIdValue, Port(1450, DeployableDef.PortKind.Output), lampB.NetIdValue, lampIn, tick);
        graph.ServerToggle(gen.NetIdValue, true, tick);
        graph.ServerToggle(dry.NetIdValue, true, tick);
        // Host ticks last, so its newest snapshot is in flight; peer-only ticks then catch the peer up to the
        // host's present accepted state without advancing it.
        void Pump(int n = 24)
        {
            for (int i = 0; i < n; i++) { Call(peer, "tick"); Call(host, "tick"); Thread.Sleep(1); }
            for (int i = 0; i < 8; i++) { Call(peer, "tick"); Thread.Sleep(1); }
        }
        Dictionary<uint, JsonElement> Rows() => JsonSerializer.SerializeToElement(peer.State()).GetProperty("deployables")
            .EnumerateArray().ToDictionary(r => r.GetProperty("netId").GetUInt32());
        bool Powered(uint id) => Call(peer, "powerView").GetProperty("devices").EnumerateArray()
            .Single(d => d.GetProperty("netId").GetUInt32() == id).GetProperty("ports")[lampIn].GetProperty("powered").GetBoolean();
        Pump(40);
        var rows = Rows();
        Assert.That(rows[turbine.NetIdValue].GetProperty("wind").GetSingle(), Is.EqualTo(turbine.Wind).And.GreaterThan(0.6f),
            "peer renders the host's accepted wind, with the height bonus above sea");
        Assert.That(Powered(lampA.NetIdValue), Is.True, "turbine powers its lamp through the original solver");
        Assert.That(rows[battery.NetIdValue].GetProperty("energy").GetSingle(), Is.GreaterThan(0f));
        Assert.That(rows[dry.NetIdValue].GetProperty("fuel").GetSingle(), Is.EqualTo(0f));
        Assert.That(rows[dry.NetIdValue].GetProperty("toggledOn").GetBoolean(), Is.False, "host-accepted dry cut-out reaches the peer");
        Assert.That(rows[gen.NetIdValue].GetProperty("fuel").GetSingle(), Is.LessThan(150_000f), "the loaded generator burns on the host");
        ulong viewKey = JsonSerializer.SerializeToElement(peer.State()).GetProperty("powerStateHash").GetUInt64();
        Pump(6);
        Assert.That(JsonSerializer.SerializeToElement(peer.State()).GetProperty("powerStateHash").GetUInt64(), Is.EqualTo(viewKey),
            "fuel burn and charge do not invalidate the native power view");

        Call(peer, "action", new { name = "toggleDeployable", args = new { netId = gen.NetIdValue, on = false } });
        Assert.That(gen.ToggledOn, Is.True, "the request waits for authority");
        float charged = battery.Energy;
        Pump(30);
        Assert.That(gen.ToggledOn, Is.False);
        Assert.That(Powered(lampB.NetIdValue), Is.True, "battery carries the lamp with the generator off");
        Assert.That(Rows()[battery.NetIdValue].GetProperty("energy").GetSingle(), Is.LessThan(charged).And.EqualTo(battery.Energy));

        wind = 0f;
        Pump(30);
        Assert.That(Powered(lampA.NetIdValue), Is.False, "calm stops the turbine");
        Assert.That(Rows()[turbine.NetIdValue].GetProperty("wind").GetSingle(), Is.EqualTo(0f));
        ulong peerHash = ((NetWorldClient)typeof(BridgeHost).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer)!).Deployables.StateHash();
        Assert.That(peerHash, Is.EqualTo(graph.StateHash()), "peer graph is the accepted host graph");
    }

    [Test]
    public void RealUdpPeerFillsItsCanAtAPoweredHostPumpThenPoursItIntoAGeneratorOnlyAfterAcceptance()
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
        Call(host, "init", new { contentRoot = content, spawn = new[] { 0, 5, 2 }, listenPort = port, allowCheats = true });
        Call(peer, "init", new { contentRoot = content, connectHost = "127.0.0.1", connectPort = port });
        var server = (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var peerClient = (NetWorldClient)typeof(BridgeHost).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer)!;
        void Pump(int n = 24)
        {
            for (int i = 0; i < n; i++) { Call(peer, "tick"); Call(host, "tick"); Thread.Sleep(1); }
            for (int i = 0; i < 8; i++) { Call(peer, "tick"); Thread.Sleep(1); }
        }
        var gen = server.Deployables.ServerPlace(server.Ids.Mint(), 458, 0, new Vector3(0, 5, 0), 0, server.Session.CurrentTick, fuel: 1000f);
        // The map's breaker box and forecourt pump, registered the way MapServices hands them to the host.
        var fixtures = Call(host, "worldServices", new { fixtures = new object[] {
            new { defId = 9200, position = new[] { 3f, 5f, 0f }, yaw = 0f },
            new { defId = 9201, position = new[] { -3f, 5f, 0f }, yaw = 0f } } }).GetProperty("fixtures");
        uint grid = fixtures[0].GetProperty("netId").GetUInt32(), pump = fixtures[1].GetProperty("netId").GetUInt32();
        Pump();   // join before the console request
        Call(peer, "action", new { name = "console", args = new { text = "give 28" } });
        Pump(30);
        Assert.That(server.Inventories.TryGet(peerClient.PlayerId, out var owned), Is.True);
        var jar = Enumerable.Range(0, PlayerInventory.OWNPAGES).SelectMany(p => owned.Inventory.items[p].items.Select(j => (page: (byte)p, j)))
            .Single(c => c.j.item.id == 28);
        float PeerCan() { peerClient.Inventories.TryGet(peerClient.PlayerId, out var mine);
            return mine.Inventory.items[jar.page].getItem(mine.Inventory.items[jar.page].getIndex(jar.j.x, jar.j.y)).item.fuelLevel; }
        float PumpPercent() => JsonSerializer.SerializeToElement(peer.State()).GetProperty("deployables").EnumerateArray()
            .Single(r => r.GetProperty("netId").GetUInt32() == pump).GetProperty("fuel").GetSingle();
        Assert.That(Math.Max(0f, PeerCan()), Is.EqualTo(0f), "a fresh Portable Gas Can is empty");
        Assert.That(PumpPercent(), Is.EqualTo(100f), "the station starts full");

        // OnExtractFuel's gate is the pump's own Consumer port: unwired, the host refuses.
        Assert.That(Call(peer, "action", new { name = "extractFuel", args = new { netId = pump } }).GetProperty("queued").GetBoolean(), Is.True);
        Pump();
        Assert.That(Math.Max(0f, jar.j.item.fuelLevel), Is.EqualTo(0f), "an unwired pump pours nothing");
        server.Deployables.ServerConnectWire(server.Ids.Mint(), grid, 0, pump, 0, server.Session.CurrentTick);
        Pump();
        Call(peer, "action", new { name = "extractFuel", args = new { netId = pump } });
        Assert.That(Math.Max(0f, PeerCan()), Is.EqualTo(0f), "the intent alone fills nothing on the peer");
        Pump();
        // The original Portable Gas Can holds 20000 mL. The v78 jar grid carries it exactly (12.2 echoed 2047.75).
        Assert.That(jar.j.item.fuelLevel, Is.EqualTo(20000f), "host filled the can from the shared station tank");
        Assert.That(PeerCan(), Is.EqualTo(20000f), "owner echo carries the full can exactly");
        Assert.That(PumpPercent(), Is.EqualTo(server.Deployables.All.Single(e => e.NetIdValue == pump).Fuel).And.LessThan(100f),
            "every peer reads the accepted station fill");

        var queued = Call(peer, "action", new { name = "depositFuel", args = new { netId = gen.NetIdValue, page = jar.page, x = jar.j.x, y = jar.j.y, id = 28 } });
        Assert.That(queued.GetProperty("queued").GetBoolean(), Is.True);
        Assert.That(gen.SimFuel, Is.EqualTo(1000f), "the intent alone pours nothing");
        Assert.That(PeerCan(), Is.EqualTo(20000f), "the peer never empties its can locally");
        Pump();
        Assert.That(gen.Fuel, Is.EqualTo(21000f), "host accepts the exact held can");
        Assert.That(jar.j.item.fuelLevel, Is.EqualTo(0f));
        Assert.That(PeerCan(), Is.EqualTo(0f), "owner echo empties the peer's can");
        Assert.That(JsonSerializer.SerializeToElement(peer.State()).GetProperty("deployables").EnumerateArray()
            .Single(r => r.GetProperty("netId").GetUInt32() == gen.NetIdValue).GetProperty("fuel").GetSingle(), Is.EqualTo(21000f));
    }
}
