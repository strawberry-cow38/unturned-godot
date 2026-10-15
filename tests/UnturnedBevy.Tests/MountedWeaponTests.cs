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

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class MountedWeaponTests
{
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,Converters={new VectorJson(),new ByteArrayJson()}};
    static string Root(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement Call(BridgeHost host,string op,object args=null)=>JsonSerializer.SerializeToElement(host.Handle(op,JsonSerializer.SerializeToElement(args??new{})),Options);
    static JsonElement State(BridgeHost host)=>JsonSerializer.SerializeToElement(host.State(),Options);
    static void Action(BridgeHost host,string name,object args)=>Call(host,"action",new{name,args});
    static NetWorldServer Server(BridgeHost host)=>(NetWorldServer)typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(host);
    static void Pump(BridgeHost host,int count=12,NetWorldClient peer=null){for(int i=0;i<count;i++){peer?.Tick();Call(host,"tick");if(peer!=null)Thread.Sleep(1);}}
    static BridgeHost Host(int port=0){var h=new BridgeHost();Call(h,"init",new{contentRoot=Root(),spawn=new[]{0,0,0},listenPort=port,starterKit=true});Pump(h);return h;}
    static uint Vehicle(BridgeHost h,byte type=27,int seats=2)=>Call(h,"worldVehicles",new{vehicles=new[]{new{typeId=type,key="mount",position=new[]{0,0,0},health=10000,fuel=100,seatCount=seats,speedMax=40,climbMax=20,fallMax=100}}}).GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
    static void Enter(BridgeHost h,uint id,byte seat=0){Action(h,"enterVehicle",new{netId=id,seat});Pump(h);}
    static JsonElement Mount(BridgeHost h,int index=0)=>State(h).GetProperty("vehicleMounts")[0].GetProperty("mounts")[index];
    static JsonElement[] Events(BridgeHost h)=>State(h).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="mountShot").ToArray();
    static void Input(BridgeHost h,uint id,int slot=0,string aimMode="point",bool trigger=false,float yaw=0,float pitch=0,float[] target=null,bool headOut=false)=>Action(h,"mountInput",new{netId=id,slot,aimMode,trigger,yaw,pitch,target=target??new[]{0f,2.8f,-600f},headOut});

    [Test]
    public void SourceBeltAndActualMuzzleAreIndependentOfHeldRifleAndExpireWithInput()
    {
        using var h=Host();uint id=Vehicle(h);Enter(h,id);
        int rifle=State(h).GetProperty("weapon").GetProperty("ammo").GetInt32();
        var meta=Call(h,"catalog").GetProperty("mountedWeapons");
        Assert.That(meta.GetProperty("tank_cannon").GetSingle(),Is.EqualTo(GunDef.FromDatText(File.ReadAllText(Path.Combine(Root(),"tank_cannon.dat"))).BlastRadius));
        Assert.That(meta.TryGetProperty("gsh23",out _),Is.True);Assert.That(meta.TryGetProperty("hmg",out _),Is.True);
        Input(h,id,trigger:true);Pump(h,8);
        Assert.That(Mount(h).GetProperty("ammo").GetInt32(),Is.EqualTo(59));
        var shot=Events(h).Single().GetProperty("data");
        Assert.That(shot.GetProperty("gun").GetString(),Is.EqualTo("tank_cannon"));
        Assert.That(shot.GetProperty("muzzle")[1].GetSingle(),Is.EqualTo(2.8).Within(.01));
        Assert.That(shot.GetProperty("muzzle")[2].GetSingle(),Is.EqualTo(-6.306).Within(.01));
        Assert.That(shot.GetProperty("direction")[2].GetSingle(),Is.EqualTo(-1).Within(.001));
        Assert.That(State(h).GetProperty("weapon").GetProperty("ammo").GetInt32(),Is.EqualTo(rifle));
        Pump(h,140);Assert.That(Events(h).Length,Is.EqualTo(1),"A stale held trigger cannot fire unattended after cycling");
    }

    [Test]
    public void ContinuouslyRefreshedCannonTriggerUsesTheSourceTwoSecondCycle()
    {
        using var h=Host();uint id=Vehicle(h);Enter(h,id);
        for(int i=0;i<95;i++){Input(h,id,trigger:true);Pump(h,1);}
        Assert.That(Events(h).Length,Is.EqualTo(1),"Held intent cannot bypass the cannon's own cycle");
        for(int i=0;i<20;i++){Input(h,id,trigger:true);Pump(h,1);}Pump(h,4);
        var shots=Events(h);Assert.That(shots.Length,Is.EqualTo(2));
        long gap=shots[1].GetProperty("data").GetProperty("tick").GetInt64()-shots[0].GetProperty("data").GetProperty("tick").GetInt64();
        Assert.That(gap,Is.InRange(100,101),"The original float2s cooldown at50Hz includes its float boundary tick");
        Assert.That(Mount(h).GetProperty("ammo").GetInt32(),Is.EqualTo(58));
        Assert.That(Mount(h).GetProperty("cooldownSeconds").GetSingle(),Is.InRange(1.5f,2f));
    }

    [Test]
    public void SourcePointSlewCannotBeBypassedByLookAndParentUsesYawOnly()
    {
        using var h=Host();uint id=Vehicle(h);Enter(h,id);
        Input(h,id,aimMode:"none",trigger:true);Pump(h,8);Assert.That(Events(h),Is.Empty,"Source crosshair trigger needs an active aim point");
        Input(h,id,aimMode:"look",yaw:90,pitch:45,trigger:true);Pump(h,8);
        Assert.That(Events(h),Is.Empty);Assert.That(Mount(h).GetProperty("yaw").GetSingle(),Is.Zero);
        Input(h,id,aimMode:"point",target:new[]{-600f,100f,0f});Pump(h,1);
        // Command arrives over the transport, then source60deg/s ->1.2deg/tick.
        Pump(h,1);float yaw=Mount(h).GetProperty("yaw").GetSingle();
        Assert.That(yaw,Is.InRange(0f,2.41f));
        for(int i=0;i<100;i++){Input(h,id,aimMode:"point",target:new[]{-600f,300f,0f});Pump(h,1);}Pump(h,4);
        Assert.That(Mount(h).GetProperty("yaw").GetSingle(),Is.EqualTo(90).Within(1));
        Assert.That(Mount(h).GetProperty("pitch").GetSingle(),Is.GreaterThan(20));
        Input(h,id,slot:1,trigger:true,target:new[]{-600f,2.8f,0f});Pump(h,8);
        var shot=Events(h).Single().GetProperty("data");
        Assert.That(shot.GetProperty("direction")[0].GetSingle(),Is.EqualTo(-1).Within(.01));
        Assert.That(shot.GetProperty("direction")[1].GetSingle(),Is.EqualTo(0).Within(.01),"Roof HMG inherits cannon yaw, not pitch");
    }

    [Test]
    public void LookMountClampsAndPlayerDoesNotRequireNpcCrew()
    {
        using var h=Host();uint id=Vehicle(h,21,4);Enter(h,id,2);
        Assert.That(Mount(h).GetProperty("crewAlive").GetBoolean(),Is.False);
        Input(h,id,aimMode:"look",yaw:180,pitch:-90,trigger:true);Pump(h,8);
        Assert.That(Mount(h).GetProperty("yaw").GetSingle(),Is.EqualTo(135));
        Assert.That(Mount(h).GetProperty("pitch").GetSingle(),Is.EqualTo(-70));
        Assert.That(Events(h).Length,Is.GreaterThan(0),"Source player mount is usable even without AI crew art");
        Assert.That(Events(h)[0].GetProperty("data").GetProperty("gun").GetString(),Is.EqualTo("dragonfang"));
    }

    [Test]
    public void MountedStateSurvivesSaveLoadAndRebaseWithoutPersistingHeldTrigger()
    {
        using var h=Host();uint id=Vehicle(h);Enter(h,id);Input(h,id,trigger:true);Pump(h,8);
        Pump(h,140);Assert.That(Mount(h).GetProperty("cooldownSeconds").GetSingle(),Is.Zero);
        string path=Path.Combine(Path.GetTempPath(),$"mounted-{Guid.NewGuid():N}.json");
        try{
            Call(h,"save",new{path});Call(h,"load",new{path});Pump(h,8);
            Assert.That(Mount(h).GetProperty("ammo").GetInt32(),Is.EqualTo(59));Pump(h,140);
            Assert.That(Events(h),Is.Empty);Assert.That(Mount(h).GetProperty("ammo").GetInt32(),Is.EqualTo(59));
            Call(h,"rebase",new{expectedEpoch=1,shift=new[]{1024,0,0}});
        }finally{File.Delete(path);}
    }

    [Test]
    public void TrustedNpcUsesOwnGunWithoutPvpOrOwnerHitmarkersAndCannotBeSubmittedRemotely()
    {
        using var h=Host();
        // Original Hind barrel is at(.229,-.136,-6.978). Put the accepted
        // owner's head-height band on its exact launch line, not a camera ray.
        Call(h,"init",new{contentRoot=Root(),spawn=new[]{.229f,-1.736f,-8.978f}});Pump(h);
        uint id=Vehicle(h,23,2);var server=Server(h);server.Combat.PvPEnabled=false;
        server.CombatState.TryGet(1,out var life);float before=life.HealthExact;
        var source=GunDef.FromDatText(File.ReadAllText(Path.Combine(Root(),"gsh23.dat")));
        Call(h,"worldMountedInput",new{netId=id,index=0,aimMode="look",yaw=0,pitch=0,trigger=true,infinite=true});
        Assert.That(server.Combat.LiveBullets,Is.EqualTo(1));Pump(h,8);
        Assert.That(life.HealthExact,Is.EqualTo(before-source.PlayerDamage*source.FalloffAt(2)).Within(.01),"NPC mounted head contact uses source flat damage, not2x player limb rules");
        Assert.That(Mount(h).GetProperty("ammo").GetInt32(),Is.EqualTo(450));
        var shot=Events(h).Single().GetProperty("data");Assert.That(shot.GetProperty("npc").GetBoolean(),Is.True);Assert.That(shot.GetProperty("operatorPlayerId").GetInt32(),Is.Zero);
        Assert.That(State(h).GetProperty("events").EnumerateArray().Any(e=>e.GetProperty("kind").GetString()=="hit"),Is.False);
    }

    [Test]
    public void ForeignSeatSpoofedStateAndReplayedSequenceCannotSpendOrRefillABelt()
    {
        using var h=Host();uint id=Vehicle(h);var server=Server(h);
        Input(h,id,trigger:true);Pump(h,8);Assert.That(Events(h),Is.Empty,"Unseated callers have no mount");
        Enter(h,id);
        Assert.Throws<ArgumentException>(()=>Action(h,"mountInput",new{netId=id,trigger=true,infinite=true,ammo=999,gun="eaglefire"}));
        var command=new MountedInputCommand{VehicleNetId=id,Slot=1,Sequence=300,Trigger=true,AimMode=MountedAimMode.Point,Target=new(0,2.8f,-600)};
        server.MountedInputRequested(1,command);Pump(h,8);int fired=Events(h).Length;
        command.Sequence=299;server.MountedInputRequested(1,command);Pump(h,20);
        Assert.That(Events(h).Length,Is.EqualTo(fired),"A late held-input sequence must not reactivate fire");
        command.Sequence=301;command.Trigger=false;server.MountedInputRequested(1,command);Pump(h,8);
        Assert.That(Events(h).Length,Is.EqualTo(fired));
        Assert.That(Mount(h,1).GetProperty("ammo").GetInt32(),Is.EqualTo(400-fired));
    }

    [Test]
    public void RearDeckDepressionAndHostCrewGatesUseOriginalMountRules()
    {
        using var h=Host();uint id=Vehicle(h);Enter(h,id);
        for(int i=0;i<180;i++){Input(h,id,aimMode:"point",target:new[]{0f,-600f,600f});Pump(h,1);}Pump(h,8);
        Assert.That(Math.Abs(Mount(h).GetProperty("yaw").GetSingle()),Is.EqualTo(180).Within(.01));
        Assert.That(Mount(h).GetProperty("pitch").GetSingle(),Is.EqualTo(-15*.95f).Within(.001));
        Action(h,"exitVehicle",new{});Pump(h);
        id=Vehicle(h,21,4);
        Call(h,"worldMountedInput",new{netId=id,index=0,trigger=true});Pump(h,8);Assert.That(Events(h),Is.Empty,"Door-gun AI has no crew until the host installs one");
        Call(h,"worldMountedInput",new{netId=id,index=0,trigger=true,crewAlive=true});Pump(h,8);
        Assert.That(Events(h).Length,Is.EqualTo(1));Assert.That(Mount(h).GetProperty("ammo").GetInt32(),Is.EqualTo(200));
        Call(h,"worldMountedInput",new{netId=id,index=0,trigger=true,crewAlive=false});Pump(h,8);Assert.That(Events(h).Length,Is.EqualTo(1));
    }

    [Test]
    public void QueuedPointIntentAndRetainedMuzzleFactsCrossTheOriginBarrierTogether()
    {
        using var h=Host();uint id=Vehicle(h);Enter(h,id);
        Input(h,id,trigger:true);Pump(h,8);float muzzle=Events(h).Single().GetProperty("data").GetProperty("muzzle")[0].GetSingle();
        Input(h,id,aimMode:"point",target:new[]{-600f,100f,0f});
        Call(h,"rebase",new{expectedEpoch=0,shift=new[]{1024,0,0}});Pump(h,4);
        Assert.That(Events(h).Single().GetProperty("data").GetProperty("muzzle")[0].GetSingle(),Is.EqualTo(muzzle-1024).Within(.001));
        Assert.That(Mount(h).GetProperty("yaw").GetSingle(),Is.GreaterThan(0),"Translated target remains left of its translated ring");
        Assert.That(Mount(h).GetProperty("ammo").GetInt32(),Is.EqualTo(59));
    }

    [Test]
    public void RealUdpGunnerTakesPriorityAndAllPeersSeeSameBeltAndAcceptedFacts()
    {
        using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);socket.Bind(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)socket.LocalEndPoint).Port;socket.Close();
        using var h=Host(port);uint id=Vehicle(h);Enter(h,id);
        var peer=new NetWorldClient(new UdpClientTransport("127.0.0.1",(ushort)port),"gunner",contentHash:NetContent.Hash);
        peer.Connect();Pump(h,30,peer);Assert.That(peer.State,Is.EqualTo(NetSessionState.Connected));
        var facts=new List<MountedShotEvent>();peer.MountedShot+=facts.Add;
        using(var remote=new BridgeHost())
        {
            Call(remote,"init",new{contentRoot=Root(),connectHost="127.0.0.1",connectPort=port});
            Assert.Throws<InvalidOperationException>(()=>Call(remote,"worldMountedInput",new{netId=id,index=0,npc=true,infinite=true,trigger=true}));
        }
        Input(h,id,trigger:true);Pump(h,8,peer);
        Assert.That(facts.Count,Is.EqualTo(1));Assert.That(peer.VehicleMounts.TryGet(id,out var view),Is.True);Assert.That(view.Mounts[0].Ammo,Is.EqualTo(59));
        peer.SendEnterVehicle(id,1);Pump(h,15,peer);Assert.That(Server(h).Vehicles.SeatOccupant(new(id),1),Is.EqualTo(peer.PlayerId));
        Input(h,id,slot:1,trigger:true);Pump(h,8,peer);Assert.That(facts.Count,Is.EqualTo(1),"Driver fallback disappears immediately when real gunner boards");
        peer.SendMountedInput(new(){VehicleNetId=id,Slot=1,Trigger=true,HeadOut=true,AimMode=MountedAimMode.Point,Target=new(0,2.8f,-600)});Pump(h,8,peer);
        Assert.That(facts.Count,Is.GreaterThan(1));Assert.That(facts[^1].Shot.PlayerId,Is.EqualTo(peer.PlayerId));Assert.That(facts[^1].Shot.Gun,Is.EqualTo("hmg"));
        Assert.That(peer.VehicleMounts.TryGet(id,out view)&&view.HeadOut,Is.True);
        peer.SendExitVehicle();Pump(h,15,peer);Assert.That(peer.VehicleMounts.TryGet(id,out view)&&view.HeadOut,Is.False);
        Input(h,id,slot:1,trigger:true);Pump(h,8,peer);Assert.That(facts[^1].Shot.PlayerId,Is.EqualTo(1));
        var late=new NetWorldClient(new UdpClientTransport("127.0.0.1",(ushort)port),"late mount view",contentHash:NetContent.Hash);
        var lateFacts=new List<MountedShotEvent>();late.MountedShot+=lateFacts.Add;late.Connect();Pump(h,30,late);
        Assert.That(late.VehicleMounts.TryGet(id,out var lateView),Is.True);
        Assert.That(lateView.Mounts[0].Ammo,Is.EqualTo(59));Assert.That(lateFacts,Is.Empty,"Join snapshots contain current belts, not historical gunfire");
        late.Disconnect();ushort previousOwner=peer.PlayerId;peer.Disconnect();Pump(h,8);
        var replacement=new NetWorldClient(new UdpClientTransport("127.0.0.1",(ushort)port),"replacement gunner",contentHash:NetContent.Hash);
        replacement.Connect();Pump(h,30,replacement);replacement.SendEnterVehicle(id,1);Pump(h,12,replacement);
        int before=Events(h).Length;
        replacement.SendMountedInput(new(){VehicleNetId=id,Slot=1,AimMode=MountedAimMode.Point,Target=new(0,2.8f,-600),Trigger=true});Pump(h,8,replacement);
        Assert.That(Events(h).Length,Is.GreaterThan(before),$"New owner {replacement.PlayerId} must not inherit previous owner {previousOwner}'s input sequence");
        replacement.Disconnect();
    }
}
