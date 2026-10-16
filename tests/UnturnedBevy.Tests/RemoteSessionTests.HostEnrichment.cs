using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

/// <summary>Row 15A (protocol v80) over actual UDP: the remote's repair tool, the host's weather/day and
/// the host's vehicle part health now reach a joined client through the server.</summary>
public sealed partial class RemoteSessionTests
{
    const ushort Blowtorch = 76;   // game/content/blowtorch.dat: Repair, Vehicle_Damage 10 -> 30 HP/s
    static readonly float[] Spawn = [35, 8, -22];

    uint HostVehicle(float z, int health, int healthMax, byte type = 3, bool heli = false) => Call(_host, "worldVehicles", new {
        replace = false, vehicles = new object[] { heli
            ? new { typeId = type, position = new[] { Spawn[0] + 1.5f, Spawn[1], Spawn[2] }, seatCount = 4, health, healthMax, fuel = 1000, battery = 10000,
                speedMax = 30, isHeli = true, engineRegion = new { rear = false, threshold = 0f, health = 4000f },
                hitRegions = new object[] {
                    new { bodyPart = "mainRotor", center = new[] { 0f, 3f, 0f }, size = new[] { 1f, 1f, 1f }, health = 450f },
                    new { bodyPart = "tailRotor", center = new[] { 0f, 2f, 6f }, size = new[] { 1f, 1f, 1f }, health = 280f } } }
            : (object)new { typeId = type, position = new[] { Spawn[0], Spawn[1], z }, seatCount = 4, health, healthMax, fuel = 1000, battery = 10000, speedMax = 30 } },
    }).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();

    /// <summary>A vertical vehicle-tagged quad across the look ray, at <paramref name="z"/>, on the host's
    /// dynamic collision -- what the native host uploads for a vehicle body.</summary>
    void HostPanel(uint id, float z) => Call(_host, "worldDynamicGeometry", new { key = $"vehicle-{id}", meshes = new[] { new {
        vertices = new[] { new[] { 33f, 7f, z }, new[] { 37f, 7f, z }, new[] { 37f, 12f, z }, new[] { 33f, 12f, z } },
        indices = new[] { 0, 1, 2, 0, 2, 3 }, vehicleNetId = id } } });

    static JsonElement Row(BridgeHost h, uint id) => State(h).GetProperty("vehicles").EnumerateArray().Single(v => v.GetProperty("netId").GetUInt32() == id);
    static float Health(BridgeHost h, uint id) => Row(h, id).GetProperty("health").GetSingle();

    void Hold(BridgeHost h, ushort item)
    {
        Action(h, "console", new { text = $"give {item}" }); Pump();
        var at = Address(State(h), item);
        Action(h, "equipAndHold", new { page = at.Page, x = at.X, y = at.Y }); Pump(24);
        Assert.That(State(h).GetProperty("player").GetProperty("heldItemId").GetInt32(), Is.EqualTo(item));
    }
    void Face(BridgeHost h, float yaw) => Call(h, "tick", new { input = new { position = Spawn, yaw, pitch = 0f, grounded = true } });
    void HoldTool(int ticks, params (BridgeHost host, float yaw)[] users)
    {
        for (int i = 0; i < ticks; i++)
        {
            foreach (var (h, yaw) in users) { Face(h, yaw); Action(h, "toolUse", new { active = true }); }
            Pump(1);
        }
        foreach (var (h, _) in users) Action(h, "toolUse", new { active = false });
        Pump(4);
    }

    [Test]
    public void RemoteBlowtorchRepairsTheHostVehicleAndBareHandsAreRefused()
    {
        Listen(); Join(); Pump();
        uint car = HostVehicle(-23.5f, 500, 1000); HostPanel(car, -23.5f); Pump();
        float before = Health(_remote, car);
        Assert.That(before, Is.EqualTo(500).Within(.5f));
        HoldTool(30, (_remote, 0f));
        Assert.That(Health(_host, car), Is.EqualTo(before).Within(.5f), "bare hands are no repair tool: the host refuses");
        Assert.That(Health(_remote, car), Is.EqualTo(before).Within(.5f));

        Hold(_remote, Blowtorch);
        HoldTool(30, (_remote, 0f));
        float host = Health(_host, car), remote = Health(_remote, car);
        Assert.That(host, Is.GreaterThan(before + 5), "a remote owner's held blowtorch repairs through Command101");
        Assert.That(remote, Is.EqualTo(host).Within(.5f), "the remote sees the host-accepted HP");
        Assert.That(Count(State(_remote), Blowtorch), Is.EqualTo(1), "the tool is not spent");
        HoldTool(0, (_remote, 0f));
        Pump(10);
        Assert.That(Health(_host, car), Is.EqualTo(host).Within(.5f), "release stops the host repair");
    }

    [Test]
    public void TwoSimultaneousRepairersEachKeepTheirOwnTool()
    {
        Listen(); Join(); Pump();
        uint ahead = HostVehicle(-23.5f, 500, 1000), behind = HostVehicle(-20.5f, 500, 1000);
        HostPanel(ahead, -23.5f); HostPanel(behind, -20.5f); Pump();
        Hold(_host, Blowtorch); Hold(_remote, Blowtorch);
        float a = Health(_host, ahead), b = Health(_host, behind);
        // The listen owner faces -Z (the car ahead), the remote faces +Z (the car behind), both every tick.
        HoldTool(40, (_host, 0f), (_remote, 180f));
        Assert.That(Health(_host, ahead), Is.GreaterThan(a + 5), "the listen owner's repair is not stolen by the remote's ToolUse");
        Assert.That(Health(_host, behind), Is.GreaterThan(b + 5), "the remote's repair runs in the same ticks");
        Assert.That(Health(_remote, ahead), Is.EqualTo(Health(_host, ahead)).Within(.5f));
    }

