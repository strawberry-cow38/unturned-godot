using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class FinitePopulationServicesTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    string temporary,content;
    static JsonElement Call(BridgeHost host,string op,object request) =>
        JsonSerializer.SerializeToElement(host.Handle(op,JsonSerializer.SerializeToElement(request,Json)),Json);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(false),Json);
    static void Tick(BridgeHost host,int count=25) { for(int i=0;i<count;i++) Call(host,"tick",new{}); }
    [SetUp] public void Setup()
    {
        var root=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"game/content")))root=root.Parent;
        content=Path.Combine(root!.FullName,"game/content");
        temporary=Path.Combine(Path.GetTempPath(),"finite-population-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary,"Spawns"));
        using var w=new BinaryWriter(File.Create(Path.Combine(temporary,"Spawns","Items.dat")));
        // Original v4 table binary: one table, one tier, one deterministic item.
        w.Write(new byte[]{4,1,255,255,255,4});w.Write(Encoding.ASCII.GetBytes("Food"));
        w.Write((ushort)0);w.Write((byte)1);w.Write((byte)4);w.Write(Encoding.ASCII.GetBytes("Food"));
        w.Write(1f);w.Write((byte)1);w.Write((ushort)13);
    }
    [TearDown] public void Cleanup()=>Directory.Delete(temporary,true);
    object Plan => new {mapRoot=temporary,mapName="Island_Population",population=new {
        zombies=new[]{new[]{500f,2,500}},animals=new[]{new[]{600f,2,600}},
        loot=new[]{new{position=new[]{20f,2,18},table=0},new{position=new[]{1000f,2,20},table=0}},
        vehicles=new[]{new{kind="sedan",variant=3,position=new[]{800f,3.1f,20},yaw=143f}}
    }};
    void Init(BridgeHost host,ushort listenPort=0)
    {
        Call(host,"init",new{contentRoot=content,mapId="Island_Population",spawn=new[]{20f,2,20},listenPort,allowCheats=true});
        Call(host,"worldGeometry",new{meshes=new[]{new{terrain=true,vertices=new[]{
            new[]{-2000f,1,-2000},new[]{2000f,1,-2000},new[]{2000f,1,2000},
            new[]{-2000f,1,-2000},new[]{2000f,1,2000},new[]{-2000f,1,2000}}}}});
    }
    [Test] public void GeneratedFieldsAreInstalledOnceAndSavedWithoutRetailPositionFiles()
    {
        using var host=new BridgeHost();Init(host);
        var result=Call(host,"finitePopulation",Plan);
        Assert.That(result.GetProperty("installed").GetBoolean(),Is.True);
        Assert.That(result.GetProperty("loot").GetProperty("points").GetInt32(),Is.EqualTo(2));
        Assert.That(result.GetProperty("zombies").GetProperty("points").GetInt32(),Is.EqualTo(1));
        Assert.That(result.GetProperty("animals").GetProperty("points").GetInt32(),Is.EqualTo(1));
        Tick(host);
        var items=State(host).GetProperty("worldItems");Assert.That(items.GetArrayLength(),Is.EqualTo(1));
        Call(host,"action",new{name="pickupItem",args=new{netId=items[0].GetProperty("netId").GetUInt32()}});
        Tick(host);Assert.That(State(host).GetProperty("worldItems").GetArrayLength(),Is.Zero);
        Assert.That(Call(host,"finitePopulation",Plan).GetProperty("installed").GetBoolean(),Is.False);
        string save=Path.Combine(temporary,"save.json");Call(host,"save",new{path=save});
        File.Delete(Path.Combine(temporary,"Spawns","Items.dat"));
        Call(host,"load",new{path=save});Tick(host);
        Assert.That(State(host).GetProperty("worldItems").GetArrayLength(),Is.Zero,"taken point is not replenished");
        var vehicle=State(host).GetProperty("finiteVehicles")[0];
        Assert.That(vehicle.GetProperty("position")[0].GetSingle(),Is.EqualTo(800));
        Assert.That(vehicle.GetProperty("variant").GetInt32(),Is.EqualTo(3));
        Call(host,"action",new{name="console",args=new{text="teleport 1000 2 20"}});Tick(host);
        items=State(host).GetProperty("worldItems");Assert.That(items.GetArrayLength(),Is.EqualTo(1));
        Assert.That(items[0].GetProperty("position")[0].GetSingle(),Is.EqualTo(1000).Within(.01));
        using var saved=JsonDocument.Parse(File.ReadAllText(save));
        Assert.That(saved.RootElement.GetProperty("BevyBridge").GetProperty("Loot").GetProperty("GeneratedPoints").GetArrayLength(),Is.EqualTo(2));
        Assert.That(Directory.GetFiles(Path.Combine(temporary,"Spawns")),Is.Empty,"no retail positions invented");
    }
    // MainMenuPlay Zombies: Off -> WorldBuilder.ZombiesOverride. The host never
    // builds the field, while loot/animals/vehicles of the same plan still install.
    [Test] public void MenuZombiesOffSkipsTheZombieFieldOnTheHostButKeepsTheRestOfThePlan()
    {
        using var host=new BridgeHost();
        Call(host,"init",new{contentRoot=content,mapId="Island_Population",spawn=new[]{20f,2,20},zombies=false});
        Call(host,"worldGeometry",new{meshes=new[]{new{terrain=true,vertices=new[]{
            new[]{-2000f,1,-2000},new[]{2000f,1,-2000},new[]{2000f,1,2000},
            new[]{-2000f,1,-2000},new[]{2000f,1,2000},new[]{-2000f,1,2000}}}}});
        var result=Call(host,"finitePopulation",Plan);
        Assert.That(result.GetProperty("installed").GetBoolean(),Is.True);
        Assert.That(result.GetProperty("zombies").GetProperty("disabled").GetBoolean(),Is.True);
        Assert.That(result.GetProperty("animals").GetProperty("points").GetInt32(),Is.EqualTo(1));
        Assert.That(result.GetProperty("loot").GetProperty("points").GetInt32(),Is.EqualTo(2));
        var retail=Call(host,"worldZombies",new{mapRoot=temporary});
        Assert.That(retail.GetProperty("disabled").GetBoolean(),Is.True);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var zombies=(ZombieServices)typeof(BridgeHost).GetField("_zombies",flags).GetValue(host);
        using var summary=JsonDocument.Parse(JsonSerializer.Serialize(zombies.Summary(),Json));
        Assert.That(summary.RootElement.GetProperty("points").GetInt32(),Is.Zero,"no zombie spawn points exist");
        Tick(host);
        Assert.That(State(host).TryGetProperty("zombies",out var z)?z.GetArrayLength():0,Is.Zero);
        // A fresh init without the field returns to the source env default (zombies on).
        Call(host,"init",new{contentRoot=content,mapId="Island_Population",spawn=new[]{20f,2,20},zombies=true});
        Init(host);
        Assert.That(Call(host,"finitePopulation",Plan).GetProperty("zombies").GetProperty("points").GetInt32(),Is.EqualTo(1));
    }
    [Test] public void ZombieRulePrefersTheMenuOverrideOverTheSourceEnvironmentSwitches()
    {
        string old=Environment.GetEnvironmentVariable("UG_NOZOMBIES");
        try
        {
            Environment.SetEnvironmentVariable("UG_NOZOMBIES","1");
            Assert.That(BridgeHost.ZombiesDisabledFor(JsonSerializer.SerializeToElement(new{})),Is.True);
            Assert.That(BridgeHost.ZombiesDisabledFor(JsonSerializer.SerializeToElement(new{zombies=true})),Is.False);
            Environment.SetEnvironmentVariable("UG_NOZOMBIES",null);
            Assert.That(BridgeHost.ZombiesDisabledFor(JsonSerializer.SerializeToElement(new{})),Is.False);
            Assert.That(BridgeHost.ZombiesDisabledFor(JsonSerializer.SerializeToElement(new{zombies=false})),Is.True);
        }
        finally { Environment.SetEnvironmentVariable("UG_NOZOMBIES",old); }
    }
    [Test] public void RebasePreservesPendingVehicleAndLootFramesAcrossReloadAndInitClearsPlan()
    {
        using var host=new BridgeHost();Init(host);Call(host,"finitePopulation",Plan);
        Call(host,"rebase",new{expectedEpoch=0,shift=new[]{256f,0,256}});
        var state=State(host);Assert.That(state.GetProperty("finiteVehicles")[0].GetProperty("position")[0].GetSingle(),Is.EqualTo(544));
        string save=Path.Combine(temporary,"shifted.json");Call(host,"save",new{path=save});
        Call(host,"load",new{path=save});
        state=State(host);Assert.That(state.GetProperty("finiteVehicles")[0].GetProperty("position")[2].GetSingle(),Is.EqualTo(-236));
        Call(host,"action",new{name="console",args=new{text="teleport 744 2 -236"}});Tick(host);
        var item=State(host).GetProperty("worldItems")[0];
        Assert.That(item.GetProperty("position")[0].GetSingle(),Is.EqualTo(744).Within(.01),"saved generated loot must not shift twice");
        Init(host);Assert.That(State(host).GetProperty("finiteVehicles").ValueKind,Is.EqualTo(JsonValueKind.Null));
        Assert.That(State(host).GetProperty("worldItems").GetArrayLength(),Is.Zero,"new/reopened map does not regenerate populations");
    }

    [Test] public void SourceFieldUsesWholeFiniteSurfaceOutsideResidentCollisionAndRetainsItAfterLoad()
    {
        string root=Path.GetDirectoryName(Path.GetDirectoryName(content));
        File.Copy(Path.Combine(root,"tests/UnturnedBevy.Tests/fixtures/pei-road-materials.dat"),Path.Combine(temporary,"Roads.dat"));
        string directory=Path.Combine(temporary,"map");
        var map=FiniteIslandFiles.Generate(content,temporary,directory,"Population",1234,1,1);
        string bias=Path.Combine(directory,"spawns","editor_Population_lootcond.txt");
        File.WriteAllText(bias,"0 -0.75\n");
        using var host=new BridgeHost();
        Call(host,"init",new{contentRoot=content,mapId="Island_Population",finiteDirectory=directory,spawn=new[]{20f,2,20}});
        Assert.That(SDG.Unturned.LootCondition.Bias(0),Is.EqualTo(-.75f));
        using(var unusedRemote=new BridgeHost()) { unusedRemote.Dispose(); }
        Assert.That(SDG.Unturned.LootCondition.Bias(0),Is.EqualTo(-.75f),"unrelated peer teardown cannot clear the active map bias");
        // Only a tiny collision patch is resident. Every generated point is
        // elsewhere, where the geometry-only fallback would have returned0.
        Call(host,"worldGeometry",new{meshes=new[]{new{terrain=true,vertices=new[]{
            new[]{0f,1,0},new[]{4f,1,0},new[]{0f,1,4}}}}});
        Call(host,"finitePopulation",new{mapName="Island_Population",mapRoot=temporary,
            population=FiniteIslandPopulation.From(map.Generated)});
        string path=Path.Combine(temporary,"source-field.json");Call(host,"save",new{path});
        using var saved=JsonDocument.Parse(File.ReadAllText(path));
        var chunks=saved.RootElement.GetProperty("BevyBridge").GetProperty("Zombies").GetProperty("Chunks");
        var original=new List<float[]>();
        int points=0;
        foreach(var chunk in chunks.EnumerateArray())foreach(var p in chunk.GetProperty("Points").EnumerateArray())
        {
            float x=p[0].GetSingle(),y=p[1].GetSingle(),z=p[2].GetSingle();
            Assert.That(y,Is.EqualTo(map.Surface.SampleHeight(x,z)).Within(1e-4));
            Assert.That(y,Is.GreaterThan(25.6f));points++;original.Add(new[]{x,y,z});
        }
        var sourcePoints=map.Generated.Spawns.Zombies.Where(p=>map.Surface.SampleDominantLayer(p.X,p.Z)!=5).ToArray();
        Assert.That(points,Is.EqualTo(sourcePoints.Length),"water/sand proxy must use whole source splat, not resident collision");
        Assert.That(points,Is.LessThan(map.Generated.Spawns.Zombies.Count),"fixture contains source-rejected sand points");
        Assert.That(points,Is.GreaterThan(100));
        Call(host,"load",new{path});
        Call(host,"rebase",new{expectedEpoch=1,shift=new[]{256f,0,256}});
        // Both source adapters must retain a usable full-terrain sampler after
        // load and origin translation, even outside the tiny resident mesh.
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var zombies=(ZombieServices)typeof(BridgeHost).GetField("_zombies",flags).GetValue(host);
        var animals=(WildlifeServices)typeof(BridgeHost).GetField("_wildlife",flags).GetValue(host);
        Assert.That(zombies.SourceGround,Is.Not.Null);Assert.That(animals.SourceGround,Is.Not.Null);
        float expected=map.Surface.SampleHeight(384,-412);
        Assert.That(expected,Is.GreaterThan(25.6f));
        Assert.That(zombies.SourceGround(128,-668),Is.EqualTo(expected));
        Assert.That(animals.SourceGround(128,-668),Is.EqualTo(expected));
        Assert.That(zombies.SourceLayer,Is.Not.Null);Assert.That(animals.SourceLayer,Is.Not.Null);
        foreach(var p in map.Generated.Spawns.Zombies)
        {
            byte layer=map.Surface.SampleDominantLayer(p.X,p.Z);
            Assert.That(zombies.SourceLayer(p.X-256,p.Z-256),Is.EqualTo(layer));
            Assert.That(animals.SourceLayer(p.X-256,p.Z-256),Is.EqualTo(layer));
        }
        // Existing homes are translated, not resampled during load/rebase.
        Call(host,"save",new{path});
        using var shifted=JsonDocument.Parse(File.ReadAllText(path));
        var moved=shifted.RootElement.GetProperty("BevyBridge").GetProperty("Zombies").GetProperty("Chunks")
            .EnumerateArray().SelectMany(c=>c.GetProperty("Points").EnumerateArray()).ToArray();
        Assert.That(moved.Length,Is.EqualTo(original.Count));
        for(int i=0;i<moved.Length;i++)
        {
            // Rebase translates the already accepted homes, preserving Y.
            // Re-sampling (x-256)+256 would introduce float cancellation.
            Assert.That(moved[i][0].GetSingle(),Is.EqualTo(original[i][0]-256));
            Assert.That(moved[i][1].GetSingle(),Is.EqualTo(original[i][1]));
            Assert.That(moved[i][2].GetSingle(),Is.EqualTo(original[i][2]-256));
        }
        Assert.That(SDG.Unturned.LootCondition.Bias(0),Is.EqualTo(-.75f),"load restores the original map sidecar");
        Init(host);
        Assert.That(SDG.Unturned.LootCondition.Count,Is.Zero,"leaving the finite map cannot bias ordinary later loot");
    }
    [Test] public void RealUdpPeerSeesGeneratedLootAndAcceptedPickupButCannotPublishPopulation()
    {
        ushort port;
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp))
        {socket.Bind(new IPEndPoint(IPAddress.Loopback,0));port=(ushort)((IPEndPoint)socket.LocalEndPoint).Port;}
        using var host=new BridgeHost();using var peer=new BridgeHost();Init(host,port);
        Call(host,"finitePopulation",Plan);
        Call(peer,"init",new{contentRoot=content,mapId="Island_Population",connectHost="127.0.0.1",connectPort=port});
        void Pump() { for(int i=0;i<30;i++){Call(peer,"tick",new{});Call(host,"tick",new{});Thread.Sleep(1);} }
        Pump();Call(peer,"action",new{name="console",args=new{text="teleport 20 2 20"}});Pump();
        Assert.Throws<InvalidOperationException>(()=>Call(peer,"finitePopulation",Plan));
        Assert.That(State(peer).GetProperty("network").GetProperty("ownsWorld").GetBoolean(),Is.False);
        var items=State(peer).GetProperty("worldItems");Assert.That(items.GetArrayLength(),Is.EqualTo(1));
        uint id=items[0].GetProperty("netId").GetUInt32();
        Assert.That(State(host).GetProperty("worldItems")[0].GetProperty("netId").GetUInt32(),Is.EqualTo(id));
        Call(peer,"action",new{name="pickupItem",args=new{netId=id}});Pump();
        Assert.That(State(peer).GetProperty("worldItems").GetArrayLength(),Is.Zero);
        Assert.That(State(host).GetProperty("worldItems").GetArrayLength(),Is.Zero);
        Assert.That(Call(host,"finitePopulation",Plan).GetProperty("installed").GetBoolean(),Is.False);
        Pump();Assert.That(State(peer).GetProperty("worldItems").GetArrayLength(),Is.Zero);
    }

}
