using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class NpcHeliServicesTests
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
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    JsonElement Aircraft=>State(_host).GetProperty("npcHelis").EnumerateArray().Single();
    uint Id=>Aircraft.GetProperty("netId").GetUInt32();
    [SetUp] public void Setup()
    {
        using(var s=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){s.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)s.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown] public void Cleanup(){_peer.Dispose();_host.Dispose();}
    void Configure()=>Call(_host,"worldNpcHelis",new{mapBounds=new[]{-1024,1024,-1024,1024},terrainPatchesReplace=true,
        terrainPatches=new[]{new{origin=new[]{-1024,-1024},cells=1,spacing=2048,heights=new[]{30,30,30,30}}}});
    void Spawn(){Configure();Action(_peer,"console",new{text="spawnheli hind"});Pump();}
    [Test]
    public void RealUdpConsoleSpawnsOriginalAirframeAtFullTerrainHeightAndReturnsOnlyControlInputs()
    {
        Spawn();var id=Id;Assert.That(Server.Vehicles.TryGet(id,out var vehicle),Is.True);
        Assert.That(vehicle.TypeId,Is.EqualTo(23));Assert.That(vehicle.Pos.y,Is.EqualTo(80));Assert.That(vehicle.Health,Is.EqualTo(12500));
        Assert.That(vehicle.EngineOn,Is.True);Assert.That(vehicle.LinVel.magnitude,Is.EqualTo(17).Within(.02));
        var original=vehicle.Pos;Pump(40);Assert.That(vehicle.Pos,Is.EqualTo(original),"The controller must not integrate a second physics body");
        Assert.That(Aircraft.GetProperty("wantHeight").GetSingle(),Is.EqualTo(80));Assert.That(Aircraft.GetProperty("controls").GetProperty("pitch").GetSingle(),Is.LessThan(0));
        Assert.That(State(_peer).GetProperty("vehicles").EnumerateArray().Any(v=>v.GetProperty("netId").GetUInt32()==id),Is.True,"Real UDP peer sees the host aircraft");
        Assert.That(State(_peer).GetProperty("npcHelis").GetArrayLength(),Is.Zero,"Remote clients never receive a second brain");
        Assert.Throws<InvalidOperationException>(()=>Call(_peer,"worldNpcHelis",new{}));
        Call(_host,"worldVehicleState",new{vehicles=new[]{new{netId=id,position=new[]{original.x,70,original.z},linVel=new[]{0,0,0},flags=1}}});Pump(1);
        Assert.That(Aircraft.GetProperty("controls").GetProperty("collective").GetSingle(),Is.EqualTo(1),"Only accepted body telemetry drives the original altitude controller");
    }
    [Test]
    public void ExistingCheatGateRejectsRemoteSpawnAndDriverOwnershipSuspendsAiWithoutSpendingBelts()
    {
        Configure();Server.Transactions.AllowCheats=false;Action(_peer,"console",new{text="spawnheli hind"});Pump();
        Assert.That(Server.Vehicles.Count,Is.Zero);Server.Transactions.AllowCheats=true;Action(_peer,"console",new{text="spawnheli hind"});Pump();
        uint id=Id;Server.Vehicles.TryGet(id,out var vehicle);
        Action(_host,"console",new{text=$"teleport {vehicle.Pos.x} {vehicle.Pos.y} {vehicle.Pos.z}"});Pump();
        Action(_host,"enterVehicle",new{netId=id,seat=0});Pump();
        Assert.That(vehicle.DriverPlayerId,Is.Not.Zero);Assert.That(Aircraft.GetProperty("active").GetBoolean(),Is.False);
        foreach(var c in Aircraft.GetProperty("controls").EnumerateObject())Assert.That(c.Value.GetSingle(),Is.Zero);
        var mounts=Field<MountedWeaponServices>("_mounted");Assert.That(mounts.ReadyNpc(id,0),Is.Null,"An accepted human seat takes priority over infinite AI belt");
    }
    [Test]
    public void AcceptedBulletOriginActivatesAiButMeleeDoesNotAndLoadPreservesTerrainAndLock()
    {
        Spawn();uint id=Id;Server.Vehicles.TryGet(id,out var v);var origin=new Vector3(123,17,-89);
        Server.Combat.DamageBody(new(new(v.Pos,vehicleNetId:id),1,1,true,Vector3.forward,Server.Session.CurrentTick));Pump(1);
        Assert.That(Aircraft.GetProperty("mode").GetString(),Is.EqualTo("patrol"));
        Server.Combat.DamageBody(new(new(v.Pos,vehicleNetId:id),1,1,false,Vector3.forward,Server.Session.CurrentTick,origin));Pump(1);
        Assert.That(Aircraft.GetProperty("mode").GetString(),Is.EqualTo("engaged"));
        Assert.That(Aircraft.GetProperty("lastSeen").Deserialize<float[]>(),Is.EqualTo(new[]{123f,17f,-89f}));
        string path=Path.Combine(TestContext.CurrentContext.WorkDirectory,"npc-heli-save-"+Guid.NewGuid()+".json");
        try
        {
            Call(_host,"save",new{path});Call(_host,"load",new{path});
            Assert.That(Aircraft.GetProperty("mode").GetString(),Is.EqualTo("engaged"));
            Assert.That(Aircraft.GetProperty("wantHeight").GetSingle(),Is.EqualTo(80));
            Assert.That(Aircraft.GetProperty("lockSeconds").GetSingle(),Is.InRange(299,300));
            Assert.That(Aircraft.GetProperty("lastSeen").Deserialize<float[]>(),Is.EqualTo(new[]{123f,17f,-89f}));
        }
        finally{File.Delete(path);}
    }
    [Test]
    public void SourceGridInterpolationClampSignedRowsAndRebaseNeverUseMissingRenderGeometry()
    {
        var grid=new NpcHeliTerrainGrid(J(new{origin=new[]{10,20},cells=1,spacing=new[]{4,-4},heights=new[]{0,10,20,30}}));
        Assert.That(grid.SampleHeight(11,19),Is.EqualTo(7.5f));Assert.That(grid.SampleHeight(-100,100),Is.Zero);
        Assert.That(grid.SampleHeight(100,-100),Is.EqualTo(30));grid.Translate(new(-1024,7,256));
        Assert.That(grid.SampleHeight(-1013,275),Is.EqualTo(14.5f));
        var copy=NpcHeliTerrainGrid.Restore(grid.Capture());Assert.That(copy.SampleHeight(-1013,275),Is.EqualTo(14.5f));
        Assert.Throws<ArgumentException>(()=>new NpcHeliTerrainGrid(J(new{origin=new[]{0,0},cells=1,spacing=0,heights=new[]{0,0,0,0}})));
    }
    [Test]
    public void SourceReadyTicketLaunchesExactlyOnceAndRejectsForeignStaleAndOccupiedCompletions()
    {
        uint id=Call(_host,"worldVehicles",new{vehicles=new[]{new{typeId=23,position=new[]{0,0,0},seatCount=4,health=12500,speedMax=34,climbMax=20,fallMax=42}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        var mounts=Field<MountedWeaponServices>("_mounted");mounts.EquipNpcCrew(id);mounts.AimNpc(id,1,0,0);
        var shot=mounts.ReadyNpc(id,1);Assert.That(shot,Is.Not.Null);
        int before=Server.Combat.LiveBullets;
        var accepted=(shot.Direction+new Vector3(.02f,.01f,0)).normalized;
        Assert.That(mounts.CompleteNpc(shot,accepted),Is.True);Assert.That(mounts.CompleteNpc(shot,accepted),Is.False);
        Assert.That(Server.Combat.LiveBullets,Is.EqualTo(before+1));
        Assert.That(Server.VehicleMounts.TryGet(id,out var state),Is.True);Assert.That(state.Mounts[0].Ammo,Is.EqualTo(450));
        Assert.That(mounts.ReadyNpc(id,1),Is.Null,"Source cycle gates the next accepted shot");Pump(8);
        var facts=State(_peer).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="mountShot").ToArray();
        Assert.That(facts.Length,Is.EqualTo(1));Assert.That(facts[0].GetProperty("data").GetProperty("npc").GetBoolean(),Is.True);
        var remoteDirection=facts[0].GetProperty("data").GetProperty("direction").Deserialize<float[]>();
        Assert.That(remoteDirection[0],Is.EqualTo(accepted.x).Within(.0001));
        shot=mounts.ReadyNpc(id,1);Assert.That(shot,Is.Not.Null);Pump(1);Assert.That(mounts.CompleteNpc(shot,accepted),Is.False,"Ticket cannot cross an authority tick");
        shot=mounts.ReadyNpc(id,1);Server.Vehicles.ServerSetSeat(new(id),1,1,Server.Session.CurrentTick);
        Assert.That(mounts.CompleteNpc(shot,accepted),Is.False,"Same-tick accepted seat takeover defeats a ready AI ticket");
        Server.Vehicles.ServerSetSeat(new(id),1,0,Server.Session.CurrentTick);
        shot=mounts.ReadyNpc(id,1);using var other=new BridgeHost();Call(other,"init",new{contentRoot=Content()});
        var foreign=(MountedWeaponServices)typeof(BridgeHost).GetField("_mounted",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(other);
        Assert.That(foreign.CompleteNpc(shot,accepted),Is.False,"A ticket is bound to one authority instance");
        Assert.That(mounts.CompleteNpc(shot,accepted),Is.True,"A foreign adapter cannot consume the issuer's ticket");
    }
    [Test]
    public void SourceCanopyScaledHeightCylinderAndOriginShiftKeepConcealment()
    {
        var c=new SourceCanopies();c.Set(11,10,2,30,2,3);
        Assert.That(c.CanopyBlocksSegment(new(-10,20,30),new(30,20,30)),Is.True);
        Assert.That(c.CanopyBlocksSegment(new(-10,16,30),new(30,16,30)),Is.False,"Original lower band is base2 +5*3");
        Assert.That(c.CanopyBlocksSegment(new(-10,51,30),new(30,51,30)),Is.False);
        c.Translate(-1024,256);Assert.That(c.CanopyBlocksSegment(new(-1034,20,286),new(-994,20,286)),Is.True);
        c.Set(11,-1014,2,286,0,3);Assert.That(c.CanopyBlocksSegment(new(-1034,20,287),new(-994,20,287)),Is.False,"Updating the same source index replaces its canopy");
    }

}