    [Test]
    public void HostWeatherAndDayReachTheRemote()
    {
        Listen(); Join(); Pump();
        Action(_host, "console", new { text = "weather rain" }); Pump(30);
        var host = State(_host); var remote = State(_remote);
        Assert.That(remote.GetProperty("weather").ValueKind, Is.EqualTo(JsonValueKind.Object), "System21 reached the client");
        Assert.That(remote.GetProperty("weather").GetProperty("name").GetString(), Is.EqualTo("Default Rain"));
        Assert.That(remote.GetProperty("weather").GetProperty("name").GetString(), Is.EqualTo(host.GetProperty("weather").GetProperty("name").GetString()));
        Assert.That(remote.GetProperty("weather").GetProperty("stage").GetString(), Is.EqualTo("Active"));
        Assert.That(remote.GetProperty("weather").GetProperty("blendAlpha").GetSingle(),
            Is.EqualTo(host.GetProperty("weather").GetProperty("blendAlpha").GetSingle()).Within(2 / 255f), "the fade-in is the host's, not a local roll");
        Assert.That(remote.GetProperty("weather").GetProperty("today").GetRawText(), Is.EqualTo(host.GetProperty("weather").GetProperty("today").GetRawText()),
            "the outlook derives from the host seed and day");
        Assert.That(remote.GetProperty("worldClock").GetProperty("day").ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(remote.GetProperty("worldClock").GetProperty("day").GetInt32(), Is.EqualTo(host.GetProperty("worldClock").GetProperty("day").GetInt32()));
        Action(_host, "console", new { text = "weather clear" }); Pump(10);
        Assert.That(State(_remote).GetProperty("weather").GetProperty("name").GetString(), Is.EqualTo("Clear"));

        // Day wrap: re-anchor the host clock so midnight falls 20 ticks from now on a one-hour day, then
        // run past it. Native traffic phase reads (day + timeOfDay) x dayLength and the radio board reads
        // today/tomorrow; both must be the host's on the remote, not null and not a local roll.
        int dayBefore = State(_host).GetProperty("worldClock").GetProperty("day").GetInt32();
        long now = Server.Session.CurrentTick;
        float baseTime = 1f - (float)((now + 20) * SimClock.FixedDelta / 3600.0 % 1.0);
        Server.Clock.ServerConfigure(baseTime, 3600f, now);
        Pump(60);
        host = State(_host); remote = State(_remote);
        int dayAfter = host.GetProperty("worldClock").GetProperty("day").GetInt32();
        Assert.That(dayAfter, Is.EqualTo(dayBefore + 1), "the host crossed midnight");
        Assert.That(remote.GetProperty("worldClock").GetProperty("day").GetInt32(), Is.EqualTo(dayAfter), "the remote's day wraps with the host's");
        Assert.That(remote.GetProperty("worldClock").GetProperty("timeOfDay").GetSingle(),
            Is.EqualTo(host.GetProperty("worldClock").GetProperty("timeOfDay").GetSingle()).Within(.001f));
        foreach (string field in new[] { "today", "tomorrow", "dayMeanC" })
            Assert.That(remote.GetProperty("weather").GetProperty(field).GetRawText(),
                Is.EqualTo(host.GetProperty("weather").GetProperty(field).GetRawText()), $"weather.{field} after the wrap");
    }

    [Test]
    public void HostHurtmainReachesTheRemoteVehicleRow()
    {
        Listen(); Join(); Pump();
        uint heli = HostVehicle(0, 10000, 10000, type: 21, heli: true); Pump();   // 21 = huey: Health 1000 -> rotors 450/280, engine 4000
        Assert.That(Row(_remote, heli).GetProperty("mainRotorHealth").GetSingle(), Is.EqualTo(450).Within(.01f), "an intact tail costs one bit and reads full");
        Assert.That(Row(_remote, heli).GetProperty("engineHealth").GetSingle(), Is.EqualTo(4000).Within(.01f));
        Action(_host, "enterVehicle", new { netId = heli, seat = 0 }); Pump();
        Action(_host, "console", new { text = "hurtmain" }); Pump();
        float main = Row(_host, heli).GetProperty("mainRotorHealth").GetSingle();
        Assert.That(main, Is.LessThan(450), "hurtmain hit the host's main rotor pool");
        var row = Row(_remote, heli);
        Assert.That(row.GetProperty("mainRotorHealth").GetSingle(), Is.EqualTo(main).Within(450f / 255f), "the System9 tail carries the host part fraction");
        Assert.That(row.GetProperty("tailRotorHealth").GetSingle(), Is.EqualTo(280).Within(.01f));
        Assert.That(row.GetProperty("onFire").GetBoolean(), Is.False);
        Action(_host, "console", new { text = "killtail" }); Pump();
        Assert.That(Row(_remote, heli).GetProperty("tailRotorHealth").GetSingle(), Is.Zero, "a dead rotor stays exactly dead");
    }
}
