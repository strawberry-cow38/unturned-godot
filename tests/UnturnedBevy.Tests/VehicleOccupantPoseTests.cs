using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.NetTransport.Udp;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;
using NVector=System.Numerics.Vector3;
using NQuat=System.Numerics.Quaternion;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class VehicleOccupantPoseTests
{
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,Converters={new VectorJson(),new ByteArrayJson()}};
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement Call(BridgeHost h,string op,object args=null)=>JsonSerializer.SerializeToElement(h.Handle(op,JsonSerializer.SerializeToElement(args??new{})),Options);
    static NetWorldServer Server(BridgeHost h)=>(NetWorldServer)typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(h);
    static MountedWeaponServices Mounted(BridgeHost h)=>(MountedWeaponServices)typeof(BridgeHost).GetField("_mounted",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(h);
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    static void Pump(BridgeHost h,int count=8,NetWorldClient peer=null){for(int i=0;i<count;i++){peer?.Tick();Call(h,"tick");if(peer!=null)Thread.Sleep(1);}}
    static BridgeHost Host(int port=0){var h=new BridgeHost();Call(h,"init",new{contentRoot=Content(),spawn=new[]{0,0,0},listenPort=port});Pump(h);return h;}
    static uint Vehicle(BridgeHost h,byte type=27,float[] euler=null)=>Call(h,"worldVehicles",new{vehicles=new[]{new{typeId=type,key="body",position=new[]{0,0,0},eulerDegrees=euler??new[]{0f,0f,0f},seatCount=4,health=10000,fuel=100,speedMax=40,climbMax=20,fallMax=100}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
    static void Enter(BridgeHost h,uint id,byte seat){Action(h,"enterVehicle",new{netId=id,seat});Pump(h);Assert.That(Server(h).Vehicles.SeatOccupant(new(id),seat),Is.EqualTo(1));}
    static void Input(BridgeHost h,uint id,bool headOut=true)=>Action(h,"mountInput",new{netId=id,slot=0,aimMode="point",target=new[]{-600f,300f,0f},trigger=false,headOut});
    static NVector N(Vector3 v)=>new(v.x,v.y,v.z);
    static NQuat Hull(VehicleReplication.VehicleEntity v)=>NQuat.CreateFromYawPitchRoll(v.YawDegrees*MathF.PI/180,v.PitchDegrees*MathF.PI/180,v.RollDegrees*MathF.PI/180);
    static void Near(Vector3 actual,NVector expected,string message)=>Assert.That(NVector.Distance(N(actual),expected),Is.LessThan(.025f),message);

    [Test]
    public void OriginalOrdinaryBodySeatUsesWholeHullTransformAndCombatHitsItBeforeAnyNativePublication()
    {
        using var h=Host();uint id=Vehicle(h,0,new[]{20f,65f,-15f});Enter(h,id,3);var server=Server(h);
        server.Vehicles.TryGet(id,out var v);server.Players.TryGetByOwner(1,out var rider);
        var spec=new MountedWeaponCatalog(Content()).For(0);
        var expected=N(v.Pos)+NVector.Transform(spec.BodySeats[3],Hull(v));
        Near(rider.Pos,expected,"Original passenger bodySeats include the driver's source offset, then full accepted hull rotation");
        Assert.That(rider.Stance,Is.EqualTo(MoveInput.WireStanceSitting));
        Assert.That(server.VehicleMounts.All,Is.Empty,"An ordinary vehicle does not manufacture mounted guns");
        server.CombatState.TryGet(1,out var life);float before=life.HealthExact;
        // Use a deliberately offset source body, without registering any native
        // player collider. The old hull-root player cylinder cannot meet this ray.
        var point=rider.Pos+new Vector3(0,.7f,0);
        var tangent=new Vector3(rider.Pos.z,0,-rider.Pos.x).normalized;
        Assert.That(server.Combat.ServerLaunchMounted(0,1,point+tangent*2,-tangent,
            new ServerGunProfile{PlayerDamage=7,MuzzleVelocity=200,BallisticSteps=5,GravityMultiplier=0},npc:true),Is.True);
        Pump(h,2);Assert.That(life.HealthExact,Is.EqualTo(before-7).Within(.001));
        // Negative control: removing the actual host provider returns the old
        // hull-root cylinder; an identical round now misses the rendered seat.
        server.VehicleHost.OccupantPoseOf=null;Pump(h,1);
        server.Combat.ServerLaunchMounted(0,2,point+tangent*2,-tangent,
            new ServerGunProfile{PlayerDamage=7,MuzzleVelocity=200,BallisticSteps=5,GravityMultiplier=0},npc:true);
        Pump(h,2);Assert.That(life.HealthExact,Is.EqualTo(before-7).Within(.001),"The source body anchor, not a nearby hull target, caused the first contact");
    }

    [Test]
    public void TankGunnerCapsuleFollowsAcceptedYawAndSourceRiseOnTheSameTickAsMountState()
    {
        using var h=Host();uint id=Vehicle(h,27,new[]{15f,25f,-12f});Enter(h,id,1);var server=Server(h);
        Input(h,id);Pump(h,1);
        Assert.That(server.VehicleMounts.TryGet(id,out var first)&&first.HeadOut,Is.True);
        Assert.That(first.GunnerRise,Is.EqualTo(.06f).Within(.0001),"Exactly one50Hz MoveToward(3/s), not once per rider or render");
        Pump(h,3);server.VehicleMounts.TryGet(id,out var fourth);
        Assert.That(fourth.GunnerRise,Is.EqualTo(.24f).Within(.0001));
        for(int n=0;n<90;n++){Input(h,id);Pump(h,1);}
        server.VehicleMounts.TryGet(id,out var mount);server.Vehicles.TryGet(id,out var v);server.Players.TryGetByOwner(1,out var rider);
        Assert.That(mount.GunnerRise,Is.EqualTo(1));Assert.That(mount.Mounts[0].Pitch,Is.GreaterThan(5));
        var catalog=new MountedWeaponCatalog(Content());var spec=catalog.For(27);
        var yaw=NQuat.CreateFromAxisAngle(NVector.UnitY,mount.Mounts[0].Yaw*MathF.PI/180);
        var local=MountedWeaponCatalog.V(spec.Mounts[0].Pivot)+NVector.Transform(catalog.TankGunnerSeat+NVector.UnitY*.7f,yaw);
        Near(rider.Pos,N(v.Pos)+NVector.Transform(local,Hull(v)),"Cannon pitch and HipSeated must not move the original gunner body origin");
        var expectedForward=NVector.Transform(-NVector.UnitZ,NQuat.Normalize(Hull(v)*yaw));
        float expectedYaw=MathF.Atan2(-expectedForward.X,-expectedForward.Z)*180/MathF.PI;
        Assert.That(Math.Abs(Mathf.DeltaAngle(rider.YawDegrees,expectedYaw)),Is.LessThan(.1));
        var snapshot=JsonSerializer.SerializeToElement(h.State(),Options).GetProperty("vehicleMounts")[0];
        Assert.That(snapshot.GetProperty("gunnerRise").GetSingle(),Is.EqualTo(1));
        var beforeRebase=rider.Pos;
        Call(h,"rebase",new{expectedEpoch=0,shift=new[]{1024,0,0}});Pump(h,1);
        Near(rider.Pos,N(beforeRebase)+new NVector(-1024,0,0),"Accepted seat pose crosses the origin barrier with its vehicle");
        Action(h,"exitVehicle",new{});Pump(h,20);
        Assert.That(server.VehicleMounts.TryGet(id,out mount)&&mount.HeadOut,Is.False);Assert.That(mount.GunnerRise,Is.Zero);
        var after=rider.Pos;Pump(h,2);Assert.That(rider.Pos,Is.EqualTo(after),"No stale occupied-seat pose after exit");
    }

    [Test]
    public void SavedPartialRiseRestoresThenLowersWithoutInventingAnOccupant()
    {
        using var h=Host();uint id=Vehicle(h);Enter(h,id,1);Input(h,id);Pump(h,12);
        float rise=Mounted(h).Capture().Single().GunnerRise;Assert.That(rise,Is.EqualTo(.72f).Within(.0001));
        string path=Path.Combine(Path.GetTempPath(),$"occupant-{Guid.NewGuid():N}.json");
        try{
            Call(h,"save",new{path});
            using(var doc=JsonDocument.Parse(File.ReadAllText(path)))Assert.That(doc.RootElement.GetProperty("BevyBridge").GetProperty("VehicleMounts")[0].GetProperty("GunnerRise").GetSingle(),Is.EqualTo(rise));
            Call(h,"load",new{path});var saved=Mounted(h).Capture().Single();
            // Load intentionally advances six real50Hz ticks to replicate restored
            // world state. No saved occupant is invented, so the source rise lowers.
            Assert.That(saved.HeadOut,Is.False);Assert.That(saved.GunnerRise,Is.EqualTo(rise-6*.06f).Within(.0001));
            Pump(h,8);Assert.That(Mounted(h).Capture().Single().GunnerRise,Is.Zero);
        }finally{File.Delete(path);}
    }

    [Test]
    public void RealUdpGunnerSeesTheSameBodyAndRiseAndLateJoinCannotRestartTheTransition()
    {
        using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);socket.Bind(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)socket.LocalEndPoint).Port;socket.Close();
        using var h=Host(port);uint id=Vehicle(h);Enter(h,id,0);
        var peer=new NetWorldClient(new UdpClientTransport("127.0.0.1",(ushort)port),"gunner pose",contentHash:NetContent.Hash);
        try{
            peer.Connect();Pump(h,30,peer);peer.SendEnterVehicle(id,1);Pump(h,12,peer);
            peer.SendMountedInput(new(){VehicleNetId=id,Slot=0,AimMode=MountedAimMode.Point,Target=new(-600,300,0),HeadOut=true});Pump(h,5,peer);
            Assert.That(peer.VehicleMounts.TryGet(id,out var view),Is.True);Assert.That(view.GunnerRise,Is.InRange(.06f,.3f));
            Assert.That(peer.Players.TryGetByOwner(peer.PlayerId,out var rider),Is.True);
            var spec=new MountedWeaponCatalog(Content());var yaw=NQuat.CreateFromAxisAngle(NVector.UnitY,view.Mounts[0].Yaw*MathF.PI/180);
            var expected=MountedWeaponCatalog.V(spec.For(27).Mounts[0].Pivot)+NVector.Transform(spec.TankGunnerSeat+NVector.UnitY*(.7f*view.GunnerRise),yaw);
            Near(rider.Pos,expected,"Player pose and mounted rise come from the same accepted snapshot tick");
            Pump(h,25,peer);Assert.That(peer.VehicleMounts.TryGet(id,out view)&&view.GunnerRise==1,Is.True);
            peer.Players.TryGetByOwner(peer.PlayerId,out rider);
            var late=new NetWorldClient(new UdpClientTransport("127.0.0.1",(ushort)port),"late pose",contentHash:NetContent.Hash);
            try{late.Connect();Pump(h,25,late);Assert.That(late.VehicleMounts.TryGet(id,out var joined)&&joined.GunnerRise==1,Is.True);Assert.That(late.Players.TryGetByOwner(peer.PlayerId,out var observed),Is.True);Near(observed.Pos,N(rider.Pos),"Late observer adopts current body pose");}
            finally{late.Disconnect();}
            var server=Server(h);server.Combat.DamagePlayerExternal(peer.PlayerId,1000,0);Pump(h,20,peer);
            Assert.That(server.VehicleHost.IsSeated(peer.PlayerId),Is.False);Assert.That(peer.VehicleMounts.TryGet(id,out view)&&view.GunnerRise==0&&!view.HeadOut,Is.True);
        }finally{peer.Disconnect();}
    }
}
