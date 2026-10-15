using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class InventoryViewTests
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    BridgeHost _host, _peer;
    static JsonElement Call(BridgeHost host, string op, object args = null) =>
        JsonSerializer.SerializeToElement(host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { }, Json)), Json);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), Json);
    static JsonElement Action(BridgeHost host, string name, object args) => Call(host, "action", new { name, args });
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);
    ushort Owner => State(_peer).GetProperty("playerId").GetUInt16();
    PlayerInventory Inventory => Server.Transactions.InventoryForTest(Owner);
    static string Content()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "game/content"))) return Path.Combine(d.FullName, "game/content");
        throw new DirectoryNotFoundException();
    }
    void Pump(int count = 12) { for (int i = 0; i < count; i++) { Call(_peer, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    [SetUp] public void Setup()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        _host = new(); _peer = new();
        Call(_host, "init", new { contentRoot = Content(), listenPort = port, allowCheats = true, spawn = new[] { 0, 0, 0 } });
        Call(_peer, "init", new { contentRoot = Content(), connectHost = "127.0.0.1", connectPort = port }); Pump(30);
        Inventory.items[2].resize(12, 12);
    }
    [TearDown] public void Cleanup() { _peer?.Dispose(); _host?.Dispose(); }
    JsonElement View() => Call(_peer, "inventoryView");
    JsonElement Row(ushort id) => View().GetProperty("items").EnumerateArray().Single(i => i.GetProperty("address").GetProperty("id").GetUInt16() == id);
    Item Put(ItemAsset asset, byte x, byte y = 0)
    { var item = new Item(asset.id); Inventory.items[2].addItem(x, y, 0, item); return item; }
    void Empty(JsonElement address) => Action(_peer, "emptyFuel", new {
        page = address.GetProperty("page").GetByte(), x = address.GetProperty("x").GetByte(), y = address.GetProperty("y").GetByte(),
        id = address.GetProperty("id").GetUInt16(), token = address.GetProperty("token").GetUInt64()
    });

    [Test]
    public void RemoteContextUsesAcceptedContentsAndExactIdentityWithoutMutatingItems()
    {
        var fuel = Assets.all().First(a => a.IsFuelContainer); var can = Put(fuel, 0); can.fuelLevel = 300;
        var fluid = Assets.all().First(a => a.IsFluidContainer); var bottle = Put(fluid, 6); bottle.autoDrink = false;
        Pump(); var row = Row(fuel.id); var address = row.GetProperty("address");
        Assert.That(address.GetProperty("token").GetUInt64(), Is.GreaterThan(0));
        Assert.That(row.GetProperty("fuel").GetSingle(), Is.EqualTo(300));
        Assert.That(row.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("label").GetString()), Does.Contain("Empty").And.Contain("Hold"));
        var b = Row(fluid.id); Assert.That(b.GetProperty("fluid").GetProperty("autoDrink").GetBoolean(), Is.False);
        Assert.That(b.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("label").GetString()), Does.Contain("Autodrink: OFF"));
        Assert.That(Call(_host, "inventoryView").GetProperty("items").GetArrayLength(), Is.Zero, "owner-only projection");
        float before = bottle.fluidAmount; View(); View(); Assert.That(bottle.fluidAmount, Is.EqualTo(before));
    }

    [Test]
    public void UdpEmptyRejectsStaleSameIdForeignTokenWrongTypeAndDeadOwnerThenReplicatesExactEmpty()
    {
        var fuel = Assets.all().First(a => a.IsFuelContainer); var first = Put(fuel, 0); first.fuelLevel = 300;
        Pump(); var stale = Row(fuel.id).GetProperty("address").Clone();
        var page = Inventory.items[2]; page.removeItem(page.getIndex(0, 0));
        var replacement = Put(fuel, 0); replacement.fuelLevel = 250;
        Empty(stale); Pump(); Assert.That(replacement.fuelLevel, Is.EqualTo(250), "same ID/address is a different instance");
        var current = Row(fuel.id).GetProperty("address").Clone();
        var hostId = State(_host).GetProperty("playerId").GetUInt16();
        var foreign = new Item(fuel.id) { fuelLevel = 200 }; var hostBag = Server.Transactions.InventoryForTest(hostId);
        hostBag.items[2].addItem(0, 0, 0, foreign); Pump();
        Action(_peer, "emptyFuel", new { page = 2, x = 0, y = 0, id = fuel.id, token = WeaponState.ItemIdentity(foreign) });
        Pump(); Assert.That(replacement.fuelLevel, Is.EqualTo(250)); Assert.That(foreign.fuelLevel, Is.EqualTo(200));
        var nonfuel = Put(Assets.find(146), 6); nonfuel.fuelLevel = 100; Pump(); Empty(Row(146).GetProperty("address")); Pump();
        Assert.That(nonfuel.fuelLevel, Is.EqualTo(100));
        Server.CombatState.TryGet(Owner, out var life); life.Alive = false;
        Empty(current); Pump(); Assert.That(replacement.fuelLevel, Is.EqualTo(250));
        life.Alive = true;
        Empty(current);
        Assert.That(replacement.fuelLevel, Is.EqualTo(250), "request is not an immediate client mutation");
        Pump(); Assert.That(replacement.fuelLevel, Is.Zero); Assert.That(Row(fuel.id).GetProperty("fuel").GetSingle(), Is.Zero);
        Assert.That(foreign.fuelLevel, Is.EqualTo(200));
    }

    [Test]
    public void CookerViewFollowsAcceptedOpenUpdatesAndCloseWithoutHostStateLeak()
    {
        const uint id = 65000;
        Server.Inventories.ServerRegisterCrate(new NetId(id), 4, 4, UnityEngine.Vector3.zero);
        Server.Transactions.Cooking.Register(id, ECookerKind.Oven);
        Action(_peer, "openStorage", new { netId = id }); Pump();
        var cooker = View().GetProperty("cooker"); Assert.That(cooker.GetProperty("kind").GetString(), Is.EqualTo("Oven"));
        Assert.That(cooker.GetProperty("netId").GetUInt32(), Is.EqualTo(id));
        Assert.That(Call(_host, "inventoryView").GetProperty("cooker").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Action(_peer, "closeStorage", new { }); Pump();
        Assert.That(View().GetProperty("cooker").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }
    void Salvage(JsonElement address) => Action(_peer, "salvageItem", new {
        page = address.GetProperty("page").GetByte(), x = address.GetProperty("x").GetByte(), y = address.GetProperty("y").GetByte(),
        id = address.GetProperty("id").GetUInt16(), token = address.GetProperty("token").GetUInt64()
    });
    JsonElement AtRow(byte x) => View().GetProperty("items").EnumerateArray()
        .Single(i => i.GetProperty("address").GetProperty("page").GetByte() == 2
            && i.GetProperty("address").GetProperty("x").GetByte() == x);

    [Test]
    public void UdpSalvageSpendsClickedWornCopyRatherThanFirstPristineCopyAndRejectsReplay()
    {
        var asset = Assets.all().First(a => a.type == EItemType.HAT && BlueprintRegistry.SalvageFor(a.id) is { } bp
            && bp.Inputs.Count == 1 && bp.Inputs[0].Amount == 1 && !bp.RequiresStation);
        var pristine = Put(asset, 0); pristine.quality = 100;
        var worn = Put(asset, 6); worn.quality = 7;
        Pump(); var row = AtRow(6); var address = row.GetProperty("address").Clone();
        Assert.That(row.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("action").GetString() == "salvage")
            .GetProperty("tooltip").GetString(), Does.StartWith("Break down into "));
        Salvage(address); Assert.That(Inventory.getItemCount(asset.id), Is.EqualTo(2), "no optimistic inventory edits");
        Pump(2);
        Assert.That(Inventory.getItemCount(asset.id), Is.EqualTo(1));
        Assert.That(Inventory.items[2].getItem(Inventory.items[2].getIndex(0, 0)).item, Is.SameAs(pristine));
        Assert.That(pristine.quality, Is.EqualTo(100));
        Assert.That(Server.CraftQueue.JobCount(Owner), Is.EqualTo(1), "original timed escrow, not instant output");
        Salvage(address); Pump(2);
        Assert.That(Inventory.getItemCount(asset.id), Is.EqualTo(1));
        Assert.That(Server.CraftQueue.JobCount(Owner), Is.EqualTo(1), "old selected instance cannot spend another copy");
        var bp = BlueprintRegistry.SalvageFor(asset.id);
        Pump((int)Math.Ceiling(ServerCrafting.SecondsFor(bp) * NetProtocol.TicksPerSecond) + 5);
        Assert.That(Server.CraftQueue.JobCount(Owner), Is.Zero);
        foreach (var output in bp.Outputs)
            Assert.That(Inventory.getItemCount(Crafting.Resolve(output.Guid)), Is.GreaterThanOrEqualTo(output.Amount));
        Assert.That(AtRow(0).GetProperty("condition").GetString(), Does.StartWith("Condition: 100%"));
    }

    [Test]
    public void UdpSalvageRejectsReplacementForeignTokenDeadOwnerAndNonsalvageBeforeEscrow()
    {
        var asset = Assets.all().First(a => a.type == EItemType.HAT && BlueprintRegistry.SalvageFor(a.id) != null);
        var first = Put(asset, 0); Pump(); var stale = AtRow(0).GetProperty("address").Clone();
        Inventory.items[2].removeItem(Inventory.items[2].getIndex(0, 0));
        var replacement = Put(asset, 0); replacement.quality = 91;
        Salvage(stale); Pump(); Assert.That(Inventory.getItemCount(asset.id), Is.EqualTo(1));
        var current = AtRow(0).GetProperty("address").Clone();
        var hostId = State(_host).GetProperty("playerId").GetUInt16();
        var foreign = new Item(asset.id); Server.Transactions.InventoryForTest(hostId).items[2].addItem(0,0,0,foreign); Pump();
        Action(_peer, "salvageItem", new {page=2,x=0,y=0,id=asset.id,token=WeaponState.ItemIdentity(foreign)}); Pump();
        Assert.That(Inventory.getItemCount(asset.id), Is.EqualTo(1));
        Server.CombatState.TryGet(Owner, out var life); life.Alive = false; Salvage(current); Pump(); life.Alive = true;
        Assert.That(Inventory.getItemCount(asset.id), Is.EqualTo(1));
        var noSalvage = Assets.all().First(a => BlueprintRegistry.SalvageFor(a.id) == null && a.size_x < 5 && a.size_y < 5);
        var other = Put(noSalvage,6); Pump(); Salvage(AtRow(6).GetProperty("address")); Pump();
        Assert.That(Inventory.getItemCount(noSalvage.id), Is.EqualTo(1));
        Assert.That(Server.CraftQueue.JobCount(Owner), Is.Zero);
        Assert.That(replacement.quality, Is.EqualTo(91));
    }

}
