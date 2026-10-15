using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class CraftingStationTests
{
    const string Workbench = "7b82c125a5a54984b8bb26576b59e977";
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true, Converters = { new VectorJson(), new ByteArrayJson() } };
    static JsonElement Call(BridgeHost h,string op,object args=null) =>
        JsonSerializer.SerializeToElement(h.Handle(op,JsonSerializer.SerializeToElement(args??new{},Json)),Json);
    static JsonElement State(BridgeHost h) => JsonSerializer.SerializeToElement(h.State(),Json);
    static T Field<T>(BridgeHost h,string name) => (T)typeof(BridgeHost).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(h)!;
    static string Content()
    {
        for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)
            if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");
        throw new DirectoryNotFoundException();
    }
    static void Wall(WorldGeometry geometry,float z,uint station=0)
        => geometry.AddMesh([new(-4,-4,z),new(4,-4,z),new(0,8,z)],[0,1,2],deployable:station);

    [Test]
    public void NullOptionalCameraLeavesBothReadOnlyViewsUsableWithoutSendingAProbe()
    {
        using var host=new BridgeHost();Call(host,"init",new{contentRoot=Content()});
        Assert.That(Call(host,"craftingView",new{eye=(float[])null}).GetProperty("view").ValueKind,Is.EqualTo(JsonValueKind.Object));
        Assert.That(Call(host,"inventoryView",new{eye=(float[])null}).GetProperty("items").ValueKind,Is.EqualTo(JsonValueKind.Array));
        Assert.That(Field<uint>(host,"_craftContextNext"),Is.Zero,"Null has the same optional meaning as an absent camera");
    }

    [Test]
    public void ExcludingTheWholeStationStillFindsWallsAndOtherBodiesBehindItsNearestFace()
    {
        var geometry=new WorldGeometry(); Wall(geometry,2,7);geometry.Build();
        Assert.That(geometry.RayDetailed(new(0,1,4),new(0,1,0),out _),Is.True);
        Assert.That(geometry.StationBlocked(7,new(0,1,4),new(0,1,0)),Is.False);
        Wall(geometry,1);geometry.Build();
        Assert.That(geometry.StationBlocked(7,new(0,1,4),new(0,1,0)),Is.True,
            "Ignoring only the nearest station hit would wrongly hide this wall");
        geometry.Clear();Wall(geometry,2,8);geometry.Build();
        Assert.That(geometry.StationBlocked(7,new(0,1,4),new(0,1,0)),Is.True,"Other deployables still block");
        geometry.Clear();var npc=new WorldGeometry();npc.AddCapsule(new(0,0,2),.35f,1.8f);geometry.SetDynamic("npc",npc);
        Assert.That(geometry.StationBlocked(7,new(0,1,4),new(0,1,0)),Is.True,"Original world-layer NPC bodies remain opaque");
    }

    [Test]
    public void SourceWorldMaskIgnoresBulletOnlyVehicleMeshesAndKeepsIndependentFallbackHulls()
    {
        var geometry=new WorldGeometry();
        var vertices=new Vector3[]{new(-4,-4,2),new(4,-4,2),new(0,8,2)};
        geometry.AddMesh(vertices,[0,1,2],vehicle:9,bodyPart:"glass:0");geometry.Build();
        Assert.That(geometry.RayDetailed(new(0,1,4),new(0,1,0),out _),Is.True);
        Assert.That(geometry.StationBlocked(7,new(0,1,4),new(0,1,0)),Is.False,
            "Source HitMesh, panes and doors carry bullet/player layers without world bit0");
        geometry.Clear();geometry.AddMesh(vertices,[0,1,2],vehicle:9,sourceWorld:true,combat:false);geometry.Build();
        Assert.That(geometry.StationBlocked(7,new(0,1,4),new(0,1,0)),Is.True,
            "A no-HitMesh fallback chassis retains its actual world collider");
        Assert.That(geometry.RayDetailed(new(0,1,4),new(0,1,0),out _),Is.False,
            "Extra world-query geometry must not add a second broad bullet hull");
        Assert.That(geometry.OverlapsBox(new(0,1,2),new(.1f,.1f,.1f)),Is.False,
            "Existing zombie/navigation queries retain their original triangle set");
    }

    [Test]
    public void OriginalRangesTagUnionAndRemovalDoNotRequirePowerOrFire()
    {
        using var host=new BridgeHost();Call(host,"init",new{contentRoot=Content()});
        var server=Field<NetWorldServer>(host,"_server");var geometry=Field<WorldGeometry>(host,"_geometry");
        var bench=server.Deployables.ServerPlace(server.Ids.Mint(),1916,0,Vector3.zero,0,0);
        Assert.That(CraftingStations.At(server,geometry,new(4,0,0)),Does.Contain(Workbench));
        Assert.That(CraftingStations.At(server,geometry,new(4.001f,0,0)),Is.Empty,"Source radius is squared eye-to-origin, inclusive at4m");
        server.Deployables.ServerRemove(bench.NetIdValue,0);
        var kiln=server.Deployables.ServerPlace(server.Ids.Mint(),1927,0,Vector3.zero,0,0);
        Assert.That(kiln.ToggledOn||kiln.OnFire,Is.False);
        Assert.That(CraftingStations.At(server,geometry,new(5,0,0)),Is.EquivalentTo(DeployableDef.Kiln.CraftingTags));
        Assert.That(CraftingStations.At(server,geometry,new(5.001f,0,0)),Is.Empty);
        server.Deployables.ServerPlace(server.Ids.Mint(),1919,0,Vector3.zero,0,0);
        var tags=CraftingStations.At(server,geometry,new(0,1.75f,2));
        Assert.That(tags.Count,Is.EqualTo(3),"Overlapping heat tags union once, preserving the two different station tags");
        Wall(geometry,1);geometry.Build();
        Assert.That(CraftingStations.At(server,geometry,new(0,1.75f,2)),Is.Empty);
        server.Deployables.ServerRemove(kiln.NetIdValue,0);geometry.Clear();
        Assert.That(CraftingStations.At(server,geometry,new(0,1.75f,2)),Is.EquivalentTo(DeployableDef.OvenBrick.CraftingTags));
    }

    [Test]
    public void SourceChaseCameraEnvelopeRequiresAnAcceptedSeatAndHostSceneBounds()
    {
        using var host=new BridgeHost();Call(host,"init",new{contentRoot=Content(),spawn=new[]{0,0,0}});
        var server=Field<NetWorldServer>(host,"_server");var geometry=Field<WorldGeometry>(host,"_geometry");
        ushort owner=State(host).GetProperty("playerId").GetUInt16();
        Assert.That(CraftingStations.EyeAllowed(server,geometry,owner,new(0,1.75f,2)),Is.True,"Source on-foot shoulder camera fits existing eye envelope");
        Assert.That(CraftingStations.EyeAllowed(server,geometry,owner,new(0,2,12)),Is.False,"An on-foot client cannot claim a distant camera");
        var id=Call(host,"worldVehicles",new{vehicles=new[]{new{typeId=2,position=new[]{0,0,0},seatCount=4,health=4000,fuel=100}}})
            .GetProperty("vehicles")[0].GetProperty("netId").GetUInt32();
        Call(host,"action",new{name="enterVehicle",args=new{netId=id,seat=0}});
        for(int n=0;n<12;n++)Call(host,"tick");
        Assert.That(server.VehicleHost.IsSeated(owner),Is.True);
        Assert.That(CraftingStations.EyeAllowed(server,geometry,owner,new(0,2,12)),Is.False,"No client-supplied hull size fallback");
        var hull=new WorldGeometry();Wall(hull,0);hull.Build();geometry.SetDynamic("vehicle:"+id,hull);
        Assert.That(CraftingStations.EyeAllowed(server,geometry,owner,new(0,2,12)),Is.True,"Source chase view can browse using its actual camera");
        Assert.That(CraftingStations.EyeAllowed(server,geometry,owner,new(0,2,1000)),Is.False);
        geometry.RemoveDynamic("vehicle:"+id);
        Assert.That(CraftingStations.EyeAllowed(server,geometry,owner,new(0,2,12)),Is.False);
    }

    [Test]
    public void UdpBrowseUsesOnlyAcceptedTagsAndCraftRechecksAChangedWallWithoutSpending()
    {
        ushort port;
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)) {
            socket.Bind(new IPEndPoint(IPAddress.Loopback,0));port=(ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        using var host=new BridgeHost();using var peer=new BridgeHost();
        Call(host,"init",new{contentRoot=Content(),listenPort=port,allowCheats=true,spawn=new[]{0,0,2}});
        Call(peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=port});
        void Pump(int count=16) { for(int n=0;n<count;n++){Call(peer,"tick");Call(host,"tick");Thread.Sleep(1);} }
        Pump(30);
        var server=Field<NetWorldServer>(host,"_server");var geometry=Field<WorldGeometry>(host,"_geometry");
        var catalog=Field<Catalog>(peer,"_catalog");var hostCatalog=Field<Catalog>(host,"_catalog");
        var recipe=BlueprintRegistry.Index().First(bp=>!bp.RequiresStation&&!bp.Locked&&bp.Operation=="Craft"
            &&!BlueprintRegistry.IsSalvage(bp)&&!BlueprintRegistry.IsRecolour(bp)&&bp.Inputs.Any(i=>i.Consume));
        int index=catalog.Recipes.IndexOf(recipe);
        // Isolated test variation of an authored recipe. Shipping content stays unchanged.
        recipe.StationTags.Add(Workbench);hostCatalog.Recipes[index].StationTags.Add(Workbench);
        var owner=State(peer).GetProperty("playerId").GetUInt16();
        var bag=server.Transactions.InventoryForTest(owner);bag.items[2].resize(30,30);
        foreach(var input in recipe.Inputs)for(int n=0;n<input.Amount*2;n++)bag.tryAddItemAuto(new Item(Crafting.Resolve(input.Guid),1),out _);
        server.Deployables.ServerPlace(server.Ids.Mint(),1916,0,Vector3.zero,0,0);Pump();
        var eye=new[]{0f,1.75f,2f};
        JsonElement View() => Call(peer,"craftingView",new{eye,selected=index}).GetProperty("view").GetProperty("selected");
        Assert.That(View().GetProperty("craftable").GetBoolean(),Is.False,"Sending a probe grants nothing locally");
        Pump();Assert.That(View().GetProperty("craftable").GetBoolean(),Is.True,"Owner event must reach the actual remote browse projection");
        for(int n=0;n<4;n++) {
            eye[1]+=.0001f;
            Assert.That(View().GetProperty("craftable").GetBoolean(),Is.True,
                "A newly submitted moving camera cannot erase the last accepted owner fact before its reply");
            Pump();
        }
        var quick=Call(peer,"inventoryView").GetProperty("quickCrafts");
        Assert.That(quick.EnumerateArray().Any(q=>q.GetProperty("index").GetInt32()==index),Is.True);
        int before=bag.getItemCount(Crafting.Resolve(recipe.Inputs.First(i=>i.Consume).Guid));
        Wall(geometry,1);geometry.Build();
        Call(peer,"action",new{name="craft",args=new{index,eye}});Pump();
        Assert.That(server.CraftQueue.JobCount(owner),Is.Zero,"A prior probe cannot authorize a later obstructed craft");
        Assert.That(bag.getItemCount(Crafting.Resolve(recipe.Inputs.First(i=>i.Consume).Guid)),Is.EqualTo(before));
        Assert.That(View().GetProperty("craftable").GetBoolean(),Is.False);
        geometry.Clear();Call(peer,"action",new{name="craft",args=new{index,eye}});Pump();
        Assert.That(server.Transactions.Diag.CraftsApplied,Is.EqualTo(1));
        Assert.That(bag.getItemCount(Crafting.Resolve(recipe.Inputs.First(i=>i.Consume).Guid)),Is.LessThan(before));
        var facts=State(peer).GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="craftingContext").ToArray();
        Assert.That(facts.Any(f=>f.GetProperty("data").GetProperty("result").GetString()=="CraftRejected"),Is.True);
        Assert.That(facts.Any(f=>f.GetProperty("data").GetProperty("result").GetString()=="Crafted"),Is.True);
        Assert.That(State(host).GetProperty("events").EnumerateArray().Any(e=>e.GetProperty("kind").GetString()=="craftingContext"),Is.False);
        // The last published protocol must fail at join, before silently losing the new request/result.
        var old=new NetClientSession(new UdpClientTransport("127.0.0.1",port),"v71",protocolVersion:71,contentHash:NetContent.Hash);
        try { old.Connect();for(int n=0;n<100&&old.State!=NetSessionState.Disconnected;n++){old.Tick();Call(host,"tick");Thread.Sleep(1);}
            Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));Assert.That(old.RejectServerVersion,Is.EqualTo(72)); }
        finally { old.Disconnect(); }
    }
}
