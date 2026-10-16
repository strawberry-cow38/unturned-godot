using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;
namespace UnturnedBevyTests;
[TestFixture, NonParallelizable]
public sealed class VehicleHitchTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    T Field<T>(string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_host);
    NetWorldServer Server=>Field<NetWorldServer>("_server");
    VehicleLightingServices Lights=>Field<VehicleLightingServices>("_vehicleLights");
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp]public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown]public void Cleanup(){_peer.Dispose();_host.Dispose();}
    uint Spawn(byte type,float x=0,float z=0)
    {
        uint id=Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=type,position=new[]{x,0,z},seatCount=2,health=6000,healthMax=6000,fuel=1000,battery=10000,speedMax=type==13?0:30}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        Lights.Restore(Lights.Capture().Select(s=>s with{Flags=(byte)(s.Flags&~192),AlarmTimer=0,AlarmBlip=0,AlarmLit=false}).ToArray());
        Pump();return id;
    }
    JsonElement Row(BridgeHost h,uint id)=>State(h).GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==id);
    uint Link(uint cab)=>Row(_peer,cab).GetProperty("coupledTrailerNetId").GetUInt32();
    void AtHitch(){Action(_peer,"console",new{text="teleport 2 0 5.7"});Pump();}
    void Hitch(uint trailer, float[] direction=null){Action(_peer,"vehicleHitch",new{trailerNetId=trailer,origin=new[]{2f,.7f,5.7f},direction=direction??new[]{-1f,0,0}});Pump();}
    (uint cab,uint trailer) Couple(){var cab=Spawn(12);var trailer=Spawn(13,z:9.9f);AtHitch();Hitch(trailer);Assert.That(Link(cab),Is.EqualTo(trailer));return(cab,trailer);}
    [Test]
    public void ActualUdpFocusChoosesFirstCabSnapsPinAndRejectsWallWrongRayAndRemoteMutation()
    {
        uint first=Spawn(12),second=Spawn(12,x:.1f),trailer=Spawn(13,z:9.9f);AtHitch();
        Hitch(trailer,new[]{1f,0,0});Assert.That(Link(first),Is.Zero,"wrong facing");
        Call(_host,"worldDynamicGeometry",new{key="hitch-wall",meshes=new[]{new{vertices=new[]{new[]{1.8f,-1,4},new[]{1.8f,3,4},new[]{1.8f,3,7},new[]{1.8f,-1,4},new[]{1.8f,3,7},new[]{1.8f,-1,7}}}}});
        Hitch(trailer);Assert.That(Link(first),Is.Zero,"intervening world owns focus");
        Call(_host,"worldDynamicGeometry",new{key="hitch-wall",meshes=Array.Empty<object>()});
        Hitch(trailer);Assert.That(Link(first),Is.EqualTo(trailer));Assert.That(Link(second),Is.Zero,"registration order, not nearest cab");
        Assert.That(Row(_peer,trailer).GetProperty("position")[2].GetSingle(),Is.EqualTo(9.6f).Within(.01),"kingpin aligned, no initial joint impulse");
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=first,coupledTrailerNetId=0}}});Pump();Assert.That(Link(first),Is.EqualTo(trailer),"raw physics cannot rewrite hitch");
        Assert.Throws<InvalidOperationException>(()=>Call(_peer,"worldVehicleState",new{vehicles=new[]{new{netId=first,coupledTrailerNetId=0}}}));
        Hitch(trailer);Assert.That(Link(first),Is.Zero,"same focused intent detaches");
        Action(_peer,"console",new{text="teleport 20 0 20"});Pump();Hitch(trailer);Assert.That(Link(first),Is.Zero,"forged nearby eye rejected");
    }
    void SendPair(uint cab,uint trailer,float x,byte ack=0)=>Action(_peer,"vehicleState",new{vehicleNetId=cab,pos=new[]{x,0,0},eulerDegrees=new[]{0,0,0},linVel=new[]{4,0,0},angVel=new[]{0,0,0},steerDegrees=0,throttle=1,steer=0,handbrake=false,flags=1,recovAck=ack,trailer=new{netId=trailer,pos=new[]{x,0,9.6f},linVel=new[]{4,0,0},angVel=new[]{0,0,0},yawDegrees=0,pitchDegrees=0,rollDegrees=0}});
    [Test]
    public void RemoteDriverOwnsBothAcceptedPosesAndHostRegainsThemOnlyAfterExit()
    {
        var(cab,trailer)=Couple();Action(_peer,"console",new{text="teleport 0 0 1"});Pump();
        Action(_peer,"enterVehicle",new{netId=cab,seat=0});Pump();Assert.That(Row(_peer,cab).GetProperty("driverPlayerId").GetUInt32(),Is.EqualTo(State(_peer).GetProperty("playerId").GetUInt32()));
        SendPair(cab,trailer,.2f);Pump();Assert.That(Row(_peer,trailer).GetProperty("position")[0].GetSingle(),Is.EqualTo(.2f).Within(.01));
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=trailer,position=new[]{9,0,9.6f}}}});Pump();Assert.That(Row(_peer,trailer).GetProperty("position")[0].GetSingle(),Is.EqualTo(.2f).Within(.01),"no competing host trailer simulation");
        Action(_peer,"exitVehicle",new{});Pump();Assert.That(Link(cab),Is.EqualTo(trailer),"source exit keeps hitch");
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=trailer,position=new[]{.3f,0,9.6f}}}});Pump();Assert.That(Row(_peer,trailer).GetProperty("position")[0].GetSingle(),Is.EqualTo(.3f).Within(.01));
        SendPair(cab,trailer,1);Pump();Assert.That(Row(_peer,trailer).GetProperty("position")[0].GetSingle(),Is.EqualTo(.3f).Within(.01),"departed owner cannot overwrite host");
    }
    [Test]
    public void SourceMagnitudeMismatchPersistsThenUnhooksAndTiltUnhooksImmediately()
    {
        var(cab,trailer)=Couple();
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=cab,linVel=new[]{10,0,0}},new{netId=trailer,linVel=new[]{0,0,10}}}});Pump(10);Assert.That(Link(cab),Is.EqualTo(trailer),"equal speed in different directions survives turning");
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=trailer,linVel=new[]{0,0,0}}}});Pump(6);Assert.That(Server.Vehicles.TryGetCoupledCab(trailer,out _),Is.True);
        Pump(6);Assert.That(Link(cab),Is.Zero,"source >.15s persistent mismatch");
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=cab,linVel=new[]{0,0,0}}}});AtHitch();Hitch(trailer);Assert.That(Link(cab),Is.EqualTo(trailer));
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=trailer,eulerDegrees=new[]{0,0,51}}}});Pump();Assert.That(Link(cab),Is.Zero,"source >50 degree total tilt");
    }
    [Test]
    public void LateJoinAndOrdinalSaveRestoreKeepTheAcceptedRelationship()
    {
        var(cab,trailer)=Couple();using var late=new BridgeHost();Call(late,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});
        for(int i=0;i<28;i++){Call(late,"tick");Pump(1);}Assert.That(Row(late,cab).GetProperty("coupledTrailerNetId").GetUInt32(),Is.EqualTo(trailer));
        string path=Path.Combine(Path.GetTempPath(),"hitch-"+Guid.NewGuid()+".json");
        try{Call(_host,"save",new{path});Call(_host,"load",new{path});
            var fleet=State(_host).GetProperty("vehicles").EnumerateArray().ToArray();var savedCab=fleet.Single(v=>v.GetProperty("typeId").GetByte()==12);var savedTrailer=fleet.Single(v=>v.GetProperty("typeId").GetByte()==13);
            Assert.That(savedCab.GetProperty("coupledTrailerNetId").GetUInt32(),Is.EqualTo(savedTrailer.GetProperty("netId").GetUInt32()));
            Assert.That(savedTrailer.GetProperty("position")[2].GetSingle(),Is.EqualTo(9.6f).Within(.01));}
        finally{File.Delete(path);}
    }
    [Test]
    public void SourceLookSphereUsesActualTrianglesAndSurvivesTheSameWorldShift()
    {
        var geometry=new WorldGeometry();geometry.AddMesh(new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(0,1,0)},new[]{0,1,2},vehicle:7);geometry.Build();
        Assert.That(geometry.VehiclesAt(new(.2f,.2f,.15f),.16f),Does.Contain(7u));
        Assert.That(geometry.VehiclesAt(new(.9f,.9f,0),.16f),Is.Empty,"AABB corner is not source face contact");
        geometry.TranslateWorld(new(800,0,-800));Assert.That(geometry.VehiclesAt(new(800.2f,.2f,-799.85f),.16f),Does.Contain(7u));
        geometry.VehiclePartSolid=(_,_)=>false;Assert.That(geometry.VehiclesAt(new(800.2f,.2f,-799.85f),.16f),Is.Empty);
    }
    [Test]
    public void AcceptedCabRunningAndBrakeStateDriveTrailerLampsUntilDetach()
    {
        var(cab,trailer)=Couple();
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=cab,flags=17}}});Pump();
        Assert.That(Row(_peer,cab).GetProperty("flags").GetInt32()&4,Is.Zero,"coupled cab tail suppressed");
        Assert.That(Row(_peer,trailer).GetProperty("flags").GetInt32()&20,Is.EqualTo(20),"source running and brake pass-through");
        Assert.That(Row(_peer,trailer).GetProperty("flags").GetInt32()&1,Is.Zero,"trailer never becomes an engine");
        Hitch(trailer);Assert.That(Link(cab),Is.Zero);
        Assert.That(Row(_peer,trailer).GetProperty("flags").GetInt32()&4,Is.Zero);
        Assert.That(Row(_peer,cab).GetProperty("flags").GetInt32()&4,Is.EqualTo(4),"cab resumes its own tails");
    }
    [TestCase(false,false),TestCase(true,false),TestCase(false,true),TestCase(true,true)]
    public void WreckAndRetirementClearOnlyTheAcceptedPair(bool cabDies,bool remove)
    {
        var(cab,trailer)=Couple();uint victim=cabDies?cab:trailer;
        if(remove)Server.Vehicles.ServerRemove(new(victim),Server.Session.CurrentTick+1);
        else Call(_host,"worldVehicleDamage",new{netId=victim,amount=6000,explodeNow=true});
        Pump();
        Assert.That(Server.Vehicles.TryGetCoupledCab(trailer,out _),Is.False);
        if(!cabDies||!remove)Assert.That(Link(cab),Is.Zero);
        Assert.That(Server.Vehicles.TryGet(cabDies?trailer:cab,out var survivor),Is.True);
        Assert.That(survivor.Health,Is.EqualTo(6000),"partner must not inherit wreck/removal damage");
    }
    [Test]
    public void IntegratedRebaseTranslatesCachedPairedRecoveryAndItsUndrainedPresentationFact()
    {
        using var local=new BridgeHost();Call(local,"init",new{contentRoot=Content(),spawn=new[]{0,0,0}});
        var rows=Call(local,"worldVehicles",new{vehicles=new[]{new{typeId=12,position=new[]{0f,0f,0f},health=6000,seatCount=2,speedMax=30},new{typeId=13,position=new[]{0,0,9.6f},health=6000,seatCount=0,speedMax=0}}}).GetProperty("vehicles");
        uint cab=rows[0].GetProperty("netId").GetUInt32(),trailer=rows[1].GetProperty("netId").GetUInt32();
        var server=(NetWorldServer)typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(local)!;
        Assert.That(server.Vehicles.ServerSetCoupledTrailer(cab,trailer,server.Session.CurrentTick),Is.True);
        Action(local,"enterVehicle",new{netId=cab,seat=0});for(int n=0;n<12;n++)Call(local,"tick");
        Action(local,"vehicleState",new{vehicleNetId=cab,pos=new[]{0,0,0},eulerDegrees=new[]{0,0,0},linVel=new[]{0,0,0},angVel=new[]{0,0,0},steerDegrees=0,throttle=0,steer=0,handbrake=false,flags=0,recovAck=0});
        for(int n=0;n<12;n++)Call(local,"tick");
        var before=State(local).GetProperty("vehicleRecoveries")[0];
        Call(local,"rebase",new{expectedEpoch=0,shift=new[]{256,0,-256}});
        var state=State(local);var recovery=state.GetProperty("vehicleRecoveries")[0];
        Assert.That(recovery.GetProperty("pos")[0].GetSingle(),Is.EqualTo(before.GetProperty("pos")[0].GetSingle()-256));
        Assert.That(recovery.GetProperty("trailer").GetProperty("pos")[2].GetSingle(),Is.EqualTo(before.GetProperty("trailer").GetProperty("pos")[2].GetSingle()+256));
        Assert.That(recovery.GetProperty("recovCounter").GetByte(),Is.EqualTo(before.GetProperty("recovCounter").GetByte()));
        var fact=state.GetProperty("events").EnumerateArray().Last(e=>e.GetProperty("kind").GetString()=="vehicleRecov").GetProperty("data");
        Assert.That(fact.GetProperty("trailer").GetProperty("pos").GetProperty("z").GetSingle(),Is.EqualTo(recovery.GetProperty("trailer").GetProperty("pos")[2].GetSingle()));
    }

}
