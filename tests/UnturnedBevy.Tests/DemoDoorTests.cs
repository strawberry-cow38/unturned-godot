using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class DemoDoorTests
{
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true,
        Converters={new VectorJson(),new ByteArrayJson()},
    };
    static string Root()
    {
        for(var dir=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);dir!=null;dir=dir.Parent)
            if(Directory.Exists(Path.Combine(dir.FullName,"game","content")))return Path.Combine(dir.FullName,"game","content");
        throw new DirectoryNotFoundException("game/content");
    }
    static JsonElement Call(BridgeHost host,string op,object args=null)=>JsonSerializer.SerializeToElement(host.Handle(op,JsonSerializer.SerializeToElement(args??new{})),JsonOptions);
    static JsonElement State(BridgeHost host)=>JsonSerializer.SerializeToElement(host.State(),JsonOptions);
    static T Private<T>(BridgeHost host,string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(host);
    static void Action(BridgeHost host,string name,object args=null)=>Call(host,"action",new{name,args=args??new{}});
    static void Pump(BridgeHost host,int count=8,BridgeHost remote=null)
    {
        for(int i=0;i<count;i++){if(remote!=null)Call(remote,"tick");Call(host,"tick");if(remote!=null)Thread.Sleep(1);}
    }
    static object Spec=>new{doors=new[]{new{netId=1,position=new[]{0f,0f,0f},hingeOffset=new[]{-.5f,0f,0f},owner=1,geometryKey="demo-door"}}};
    static readonly float[][] Vertices=[[-.5f,0,-.06f],[.5f,0,-.06f],[.5f,2,-.06f],[-.5f,2,-.06f],[-.5f,0,.06f],[.5f,0,.06f],[.5f,2,.06f],[-.5f,2,.06f]];
    static readonly int[] Indices=[0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5];
    static void Geometry(BridgeHost host)=>Call(host,"worldDynamicGeometry",new{key="demo-door",meshes=new[]{new{vertices=Vertices,indices=Indices}}});
    static BridgeHost Init()
    {
        var host=new BridgeHost();Call(host,"init",new{contentRoot=Root(),spawn=new[]{0,0,3}});Call(host,"worldServices",Spec);Geometry(host);Pump(host);return host;
    }
    [Test]
    public void AcceptedDoorCooldownLockAndFixedHingeNoiseUseOneAuthorityDecision()
    {
        using var host=Init();var server=Private<NetWorldServer>(host,"_server");
        var noises=new List<(Vector3,float)>();Private<WorldServices>(host,"_services").DoorNoise=(p,r)=>noises.Add((p,r));
        Action(host,"toggleDoor",new{netId=1});Pump(host);
        Assert.That(server.Interactables.IsDoorOpen(1),Is.True);
        Assert.That(noises.Count,Is.EqualTo(1));Assert.That(noises[0].Item1.x,Is.EqualTo(-.5f));
        Assert.That(noises[0].Item1.y,Is.Zero);Assert.That(noises[0].Item1.z,Is.Zero);
        Assert.That(noises[0].Item2,Is.EqualTo(8));
        Action(host,"toggleDoor",new{netId=1});Pump(host);
        Assert.That(server.Interactables.IsDoorOpen(1),Is.True);Assert.That(noises.Count,Is.EqualTo(1));
        Action(host,"setDoorLocked",new{netId=1,locked=true});Pump(host);
        Assert.That(server.Interactables.IsDoorLocked(1),Is.True);
        Call(host,"worldServices",Spec);
        Assert.That(server.Interactables.IsDoorOpen(1),Is.True);Assert.That(server.Interactables.IsDoorLocked(1),Is.True);
        Call(host,"rebase",new{shift=new[]{256,0,0},expectedEpoch=0});Pump(host,40);
        Action(host,"toggleDoor",new{netId=1});Pump(host);
        Assert.That(server.Interactables.IsDoorOpen(1),Is.False);Assert.That(noises.Count,Is.EqualTo(2));
        Assert.That(noises[1].Item1.x,Is.EqualTo(-256.5f));
        // Persist the authored local hinge offset, not an absolute anchor that
        // would miss CoordinateJson's source-position translation on rebases.
        string path=Path.Combine(Path.GetTempPath(),$"bevy-door-{Guid.NewGuid():N}.json");
        try
        {
            Call(host,"save",new{path});Call(host,"load",new{path});
            server=Private<NetWorldServer>(host,"_server");
            Private<WorldServices>(host,"_services").DoorNoise=(p,r)=>noises.Add((p,r));
            Pump(host,40);Action(host,"toggleDoor",new{netId=1});Pump(host);
            Assert.That(server.Interactables.IsDoorOpen(1),Is.True);
            Assert.That(noises[^1].Item1.x,Is.EqualTo(-256.5f));
        }
        finally{File.Delete(path);}
    }
    [Test]
    public void Original250HealthNearestObstructionAndRetiredGeometryCannotHealOrRevive()
    {
        using var host=Init();var server=Private<NetWorldServer>(host,"_server");
        bool Damage(float amount)=>server.Combat.DamageBarricadeAlong(new(0,1,3),new(0,1,-3),amount,server.Session.CurrentTick);
        var wall=new[]{new[]{-2f,-1f,2f},new[]{2f,-1f,2f},new[]{0f,3f,2f}};
        Call(host,"worldGeometry",new{meshes=new[]{new{vertices=wall}}});Geometry(host);
        Assert.That(Damage(999),Is.False);Assert.That(server.Interactables.DoorCount,Is.EqualTo(1));
        Call(host,"worldGeometry",new{replace=true,meshes=Array.Empty<object>()});Geometry(host);
        Assert.That(Damage(249),Is.True);Call(host,"worldServices",Spec);
        Assert.That(server.Interactables.DoorCount,Is.EqualTo(1));Assert.That(Damage(1),Is.True);
        Assert.That(server.Interactables.DoorCount,Is.Zero);Call(host,"worldServices",Spec);Geometry(host);
        Assert.That(server.Interactables.DoorCount,Is.Zero);
        Assert.That(Call(host,"worldRay",new{from=new[]{0,1,3},to=new[]{0,1,-3}}).GetProperty("hit").GetBoolean(),Is.False);
        Pump(host);Assert.That(State(host).GetProperty("interactables").GetProperty("doors").GetArrayLength(),Is.Zero);
    }
    [Test]
    public void RealUdpPeerCannotLockOwnersDoorAndAcceptedEightShotsRetireItsReplica()
    {
        using var reservation=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);reservation.Bind(new IPEndPoint(IPAddress.Loopback,0));
        ushort port=(ushort)((IPEndPoint)reservation.LocalEndPoint!).Port;reservation.Close();
        using var host=new BridgeHost();using var peer=new BridgeHost();
        Call(host,"init",new{contentRoot=Root(),spawn=new[]{0,0,3},listenPort=port,mapId="Fixture",allowCheats=true});Call(host,"worldServices",Spec);Geometry(host);
        Call(peer,"init",new{contentRoot=Root(),connectHost="127.0.0.1",connectPort=port,mapId="Fixture"});Pump(host,30,peer);
        Action(host,"console",new{text="teleport 3 0 0"});Pump(host,10,peer);
        Action(peer,"setDoorLocked",new{netId=1,locked=true});Pump(host,10,peer);
        Assert.That(Private<NetWorldServer>(host,"_server").Interactables.IsDoorLocked(1),Is.False);
        Action(peer,"console",new{text="give 4"});Pump(host,10,peer);
        var page=State(peer).GetProperty("inventory").GetProperty("pages").EnumerateArray().First(p=>p.GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("id").GetInt32()==4));
        var item=page.GetProperty("items").EnumerateArray().First(i=>i.GetProperty("id").GetInt32()==4);
        Action(peer,"equipAndHold",new{page=page.GetProperty("index").GetInt32(),x=item.GetProperty("x").GetInt32(),y=item.GetProperty("y").GetInt32()});Pump(host,80,peer);
        for(int shot=1;shot<=8;shot++)
        {
            Action(peer,"fire",new{origin=new[]{0f,1.75f,3f},dir=new[]{0,0,-1}});Pump(host,12,peer);
            Assert.That(State(peer).GetProperty("interactables").GetProperty("doors").GetArrayLength(),Is.EqualTo(shot<8?1:0));
        }
        Assert.That(State(peer).GetProperty("weapon").GetProperty("ammo").GetInt32(),Is.EqualTo(22));
        Assert.That(State(peer).GetProperty("network").GetProperty("ownsWorld").GetBoolean(),Is.False);
    }
}
