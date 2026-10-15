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
public sealed class VehicleLightingTests
{
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,Converters={new VectorJson(),new ByteArrayJson()}};
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    T Field<T>(string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_host);
    NetWorldServer Server=>Field<NetWorldServer>("_server");VehicleLightingServices Lights=>Field<VehicleLightingServices>("_vehicleLights");
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp] public void Setup()
    {
        using(var s=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){s.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)s.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown] public void Cleanup(){_peer.Dispose();_host.Dispose();}
    uint Spawn(byte type=11,float battery=10000)
    {
        var id=Call(_host,"worldVehicles",new{vehicles=new[]{new{typeId=type,position=new[]{0,0,0},seatCount=4,health=6000,healthMax=6000,fuel=1000,battery,speedMax=100,engineRegion=new{rear=false,threshold=-.5f,health=2400}}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        var saved=Lights.Capture();Lights.Restore(saved.Select(s=>s with{Flags=0,AlarmTimer=0}).ToArray());Pump();return id;
    }
    void Switch(BridgeHost h,uint id,string action,int value=0){Action(h,"vehicleLighting",new{netId=id,action,value});Pump();}
    JsonElement Row(BridgeHost h,uint id)=>State(h).GetProperty("vehicles").EnumerateArray().Single(v=>v.GetProperty("netId").GetUInt32()==id);
    void Body(uint id,byte flags,bool swamped=false)=>Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,flags,swamped}}});
    void Lamps(uint id)=>Call(_host,"worldVehicleLighting",new{netId=id,lamps=new[]{new{label="headlight_l",center=new[]{-.6f,1f,-2f}},new{label="headlight_r",center=new[]{.6f,1f,-2f}},new{label="lightbar_c",center=new[]{0f,2f,0f}}}});
    [Test]
    public void EveryOriginalFleetTypeRegistersAndStepsIncludingSourceNullPartsAircraftAndShip()
    {
        var catalog=Field<Catalog>("_catalog");var fleet=catalog.Mounted.All.ToArray();Assert.That(fleet.Length,Is.EqualTo(39));
        foreach(var original in fleet)
        {
            Call(_host,"worldVehicles",new{replace=false,vehicles=new[]{new{typeId=original.TypeId,position=new[]{100,20,100},
                health=1000,fuel=1000,battery=10000,seatCount=original.BodySeats.Length}}});
        }
        Pump(12);Assert.That(Server.Vehicles.Count,Is.EqualTo(fleet.Length));
        Assert.That(State(_peer).GetProperty("vehicles").GetArrayLength(),Is.EqualTo(fleet.Length));
        Assert.That(Server.Vehicles.All.All(v=>v.Battery==10000),Is.True);
    }
    [Test]
    public void ActualUdpDriverControlsSourceCyclesAndPassengerOrStaleFlagsCannotOverrideThem()
    {
        uint id=Spawn();Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();
        Switch(_host,id,"CycleHeadlights");Assert.That(Row(_peer,id).GetProperty("flags").GetInt32()&2,Is.Zero,"Non-driver cannot switch lamps");
        Switch(_peer,id,"CycleHeadlights");Assert.That(Row(_peer,id).GetProperty("flags").GetInt32()&6,Is.EqualTo(6));Assert.That(Row(_peer,id).GetProperty("highBeam").GetBoolean(),Is.False);
        Switch(_peer,id,"CycleHeadlights");Assert.That(Row(_peer,id).GetProperty("highBeam").GetBoolean(),Is.True);
        Switch(_peer,id,"SetPattern",2);Assert.That(Row(_peer,id).GetProperty("lightbarPattern").GetInt32(),Is.EqualTo(2));
        Body(id,0);Pump();Assert.That(Row(_peer,id).GetProperty("highBeam").GetBoolean(),Is.True,"Stale host body flags cannot turn off an accepted switch");
        var peerClient=(NetWorldClient)typeof(BridgeHost).GetField("_client",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_peer)!;
        Server.Vehicles.TryGet(id,out var v);peerClient.SendVehicleState(id,v.Pos,Vector3.zero,Vector3.zero,Vector3.zero,0,0,0,false,0,0);Pump();
        Assert.That(v.HighBeam,Is.True);Assert.That(v.Siren,Is.True);Assert.That(v.Alarmed,Is.False);
        peerClient.SendVehicleState(id,v.Pos,Vector3.zero,Vector3.zero,Vector3.zero,0,0,0,false,255,0);Pump();Assert.That(v.Alarmed,Is.False);Assert.That(v.Exploded,Is.False);
        Switch(_peer,id,"CycleHeadlights");Assert.That(v.Headlights,Is.False);Assert.That(v.HighBeam,Is.False);
        Switch(_peer,id,"SetPattern",-1);Assert.That(v.Siren,Is.False);
        Assert.Throws<InvalidOperationException>(()=>Call(_peer,"worldVehicleLighting",new{netId=id,lamps=Array.Empty<object>()}));
    }
    [Test]
    public void ExtractedBatteryLoopRetainsFractionalDrainAlternatorPriorityAndAircraftEarlyReturn()
    {
        var s=new VehicleLightState{Battery=1000,HasSiren=true};s.ToggleHeadlights();for(int i=0;i<50;i++)s.Step(.02);Assert.That(s.Battery,Is.EqualTo(980).Within(.01));
        s.ToggleHeadlights();s.ToggleSiren();for(int i=0;i<50;i++)s.Step(.02);Assert.That(s.Battery,Is.EqualTo(909).Within(.02));
        s.EngineOn=true;for(int i=0;i<50;i++)s.Step(.02);Assert.That(s.Battery,Is.EqualTo(949).Within(.02),"Alternator charges40 without subtracting lamps/siren");
        s.Battery=.1f;s.EngineOn=false;s.Step(.02);Assert.That(s.Battery,Is.Zero);Assert.That(s._headlightsOn||s._highBeam||s._sirenOn,Is.False);
        uint id=Spawn(battery:10000);Assert.That(Row(_peer,id).GetProperty("battery").GetSingle(),Is.EqualTo(10000));
        Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();Switch(_peer,id,"CycleHeadlights");float before=Lights.BatteryFor(id,0);Pump(50);Assert.That(Lights.BatteryFor(id,0),Is.EqualTo(before-20).Within(.03));
        id=Spawn(23,1000);Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();Switch(_peer,id,"CycleHeadlights");before=Lights.BatteryFor(id,0);Pump(50);Assert.That(Lights.BatteryFor(id,0),Is.EqualTo(before),"Source StepHeli returns before road battery loop");
    }
    [Test]
    public void BatteryAndDamageGatesRejectDriverEngineAssertionsAndLateJoinSeesOnlyLastingLampState()
    {
        uint id=Spawn(battery:399);Lamps(id);Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();
        var client=(NetWorldClient)typeof(BridgeHost).GetField("_client",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(_peer)!;
        Server.Vehicles.TryGet(id,out var v);client.SendVehicleState(id,v.Pos,Vector3.zero,Vector3.zero,Vector3.zero,0,0,0,false,255,0);Pump();
        Assert.That(v.EngineOn,Is.False,"Source starter threshold400 cannot be bypassed with the motion stream");
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,battery=10000,flags=255}}});Pump();Assert.That(Lights.BatteryFor(id,0),Is.EqualTo(399));
        Lights.BreakLamp(id,0);Pump();using var late=new BridgeHost();Call(late,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});
        for(int i=0;i<30;i++){Call(late,"tick");Pump(1);}Assert.That(Row(late,id).GetProperty("brokenLampMask").GetInt32(),Is.EqualTo(1));
        Assert.That(State(late).GetProperty("events").EnumerateArray().Any(e=>e.GetProperty("kind").GetString()=="vehicleLampBroken"),Is.False,"Late join consumes lasting damage, never replays break shards");
        var saved=Lights.Capture();Lights.Restore(saved.Select(s=>s with{Battery=1000}).ToArray());
        client.SendVehicleState(id,v.Pos,Vector3.zero,Vector3.zero,Vector3.zero,0,0,0,false,1,0);Pump();Assert.That(v.EngineOn,Is.True);
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,engineDrowned=true,swamped=false}}});
        Assert.That(v.EngineOn,Is.False,"A driver fact cuts an irreversibly drowned engine before any later tick or motion assertion");
        Pump();
        client.SendVehicleState(id,v.Pos,Vector3.zero,Vector3.zero,Vector3.zero,0,0,0,false,1,0);Pump();Assert.That(v.EngineOn,Is.False);
    }
    [Test]
    public void SourceAlarmIsOneUseHornEdgesReplicateAndLoadedLifetimeDoesNotRestartOrReroll()
    {
        uint id=Spawn();var saved=Lights.Capture();Lights.Restore(saved.Select(s=>s with{Flags=64}).ToArray());Pump(40);
        Server.Vehicles.TryGet(id,out var v);Assert.That(v.Alarming,Is.True);var facts=State(_peer).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="vehicleAlarmHorn").ToArray();Assert.That(facts.Length,Is.EqualTo(1));
        Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();Switch(_peer,id,"CycleHeadlights");Assert.That(v.HighBeam,Is.False);
        string path=Path.Combine(TestContext.CurrentContext.WorkDirectory,"lights-"+Guid.NewGuid()+".json");
        try{float remaining=Lights.Capture()[0].AlarmTimer;Call(_host,"save",new{path});_peer.Dispose();Call(_host,"load",new{path});
            var loaded=Lights.Capture()[0];Assert.That(loaded.AlarmTimer,Is.InRange(remaining-1,remaining));Assert.That(loaded.Battery,Is.GreaterThan(9900));
            for(int i=0;i<1600;i++)Call(_host,"tick");Assert.That(Server.Vehicles.All.Single().Alarmed,Is.False);Assert.That(Server.Vehicles.All.Single().Alarming,Is.False);
            for(int i=0;i<40;i++)Call(_host,"tick");Assert.That(Server.Vehicles.All.Single().Alarming,Is.False,"Nearby owner never rearms an exhausted alarm");
        }finally{File.Delete(path);}
    }
    [TestCase(0f),TestCase(100f)]
    public void TiltedOriginalLampContactConsumesDamageBeforeEngineAndRepairCannotBeForgedFromFarAway(float damage)
    {
        uint id=Spawn();Lamps(id);Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,eulerDegrees=new[]{15f,30f,20f}}}});Pump();Server.Vehicles.TryGet(id,out var v);
        var rot=System.Numerics.Quaternion.CreateFromYawPitchRoll(v.YawDegrees*Mathf.Deg2Rad,v.PitchDegrees*Mathf.Deg2Rad,v.RollDegrees*Mathf.Deg2Rad);
        var local=System.Numerics.Vector3.Transform(new(-.6f,1,-2),rot);var point=v.Pos+new Vector3(local.X,local.Y,local.Z);
        float engine=Field<VehicleServices>("_vehicles").EngineHealth(id);var hit=new CombatWorldHit(point,vehicleNetId:id,bodyPart:"hull");
        Server.Combat.DamageBody(new(hit,1,damage,false,Vector3.forward,Server.Session.CurrentTick));Pump();
        Assert.That(v.Health,Is.EqualTo(6000));Assert.That(Field<VehicleServices>("_vehicles").EngineHealth(id),Is.EqualTo(engine));Assert.That(v.BrokenLampMask,Is.EqualTo(1));
        Assert.That(Row(_peer,id).GetProperty("brokenLampMask").GetInt32(),Is.EqualTo(1));Lamps(id);Assert.That(v.BrokenLampMask,Is.EqualTo(1),"Streaming metadata never heals a lamp");
        Assert.Throws<ArgumentException>(()=>Call(_host,"worldVehicleLighting",new{netId=id,lamps=Array.Empty<object>()}));
        Assert.That(v.BrokenLampMask,Is.EqualTo(1));
        Assert.That(Lights.ResolveLamp(id,point),Is.EqualTo(-1));Assert.That(State(_peer).GetProperty("events").EnumerateArray().Count(e=>e.GetProperty("kind").GetString()=="vehicleLampBroken"),Is.EqualTo(1));
        Action(_peer,"console",new{text="teleport 100 0 0"});Pump();Switch(_peer,id,"RepairLamp",0);Assert.That(v.BrokenLampMask,Is.EqualTo(1));
        Action(_peer,"console",new{text="teleport 0 0 0"});Pump();Switch(_peer,id,"RepairLamp",0);Assert.That(v.BrokenLampMask,Is.Zero);Assert.That(Lights.ResolveLamp(id,point),Is.Zero);
        // At exactly .42m the strict source nearest-center rule does not hit.
        Assert.That(Lights.ResolveLamp(id,point+new Vector3(0,.421f,0)),Is.EqualTo(-1));
    }
    [Test]
    public void SourceDamageTriggersAlarmOnlyForHullOrLiveEngineNotAConsumedLens()
    {
        uint id=Spawn();Lamps(id);Action(_peer,"console",new{text="teleport 100 0 0"});Action(_host,"console",new{text="teleport 100 0 0"});Pump();
        void Arm()=>Lights.Restore(Lights.Capture().Select(s=>s with{Flags=64,AlarmTimer=0,AlarmBlip=0,AlarmLit=false}).ToArray());
        Server.Vehicles.TryGet(id,out var v);Arm();Lights.BreakLamp(id,0);Pump(30);Assert.That(v.Alarming,Is.False);
        Call(_host,"worldVehicleDamage",new{netId=id,amount=1,bodyPart="engine"});Pump(1);Assert.That(v.Alarming,Is.True);
        Arm();Call(_host,"worldVehicleDamage",new{netId=id,amount=2400,bodyPart="engine"});Pump(1);Assert.That(v.Alarming,Is.True);
        Arm();Call(_host,"worldVehicleDamage",new{netId=id,amount=1,bodyPart="engine"});Pump(1);Assert.That(v.Alarming,Is.False,"Source dead engine ignores further hits before TriggerAlarm");
        Call(_host,"worldVehicleDamage",new{netId=id,amount=1,bodyPart="hull",engineShare=0});Pump(1);Assert.That(v.Alarming,Is.True);
    }
    [Test]
    public void SwampingStopsElectricsButPersistentDrownedEngineDoesNotKeepRecoveredLampsDark()
    {
        uint id=Spawn();Action(_peer,"enterVehicle",new{netId=id,seat=0});Pump();Switch(_peer,id,"CycleHeadlights");Switch(_peer,id,"ToggleSiren");
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,flags=255,engineDrowned=true,swamped=true}}});Pump();Server.Vehicles.TryGet(id,out var v);
        Assert.That(v.Headlights||v.Siren||v.Alarming||v.Alarmed||v.EngineOn,Is.False);Switch(_peer,id,"CycleHeadlights");Assert.That(v.Headlights,Is.False);
        Body(id,255,false);Pump();Switch(_peer,id,"CycleHeadlights");Assert.That(v.Headlights,Is.True);Assert.That(v.EngineOn,Is.False);
        Call(_host,"worldVehicleDamage",new{netId=id,amount=6000,explodeNow=true});Pump();Assert.That(v.Exploded,Is.True);Assert.That(v.Headlights||v.Siren||v.Alarming||v.EngineOn,Is.False);
        Switch(_peer,id,"CycleHeadlights");Assert.That(v.Headlights,Is.False);
    }
}
