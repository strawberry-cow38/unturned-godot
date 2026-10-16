using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UnturnedBevy;
namespace UnturnedBevyTests;
/// <summary>Original crop plant/harvest over actual UDP: the managed peer is a
/// joined client (no CropManager), so its console `plant` sends PlantCrop and
/// its harvest is the HarvestCrop intent the server validates.</summary>
[TestFixture, NonParallelizable]
public sealed class CropTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    void Pump(int n=12){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp]public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown]public void Cleanup(){_peer.Dispose();_host.Dispose();}
    JsonElement[] Crops(BridgeHost h)=>State(h).GetProperty("crops").EnumerateArray().ToArray();
    int Seeds(ushort id)=>State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray()
        .SelectMany(p=>p.GetProperty("items").EnumerateArray()).Where(i=>i.GetProperty("id").GetUInt16()==id).Sum(i=>i.GetProperty("amount").GetInt32());
    string[] Console(BridgeHost h)=>State(h).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="console")
        .Select(e=>e.GetProperty("data").GetProperty("text").GetString()).ToArray();

    [Test]
    public void AJoinedClientsPlantSpendsASeedAndOnlyAGrownCropInReachHarvests()
    {
        Action(_peer,"console",new{text="plant carrot"});Pump();
        Assert.That(Console(_peer),Does.Contain("-> plant carrot sent to server"));
        Assert.That(Crops(_peer),Is.Empty,"no seed in the bag: the server's PlantCrop validator refuses");
        Action(_peer,"console",new{text="give 330"});Pump();Assert.That(Seeds(330),Is.EqualTo(1));
        Action(_peer,"console",new{text="plant carrot"});Pump();
        Assert.That(Crops(_peer),Has.Length.EqualTo(1));Assert.That(Seeds(330),Is.Zero,"the seed is spent server-side");
        uint young=Crops(_peer)[0].GetProperty("netId").GetUInt32();
        Assert.That(Crops(_peer)[0].GetProperty("grown").GetBoolean(),Is.False);
        Action(_peer,"harvestCrop",new{netId=young});Pump();
        Assert.That(Crops(_peer),Has.Length.EqualTo(1),"an unripe crop refuses the harvest");
        Action(_host,"console",new{text="plant corn grown"});Pump();
        Assert.That(Console(_host),Does.Contain("planted corn (grown) -- UG_FARMSPEED speeds growth; E near a grown crop to harvest"));
        uint ripe=Crops(_peer).Single(c=>c.GetProperty("grown").GetBoolean()).GetProperty("netId").GetUInt32();
        Action(_peer,"console",new{text="teleport 30 0 30"});Pump();
        Action(_peer,"harvestCrop",new{netId=ripe});Pump();
        Assert.That(Crops(_peer).Any(c=>c.GetProperty("netId").GetUInt32()==ripe),"beyond CropReach the server refuses");
        Action(_peer,"console",new{text="teleport 0 0 0"});Pump();
        Action(_peer,"harvestCrop",new{netId=ripe});Pump();
        Assert.That(Crops(_peer).Select(c=>c.GetProperty("netId").GetUInt32()),Is.EqualTo(new[]{young}));
        Assert.That(State(_peer).GetProperty("worldItems").EnumerateArray().Any(i=>i.GetProperty("itemId").GetUInt16()==335),"the corn yield drops as a replicated world item");
    }

    [Test]
    public void TheNativeLookPointIsUsedAndACheatsLockedHostRefusesToPlant()
    {
        Action(_peer,"console",new{text="give 330"});Pump();
        Action(_peer,"console",new{text="plant carrot",at=new[]{1.5f,0.2f,-2f}});Pump();
        var row=Crops(_peer).Single().GetProperty("position").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        Assert.That(row,Is.EqualTo(new[]{1.5f,0.2f,-2f}).Within(.01f),"PlayerController.LookPoint is resolved by the planting client");
        using var locked=new BridgeHost();
        Call(locked,"init",new{contentRoot=Content(),listenPort=0,allowCheats=false,spawn=new[]{0,0,0}});
        Action(locked,"console",new{text="plant corn grown",at=new[]{0f,0,-2f}});for(int i=0;i<8;i++)Call(locked,"tick");
        Assert.That(Console(locked),Does.Contain("console commands are disabled on this server"));
        Assert.That(Crops(locked),Is.Empty,"no free crops (and free yields) on a cheats-locked listen host");
    }
}
