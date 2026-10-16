using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using PlayerInventory = SDG.Unturned.PlayerInventory;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

/// <summary>Owner temperature computed where the source computes it (the client shell's
/// PlayerTemperatureSim off its own probes) and streamed on PlayerStateCommand.TempBand, which the
/// server's vitals step reads; plus the HUD deadzone status off the host's own DeadzoneSim clock.</summary>
[TestFixture, NonParallelizable]
public sealed class SurvivalFeedbackTests
{
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };
    BridgeHost _host;

    static string Root()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException("game/content");
    }
    JsonElement Call(string op, object args = null) =>
        JsonSerializer.SerializeToElement(_host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), JsonOptions);
    JsonElement Player() => JsonSerializer.SerializeToElement(_host.State(), JsonOptions).GetProperty("player");
    JsonElement Temperature() => Player().GetProperty("temperature");
    T Private<T>(string name) => (T)typeof(BridgeHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);
    NetWorldServer Server => Private<NetWorldServer>("_server");
    ushort Owner => Private<NetWorldClient>("_client").PlayerId;
    void Init() { _host = new BridgeHost(); Call("init", new { contentRoot = Root(), mapId = "PEI", spawn = new[] { 0f, 0f, 0f } }); }
    void Pump(int count, float sourceC = 0f, bool sheltered = false)
    {
        for (int n = 0; n < count; n++) Call("tick", new { input = new { temperatureProbe = new { sheltered, sourceC } } });
    }

    [TearDown] public void TearDown() { _host?.Dispose(); _host = null; Environment.SetEnvironmentVariable("UG_WEATHER", null); }

    [Test]
    public void ColdAndHotProbesReachTheServerVitalsThroughTheStateStreamBand()
    {
        Init();
        var start = Temperature();
        Assert.That(start.GetProperty("bodyC").GetSingle(), Is.EqualTo((PlayerTemperatureSim.ComfortLowC + PlayerTemperatureSim.ComfortHighC) * .5f).Within(.5f),
            "a fresh body starts mid-comfort and eases toward the ambient");
        Assert.That(start.GetProperty("band").GetString(), Is.EqualTo("Comfortable"));
        // A cooling sum at the ThermalFalloff clamp: the body follows the source 45 s time constant.
        Pump(3000, sourceC: -40f);
        var cold = Temperature();
        Assert.That(cold.GetProperty("band").GetString(), Is.EqualTo("Freezing"));
        Assert.That(cold.GetProperty("comfort").GetSingle(), Is.EqualTo(-1f));
        Assert.That(cold.GetProperty("sourceC").GetSingle(), Is.EqualTo(-40f));
        Assert.That(Server.PlayerHost.TryGetDrivenState(Owner, out var driven), Is.True);
        Assert.That(driven.TempBand, Is.EqualTo((byte)PlayerTemperatureSim.Band.Freezing), "the band rides PlayerStateCommand v59");
        Assert.That(Server.Vitals.TemperatureBandOf(Owner), Is.EqualTo(PlayerTemperatureSim.Band.Freezing));
        Server.CombatState.TryGet(Owner, out var life);
        float before = life.HealthExact;
        Pump(250, sourceC: -40f);
        Assert.That(life.HealthExact, Is.LessThan(before - 4f), "Freezing exposure costs ExposureHealthPerSecond on the server");

        Pump(6000, sourceC: 40f);
        var hot = Temperature();
        Assert.That(hot.GetProperty("band").GetString(), Is.EqualTo("Boiling").Or.EqualTo("Hot"));
        Assert.That(hot.GetProperty("comfort").GetSingle(), Is.GreaterThan(0f));
        Assert.That(Server.Vitals.TemperatureBandOf(Owner).ToString(), Is.EqualTo(hot.GetProperty("band").GetString()));
    }

    [Test]
    public void RainSoaksOnlyWhenTheNativeProbeSaysTheSkyIsOpen()
    {
        Environment.SetEnvironmentVariable("UG_WEATHER", "rain");
        Init();
        Pump(400, sheltered: true);
        var sheltered = Temperature();
        Assert.That(sheltered.GetProperty("raining").GetBoolean(), Is.True);
        Assert.That(sheltered.GetProperty("sheltered").GetBoolean(), Is.True);
        Assert.That(sheltered.GetProperty("wetness").GetSingle(), Is.Zero, "cover keeps the player dry");
        Pump(500, sheltered: false);
        var open = Temperature();
        Assert.That(open.GetProperty("sheltered").GetBoolean(), Is.False);
        // SoakSeconds 40: ten simulated seconds soak a quarter.
        Assert.That(open.GetProperty("wetness").GetSingle(), Is.EqualTo(.25f).Within(.02f));
    }

    [Test]
    public void ThermometerReadoutFollowsTheAcceptedOwnerBag()
    {
        Init();
        Pump(4);
        Assert.That(Temperature().GetProperty("hasThermometer").GetBoolean(), Is.False);
        Call("action", new { name = "console", args = new { text = "give 9346" } });
        Pump(6);
        Assert.That(Temperature().GetProperty("hasThermometer").GetBoolean(), Is.True);
    }

    [Test]
    public void DeadzoneStatusUsesTheHostDeadzoneClockForTheOwner()
    {
        Init();
        Call("worldServices", new { deadzones = new[] { new { center = new[] { 0f, 0f, 0f }, halfExtent = new[] { 8f, 8f, 8f }, kind = "Radiation", shape = "Box" } } });
        Pump(13);   // ServerDeadzones polls every 0.25 s
        Assert.That(Player().GetProperty("inDeadzone").GetBoolean(), Is.True);
        Pump(200);
        Assert.That(Player().GetProperty("radiation").GetSingle(), Is.GreaterThan(0f), "past the entry grace the dose rises");
        Call("action", new { name = "console", args = new { text = "teleport 40 0 40" } });
        Pump(30);
        Assert.That(Player().GetProperty("inDeadzone").GetBoolean(), Is.False, "leaving clears the status");
    }

    (int page, int x, int y) Bottle()
    {
        Server.Inventories.TryGet(Owner, out var bag);
        for (int page = 0; page < PlayerInventory.OWNPAGES; page++)
            foreach (var jar in bag.Inventory.items[page].items)
                if (jar.item.id == 14) return (page, jar.x, jar.y);
        throw new AssertionException("starter bottle missing");
    }
    Item ServerBottle()
    {
        var (page, x, y) = Bottle();
        Server.Inventories.TryGet(Owner, out var bag);
        return bag.Inventory.items[page].getItem(bag.Inventory.items[page].getIndex((byte)x, (byte)y)).item;
    }
    string BottleLabel()
    {
        var (page, x, y) = Bottle();
        return JsonSerializer.SerializeToElement(_host.State(), JsonOptions).GetProperty("inventory").GetProperty("pages").EnumerateArray()
            .Single(p => p.GetProperty("index").GetInt32() == page).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("x").GetInt32() == x && i.GetProperty("y").GetInt32() == y)
            .GetProperty("fluid").GetProperty("label").GetString();
    }

    [Test]
    public void HeldBottleDrinksAndFillsAtATapOnlyThroughAcceptedCommands()
    {
        _host = new BridgeHost();
        Call("init", new { contentRoot = Root(), mapId = "PEI", spawn = new[] { 0f, 0f, 0f }, starterKit = true });
        // A sink beside the spawn: WaterTap bounds, the source 2.5 m server reach.
        Call("worldServices", new { taps = new[] { new { position = new[] { 1f, 0f, 0f }, rotation = new[] { 0f, 0f, 0f },
            scale = new[] { 1f, 1f, 1f }, min = new[] { -.4f, 0f, -.3f }, max = new[] { .4f, .9f, .3f }, label = "Sink" } } });
        Pump(4);
        var (page, x, y) = Bottle();
        var cell = new { page, x, y, id = 14 };
        Assert.That(BottleLabel(), Is.EqualTo("Bottled Water -- 1.0 L Clean Water"));
        Server.Vitals.TryGet(Owner, out var vitals);
        vitals.Sim.Water = 0.2f;

        var drink = Call("action", new { name = "drinkHeldFluid", args = cell });
        Assert.That(drink.GetProperty("queued").GetBoolean(), Is.True);
        Assert.That(drink.GetProperty("toast").GetString(), Does.StartWith("drank 1.0 L  (+"));
        Assert.That(BottleLabel(), Is.EqualTo("Bottled Water -- 1.0 L Clean Water"), "no local mutation before the echo");
        Pump(8);
        Assert.That(Server.Transactions.Diag.DrinksApplied, Is.EqualTo(1));
        Assert.That(vitals.Sim.Water, Is.GreaterThan(0.2f + 0.1f), "the server raised Water");
        Assert.That(Player().GetProperty("water").GetSingle(), Is.EqualTo(vitals.Sim.Water).Within(0.01f));
        Assert.That(BottleLabel(), Is.EqualTo("Bottled Water -- empty (1.0 L)"));
        var dry = Call("action", new { name = "drinkHeldFluid", args = cell });
        Assert.That(dry.GetProperty("queued").GetBoolean(), Is.False);
        Assert.That(dry.GetProperty("toast").GetString(), Is.EqualTo("container is empty"));

        var away = Call("action", new { name = "fillHeldFluidAtTap", args = cell });
        Assert.That(away.GetProperty("toast").GetString(), Is.EqualTo("aim at a tank, a sink or a bathtub to fill"));
        Call("tick", new { input = new { lookEnd = new[] { 1f, .95f, .1f } } });
        Assert.That(Player().GetProperty("aimedTap").GetString(), Is.EqualTo("Sink"), "AimSlack lets the rim count");
        var fill = Call("action", new { name = "fillHeldFluidAtTap", args = cell });
        Assert.That(fill.GetProperty("queued").GetBoolean(), Is.True);
        Assert.That(fill.GetProperty("toast").GetString(), Is.EqualTo("filled 1.0 L clean water"));
        Pump(8);
        Assert.That(Server.Transactions.Diag.TapFillsApplied, Is.EqualTo(1));
        Assert.That(ServerBottle().fluidAmount, Is.EqualTo(1000f));
        Assert.That(BottleLabel(), Is.EqualTo("Bottled Water -- 1.0 L Clean Water"));
        // The server rechecks reach from its own copy of the position.
        Call("action", new { name = "console", args = new { text = "teleport 30 0 30" } });
        Pump(20);
        Call("action", new { name = "drinkHeldFluid", args = cell }); Pump(8);
        Call("tick", new { input = new { lookEnd = new[] { 1f, .5f, 0f } } });
        Call("action", new { name = "fillHeldFluidAtTap", args = cell }); Pump(8);
        Assert.That(Server.Transactions.Diag.TapFillsRejected, Is.EqualTo(1));
        Assert.That(BottleLabel(), Is.EqualTo("Bottled Water -- empty (1.0 L)"));
    }
}
