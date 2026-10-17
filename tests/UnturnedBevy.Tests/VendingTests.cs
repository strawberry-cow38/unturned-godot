using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UnturnedBevy;
namespace UnturnedBevyTests;
/// <summary>Source MpLoopback vending seams over actual UDP: the listen host pays a dollar
/// and spawns the can on the server (the owner echo confirms both); a joined client has no
/// seam (the source machine reads "Out of order") and the bridge refuses it.</summary>
[TestFixture, NonParallelizable]
public sealed class VendingTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static bool Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args}).GetProperty("queued").GetBoolean();
    void Pump(int n=12){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp]public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown]public void Cleanup(){_peer.Dispose();_host.Dispose();}
    static int Dollars(BridgeHost h)=>State(h).GetProperty("inventory").GetProperty("pages").EnumerateArray()
        .Where(p=>p.GetProperty("index").GetInt32()<7)
        .SelectMany(p=>p.GetProperty("items").EnumerateArray()).Where(i=>i.GetProperty("id").GetUInt16()==1056).Sum(i=>i.GetProperty("amount").GetInt32());
    static int Cans(BridgeHost h,ushort id)=>State(h).GetProperty("worldItems").EnumerateArray().Count(i=>i.GetProperty("itemId").GetUInt16()==id);

    /// <summary>PEI placement 608: Vendor_0 (cola) at (-615.061, 33.562, -189.329), RotationDegrees
    /// (270, 0, 0). The Godot oracle (bevy/tools/VendingOracle.cs) puts that pose's DispensePoint at
    /// (0, 0.30000004, -1.494679) from the placement.</summary>
    const int Machine = 608;
    static readonly float[] Tray = { -615.061f, 33.562f + 0.30000004f, -189.329f - 1.494679f };
    void Map(BridgeHost h)=>Call(h,"mapServices",new{contentRoot=Content(),mapName="PEI"});

    [Test]
    public void TheTrayIsTheGodotDispensePoint()
    {
        // The real Vendor_0 bounds through the manifest's pose.
        var parsed = MapServices.Parse(J(new{contentRoot=Content(),mapName="PEI"}));
        var tray = parsed.Vendors[Machine].Tray;
        Assert.That(tray, Is.EqualTo(Tray).Within(1e-3f));
        Assert.That(parsed.Vendors[Machine].Drink, Is.EqualTo(80));
        Assert.That(parsed.Vendors.Values.Count(v => v.Drink == 465), Is.GreaterThan(0), "Vendor_1 sells soda");
    }

    [Test]
    public void TheHostPaysADollarThenItsCanDropsAtTheServersTray()
    {
        Map(_host);
        Assert.That(Action(_host,"vendPay",new{sourceIndex=Machine}),Is.False,"no dollar in the bag: nothing is taken");
        Assert.That(Action(_host,"vendDrop",new{sourceIndex=Machine}),Is.False,"no can without a paid vend");
        Action(_host,"console",new{text="give 1056"});Pump();
        int before=Dollars(_host);
        Assert.That(before,Is.GreaterThanOrEqualTo(1));
        Assert.That(Action(_host,"vendPay",new{sourceIndex=Machine}),Is.False,"600 m away: out of reach");
        Assert.That(Action(_host,"vendPay",new{sourceIndex=12345}),Is.False,"not a machine");
        Pump();
        Assert.That(Dollars(_host),Is.EqualTo(before),"a refused pay takes nothing");
        Action(_host,"console",new{text=$"teleport {Tray[0]} {Tray[1]} {Tray[2]+1.5f}"});Pump(2);
        Assert.That(Action(_host,"vendPay",new{sourceIndex=Machine}),Is.True);Pump();
        Assert.That(Dollars(_host),Is.EqualTo(before-1),"the server spends exactly one dollar and the owner echo shows it");
        Assert.That(Action(_host,"vendDrop",new{sourceIndex=Machine+1}),Is.False,"the can is owed at the machine that was paid");
        // A request position is not the server's business: the can lands at the tray.
        Assert.That(Action(_host,"vendDrop",new{sourceIndex=Machine,drinkId=465,position=new[]{0f,500f,0f}}),Is.True);Pump();
        Assert.That(Cans(_host,465),Is.Zero,"the machine's drink, not the request's");
        Assert.That(Cans(_host,80),Is.EqualTo(1),"the cola is a replicated world item");
        Assert.That(Cans(_peer,80),Is.EqualTo(1),"and the joined peer sees it too");
        var row=State(_host).GetProperty("worldItems").EnumerateArray().Single(i=>i.GetProperty("itemId").GetUInt16()==80);
        var at=row.GetProperty("position").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        Assert.That(new[]{at[0],at[2]},Is.EqualTo(new[]{Tray[0],Tray[2]}).Within(.3f),"at the machine's dispense point");
        Assert.That(Action(_host,"vendDrop",new{sourceIndex=Machine}),Is.False,"one can per dollar");
    }

    [Test]
    public void ANewSessionForgetsAPaidButUndroppedCan()
    {
        Map(_host);
        Action(_host,"console",new{text="give 1056"});Pump();
        Action(_host,"console",new{text=$"teleport {Tray[0]} {Tray[1]} {Tray[2]+1.5f}"});Pump(2);
        Assert.That(Action(_host,"vendPay",new{sourceIndex=Machine}),Is.True);
        Assert.That(_host.VendPendingForTest,Is.EqualTo(1));
        _peer.Dispose();
        Call(_host,"init",new{contentRoot=Content(),spawn=new[]{0,0,0}});Map(_host);
        Assert.That(_host.VendPendingForTest,Is.Zero,"StartSession drops the old world's owed cans");
        Assert.That(Action(_host,"vendDrop",new{sourceIndex=Machine}),Is.False);
        _peer=new();
    }

    [Test]
    public void AJoinedClientHasNoVendSeam()
    {
        Action(_peer,"console",new{text="give 1056"});Pump();
        int before=Dollars(_peer);
        Assert.That(before,Is.GreaterThanOrEqualTo(1));
        Assert.That(Action(_peer,"vendPay",new{sourceIndex=Machine}),Is.False,"PlayerController.CanVend is false off the listen host");
        Assert.That(Action(_peer,"vendDrop",new{sourceIndex=Machine}),Is.False);Pump();
        Assert.That(Dollars(_peer),Is.EqualTo(before));
        Assert.That(Cans(_peer,465),Is.Zero);
    }
}
