using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;
namespace UnturnedBevyTests;
/// <summary>Original VehicleNetSync rope choke point over actual UDP: the
/// managed peer is a remote client of the listen host.</summary>
[TestFixture, NonParallelizable]
public sealed class VehicleTowTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    // Sedan tow nodes: rear z 2.915, front z -3.041 (Vehicle.Build box faces + .15).
    const float SedanSpan=5.956f;
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
    JsonElement Row(uint id)=>State(_peer).GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==id);
    uint Towed(uint id)=>Row(id).GetProperty("towedNetId").GetUInt32();
    void Teleport(float x,float z){Action(_peer,"console",new{text=$"teleport {x} 0 {z}"});Pump();}
    void Tie(uint tower,uint towed){Action(_peer,"attachTow",new{towerNetId=tower,towedNetId=towed});Pump();}
    void Move(uint id,float z){Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,position=new[]{0,0,z}}}});Pump();}
    T Field<T>(string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_host);
    NetWorldServer Server=>Field<NetWorldServer>("_server");
    uint SpawnType(byte type,float z)=>Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=type,position=new[]{0,0,z},seatCount=2,health=6000,healthMax=6000,fuel=1000,battery=10000,speedMax=type==13?0:30}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
    void Wreck(uint id){Server.Vehicles.TryGet(id,out var v);Server.Vehicles.ServerPublish(new(id),v.Pos,new(0,v.YawDegrees,0),v.LinVel,v.AngVel,0,v.Fuel,0,v.Battery,VehicleReplication.FlagExploded,Server.Session.CurrentTick+1);Pump();}
    float Spec(byte type,string key,int axis)=>Field<Catalog>("_catalog").Mounted.For(type)!.Source.GetProperty("spec").GetProperty(key)[axis].GetSingle();

    [Test]
    public void ActualUdpTieUsesTheLiveGapAsRestAndRejectsReachGapDuplicateAndWrecks()
    {
        uint tower=Spawn(0),towed=Spawn(SedanSpan+3);Teleport(3,4);
        Tie(towed,tower);Assert.That(Towed(towed),Is.Zero,"rear-to-front only: the towed car's front is far from the tower's rear here");
        Teleport(30,30);Tie(tower,towed);Assert.That(Towed(tower),Is.Zero,"requester beyond the 9 m RopeReach of both");
        Teleport(3,4);Move(towed,SedanSpan+4.6f);Tie(tower,towed);Assert.That(Towed(tower),Is.Zero,"tow-point gap over TowAttachReach 4.5");
        Move(towed,SedanSpan+3);Tie(tower,towed);
        Assert.That(Towed(tower),Is.EqualTo(towed),"accepted tie echoes back on the tower's row");
        Assert.That(Row(tower).GetProperty("towRestLen").GetSingle(),Is.EqualTo(3f).Within(1/16f),"rest = clamp(gap, 2, 4.5), wire-quantized to 1/16 m");
        uint third=Spawn(-SedanSpan-3);Tie(third,tower);Assert.That(Towed(third),Is.Zero,"one rope per car end");
        Call(_peer,"tick");
        Assert.Throws<InvalidOperationException>(()=>Call(_peer,"worldVehicleState",new{vehicles=new[]{new{netId=tower,towedNetId=0}}}),"a remote peer cannot write the relationship");
    }

    [Test]
    public void EitherEndDetachesAndTheAuthoritySnapsAnOverstretchedRope()
    {
        uint tower=Spawn(0),towed=Spawn(SedanSpan+1);Teleport(3,4);Tie(tower,towed);
        Assert.That(Row(tower).GetProperty("towRestLen").GetSingle(),Is.EqualTo(2f).Within(1/16f),"TowRestMin floor");
        Action(_peer,"detachTow",new{netId=towed});Pump();Assert.That(Towed(tower),Is.Zero,"towed end resolves to the tower");
        Tie(tower,towed);Assert.That(Towed(tower),Is.EqualTo(towed));
        Move(towed,SedanSpan+7);Assert.That(Towed(tower),Is.EqualTo(towed),"still under TowBreakLen 7.5");
        Move(towed,SedanSpan+7.6f);Assert.That(Towed(tower),Is.Zero,"stretched past TowBreakLen: the rope snaps");
    }

    [Test]
    public void RemoteDriverBoardingEitherEndDropsTheRopeAndBlocksANewTie()
    {
        uint tower=Spawn(0),towed=Spawn(SedanSpan+3);Teleport(-1.5f,4);Tie(tower,towed);Assert.That(Towed(tower),Is.EqualTo(towed));
        Action(_peer,"enterVehicle",new{netId=tower,seat=0});Pump();
        Assert.That(Row(tower).GetProperty("driverPlayerId").GetUInt32(),Is.Not.Zero);
        Assert.That(Towed(tower),Is.Zero,"VehicleNetSync enter guard: a remote driver drops the rope");
        Tie(tower,towed);Assert.That(Towed(tower),Is.Zero,"OnAttachTow refuses a remote-driven end");
    }

    [Test]
    public void TheHostRowCarriesTheExactRestWhileTheWireFloorsAndCapsIt()
    {
        uint tower=Spawn(0),towed=Spawn(SedanSpan+4.2f);Teleport(3,4);Tie(tower,towed);
        Assert.That(Towed(tower),Is.EqualTo(towed));
        float host=State(_host).GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==tower).GetProperty("towRestLen").GetSingle();
        Assert.That(host,Is.EqualTo(4.2f).Within(.01f).And.GreaterThan(3.9375f+.1f),"the physics owner's _towRestLen is the exact gap (never yanks on attach)");
        Assert.That(Row(tower).GetProperty("towRestLen").GetSingle(),Is.EqualTo(3.9375f),"a remote peer's cosmetic copy is floored to 1/16 m and capped");
    }

    [Test]
    public void WrecksAndDespawnsDropTheRopeAndAWreckCannotBeTied()
    {
        uint tower=Spawn(0),towed=Spawn(SedanSpan+3);Teleport(3,4);Tie(tower,towed);Assert.That(Towed(tower),Is.EqualTo(towed));
        Wreck(towed);Assert.That(Towed(tower),Is.Zero,"Explode() drops the rope at either end");
        Tie(tower,towed);Assert.That(Towed(tower),Is.Zero,"AttachTow refuses a wreck");
        uint fresh=Spawn(-SedanSpan-3);Teleport(3,-3);Tie(fresh,tower);Assert.That(Towed(fresh),Is.EqualTo(tower));
        Server.Vehicles.ServerRemove(new NetId(tower),Server.Session.CurrentTick+1);Pump();
        Assert.That(Towed(fresh),Is.Zero,"_ExitTree drops the rope of a despawned car");
    }

    [Test]
    public void RemoteBoardingTheTowedEndDropsButTheListenHostsOwnDriverKeepsTheRope()
    {
        uint tower=Spawn(0),towed=Spawn(SedanSpan+3);Teleport(3,4);Tie(tower,towed);
        Action(_host,"enterVehicle",new{netId=tower,seat=0});Pump();
        Assert.That(State(_host).GetProperty("player").GetProperty("vehicleNetId").GetUInt32(),Is.EqualTo(tower));
        Assert.That(Towed(tower),Is.EqualTo(towed),"the host's own driver simulates the rope");
        Teleport(-1.5f,SedanSpan+4);Action(_peer,"enterVehicle",new{netId=towed,seat=0});Pump();
        Assert.That(Row(towed).GetProperty("driverPlayerId").GetUInt32(),Is.Not.Zero);
        Assert.That(Towed(tower),Is.Zero,"a remote driver boarding the TOWED end drops the rope");
    }

    [Test]
    public void ASemiHitchedPairCannotAlsoBeRopedAndARemoteCabDropsItsTrailersRope()
    {
        uint cab=SpawnType(12,0),trailer=SpawnType(13,9.9f);
        Action(_peer,"console",new{text="teleport 2 0 5.7"});Pump();
        Action(_peer,"vehicleHitch",new{trailerNetId=trailer,origin=new[]{2f,.7f,5.7f},direction=new[]{-1f,0,0}});Pump();
        Assert.That(Row(cab).GetProperty("coupledTrailerNetId").GetUInt32(),Is.EqualTo(trailer));
        Tie(cab,trailer);Tie(trailer,cab);
        Assert.That(Towed(cab)+Towed(trailer),Is.Zero,"AttachTow refuses a pair the hitch already joins");
        Server.Vehicles.TryGet(trailer,out var t);
        float rear=t.Pos.z+Spec(13,"BoxCenter",2)+Spec(13,"BoxSize",2)*.5f+.15f;
        uint car=Spawn(rear+3+3.041f);Teleport(3,(t.Pos.z+rear+3+3.041f)*.5f);
        Tie(trailer,car);
        Assert.That(Towed(trailer),Is.EqualTo(car),"a hitched trailer may still tow a car on a rope");
        Teleport(0,1);Action(_peer,"enterVehicle",new{netId=cab,seat=0});Pump();
        Assert.That(Row(cab).GetProperty("driverPlayerId").GetUInt32(),Is.Not.Zero);
        Assert.That(Towed(trailer),Is.Zero,"a remote cab hands its trailer to another host: the rope drops");
    }

    [Test]
    public void AnAircraftTowerNeverSnapsItsRope()
    {
        uint heli=SpawnType(21,0);
        float rear=Spec(21,"BoxCenter",2)+Spec(21,"BoxSize",2)*.5f+.15f;
        uint car=Spawn(rear+3+3.041f);Teleport(3,4);Tie(heli,car);Assert.That(Towed(heli),Is.EqualTo(car));
        Move(car,rear+8+3.041f);
        Assert.That(Towed(heli),Is.EqualTo(car),"StepHeli returns before UpdateTow's TowBreakLen snap");
    }
}
