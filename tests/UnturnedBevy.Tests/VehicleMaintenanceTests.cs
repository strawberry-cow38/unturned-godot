using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;
using Q=System.Numerics.Quaternion;
using V=System.Numerics.Vector3;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class VehicleMaintenanceTests
{
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,Converters={new VectorJson(),new ByteArrayJson()}};
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    T Field<T>(string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_host);
    NetWorldServer Server=>Field<NetWorldServer>("_server");VehicleMaintenanceServices Service=>Field<VehicleMaintenanceServices>("_vehicleMaintenance");
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp] public void Setup()
    {
        using(var s=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){s.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)s.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown] public void Cleanup(){_peer.Dispose();_host.Dispose();}
    uint Spawn(byte type=11,float fuel=60000,float health=6000)
    {
        var id=Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=type,position=new[]{0,0,0},seatCount=4,health,healthMax=health,fuel,battery=10000,speedMax=100,engineRegion=new{rear=false,threshold=-.5f,health=2400}}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        var lights=Field<VehicleLightingServices>("_vehicleLights");lights.Restore(lights.Capture().Select(s=>s with{Flags=0,AlarmTimer=0}).ToArray());Pump();return id;
    }
    JsonElement Row(BridgeHost h,uint id)=>State(h).GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==id);
    void Request(uint id,string action,int index=0){Action(_peer,"vehicleMaintenance",new{netId=id,action,index});Pump();}
    object Pane(string label="windshield",float z=-2)=>new{label,center=new[]{0f,1f,z},size=new[]{2f,1f,.02f},rotation=new[]{0f,0f,0f,1f},scale=new[]{1f,1f,1f}};
    void Register(uint id,float z=-2)=>Call(_host,"worldVehicleGlazing",new{netId=id,panes=new[]{Pane(z:z)},tireMask=15});
    int Events(BridgeHost h,string kind)=>State(h).GetProperty("events").EnumerateArray().Count(e=>e.GetProperty("kind").GetString()==kind);
    [Test]
    public void EveryOriginalFleetFuelAndHullValueArrivesWithoutOldSignedWireTruncation()
    {
        foreach(var original in Field<Catalog>("_catalog").Mounted.All)
        {
            var spec=original.Source.GetProperty("spec");float fuel=spec.GetProperty("Fuel").GetSingle();float hp=spec.GetProperty("Health").GetSingle()*10;
            uint id=Spawn(original.TypeId,fuel,hp);var row=Row(_peer,id);
            Assert.That(row.GetProperty("fuel").GetSingle(),Is.EqualTo(fuel).Within(.5),original.Key);
            Assert.That(row.GetProperty("health").GetSingle(),Is.EqualTo(hp).Within(.5),original.Key);
        }
    }
    [Test]
    public void AcceptedGlassBreakRemovesUploadedTrianglesBeforeNativeRefreshAndRemoteRepairRestoresThem()
    {
        uint id=Spawn();Register(id);Call(_host,"worldDynamicGeometry",new{key="vehicle:"+id,meshes=new[]{new{vehicleNetId=id,bodyPart="glass:0",vertices=new[]{new[]{-1f,.5f,-2f},new[]{1f,.5f,-2f},new[]{0f,2f,-2f}}}}});
        var geometry=Field<WorldGeometry>("_geometry");Assert.That(geometry.RayDetailed(new(0,1,0),new(0,1,-4),out _),Is.True);
        var hit=new CombatWorldHit(new(0,1,-2),vehicleNetId:id,bodyPart:"glass:0");Server.Combat.DamageBody(new(hit,1,0,false,Vector3.forward,Server.Session.CurrentTick));
        Assert.That(geometry.RayDetailed(new(0,1,0),new(0,1,-4),out _),Is.False,"Authority hole is immediate, before renderer uploads a replacement mesh");Pump();
        Assert.That(Row(_peer,id).GetProperty("brokenGlassMask").GetUInt32(),Is.EqualTo(1));Assert.That(Row(_peer,id).GetProperty("health").GetSingle(),Is.EqualTo(6000));
        Assert.That(Events(_peer,"vehicleGlassBroken"),Is.EqualTo(1));var fact=State(_peer).GetProperty("events").EnumerateArray().Single(e=>e.GetProperty("kind").GetString()=="vehicleGlassBroken").GetProperty("data");
        Assert.That(fact.GetProperty("normal")[2].GetSingle(),Is.EqualTo(-1));Assert.That(fact.GetProperty("halfSize")[2].GetSingle(),Is.EqualTo(.2f));
        Register(id);Assert.That(Service.Solid(id,"glass:0"),Is.False,"Metadata refresh never heals");
        Assert.Throws<ArgumentException>(()=>Call(_host,"worldVehicleGlazing",new{netId=id,panes=Array.Empty<object>(),tireMask=15}));
        Action(_peer,"console",new{text="teleport 20 0 0"});Pump();Request(id,"RepairGlass");Assert.That(Service.Solid(id,"glass:0"),Is.False);
        Action(_peer,"console",new{text="teleport 0 0 0"});Pump();Request(id,"RepairGlass");Assert.That(Service.Solid(id,"glass:0"),Is.True);
        Assert.That(geometry.RayDetailed(new(0,1,0),new(0,1,-4),out _),Is.True);Assert.That(Events(_peer,"vehicleGlassRepaired"),Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(()=>Call(_peer,"worldVehicleGlazing",new{netId=id,panes=Array.Empty<object>(),tireMask=0}));
    }
    [Test]
    public void PaneResolutionUsesCurrentArticulationFullHullRotationRawLocalToleranceAndOriginalTieOrder()
    {
        uint id=Spawn();Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,eulerDegrees=new[]{15f,30f,20f}}}});Server.Vehicles.TryGet(id,out var body);
        Q rot=Q.CreateFromAxisAngle(V.UnitY,.6f);float[] rotation=[rot.X,rot.Y,rot.Z,rot.W];
        Call(_host,"worldVehicleGlazing",new{netId=id,panes=new[]{new{label="r_front",center=new[]{1f,1f,-2f},size=new[]{2f,1f,.02f},rotation,scale=new[]{2f,1f,.5f}},new{label="l_front",center=new[]{1f,1f,-2f},size=new[]{2f,1f,.02f},rotation,scale=new[]{2f,1f,.5f}}},tireMask=0});
        Vector3 World(V point){var v=V.Transform(point,Q.CreateFromYawPitchRoll(body.YawDegrees*Mathf.Deg2Rad,body.PitchDegrees*Mathf.Deg2Rad,body.RollDegrees*Mathf.Deg2Rad));return body.Pos+new Vector3(v.X,v.Y,v.Z);}
        Assert.That(Service.ResolveGlass(id,World(new(1,1,-2))),Is.EqualTo(3),"Equal distances preserve original upload order, not canonical sort");
        var inside=new V(1,1,-2)+V.Transform(new V(0,0,.28f)*new V(2,1,.5f),rot);
        Assert.That(Service.ResolveGlass(id,World(inside)),Is.EqualTo(3));
        var outside=new V(1,1,-2)+V.Transform(new V(0,0,.30f)*new V(2,1,.5f),rot);
        Assert.That(Service.ResolveGlass(id,World(outside)),Is.EqualTo(-1));
        Service.BreakGlass(id,3);Assert.That(Service.ResolveGlass(id,World(new(1,1,-2))),Is.EqualTo(2));
        Call(_host,"worldVehicleGlazing",new{netId=id,panes=new[]{new{label="r_front",center=new[]{1f,1f,2f},size=new[]{2f,1f,.02f},rotation,scale=new[]{2f,1f,.5f}},new{label="l_front",center=new[]{1f,1f,2f},size=new[]{2f,1f,.02f},rotation,scale=new[]{2f,1f,.5f}}},tireMask=0});
        Assert.That(Service.ResolveGlass(id,World(new(1,1,-2))),Is.EqualTo(-1));Assert.That(Service.ResolveGlass(id,World(new(1,1,2))),Is.EqualTo(2));
    }
    [Test]
    public void IndexedTireRepairIsFreeAndDoesNotRepairEarlierFlatOrAbsentWheel()
    {
        uint id=Spawn();Register(id);Server.Vehicles.ServerSetPoppedTires(new(id),0b1011,Server.Session.CurrentTick+1);Pump();
        Request(id,"RepairTire",3);Assert.That(Row(_peer,id).GetProperty("poppedTireMask").GetInt32(),Is.EqualTo(3));
        Request(id,"RepairTire",2);Assert.That(Row(_peer,id).GetProperty("poppedTireMask").GetInt32(),Is.EqualTo(3));
        Request(id,"RepairTire",7);Assert.That(Events(_peer,"vehicleTireRepaired"),Is.EqualTo(1));
        Request(id,"RepairTire",0);Assert.That(Row(_peer,id).GetProperty("poppedTireMask").GetInt32(),Is.EqualTo(2));
    }
    [Test]
    public void ManualHornRequiresDriverBatteryAndQuietAlarmAndUsesSourceCooldownNotClientCounters()
    {
        uint id=Spawn();int noise=0;Service.Noise=(_,radius)=>{Assert.That(radius,Is.EqualTo(32));noise++;};
        Request(id,"Horn");Assert.That(Events(_peer,"vehicleHorn"),Is.Zero);
        Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();
        Action(_peer,"vehicleMaintenance",new{netId=id,action="Horn",index=0});Action(_peer,"vehicleMaintenance",new{netId=id,action="Horn",index=0});Pump();
        Assert.That(Events(_peer,"vehicleHorn"),Is.EqualTo(1),"Two same-batch requests cannot bypass authority cooldown");
        Request(id,"Horn");Assert.That(Events(_peer,"vehicleHorn"),Is.EqualTo(1));Pump(30);Request(id,"Horn");Assert.That(noise,Is.EqualTo(2));
        var lights=Field<VehicleLightingServices>("_vehicleLights");lights.Restore(lights.Capture().Select(s=>s with{Battery=0}).ToArray());Pump(30);Request(id,"Horn");Assert.That(noise,Is.EqualTo(2));
        lights.Restore(lights.Capture().Select(s=>s with{Battery=10000,Flags=64,AlarmTimer=20}).ToArray());Request(id,"Horn");Assert.That(noise,Is.EqualTo(2));
    }
    [Test]
    public void SaveLoadAndLateJoinPreservePaneDamageWithoutReplayingBreakFactsOrResettingHorn()
    {
        uint id=Spawn();Register(id);Service.BreakGlass(id,0);Pump();using var late=new BridgeHost();Call(late,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});
        for(int i=0;i<30;i++){Call(late,"tick");Pump(1);}Assert.That(Row(late,id).GetProperty("brokenGlassMask").GetUInt32(),Is.EqualTo(1));Assert.That(Events(late,"vehicleGlassBroken"),Is.Zero);
        string path=Path.Combine(TestContext.CurrentContext.WorkDirectory,"maintenance-"+Guid.NewGuid()+".json");
        try{Call(_host,"save",new{path});Call(_host,"load",new{path});var row=Server.Vehicles.All.Single();Assert.That(row.BrokenGlassMask,Is.EqualTo(1));Assert.That(Service.ResolveGlass(row.NetIdValue,new(0,1,-2)),Is.EqualTo(-1));Assert.That(row.Fuel,Is.EqualTo(60000));}
        finally{File.Delete(path);}
    }
    [Test]
    public void ActualRemoteShotBreaksIntactWreckPaneWithoutRevivingOrDamagingTheHull()
    {
        uint id=Spawn();Register(id);Server.Vehicles.ServerPublishVitals(new(id),60000,0,0,true,Server.Session.CurrentTick+1);
        Action(_host,"console",new{text="teleport 10 0 0"});Pump();
        Call(_host,"worldDynamicGeometry",new{key="vehicle:"+id,meshes=new[]{new{vehicleNetId=id,bodyPart="glass:0",vertices=new[]{new[]{-2f,0f,-2f},new[]{2f,0f,-2f},new[]{0f,4f,-2f}}}}});
        Action(_peer,"console",new{text="give 4"});Pump();
        var page=State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray()
            .First(p=>p.GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("id").GetInt32()==4));
        var gun=page.GetProperty("items").EnumerateArray().First(i=>i.GetProperty("id").GetInt32()==4);
        Action(_peer,"equipAndHold",new{page=page.GetProperty("index").GetInt32(),x=gun.GetProperty("x").GetInt32(),y=gun.GetProperty("y").GetInt32()});Pump(80);
        Action(_peer,"fire",new{origin=new[]{0f,1.6f,0f},dir=new[]{0f,0f,-1f}});Pump(16);
        Assert.That(State(_peer).GetProperty("weapon").GetProperty("ammo").GetInt32(),Is.EqualTo(29),"Actual accepted normal gun command");
        Assert.That(Row(_peer,id).GetProperty("brokenGlassMask").GetUInt32(),Is.EqualTo(1));
        Assert.That(Row(_peer,id).GetProperty("health").GetSingle(),Is.Zero);Assert.That(Row(_peer,id).GetProperty("flags").GetInt32()&32,Is.EqualTo(32));
        Assert.That(Events(_peer,"vehicleGlassBroken"),Is.EqualTo(1));
    }
    [Test]
    public void ShipHornUsesLongOriginalCooldownAndRestoresRemainingTimeWithoutBlockingFirstGeometry()
    {
        uint id=Spawn(28);Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();Request(id,"Horn");
        float remaining=Service.Capture().Single().HornRemaining;Assert.That(remaining,Is.InRange(3.8f,4.1f));
        Pump(30);Request(id,"Horn");Assert.That(Events(_peer,"vehicleHorn"),Is.EqualTo(1));
        string path=Path.Combine(TestContext.CurrentContext.WorkDirectory,"horn-"+Guid.NewGuid()+".json");
        try{remaining=Service.Capture().Single().HornRemaining;Call(_host,"save",new{path});Call(_host,"load",new{path});
            Assert.That(Service.Capture().Single().HornRemaining,Is.InRange(remaining-.5f,remaining));
            uint restored=Server.Vehicles.All.Single().NetIdValue;
            Assert.DoesNotThrow(()=>Register(restored),"Horn-only pre-save entry had no original pane set registered yet");
        }finally{File.Delete(path);}
    }

    [Test]
    public void MalformedPaneRefreshAndSavedNullCannotPartiallyReplaceAcceptedGeometry()
    {
        uint id=Spawn();Register(id);Service.BreakGlass(id,0);var before=Service.Capture().Single();
        Assert.Throws<ArgumentException>(()=>Call(_host,"worldVehicleGlazing",new{netId=id,panes=new[]{Pane(),Pane()},tireMask=15}));
        Assert.Throws<ArgumentException>(()=>Call(_host,"worldVehicleGlazing",new{netId=id,panes=new[]{new{label="windshield",center=new[]{0,1,-2},size=new[]{2,1,0},rotation=new[]{0,0,0,0},scale=new[]{1,1,1}}},tireMask=15}));
        Assert.Throws<InvalidDataException>(()=>Service.Restore([before with{Panes=[null]}]));
        Assert.That(Service.Capture().Single(),Is.EqualTo(before));Assert.That(Service.Solid(id,"glass:0"),Is.False);
        Request(id,"RepairGlass");Assert.That(Service.ResolveGlass(id,new(0,1,-2)),Is.Zero);
    }

}
