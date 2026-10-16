using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using UnturnedBevy;
using UnturnedGodot.Net;
namespace UnturnedBevyTests;
/// <summary>Source vehicle hand tools (commands 50/55/56) over actual UDP: the
/// managed peer is a remote client of the listen host, so every request
/// crosses the core choke point and every result comes back replicated.</summary>
[TestFixture, NonParallelizable]
public sealed class VehicleHandToolTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    const ushort Black=1840, Tire=1451, Jack=277; // vehicle_paints.tsv: 1840 = 0a0a0a Midnight Black
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp]public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown]public void Cleanup(){_peer.Dispose();_host.Dispose();}
    uint Spawn(float z)=>Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=3,position=new[]{0,0,z},seatCount=4,health=6000,healthMax=6000,fuel=1000,battery=10000,speedMax=30}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
    static JsonElement Row(BridgeHost h,uint id)=>State(h).GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==id);
    JsonElement Row(uint id)=>Row(_peer,id);
    uint? Paint(BridgeHost h,uint id)=>Row(h,id).GetProperty("paintRgb") is {ValueKind:JsonValueKind.Number} p?p.GetUInt32():null;
    int Mask(uint id)=>Row(id).GetProperty("poppedTireMask").GetInt32();
    static int Count(BridgeHost h,ushort item)=>State(h).GetProperty("inventory").GetProperty("pages").EnumerateArray()
        .SelectMany(p=>p.GetProperty("items").EnumerateArray()).Where(i=>i.GetProperty("id").GetUInt16()==item).Sum(i=>i.GetProperty("amount").GetInt32());
    void Give(ushort item){Action(_peer,"console",new{text=$"give {item}"});Pump();}
    void Teleport(float x,float z){Action(_peer,"console",new{text=$"teleport {x} 0 {z}"});Pump();}
    T Field<T>(string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_host);
    NetWorldServer Server=>Field<NetWorldServer>("_server");
    void Pop(uint id,byte mask){Server.Vehicles.ServerSetPoppedTires(new NetId(id),mask,Server.Session.CurrentTick+1);Pump();}
    void Wreck(uint id){Server.Vehicles.TryGet(id,out var v);Server.Vehicles.ServerPublish(new(id),v.Pos,new(0,v.YawDegrees,0),v.LinVel,v.AngVel,0,v.Fuel,0,v.Battery,VehicleReplication.FlagExploded,Server.Session.CurrentTick+1);Pump();}
    JsonElement[] Jacks(BridgeHost h)=>State(h).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="carjack").ToArray();

    const ushort GasCan=28; // Portable Gas Can, 20000 mL; the sedan tank is 50000 mL
    uint SpawnFuel(float z,float fuel)=>Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=3,position=new[]{0,0,z},seatCount=4,health=6000,healthMax=6000,fuel,battery=10000,speedMax=30}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
    float Tank(uint id)=>Row(id).GetProperty("fuel").GetSingle();
    float CanFuel()=>Call(_peer,"inventoryView").GetProperty("items").EnumerateArray().Single(i=>i.GetProperty("address").GetProperty("id").GetUInt16()==GasCan).GetProperty("fuel").GetSingle();
    (byte Page,byte X,byte Y) CanCell()
    {
        foreach(var p in State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray())
            foreach(var i in p.GetProperty("items").EnumerateArray())
                if(i.GetProperty("id").GetUInt16()==GasCan)return(p.GetProperty("index").GetByte(),i.GetProperty("x").GetByte(),i.GetProperty("y").GetByte());
        throw new InvalidOperationException("no can");
    }
    void Can(string action,uint car){var c=CanCell();Action(_peer,action,new{vehicleNetId=car,page=c.Page,x=c.X,y=c.Y,id=GasCan});Pump();}

    [Test]
    public void ARemoteCanSiphonsAndPoursTheExactHeldInstanceThroughTheHost()
    {
        uint car=SpawnFuel(0,1000),full=SpawnFuel(-8,50000);Give(GasCan);Teleport(3,-3);
        Assert.That(CanFuel(),Is.Zero,"a fresh Portable Gas Can is empty");
        Can("pourVehicleFuel",car);Assert.That(Tank(car),Is.EqualTo(1000f),"[fuel] can is empty: refused");
        Can("siphonVehicleFuel",car);
        Assert.That(Tank(car),Is.Zero,"min(free can 20000, tank 1000) left the car");
        Assert.That(CanFuel(),Is.EqualTo(1000f),"and reached the exact held can through the owner echo");
        Can("siphonVehicleFuel",car);Assert.That(CanFuel(),Is.EqualTo(1000f),"[fuel] that vehicle is empty: refused");
        Can("pourVehicleFuel",full);Assert.That((Tank(full),CanFuel()),Is.EqualTo((50000f,1000f)),"[fuel] that tank is full: refused");
        Teleport(30,30);Can("pourVehicleFuel",car);Assert.That(Tank(car),Is.Zero,"beyond reach");
        Teleport(3,-3);
        var client=(NetWorldClient)typeof(BridgeHost).GetField("_client",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_peer)!;
        var c=CanCell();client.SendPourVehicleFuel(car,c.Page,c.X,c.Y,GasCan,77);Pump();
        Assert.That(Tank(car),Is.Zero,"a stale or forged instance token is refused");
        Can("pourVehicleFuel",car);
        Assert.That((Tank(car),CanFuel()),Is.EqualTo((1000f,0f)),"min(can, free tank) poured back");
        Assert.That(Tank(car),Is.EqualTo(Row(_host,car).GetProperty("fuel").GetSingle()),"host and remote agree");
    }

    [Test]
    public void ARemoteResprayIsSpentFromTheBagAndReplicatesTheCansColour()
    {
        uint car=Spawn(0);Give(Black);Teleport(3,4);
        Assert.That(Paint(_peer,car),Is.Null,"never sprayed: the spawn colour stands");
        Action(_peer,"paintVehicle",new{netId=car,itemId=Black});Pump();
        Assert.That(Paint(_peer,car),Is.EqualTo(0x0a0a0au),"the remote client sees the can's .dat colour");
        Assert.That(Paint(_host,car),Is.EqualTo(0x0a0a0au),"and so does the host");
        Assert.That(Count(_peer,Black),Is.Zero,"the server spent the can");
    }

    [Test]
    public void ResprayRefusesReachForgedCansNonPaintItemsAndWrecks()
    {
        uint car=Spawn(0);Teleport(3,4);
        Action(_peer,"paintVehicle",new{netId=car,itemId=Black});Pump();
        Assert.That(Paint(_peer,car),Is.Null,"a can the sender does not own");
        Give(4);Action(_peer,"paintVehicle",new{netId=car,itemId=(ushort)4});Pump();
        Assert.That(Paint(_peer,car),Is.Null,"an owned item that is not a spraypaint");
        Give(Black);Teleport(30,30);Action(_peer,"paintVehicle",new{netId=car,itemId=Black});Pump();
        Assert.That(Paint(_peer,car),Is.Null,"beyond PaintReach 8 m");
        Assert.That(Count(_peer,Black),Is.EqualTo(1),"a refusal keeps the can");
        Teleport(3,4);Wreck(car);Action(_peer,"paintVehicle",new{netId=car,itemId=Black});Pump();
        Assert.That(Paint(_peer,car),Is.Null,"a burnt-out wreck");
        Assert.That(Count(_peer,Black),Is.EqualTo(1));
    }

    [Test]
    public void ASpareRefitsTheFirstFlatAndIsSpent()
    {
        uint car=Spawn(0);Pop(car,0b0110);Give(Tire);Teleport(3,4);
        Assert.That(Mask(car),Is.EqualTo(0b0110));
        Action(_peer,"fitTire",new{vehicleNetId=car,wheelIndex=(byte)2});Pump();
        Assert.That(Mask(car),Is.EqualTo(0b0100),"the server fits the lowest flat bit; the wire index is not trusted");
        Assert.That(Count(_peer,Tire),Is.Zero,"the spare is spent");
    }

    [Test]
    public void ASpareIsRefusedWithoutAFlatADriverReachOrASpare()
    {
        uint car=Spawn(0);Teleport(3,4);
        Action(_peer,"fitTire",new{vehicleNetId=car,wheelIndex=(byte)0});Pump();
        Assert.That(Mask(car),Is.Zero);
        Pop(car,0b0001);
        Action(_peer,"fitTire",new{vehicleNetId=car,wheelIndex=(byte)0});Pump();
        Assert.That(Mask(car),Is.EqualTo(1),"no spare in the bag");
        Give(Tire);Teleport(30,30);Action(_peer,"fitTire",new{vehicleNetId=car,wheelIndex=(byte)0});Pump();
        Assert.That(Mask(car),Is.EqualTo(1),"beyond reach");
        Action(_host,"enterVehicle",new{netId=car,seat=0});Pump();
        Assert.That(Row(car).GetProperty("driverPlayerId").GetUInt32(),Is.Not.Zero);
        Teleport(3,4);Action(_peer,"fitTire",new{vehicleNetId=car,wheelIndex=(byte)0});Pump();
        Assert.That(Mask(car),Is.EqualTo(1),"isTireReplaceable: not while driven");
        Assert.That(Count(_peer,Tire),Is.EqualTo(1),"every refusal keeps the spare");
        Action(_host,"exitVehicle",new{});Pump();
        Action(_peer,"fitTire",new{vehicleNetId=car,wheelIndex=(byte)0});Pump();
        Assert.That(Mask(car),Is.Zero);
    }

    [Test]
    public void TheCarjackIsRolledByTheServerAndHandedOnlyToThePhysicsOwner()
    {
        uint car=Spawn(0);Give(Jack);Teleport(3,4);
        Action(_peer,"carjack",new{vehicleNetId=car});Pump();
        var jacks=Jacks(_host);
        Assert.That(jacks,Has.Length.EqualTo(1),"the host's physics owner is told to shove");
        var data=jacks[0].GetProperty("data");
        Assert.That(data.GetProperty("netId").GetUInt32(),Is.EqualTo(car));
        var f=data.GetProperty("force").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        var t=data.GetProperty("torque").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        Assert.That(f[0],Is.InRange(-32f,32f));Assert.That(f[1],Is.InRange(480f,544f));Assert.That(f[2],Is.InRange(-32f,32f));
        Assert.That(t,Has.All.InRange(-64f,64f),"Vehicle.Carjack's source ranges");
        Assert.That(Jacks(_peer),Is.Empty,"a remote client never shoves its own copy");
        Assert.That(Count(_peer,Jack),Is.EqualTo(1),"the jack is a tool: not consumed");
    }

    [Test]
    public void TheCarjackRefusesDrivenWreckedAndDistantVehicles()
    {
        uint car=Spawn(0),wreck=Spawn(-8);Teleport(30,30);
        Action(_peer,"carjack",new{vehicleNetId=car});Pump();
        Assert.That(Jacks(_host),Is.Empty,"beyond reach");
        Teleport(3,4);Action(_host,"enterVehicle",new{netId=car,seat=0});Pump();
        Action(_peer,"carjack",new{vehicleNetId=car});Pump();
        Assert.That(Jacks(_host),Is.Empty,"an occupied car is never launched");
        Wreck(wreck);Teleport(3,-6);Action(_peer,"carjack",new{vehicleNetId=wreck});Pump();
        Assert.That(Jacks(_host),Is.Empty,"a wreck");
    }
}
