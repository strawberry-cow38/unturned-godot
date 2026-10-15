using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class WorldCapsuleTests
{
    static WorldGeometry Npc(Vector3 foot = default)
    { var geometry = new WorldGeometry(); geometry.AddCapsule(foot, .35f, 1.8f); return geometry; }
    static WorldGeometry Wall(float z)
    {
        var geometry = new WorldGeometry();
        geometry.AddMesh([new(-2,-1,z),new(2,-1,z),new(0,4,z)], [0,1,2]); geometry.Build();
        return geometry;
    }
    [Test]
    public void OriginalCapsuleHasExactRoundedCapsNormalAndOpaqueClassification()
    {
        var world = new WorldGeometry(); world.SetDynamic("npc", Npc());
        Assert.That(world.RayDetailed(new(0,1,2),new(0,1,-2),out var side),Is.True);
        Assert.That(side.Point.z,Is.EqualTo(.35f).Within(.0001));
        Assert.That(side.Normal.Value.z,Is.EqualTo(1).Within(.0001));
        Assert.That((side.DestructibleIndex,side.ResourceIndex,side.VehicleNetId,side.DeployableNetId),Is.EqualTo((-1,-1,0u,0u)));
        Assert.That(side.Thin || side.Water,Is.False);
        Assert.That(world.RayDetailed(new(.3f,3,0),new(.3f,-1,0),out var top),Is.True);
        Assert.That(top.Point.y,Is.EqualTo(1.45f+MathF.Sqrt(.35f*.35f-.3f*.3f)).Within(.0001));
        Assert.That(top.Normal.Value.x,Is.EqualTo(.3f/.35f).Within(.0001));
        Assert.That(world.RayDetailed(new(.3f,1.7f,2),new(.3f,1.7f,-2),out _),Is.False,"The AABB corner is outside the round cap");
        Assert.That(world.RayDetailed(new(0,1,2),new(0,1,.4f),out _),Is.False,"Do not extend a short segment to an infinite ray");
        Assert.That(world.RayCosmetic(new(0,1,2),new(0,1,-2),out var cosmetic,out bool thin,out bool water),Is.True);
        Assert.That(cosmetic.z,Is.EqualTo(side.Point.z)); Assert.That(thin||water,Is.False);
        Assert.That(world.BlastBlocked(new(0,.2f,2),new(0,.2f,-2)),Is.True);
        Assert.That(world.OverlapsBox(new(.3f,1.7f,0),new(.02f,.02f,.02f)),Is.False);
        Assert.That(world.OverlapsBox(new(.3f,1.5f,0),new(.02f,.02f,.02f)),Is.True);
        Assert.Throws<ArgumentException>(()=>world.AddCapsule(new(float.NaN,0,0),.35f,1.8f));
        Assert.Throws<ArgumentException>(()=>world.AddCapsule(default,.35f,.6f));
    }
    [Test]
    public void CapsuleParticipatesInNearestIdentityReplacementRemovalAndOriginTranslation()
    {
        var world = Wall(1); world.SetDynamic("npc",Npc());
        Assert.That(world.RayDynamicKey(new(0,1,2),new(0,1,-2),out var key),Is.True); Assert.That(key,Is.Null);
        world.Clear(); world.SetDynamic("npc",Npc()); world.SetDynamic("rear-wall",Wall(-1));
        world.RayDynamicKey(new(0,1,2),new(0,1,-2),out key); Assert.That(key,Is.EqualTo("npc"));
        world.SetDynamic("front-wall",Wall(1));
        world.RayDynamicKey(new(0,1,2),new(0,1,-2),out key); Assert.That(key,Is.EqualTo("front-wall"));
        world.RemoveDynamic("front-wall"); world.TranslateWorld(new(-256,0,0));
        world.RayDynamicKey(new(-256,1,2),new(-256,1,-2),out key); Assert.That(key,Is.EqualTo("npc"));
        world.SetDynamic("npc",new WorldGeometry());
        world.RayDynamicKey(new(-256,1,2),new(-256,1,-2),out key); Assert.That(key,Is.EqualTo("rear-wall"));
        world.ClearDynamic(); Assert.That(world.RayDetailed(new(-256,1,2),new(-256,1,-2),out _),Is.False);
        world.DynamicAlive = _=>false; world.SetDynamic("npc",Npc());
        Assert.That(world.RayDetailed(new(0,1,2),new(0,1,-2),out _),Is.False);
    }
    static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };
    static JsonElement Call(BridgeHost host,string op,object args=null) => JsonSerializer.SerializeToElement(host.Handle(op,JsonSerializer.SerializeToElement(args??new{})),Options);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(),Options);
    static void Action(BridgeHost host,string name,object args=null) => Call(host,"action",new{name,args=args??new{}});
    static string Root()
    {
        for(var dir=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);dir!=null;dir=dir.Parent)
            if(Directory.Exists(Path.Combine(dir.FullName,"game","content")))return Path.Combine(dir.FullName,"game","content");
        throw new DirectoryNotFoundException("game/content");
    }
    [Test]
    public void RealUdpShotsStopAtHostNpcAndRemoteClientsCannotUploadTheirDiagnosticBodies()
    {
        ushort port;
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp))
        { socket.Bind(new IPEndPoint(IPAddress.Loopback,0));port=(ushort)((IPEndPoint)socket.LocalEndPoint!).Port; }
        using var host=new BridgeHost();using var remote=new BridgeHost();
        Call(host,"init",new{contentRoot=Root(),listenPort=port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(remote,"init",new{contentRoot=Root(),connectHost="127.0.0.1",connectPort=port});
        void Pump(int count=16) { for(int i=0;i<count;i++){Call(remote,"tick");Call(host,"tick");Thread.Sleep(1);} }
        Pump();Action(host,"console",new{text="teleport 10 0 0"});Pump();
        var capsule=new{key="source-npc:map:0",meshes=Array.Empty<object>(),capsules=new[]{new{foot=new[]{0,0,-2},radius=.35f,height=1.8f}}};
        Call(host,"worldServices",new{resourceCount=1,resources=new[]{new{index=0,kind="tree",name="Birch_0",position=new[]{0,0,-4},health=1,resetSeconds=60,trunkLength=8f}}});
        Call(host,"worldGeometry",new{meshes=new[]{new{resourceIndex=0,vertices=new[]{new[]{-2,0,-4},new[]{2,0,-4},new[]{0,4,-4}}}}});
        Call(host,"worldDynamicGeometry",capsule);
        Assert.Throws<InvalidOperationException>(()=>Call(remote,"worldDynamicGeometry",capsule));
        Action(remote,"console",new{text="give 4"});Pump();
        var page=State(remote).GetProperty("inventory").GetProperty("pages").EnumerateArray()
            .First(p=>p.GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("id").GetInt32()==4));
        var gun=page.GetProperty("items").EnumerateArray().First(i=>i.GetProperty("id").GetInt32()==4);
        Action(remote,"equipAndHold",new{page=page.GetProperty("index").GetInt32(),x=gun.GetProperty("x").GetInt32(),y=gun.GetProperty("y").GetInt32()});Pump(80);
        void Fire() { Action(remote,"fire",new{origin=new[]{0f,1.75f,0f},dir=new[]{0f,0f,-1f}});Pump(); }
        Fire();
        Assert.That(State(remote).GetProperty("weapon").GetProperty("ammo").GetInt32(),Is.EqualTo(29),"Accepted shot, not a rejected intent");
        Assert.That(State(remote).GetProperty("resources").GetProperty("dead").GetArrayLength(),Is.Zero,"NPC stops damage to the resource behind it");
        Call(host,"worldDynamicGeometry",new{key="source-npc:map:0",meshes=Array.Empty<object>()});Fire();
        Assert.That(State(remote).GetProperty("weapon").GetProperty("ammo").GetInt32(),Is.EqualTo(28));
        Assert.That(State(remote).GetProperty("resources").GetProperty("dead").GetArrayLength(),Is.EqualTo(1),"Removing NPC restores the same accepted shot's path");
    }
}
