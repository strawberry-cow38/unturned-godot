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

    [Test]
    public void TheHostPaysADollarThenItsCanDropsWhereTheMachineSays()
    {
        Assert.That(Action(_host,"vendPay",new{}),Is.False,"no dollar in the bag: nothing is taken");
        Assert.That(Action(_host,"vendDrop",new{drinkId=80,position=new[]{1f,0.5f,-2f}}),Is.False,"no can without a paid vend");
        Action(_host,"console",new{text="give 1056"});Pump();
        int before=Dollars(_host);
        Assert.That(before,Is.GreaterThanOrEqualTo(1));
        Assert.That(Action(_host,"vendPay",new{}),Is.True);Pump();
        Assert.That(Dollars(_host),Is.EqualTo(before-1),"the server spends exactly one dollar and the owner echo shows it");
        Assert.That(Action(_host,"vendDrop",new{drinkId=999,position=new[]{1f,0.5f,-2f}}),Is.False,"only the two machine drinks");
        Assert.That(Action(_host,"vendDrop",new{drinkId=80,position=new[]{1f,0.5f,-2f}}),Is.True);Pump();
        Assert.That(Cans(_host,80),Is.EqualTo(1),"the cola is a replicated world item");
        Assert.That(Cans(_peer,80),Is.EqualTo(1),"and the joined peer sees it too");
        var row=State(_host).GetProperty("worldItems").EnumerateArray().Single(i=>i.GetProperty("itemId").GetUInt16()==80);
        var at=row.GetProperty("position").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        Assert.That(new[]{at[0],at[2]},Is.EqualTo(new[]{1f,-2f}).Within(.3f),"at the machine's dispense point");
        Assert.That(Action(_host,"vendDrop",new{drinkId=80,position=new[]{1f,0.5f,-2f}}),Is.False,"one can per dollar");
    }

    [Test]
    public void AJoinedClientHasNoVendSeam()
    {
        Action(_peer,"console",new{text="give 1056"});Pump();
        int before=Dollars(_peer);
        Assert.That(before,Is.GreaterThanOrEqualTo(1));
        Assert.That(Action(_peer,"vendPay",new{}),Is.False,"PlayerController.CanVend is false off the listen host");
        Assert.That(Action(_peer,"vendDrop",new{drinkId=465,position=new[]{0f,0.5f,-2f}}),Is.False);Pump();
        Assert.That(Dollars(_peer),Is.EqualTo(before));
        Assert.That(Cans(_peer,465),Is.Zero);
    }
}
