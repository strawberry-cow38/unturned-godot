using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class VehicleDoorTests
{
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,Converters={new VectorJson(),new ByteArrayJson()}};
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    T Field<T>(string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_host);
    NetWorldServer Server=>Field<NetWorldServer>("_server");
    VehicleDoorServices Service=>Field<VehicleDoorServices>("_vehicleDoors");
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp] public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown] public void Cleanup(){_peer.Dispose();_host.Dispose();}
    uint Spawn(byte type=2)
    {
        uint id=Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=type,position=new[]{0,0,0},seatCount=10,health=6000,healthMax=6000,fuel=1000,battery=10000,speedMax=30}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();Pump();return id;
    }
    JsonElement Row(BridgeHost h,uint id)=>State(h).GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==id);
    int Edges(BridgeHost h)=>State(h).GetProperty("events").EnumerateArray().Count(e=>e.GetProperty("kind").GetString()=="vehicleDoor");
    void Driver(uint id){Action(_peer,"vehicleDoor",new{netId=id,driver=true});Pump(4);}
    void Focus(uint id,float x=3){Action(_peer,"vehicleDoor",new{netId=id,origin=new[]{x,.7f,-2.75f},direction=new[]{-1,0,0}});Pump(4);}
    [Test]
    public void ExtractedStateRetainsMidSwingRefusalAndAutoCloseRetryWithoutReplayingEdges()
    {
        int sounds=0;var state=new VehicleDoorState(90,()=>sounds++);
        state.ToggleDoor();state.UpdateDoor(.4f);Assert.That(state.Fold,Is.EqualTo(.5f));
        state.ToggleDoor();Assert.That(state.Open,Is.True);Assert.That(sounds,Is.EqualTo(1));
        state.Restore(true,.2f,.01f);state.UpdateDoor(.02f);Assert.That(state.Open,Is.True);Assert.That(state.Hold,Is.EqualTo(.2f));
        for(int i=0;i<80;i++)state.UpdateDoor(.02f);
        Assert.That(state.Open,Is.False);Assert.That(state.Fold,Is.Zero);Assert.That(sounds,Is.EqualTo(2));
        state.ToggleDoor();state.UpdateDoor(.8f);state.ToggleDoor();state.UpdateDoor(.2f);state.CycleDoor();
        Assert.That(state.Open,Is.False,"boarding during closing does not reverse the source moving guard");
        Assert.That(state.Hold,Is.EqualTo(2.5f));
    }
    [Test]
    public void RealUdpBoardingCycleDriverTogglePassengerRefusalAndSameVehicleSwitch()
    {
        uint id=Spawn();Assert.That(Row(_peer,id).GetProperty("hasBiFoldDoor").GetBoolean(),Is.True);
        Driver(id);Assert.That(Edges(_peer),Is.Zero,"not yet the driver");
        Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump(10);
        Assert.That(Row(_peer,id).GetProperty("doorOpen").GetBoolean(),Is.True);
        float phase=Row(_peer,id).GetProperty("doorFold").GetSingle();Assert.That(phase,Is.InRange(.1f,.7f));
        Driver(id);Assert.That(Edges(_peer),Is.EqualTo(1),"mid-swing refused");
        Pump(45);Driver(id);Assert.That(Row(_peer,id).GetProperty("doorOpen").GetBoolean(),Is.False);
        Pump(45);Assert.That(Row(_peer,id).GetProperty("doorFold").GetSingle(),Is.Zero);
        Action(_peer,"enterVehicle",new{netId=id,seat=1});Pump(45);
        Assert.That(Row(_peer,id).GetProperty("doorOpen").GetBoolean(),Is.True,"same bus seat switch cycles once");
        int before=Edges(_peer);Driver(id);Assert.That(Edges(_peer),Is.EqualTo(before),"passenger key cannot close");
        Pump(150);Assert.That(Row(_peer,id).GetProperty("doorFold").GetSingle(),Is.Zero);
        Action(_peer,"exitVehicle",new{});Pump(45);Assert.That(Row(_peer,id).GetProperty("doorOpen").GetBoolean(),Is.True,"accepted exit cycles");
    }
    [Test]
    public void FocusedIntentUsesOriginalZoneReachAndWorldOcclusionAndRejectsForeignState()
    {
        uint id=Spawn();Action(_peer,"console",new{text="teleport 3 0 -2.75"});Pump();
        // Source direct hull-box fallback with exact original BiFold zone.
        Focus(id);Assert.That(Edges(_peer),Is.EqualTo(1));Pump(45);
        Action(_peer,"vehicleDoor",new{netId=id,origin=new[]{3f,.7f,-2.75f},direction=new[]{1,0,0}});Pump();Assert.That(Edges(_peer),Is.EqualTo(1),"wrong facing");
        Call(_host,"worldDynamicGeometry",new{key="door-occluder",meshes=new[]{new{vertices=new[]{new[]{2f,-1f,-4f},new[]{2f,3f,-4f},new[]{2f,3f,-1f},new[]{2f,-1f,-4f},new[]{2f,3f,-1f},new[]{2f,-1f,-1f}}}}});
        Focus(id);Assert.That(Edges(_peer),Is.EqualTo(1),"world face blocks");
        Call(_host,"worldDynamicGeometry",new{key="door-occluder",meshes=System.Array.Empty<object>()});
        Action(_peer,"console",new{text="teleport 9 0 -2.75"});Pump();Focus(id,9);Assert.That(Edges(_peer),Is.EqualTo(1),"out of reach");
        Focus(id,3);Assert.That(Edges(_peer),Is.EqualTo(1),"forged remote eye");
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,doorOpen=false,doorFold=0}}});Pump();
        Assert.That(Row(_peer,id).GetProperty("doorOpen").GetBoolean(),Is.True,"physics publication cannot overwrite door authority");
        Assert.Throws<InvalidOperationException>(()=>Call(_peer,"worldVehicleState",new{vehicles=new[]{new{netId=id,doorOpen=false}}}));
    }
    [Test]
    public void ExpiredSourceHoldSavesAsInactiveAndReloadsWithoutAnAudioEdge()
    {
        uint id=Spawn();Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump(180);
        Assert.That(Service.Capture().Single().Hold,Is.Zero);
        string path=Path.Combine(Path.GetTempPath(),"bus-expired-"+Guid.NewGuid()+".json");
        try{Call(_host,"save",new{path});Assert.DoesNotThrow(()=>Call(_host,"load",new{path}));Assert.That(Service.Capture().Single().Hold,Is.Zero);Assert.That(Service.Capture().Single().Fold,Is.Zero);Assert.That(Edges(_host),Is.Zero);}
        finally{File.Delete(path);}
    }
    [Test]
    public void LateJoinAdoptsPhaseWithoutAudioAndSaveRestoresRemainingHold()
    {
        uint id=Spawn();Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump(12);
        using var late=new BridgeHost();Call(late,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});
        for(int i=0;i<28;i++){Call(late,"tick");Pump(1);}
        Assert.That(Row(late,id).GetProperty("doorOpen").GetBoolean(),Is.True);Assert.That(Row(late,id).GetProperty("doorFold").GetSingle(),Is.GreaterThan(.5));
        Assert.That(Edges(late),Is.Zero,"late join adopts enduring state, no original sound edge replay");
        string path=Path.Combine(Path.GetTempPath(),"bus-save-"+Guid.NewGuid()+".json");
        var before=Service.Capture().Single();
        try{Call(_host,"save",new{path});Call(_host,"load",new{path});var after=Service.Capture().Single();var expected=new VehicleDoorState(90,()=>{});expected.Restore(before.Open,before.Fold,before.Hold);
            for(int t=0;t<Server.Session.CurrentTick;t++)expected.UpdateDoor(.02f);
            Assert.That(after.Open,Is.EqualTo(expected.Open));Assert.That(after.Fold,Is.EqualTo(expected.Fold));Assert.That(after.Hold,Is.EqualTo(expected.Hold).Within(.00001),"restore keeps remaining hold, then advances only normal initialization ticks");
            int ticks=(int)Math.Ceiling(before.Hold/.02f)+45;for(int i=0;i<ticks;i++)Call(_host,"tick");Assert.That(Service.Capture().Single().Fold,Is.Zero);}
        finally{File.Delete(path);}
    }
}
