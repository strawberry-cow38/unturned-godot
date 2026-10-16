using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot.Net;
namespace UnturnedBevyTests;
/// <summary>Source restraints (ArrestSim, commands 52/53/54) over actual UDP: the listen host is the captor
/// and the managed peer a joined client, so the cuffs, the key and every struggle cross the core choke
/// point, and what each side sees is the replicated gesture.</summary>
[TestFixture, NonParallelizable]
public sealed class RestraintTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    const byte Surrender=(byte)EPlayerGesture.SURRENDER_START, Arrest=(byte)EPlayerGesture.ARREST_START, Wave=(byte)EPlayerGesture.WAVE;
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static JsonElement Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp]public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown]public void Cleanup(){_peer.Dispose();_host.Dispose();}
    ushort PeerId=>State(_peer).GetProperty("playerId").GetUInt16();
    static int Count(BridgeHost h,ushort item)=>State(h).GetProperty("inventory").GetProperty("pages").EnumerateArray()
        .SelectMany(p=>p.GetProperty("items").EnumerateArray()).Where(i=>i.GetProperty("id").GetUInt16()==item).Sum(i=>i.GetProperty("amount").GetInt32());
    static (int Page,int X,int Y) Cell(BridgeHost h,ushort item)
    {
        foreach(var p in State(h).GetProperty("inventory").GetProperty("pages").EnumerateArray())
            foreach(var i in p.GetProperty("items").EnumerateArray())
                if(i.GetProperty("id").GetUInt16()==item)return(p.GetProperty("index").GetInt32(),i.GetProperty("x").GetInt32(),i.GetProperty("y").GetInt32());
        throw new InvalidOperationException($"no {item}");
    }
    JsonElement Hold(BridgeHost h,ushort item){var c=Cell(h,item);var r=Action(h,"holdItem",new{page=c.Page,x=c.X,y=c.Y});Pump();return r;}
    void Give(BridgeHost h,ushort item){Action(h,"console",new{text=$"give {item}"});Pump();}
    static byte Gesture(BridgeHost h)=>State(h).GetProperty("player").GetProperty("gesture").GetByte();
    byte HostSeesPeer=>State(_host).GetProperty("players").EnumerateArray().Single(p=>p.GetProperty("playerId").GetUInt16()==PeerId).GetProperty("gesture").GetByte();
    static string[] Notices(BridgeHost h)=>State(h).GetProperty("events").EnumerateArray()
        .Where(e=>e.GetProperty("data").ValueKind==JsonValueKind.Object&&e.GetProperty("data").TryGetProperty("text",out _))
        .Select(e=>e.GetProperty("data").GetProperty("text").GetString()).ToArray();
    void Cuff(){Action(_host,"arrestPlayer",new{targetPlayerId=PeerId});Pump();}

    [Test]
    public void AHostCuffsOnlyASurrenderingPeerInReachAndTheCuffedPeerCannotEquipOrGesture()
    {
        Give(_host,ArrestDef.Handcuffs);
        Assert.That(Hold(_host,ArrestDef.Handcuffs).GetProperty("heldItemId").GetUInt16(),Is.EqualTo(ArrestDef.Handcuffs));
        Pump();
        Cuff();
        Assert.That(Gesture(_peer),Is.Not.EqualTo(Arrest),"UseableArrestStart: not surrendering");
        Action(_peer,"requestGesture",new{gesture=Surrender});Pump();
        Assert.That((Gesture(_peer),HostSeesPeer),Is.EqualTo((Surrender,Surrender)),"hands up is the server's accepted gesture");
        Action(_peer,"console",new{text="teleport 30 0 30"});Pump();
        Cuff();
        Assert.That(Gesture(_peer),Is.EqualTo(Surrender),"beyond the 7 m server reach");
        Action(_peer,"console",new{text="teleport 1 0 1"});Pump();
        Cuff();
        Assert.That(State(_peer).GetProperty("player").GetProperty("arrested").GetBoolean(),Is.True,"the captor's cuffs arrive on the peer's own entity");
        Assert.That(HostSeesPeer,Is.EqualTo(Arrest),"and everyone sees ARREST_START");
        Assert.That(Count(_host,ArrestDef.Handcuffs),Is.Zero,"the cuffs leave the captor's bag");

        Give(_peer,13);
        var refused=Hold(_peer,13);
        Assert.That(refused.GetProperty("queued").GetBoolean(),Is.False);
        Assert.That(State(_peer).GetProperty("player").GetProperty("heldItemId").GetInt32(),Is.Zero,"EquipItemAsset: your hands are bound");
        Assert.That(Notices(_peer),Does.Contain("Your hands are bound."));
        Action(_peer,"requestGesture",new{gesture=Wave});Pump();
        Assert.That(Notices(_peer),Does.Contain("You're cuffed."),"GestureRules.RefusalFor");
        Assert.That(Gesture(_peer),Is.EqualTo(Arrest));
    }

    [Test]
    public void AKeyFreesAndReturnsTheCuffsAndAHundredTwentyEightAlternatingLeansBreakThem()
    {
        Give(_host,ArrestDef.Handcuffs);Give(_host,ArrestDef.HandcuffsKey);
        Hold(_host,ArrestDef.Handcuffs);
        Action(_peer,"requestGesture",new{gesture=Surrender});Pump();Cuff();
        Assert.That(Gesture(_peer),Is.EqualTo(Arrest));
        Hold(_host,ArrestDef.HandcuffsKey);
        Action(_host,"unlockArrest",new{targetPlayerId=PeerId});Pump();
        Assert.That((Gesture(_peer),HostSeesPeer),Is.EqualTo(((byte)0,(byte)0)),"anyone with the key frees");
        Assert.That(Count(_host,ArrestDef.Handcuffs),Is.EqualTo(1),"Recover hands the cuffs back to the freer");

        Hold(_host,ArrestDef.Handcuffs);
        Action(_peer,"requestGesture",new{gesture=Surrender});Pump();Cuff();
        Assert.That(Gesture(_peer),Is.EqualTo(Arrest));
        for(int n=0;n<127;n++){Action(_peer,"struggle",new{side=(byte)(n&1)});Pump(2);}
        Action(_peer,"struggle",new{side=(byte)0});Pump(4);
        Assert.That(Gesture(_peer),Is.EqualTo(Arrest),"127 alternating leans and a repeated side are not 128");
        Action(_peer,"struggle",new{side=(byte)1});Pump(4);
        Assert.That((Gesture(_peer),HostSeesPeer),Is.EqualTo(((byte)0,(byte)0)),"the 128th alternating lean breaks the handcuffs");
    }
}
