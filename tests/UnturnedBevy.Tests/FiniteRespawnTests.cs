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
public sealed class FiniteRespawnTests
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields=true,
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase, Converters={new VectorJson(),new ByteArrayJson()} };
    string _content, _temp, _map, _players;
    static readonly Vector3 Spawn = new(120.25f, 80.75f, -44.5f);
    const float Yaw = 137.25f;
    static JsonElement Call(BridgeHost host,string op,object args=null) => JsonSerializer.SerializeToElement(
        host.Handle(op,JsonSerializer.SerializeToElement(args??new{})),Json);
    static JsonElement State(BridgeHost host)=>JsonSerializer.SerializeToElement(host.State(),Json);
    static NetWorldServer Server(BridgeHost host)=>(NetWorldServer)typeof(BridgeHost)
        .GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(host);
    static ushort Owner(BridgeHost host)=>State(host).GetProperty("playerId").GetUInt16();
    static void Pump(BridgeHost host,BridgeHost remote=null,int count=16)
    {for(int i=0;i<count;i++){if(remote!=null)Call(remote,"tick");Call(host,"tick");if(remote!=null)Thread.Sleep(1);}}
    static JsonElement Player(BridgeHost host)=>State(host).GetProperty("player");
    static float[] Position(JsonElement player)=>player.GetProperty("position").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
    static void Die(BridgeHost host,ushort owner)
    { var server=Server(host);server.Combat.DamagePlayerExternal(owner,500,0);Call(host,"tick"); Assert.That(server.CombatState.TryGet(owner,out var life)&&!life.Alive,Is.True); life.RespawnAtTick=server.Session.CurrentTick+5; }
    void Init(BridgeHost host,ushort port=0)=>Call(host,"init",new {contentRoot=_content,mapId="Island_Respawn",finiteDirectory=_map,
        listenPort=port,allowCheats=true,spawn=new[]{20,40,20},yaw=23});

    [OneTimeSetUp] public void SourceMap()
    {
        string root=null;
        for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)
            if(Directory.Exists(Path.Combine(d.FullName,"game/content"))){root=d.FullName;break;}
        Assert.That(root,Is.Not.Null);_content=Path.Combine(root,"game/content");
        _temp=Path.Combine(Path.GetTempPath(),"finite-respawn-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_temp);
        File.Copy(Path.Combine(root,"tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),Path.Combine(_temp,"Roads.dat"));
        _map=Path.Combine(_temp,"map");var files=FiniteIslandFiles.Generate(_content,_temp,_map,"Respawn",22,1,1);
        _players=files.PlayersPath;
    }
    [SetUp] public void ResetPoints()=>File.WriteAllText(_players,"120.25 80.75 -44.5 137.25 0 5\n999 999 999 9 1 0\n");
    [OneTimeTearDown] public void Cleanup(){if(_temp!=null)Directory.Delete(_temp,true);}

    [Test] public void ExactInitialChoiceRespawnFacingAndSaveUseAuthoredPoolWithoutRegeneration()
    {
        using var host=new BridgeHost();
        var prepared=Call(host,"finiteMap",new{directory=_map,contentRoot=_content});
        Assert.That(prepared.GetProperty("initialSpawn").EnumerateArray().Select(v=>v.GetSingle()),Is.EqualTo(new[]{Spawn.x,Spawn.y,Spawn.z}));
        Init(host);Assert.That(Player(host).GetProperty("respawnLook").GetProperty("version").GetInt32(),Is.Zero);
        Call(host,"tick",new{input=new{pitch=41,yaw=23}});Die(host,Owner(host));Pump(host);
        var look=Player(host).GetProperty("respawnLook");
        Assert.That(Position(Player(host)),Is.EqualTo(new[]{Spawn.x,Spawn.y,Spawn.z}).Within(.04));
        Assert.That(look.GetProperty("yaw").GetSingle(),Is.EqualTo(NetQuantization.QuantizeDegrees(Yaw,NetQuantization.YawBits)));
        Assert.That(look.GetProperty("pitch").GetSingle(),Is.Zero);int version=look.GetProperty("version").GetInt32();
        // A delayed native publication cannot erase the stored fact. Native
        // consumes its separate version once rather than following later yaw.
        Call(host,"tick",new{input=new{pitch=18,yaw=67}});Pump(host);
        Assert.That(Player(host).GetProperty("respawnLook").GetProperty("yaw").GetSingle(),Is.EqualTo(look.GetProperty("yaw").GetSingle()));
        Assert.That(Player(host).GetProperty("respawnLook").GetProperty("version").GetInt32(),Is.EqualTo(version));
        string save=Path.Combine(_temp,"world.json");Call(host,"save",new{path=save});
        File.WriteAllText(_players,"800 900 700 11 0 0\n");
        Call(host,"load",new{path=save});Assert.That(Player(host).GetProperty("respawnLook").GetProperty("version").GetInt32(),Is.Zero);
        Die(host,Owner(host));Pump(host);
        Assert.That(Position(Player(host)),Is.EqualTo(new[]{Spawn.x,Spawn.y,Spawn.z}).Within(.04));
        Assert.That(Player(host).GetProperty("respawnLook").GetProperty("yaw").GetSingle(),Is.EqualTo(look.GetProperty("yaw").GetSingle()));
    }

    [Test] public void ChangingBedDuringCountdownClearsCachedRandomAndGenericRespawnResetsOnlyPitch()
    {
        using var host=new BridgeHost();Init(host);var server=Server(host);ushort owner=Owner(host);
        Call(host,"tick",new{input=new{yaw=63,pitch=42}});Pump(host);
        Die(host,owner);server.Combat.RespawnPositionOf(owner); // preflight picks the pool before a new claim
        server.Interactables.RegisterBed(101,new(50,60,70),278);
        Assert.That(server.Interactables.ClaimBed(101,owner,out _),Is.True);Pump(host);
        Assert.That(Position(Player(host)),Is.EqualTo(new[]{50f,60.5f,70f}).Within(.04));
        Assert.That(Player(host).GetProperty("yaw").GetSingle(),Is.EqualTo(63));
        Assert.That(Player(host).GetProperty("pitch").GetSingle(),Is.Zero);
        Assert.That(Player(host).GetProperty("respawnLook").GetProperty("yaw").ValueKind,Is.EqualTo(JsonValueKind.Null));
        Assert.That(State(host).GetProperty("events").EnumerateArray().Any(e=>e.GetProperty("kind").GetString()=="respawnPose"),Is.False);
        Die(host,owner);server.Combat.RespawnPositionOf(owner);server.Interactables.RemoveBed(101);Pump(host);
        Assert.That(Position(Player(host)),Is.EqualTo(new[]{Spawn.x,Spawn.y,Spawn.z}).Within(.04));
        Assert.That(Player(host).GetProperty("respawnLook").GetProperty("yaw").GetSingle(),Is.EqualTo(NetQuantization.QuantizeDegrees(Yaw,NetQuantization.YawBits)));
        Call(host,"init",new{contentRoot=_content,spawn=new[]{20,40,20},yaw=19});
        Call(host,"tick",new{input=new{pitch=-35}});Die(host,Owner(host));Pump(host);
        Assert.That(Player(host).GetProperty("pitch").GetSingle(),Is.Zero);
        Assert.That(Player(host).GetProperty("yaw").GetSingle(),Is.EqualTo(19));
        Assert.That(Player(host).GetProperty("respawnLook").GetProperty("yaw").ValueKind,Is.EqualTo(JsonValueKind.Null));
    }

    [Test] public void RealUdpOwnerReceivesOnePoseOtherPeerReceivesNoneAndRebasePreservesPool()
    {
        ushort port;using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp))
        {socket.Bind(new IPEndPoint(IPAddress.Loopback,0));port=(ushort)((IPEndPoint)socket.LocalEndPoint).Port;}
        using var host=new BridgeHost();using var remote=new BridgeHost();Init(host,port);
        Call(remote,"init",new{contentRoot=_content,mapId="Island_Respawn",connectHost="127.0.0.1",connectPort=port});Pump(host,remote,30);
        Die(host,Owner(remote));Pump(host,remote,30);
        var facts=State(remote).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="respawnPose").ToArray();
        Assert.That(facts.Length,Is.EqualTo(1));Assert.That(Position(Player(remote)),Is.EqualTo(new[]{Spawn.x,Spawn.y,Spawn.z}).Within(.04));
        Assert.That(Player(remote).GetProperty("respawnLook").GetProperty("yaw").GetSingle(),Is.EqualTo(NetQuantization.QuantizeDegrees(Yaw,NetQuantization.YawBits)));
        Assert.That(State(host).GetProperty("events").EnumerateArray().Any(e=>e.GetProperty("kind").GetString()=="respawnPose"),Is.False);
        // Negative control: remove only the bridge's typed owner-fact consumer.
        // Replicated player facing and ordinary respawn still arrive, but they
        // cannot manufacture the stable one-time camera fact.
        var client=(NetWorldClient)typeof(BridgeHost).GetField("_client",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(remote);
        typeof(NetWorldClient).GetField("RespawnPose",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(client,null);
        int previous=Player(remote).GetProperty("respawnLook").GetProperty("version").GetInt32();
        Die(host,Owner(remote));Pump(host,remote,30);
        Assert.That(Player(remote).GetProperty("alive").GetBoolean(),Is.True);
        Assert.That(Player(remote).GetProperty("respawnLook").GetProperty("version").GetInt32(),Is.EqualTo(previous+1));
        Assert.That(Player(remote).GetProperty("respawnLook").GetProperty("yaw").ValueKind,Is.EqualTo(JsonValueKind.Null),"disabling the consumer loses the yaw fact despite server state and normal respawn");
        Assert.Throws<InvalidOperationException>(()=>Call(remote,"init",new{contentRoot=_content,mapId="Island_Respawn",finiteDirectory=_map,connectHost="127.0.0.1",connectPort=port}));
        Init(host);Call(host,"rebase",new{expectedEpoch=0,shift=new[]{256,0,-512}});
        string save=Path.Combine(_temp,"rebased.json");Call(host,"save",new{path=save});Call(host,"load",new{path=save});
        Die(host,Owner(host));Pump(host);
        Assert.That(Position(Player(host)),Is.EqualTo(new[]{Spawn.x-256,Spawn.y,Spawn.z+512}).Within(.04));
    }
}
