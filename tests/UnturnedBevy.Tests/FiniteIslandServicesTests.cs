using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class FiniteIslandServicesTests
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new VectorJson(), new ByteArrayJson() } };
    string _root, _content, _temp, _map;
    FiniteIslandFiles _files;
    object Request => new { directory = _map, contentRoot = _content, mapName = "Island_Services", mapRoot = _temp };
    static JsonElement Call(BridgeHost host, string op, object request = null) => JsonSerializer.SerializeToElement(
        host.Handle(op, JsonSerializer.SerializeToElement(request ?? new {})), Json);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), Json);
    static void Action(BridgeHost host, string name, object args) => Call(host, "action", new { name, args });
    static NetWorldServer Server(BridgeHost host) => (NetWorldServer)typeof(BridgeHost)
        .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
    static void Pump(BridgeHost host, BridgeHost remote = null, int count = 20)
    { for (int i=0;i<count;i++) { if(remote!=null)Call(remote,"tick");Call(host,"tick");if(remote!=null)Thread.Sleep(1); } }

    [OneTimeSetUp] public void SourceMap()
    {
        for(var p=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);p!=null;p=p.Parent)
            if(Directory.Exists(Path.Combine(p.FullName,"game/content"))) { _root=p.FullName;break; }
        Assert.That(_root,Is.Not.Null);
        _content=Path.Combine(_root,"game/content");_temp=Path.Combine(Path.GetTempPath(),"finite-services-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);_map=Path.Combine(_temp,"map");
        File.Copy(Path.Combine(_root,"tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),Path.Combine(_temp,"Roads.dat"));
        var generated=FiniteIslandFiles.Generate(_content,_temp,_map,"Services",1234,1,1);
        File.WriteAllText(generated.PropsPath,
            "91dbbf923c8c401bb6b2d56084783f73 20 50 -20 270 180 0 1 1 1\n"+
            "aa7fd5bc5e634a0c846770a28ff6cd65 30 50 -30 270 180 0 1 1 1\n"+
            "be02c0e774714f2a963e8d6ee54bbabf 40 50 -40 270 180 0 1 1 1\n"+
            "cf906420511b4eb6a4efba1838068cb4 50 50 -50 270 180 0 1 1 1\n");
        _files=FiniteIslandFiles.Open(_content,_map);
        // A fresh live placement has more precision than the authored file. Both
        // native and authority must use its source handoff before a later reopen.
        _files.Objects.Place("Wardrobe_0",new(60.123456f,50.234567f,60.345678f),
            new Godot.Basis(Godot.Vector3.Up,.723456f)*new Godot.Basis(Godot.Vector3.Right,-1.234567f));
        _files.PrepareRuntime();
    }
    [OneTimeTearDown] public void DeleteMap() { if(_temp!=null)Directory.Delete(_temp,true); }

    [Test] public void OriginalEditorRulesRegisterIndependentDoorsContainersAndGeneratedResourcesOnce()
    {
        using var host=new BridgeHost();
        Call(host,"init",new {contentRoot=_content,mapId="Island_Services",spawn=new[]{20,50,20}});
        var actual=Call(host,"finiteServices",Request);
        Assert.That(actual.GetProperty("containerCount").GetInt32(),Is.EqualTo(2));
        Assert.That(actual.GetProperty("objectDoors").GetArrayLength(),Is.EqualTo(3));
        Assert.That(actual.GetProperty("containerDoors").GetArrayLength(),Is.Zero,"SmartProps creates independent ObjectDoor children");
        Assert.That(actual.GetProperty("fixtureCount").GetInt32(),Is.Zero,"ordinary editor Circuit/Gas_Pump are not the separate custom marker nodes");
        Assert.That(actual.GetProperty("seats").GetArrayLength(),Is.Zero);
        Assert.That(actual.GetProperty("destructibleCount").GetInt32(),Is.Zero);
        Assert.That(actual.GetProperty("resourceCount").GetInt32(),Is.GreaterThan(400));
        var rows=actual.GetProperty("objects").EnumerateArray().ToDictionary(p=>p.GetProperty("sourceIndex").GetInt32());
        Assert.That(rows.Keys,Is.EquivalentTo(new[]{0,1,4}));
        Assert.That(rows[4].GetProperty("position")[0].GetSingle(),Is.EqualTo(_files.Objects.Placements[4].Position.X));
        var wardrobe = actual.GetProperty("objectDoors").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Wardrobe_0");
        string leaf = wardrobe.GetProperty("leaves")[0].GetProperty("mesh").GetString();
        var vertices = File.ReadLines(Path.Combine(_content, leaf)).Where(l => l.StartsWith("v "))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(3)
                .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        var center = new Godot.Vector3(
            (vertices.Min(p=>p[0])+vertices.Max(p=>p[0]))*.5f,
            (vertices.Min(p=>p[1])+vertices.Max(p=>p[1]))*.5f,
            (vertices.Min(p=>p[2])+vertices.Max(p=>p[2]))*.5f);
        var placement = _files.Objects.Placements[4];
        var expected = placement.Position + placement.Basis * center;
        var actualCenter = wardrobe.GetProperty("interactionPosition").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        Assert.That(actualCenter, Is.EqualTo(new[]{expected.X,expected.Y,expected.Z}).Within(.00001f),
            "door reach must use the full live tilted source basis, not saved Euler rounding or yaw only");
        var repeat=Call(host,"finiteServices",Request);
        Assert.That(repeat.GetRawText(),Is.EqualTo(actual.GetRawText()));
        Assert.That(Server(host).Containers.Count,Is.EqualTo(2),"streaming cannot restock or duplicate fixtures");
        Assert.That(Server(host).Interactables.ObjectDoorCount,Is.EqualTo(3));
        var indices=actual.GetProperty("resources").EnumerateArray().Select(p=>p.GetProperty("index").GetInt32()).ToArray();
        Assert.That(indices,Is.EqualTo(Enumerable.Range(0,indices.Length)));
    }

    [Test] public void RealUdpDoorAndForageUseAcceptedIdsRejectRemoteRegistrationAndSurviveSave()
    {
        ushort port;
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp))
        {socket.Bind(new IPEndPoint(IPAddress.Loopback,0));port=(ushort)((IPEndPoint)socket.LocalEndPoint).Port;}
        using var host=new BridgeHost();using var remote=new BridgeHost();
        Call(host,"init",new {contentRoot=_content,mapId="Island_Services",listenPort=port,allowCheats=true,spawn=new[]{30,50,30}});
        var manifest=Call(host,"finiteServices",Request);
        Call(remote,"init",new {contentRoot=_content,mapId="Island_Services",connectHost="127.0.0.1",connectPort=port});Pump(host,remote);
        Assert.Throws<InvalidOperationException>(()=>Call(remote,"finiteServices",Request));
        var bound=Call(remote,"mapBindings",Request);
        Assert.That(bound.GetProperty("readOnly").GetBoolean(),Is.True);
        Assert.That(bound.GetProperty("complete").GetBoolean(),Is.True);
        Assert.That(bound.GetProperty("objectDoors").GetRawText(),Is.EqualTo(manifest.GetProperty("objectDoors").GetRawText()));
        var door=manifest.GetProperty("objectDoors").EnumerateArray().Single(p=>p.GetProperty("name").GetString()=="Container_0");
        var at=door.GetProperty("interactionPosition").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        uint id=door.GetProperty("netId").GetUInt32();
        Action(remote,"console",new{text=FormattableString.Invariant($"teleport {at[0]} {at[1]} {at[2]}")});Pump(host,remote);
        Action(remote,"toggleObjectDoor",new{netId=id});Pump(host,remote);
        Assert.That(Server(host).Interactables.IsObjectDoorOpen(id),Is.True);
        Assert.That(State(remote).GetProperty("interactables").GetProperty("objectDoors").EnumerateArray()
            .Single(d=>d.GetProperty("netId").GetUInt32()==id).GetProperty("open").GetBoolean(),Is.True);
        var forage=manifest.GetProperty("resources").EnumerateArray().First(p=>p.GetProperty("rewardItemId").GetInt32()>0);
        int index=forage.GetProperty("index").GetInt32();at=forage.GetProperty("position").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        Action(remote,"forageResource",new{index});Pump(host,remote);
        Assert.That(Server(host).Resources.IsAlive(index),Is.True,"far use is rejected by existing authority reach");
        Action(remote,"console",new{text=FormattableString.Invariant($"teleport {at[0]} {at[1]} {at[2]}")});Pump(host,remote);
        Action(remote,"forageResource",new{index});Pump(host,remote);
        Assert.That(Server(host).Resources.IsAlive(index),Is.False);
        Assert.That(State(remote).GetProperty("resources").GetProperty("dead").EnumerateArray().Select(x=>x.GetInt32()),Does.Contain(index));
        string save=Path.Combine(_temp,"world.json");Call(host,"save",new{path=save});
        Call(remote,"disconnect");Call(host,"load",new{path=save});Pump(host);
        Assert.That(Server(host).Resources.IsAlive(index),Is.False);
        Assert.That(Server(host).Interactables.IsObjectDoorOpen(id),Is.True);
        Assert.That(Call(host,"finiteServices",Request).GetProperty("containerCount").GetInt32(),Is.EqualTo(2));
        Assert.That(Server(host).Resources.IsAlive(index),Is.False,"re-register after Load must not revive harvested resources");
    }
}
