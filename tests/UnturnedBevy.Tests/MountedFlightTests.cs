using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class MountedFlightTests
{
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,Converters={new VectorJson(),new ByteArrayJson()}};
    BridgeHost _host,_remote;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement Call(BridgeHost h,string op,object args=null)=>JsonSerializer.SerializeToElement(h.Handle(op,JsonSerializer.SerializeToElement(args??new{})),Options);
    static JsonElement State(BridgeHost h)=>JsonSerializer.SerializeToElement(h.State(),Options);
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    NetWorldServer Server=>(NetWorldServer)typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_host);
    void Pump(int count=12,BridgeHost late=null){for(int n=0;n<count;n++){Call(_remote,"tick");if(late!=null)Call(late,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    void Join(BridgeHost h)=>Call(h,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});
    static JsonElement[] Flights(BridgeHost h)=>State(h).GetProperty("bullets").EnumerateArray().ToArray();
    static JsonElement[] Events(BridgeHost h,string name)=>State(h).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()==name).ToArray();
    static Vector3 Vec(JsonElement v)=>new(v[0].GetSingle(),v[1].GetSingle(),v[2].GetSingle());
    uint Vehicle(byte type=27)=>Call(_host,"worldVehicles",new{vehicles=new[]{new{typeId=type,key="flight",position=new[]{0,0,0},seatCount=4,health=10000,fuel=100,speedMax=40,climbMax=20,fallMax=100}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
    void Enter(uint id){Action(_host,"enterVehicle",new{netId=id,seat=0});Pump();}
    void Input(uint id,int slot=0,float[] target=null,bool trigger=true)=>Action(_host,"mountInput",new{netId=id,slot,aimMode="point",target=target??new[]{0f,2.8f,-600f},trigger});
    [SetUp] public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_remote=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});Join(_remote);Pump(24);
        Action(_remote,"console",new{text="teleport 30 0 0"});Pump();
    }
    [TearDown] public void Cleanup(){_remote.Dispose();_host.Dispose();}

    [Test]
    public void ActualUdpCannonFlightMatchesSourceMathAndOneAcceptedBlastRetiresEveryPicture()
    {
        uint id=Vehicle();Enter(id);
        Call(_host,"worldGeometry",new{meshes=new[]{new{vertices=new[]{new[]{-20f,-10f,-400f},new[]{20f,-10f,-400f},new[]{0f,25f,-400f}}}}});
        Input(id);int seen=0;var gun=GunDef.FromDatText(File.ReadAllText(Path.Combine(Content(),"tank_cannon.dat")));
        for(int n=0;n<14;n++)
        {
            Pump(1);var flights=Flights(_remote);if(flights.Length==0)continue;seen++;
            var f=flights.Single();Assert.That(f.GetProperty("rocket").GetBoolean(),Is.True);
            Assert.That(f.GetProperty("gun").GetString(),Is.EqualTo("tank_cannon"));
            int steps=f.GetProperty("remainingSteps").GetInt32();var origin=Vec(f.GetProperty("origin"));
            Assert.That(Vec(f.GetProperty("velocity")).magnitude,Is.EqualTo(gun.MuzzleVelocity).Within(.001));
            Assert.That(Vec(f.GetProperty("position")).z,Is.EqualTo(origin.z-(gun.BallisticSteps-steps)*gun.MuzzleVelocity*.02f).Within(.001));
        }
        Assert.That(seen,Is.GreaterThan(3),"The actual remote bridge must produce live source flight, not only a muzzle fact");
        Assert.That(typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_remote),Is.Null);
        using var late=new BridgeHost();Join(late);Pump(28,late);
        Assert.That(Flights(late),Is.Empty,"Join does not replay an earlier rocket launch");
        Pump(60,late);
        Assert.That(Flights(_host),Is.Empty);Assert.That(Flights(_remote),Is.Empty);
        var local=Events(_host,"bulletExplosion");var remote=Events(_remote,"bulletExplosion");var joined=Events(late,"bulletExplosion");
        Assert.That(local.Length,Is.EqualTo(1));Assert.That(remote.Length,Is.EqualTo(1));Assert.That(joined.Length,Is.EqualTo(1),"Late observer still receives the newly accepted blast");
        Assert.That(Vec(remote[0].GetProperty("data").GetProperty("position")).z,Is.EqualTo(-400).Within(.01));
        Assert.That(remote[0].GetProperty("data").GetProperty("radius").GetSingle(),Is.EqualTo(gun.BlastRadius));
        Assert.That(remote[0].GetProperty("tick").GetInt64(),Is.EqualTo(local[0].GetProperty("tick").GetInt64()));
        Assert.That(Events(_remote,"bulletRetired").Length,Is.EqualTo(1));
        Assert.That(Server.VehicleMounts.TryGet(id,out var mounts)&&mounts.Mounts[0].Ammo==59,Is.True);
        Assert.That(Server.Combat.LiveBullets,Is.Zero);
        Call(_remote,"disconnect");Join(_remote);Pump(25);Assert.That(Flights(_remote),Is.Empty);Assert.That(Events(_remote,"bulletExplosion"),Is.Empty,"Session replacement cannot replay old FX");
    }

    [Test]
    public void ActualUdpDisconnectClearsAnActivePictureWithoutInventingAnImpact()
    {
        uint id=Vehicle();Enter(id);Input(id);Pump(8);
        Assert.That(Flights(_remote).Length,Is.EqualTo(1));
        Call(_remote,"disconnect");
        Assert.That(Flights(_remote),Is.Empty,"The retained terminal session must not keep a frozen rocket");
        Assert.That(Events(_remote,"bulletRetired"),Is.Empty,"Disconnect is not an accepted collision");
        Assert.That(Events(_remote,"bulletExplosion"),Is.Empty);
        Join(_remote);Pump(25);
        Assert.That(Flights(_remote),Is.Empty,"The new connection does not replay the old launch");
    }

    [Test]
    public void ActualUdpWaterContinuationAdoptsAcceptedPointAndVelocityWithoutAnyRemoteGeometry()
    {
        uint id=Vehicle();Enter(id);
        Call(_host,"worldCosmeticGeometry",new{waterBoxes=new[]{new{center=new[]{0f,2.5f,0f},size=new[]{1000f,.2f,1000f}}}});
        for(int n=0;n<10;n++){Input(id,1,new[]{0f,-100f,-600f},false);Pump(1);}
        Input(id,1,new[]{0f,-100f,-600f});Pump(1);Input(id,1,new[]{0f,-100f,-600f},false);
        var historical=new Dictionary<int,JsonElement>();int paired=0;
        for(int n=0;n<30;n++)
        {
            foreach(var host in Flights(_host))historical[host.GetProperty("remainingSteps").GetInt32()]=host.Clone();
            Pump(1);
            foreach(var remote in Flights(_remote))
            {
                if(remote.GetProperty("pierced").GetInt32()!=1)continue;
                int steps=remote.GetProperty("remainingSteps").GetInt32();if(!historical.TryGetValue(steps,out var host))continue;
                Assert.That((Vec(remote.GetProperty("position"))-Vec(host.GetProperty("position"))).magnitude,Is.LessThan(.001));
                Assert.That((Vec(remote.GetProperty("velocity"))-Vec(host.GetProperty("velocity"))).magnitude,Is.LessThan(.001));paired++;
            }
        }
        Assert.That(paired,Is.GreaterThan(5),"Accepted continuation must reach the separate read-only remote view");
        var f=Flights(_remote).Single();var gun=GunDef.FromDatText(File.ReadAllText(Path.Combine(Content(),"hmg.dat")));
        Assert.That(Vec(f.GetProperty("velocity")).magnitude,Is.EqualTo(gun.MuzzleVelocity*CosmeticRules.WaterVelKeep).Within(.001));
        Assert.That(Events(_remote,"bulletExplosion"),Is.Empty);Assert.That(Events(_host,"bulletPierced").Length,Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(()=>Call(_remote,"worldCosmeticGeometry",new{waterBoxes=Array.Empty<object>()}));
    }

    [Test]
    public void AcceptedNpcPlayerContactStopsThePictureEvenWithoutALocalPlayerGeometryCopy()
    {
        uint id=Vehicle(23);
        // Hind's actual forward barrel: source NPC rounds hit the player bit.
        Action(_remote,"console",new{text="teleport 0.229 -1.736 -8.978"});Pump();
        Server.Players.TryGetByOwner(2,out var target);Server.CombatState.TryGet(2,out var life);float hp=life.HealthExact;
        Call(_host,"worldMountedInput",new{netId=id,index=0,aimMode="look",yaw=0,pitch=0,trigger=true,infinite=true});Pump(12);
        Assert.That(life.HealthExact,Is.LessThan(hp));Assert.That(Flights(_host),Is.Empty,"Actual accepted player hit retires its mounted picture");
        Assert.That(Flights(_remote),Is.Empty);Assert.That(Events(_remote,"bulletRetired").Length,Is.EqualTo(1));
        Assert.That(Events(_remote,"hit"),Is.Empty,"An NPC cosmetic event does not invent an owner hitmarker");
        Assert.That(Events(_remote,"bulletExplosion"),Is.Empty);
    }

    [Test]
    public void ReadOnlyFlightViewBoundsRetentionAndUsesActualSourceExpiryAndSnapshotClock()
    {
        var events=new List<string>();var view=new RemoteMountedFlights(new MountedWeaponCatalog(Content()),(kind,data,tick)=>events.Add(kind));
        var shot=new MountedShotEvent{VehicleNetId=1,Mount=0,Tick=100,Sequence=1,Shot=new(){PlayerId=1,Gun="tank_cannon",Origin=new(0,2,-6),Dir=new(0,0,-1)}};
        view.Launch(shot);view.Launch(shot);Assert.That(view.Count,Is.EqualTo(1));
        view.Step(99);var initial=JsonSerializer.SerializeToElement(view.Snapshots(),Options)[0];Assert.That(Vec(initial.GetProperty("position")).z,Is.EqualTo(-6));
        view.Step(101);var moved=JsonSerializer.SerializeToElement(view.Snapshots(),Options)[0];Assert.That(Vec(moved.GetProperty("position")).z,Is.EqualTo(-11));
        view.Step(10000);Assert.That(view.Count,Is.Zero);Assert.That(events,Is.Empty,"Local cosmetic expiry does not publish authoritative impact facts");
        for(int n=0;n<RemoteMountedFlights.MaxFlights+8;n++){shot.Tick=(uint)(100+n);view.Launch(shot);}
        Assert.That(view.Count,Is.EqualTo(RemoteMountedFlights.MaxFlights));
    }
}
