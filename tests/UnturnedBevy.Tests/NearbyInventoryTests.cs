using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture,NonParallelizable]
public sealed class NearbyInventoryTests
{
    static readonly JsonSerializerOptions Json = new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true,
        Converters={new VectorJson(),new ByteArrayJson()}};
    BridgeHost _host,_peer;
    ushort _port;
    static T Field<T>(BridgeHost h,string name)=>(T)typeof(BridgeHost).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(h)!;
    NetWorldServer Server=>Field<NetWorldServer>(_host,"_server");
    NetWorldClient Client=>Field<NetWorldClient>(_peer,"_client");
    static JsonElement Call(BridgeHost h,string op,object args=null)=>JsonSerializer.SerializeToElement(h.Handle(op,JsonSerializer.SerializeToElement(args??new{},Json)),Json);
    static JsonElement State(BridgeHost h)=>JsonSerializer.SerializeToElement(h.State(),Json);
    static JsonElement Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    static string Content() {for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)
        if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static readonly Vector3 Eye=new(0,1.75f,0);
    void Pump(int n=15){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp] public void Setup()
    {
        using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback,0));ushort port=_port=(ushort)((IPEndPoint)socket.LocalEndPoint!).Port;socket.Close();
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=port});Pump(30);
    }
    [TearDown] public void Dispose(){_peer?.Dispose();_host?.Dispose();}
    WorldItemReplication.WorldItemEntity Spawn(Item item,Vector3? pos=null,uint? id=null)
    {var e=Server.WorldItems.ServerSpawn(id.HasValue?new NetId(id.Value):Server.Ids.Mint(),item,pos??new(0,1.75f,-2),Server.Session.CurrentTick);
     Server.WorldItems.ServerSettle(e.NetIdValue,e.Pos,Server.Session.CurrentTick);return e;}
    static float[] Array(Vector3 v)=>new[]{v.x,v.y,v.z};
    void Open(uint request=1,Vector3? eye=null){Action(_peer,"nearbyInventory",new{requestId=request,action="Open",eye=Array(eye??Eye)});Pump();}
    JsonElement Address(int index=0)=>Call(_peer,"inventoryView").GetProperty("items").EnumerateArray()
        .Where(r=>r.GetProperty("address").GetProperty("page").GetByte()==PlayerInventory.AREA).ElementAt(index).GetProperty("address").Clone();
    JsonElement Request(JsonElement address,uint request,uint crate=0,byte destination=255,Vector3? eye=null,ulong? token=null)
        =>JsonSerializer.SerializeToElement(new{requestId=request,action="Transfer",scanId=address.GetProperty("scanId").GetUInt32(),
            revision=address.GetProperty("revision").GetUInt32(),worldId=address.GetProperty("worldId").GetUInt32(),
            token=token??address.GetProperty("token").GetUInt64(),x=address.GetProperty("x").GetByte(),y=address.GetProperty("y").GetByte(),
            crateId=crate,destination,eye=Array(eye??Eye)},Json);
    void Send(JsonElement request){Action(_peer,"nearbyInventory",request);Pump();}
    [Test]
    public void ActualUdpFullIdentityTilesAndTakeUseReceivedTokensAndPreserveRealWorldState()
    {
        var item=new Item(4,1,37){gunAmmo=11,gunChambered=true,gunChamberedType="HP",gunMagId=6,
            gunSightId=22,gunAttachSeeded=true,fuelLevel=-1,fluidAmount=-1,preserved=true};
        var e=Spawn(item);Open();var a=Address();
        Assert.That(a.GetProperty("worldId").GetUInt32(),Is.EqualTo(e.NetIdValue));Assert.That(a.GetProperty("token").GetUInt64(),Is.Not.Zero);
        var received=Client.Nearby.Rows[0].Jar.item;
        foreach(var field in typeof(Item).GetFields())Assert.That(field.GetValue(received),Is.EqualTo(field.GetValue(item)),field.Name);
        Assert.That(Field<NetWorldClient>(_host,"_client").Nearby.Revision,Is.Zero,"owner-only projection");
        var bag=Server.Transactions.InventoryForTest(Client.PlayerId);
        Assert.That(bag.items[PlayerInventory.AREA].getItemCount(),Is.Zero,"authority has no synthetic ground bag");
        var state=State(_peer);Assert.That(state.GetProperty("inventory").GetProperty("pages").EnumerateArray()
            .Single(p=>p.GetProperty("index").GetByte()==8).GetProperty("items").GetArrayLength(),Is.EqualTo(1));
        var req=Request(a,2);Action(_peer,"nearbyInventory",req);Assert.That(Server.WorldItems.TryGet(e.NetIdValue,out _),Is.True,"queued intent is not a local pickup");Pump();
        Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Transferred));Assert.That(Server.WorldItems.TryGet(e.NetIdValue,out _),Is.False);
        var own=bag.items.Take(PlayerInventory.OWNPAGES).SelectMany(p=>p.items).Single(j=>j.item.id==4).item;
        foreach(var field in typeof(Item).GetFields())Assert.That(field.GetValue(own),Is.EqualTo(field.GetValue(item)),field.Name);
        Assert.That(bag.items[0].getItemCount(),Is.Zero,"source Nearby Take uses grid add, not auto-holster");
        Send(req);Assert.That(bag.getItemCount(4),Is.EqualTo(1),"exact request replay cannot duplicate the pickup");
        Assert.That(State(_peer).GetProperty("worldItems").EnumerateArray().Any(i=>i.GetProperty("netId").GetUInt32()==e.NetIdValue),Is.False);
    }
    void Wall(bool sourceWorld=true,float half=20)
    {
        Call(_host,"worldDynamicGeometry",new{key="nearby-test-wall",meshes=new[]{new{sourceWorld,
            vertices=new[]{new[]{-half,1.75f-half,-1f},new[]{half,1.75f-half,-1f},new[]{half,1.75f+half,-1f},new[]{-half,1.75f+half,-1f}},indices=new[]{0,1,2,0,2,3}}}});
    }
    [Test]
    public void SourceRadiusCameraAndWorldLayerLosAreRecheckedAtTransferNotCachedFromOpen()
    {
        var e=Spawn(new Item(13));Spawn(new Item(14),Eye+new Vector3(4,0,0));Open();Assert.That(Client.Nearby.Rows,Has.Length.EqualTo(1),"strict four metres");
        var a=Address();Wall();Send(Request(a,2));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Denied));
        Assert.That(Server.WorldItems.TryGet(e.NetIdValue,out _),Is.True);
        Open(3);Assert.That(Client.Nearby.Rows,Is.Empty,"full opaque world wall");
        Wall(sourceWorld:false);Open(4);Assert.That(Client.Nearby.Rows,Has.Length.EqualTo(1),"small/transparent non-world geometry is not source LOS");
        Send(Request(Address(),5,eye:new Vector3(100,1.75f,0)));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.InvalidEye));
        Send(Request(Address(),6,token:1));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Stale));
    }
    [Test]
    public void AnyVisiblePaddedCornerAdmitsTheItemAndActualNativePoseControlsTheSamples()
    {
        var geometry=new WorldGeometry();var adapter=new NearbyGeometry(Content(),geometry,_=>null);
        var item=Spawn(new Item(4));var points=adapter.Samples(item);Assert.That(points,Has.Length.EqualTo(9));
        var centre=points[0];var mid=(Eye+centre)*.5f;
        geometry.AddMesh(new[]{mid+new Vector3(-.001f,-.001f,0),mid+new Vector3(.001f,-.001f,0),mid+new Vector3(.001f,.001f,0),mid+new Vector3(-.001f,.001f,0)},new[]{0,1,2,0,2,3});geometry.Build();
        Assert.That(geometry.SourceWorldBlocked(Eye,centre),Is.True);Assert.That(adapter.Visible(item,Eye),Is.True,"a clear corner suffices, no centre-only ray");
        var next=adapter.Samples(new(){NetIdValue=item.NetIdValue+1,ItemId=item.ItemId,Pos=item.Pos});
        Assert.That(points.Skip(1).ToArray(),Is.Not.EqualTo(next.Skip(1).ToArray()),"same current rendered net-ID yaw");
    }
    [Test]
    public void SceneInsertionOrderSurvivesIdsAndCloseReopenForeignTokenAndReplacementAreRejected()
    {
        Spawn(new Item(13,1,41),id:900);Spawn(new Item(14,1,42),id:2);Open();
        Assert.That(Client.Nearby.Rows.Select(r=>r.Sources[0].WorldId),Is.EqualTo(new uint[]{900,2}));
        var old=Address();Action(_peer,"nearbyInventory",new{requestId=2,action="Close"});Open(3);
        Send(Request(old,4));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Stale));
        var current=Address();Server.WorldItems.ServerSpawn(new NetId(900),new Item(13,1,41),new(0,1.75f,-2),Server.Session.CurrentTick);
        Send(Request(current,5));Assert.That(Client.Nearby.Result,Is.AnyOf(NearbyResult.Denied,NearbyResult.Stale));
        Assert.That(Server.WorldItems.TryGet(900,out _),Is.True);
    }
    [Test]
    public void StoreRequiresCurrentOpenViewerAccessAndRepaintsOtherViewerAfterPartialMoneyTransfer()
    {
        var raw=new Item(1055,7,76);var e=Spawn(raw);var crate=Server.Inventories.ServerRegisterCrate(new NetId(60000),1,1,Vector3.zero);
        crate.Storage.addItem(0,0,0,new Item(Currency.StackId,450));
        Action(_peer,"openStorage",new{netId=60000});Action(_host,"openStorage",new{netId=60000});Pump();Open();
        var a=Address();Send(Request(a,2,60001,7));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Denied));
        Send(Request(Address(),3,60000,7));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Transferred));
        Assert.That((raw.id,raw.amount),Is.EqualTo(((ushort)1056,(ushort)650)));Assert.That(crate.Storage.getItem(0).item.amount,Is.EqualTo(500));
        var other=Call(_host,"inventoryView").GetProperty("tiles").GetProperty("cells").EnumerateArray().Single(c=>c.GetProperty("page").GetByte()==7);
        Assert.That(other.GetProperty("tile").GetProperty("amount").GetUInt16(),Is.EqualTo(500));
        Send(Request(Address(),4,60000,7));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.NoRoom));Assert.That(raw.amount,Is.EqualTo(650));
        crate.Access=(_,_)=>false;Send(Request(Address(),5,60000,7));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Denied));
        Action(_peer,"closeStorage",new{});Pump();Send(Request(Address(),6,60000,7));Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Denied));
        Assert.That(Server.WorldItems.TryGet(e.NetIdValue,out _),Is.True);
    }
    JsonElement Operation(JsonElement address,uint request,string operation,byte page=0,byte toX=0,byte toY=0,byte toRot=0,
        ushort amount=0,uint crate=0,Vector3? eye=null)=>JsonSerializer.SerializeToElement(new{requestId=request,operation,
        scanId=address.GetProperty("scanId").GetUInt32(),revision=address.GetProperty("revision").GetUInt32(),
        worldId=address.GetProperty("worldId").GetUInt32(),token=address.GetProperty("token").GetUInt64(),
        x=address.GetProperty("x").GetByte(),y=address.GetProperty("y").GetByte(),amount,crateId=crate,page,toX,toY,toRot,
        eye=Array(eye??Eye)},Json);
    NearbyResult Operate(JsonElement request)
    {
        uint id=request.GetProperty("requestId").GetUInt32();NearbyResult? seen=null;
        void Saw(NearbyInventoryEvent e){if(e.RequestId==id)seen??=e.Result;}
        Client.NearbyChanged+=Saw;
        try{Assert.That(Action(_peer,"nearbyOperation",request).GetProperty("queued").GetBoolean(),Is.True);Pump();
            Assert.That(seen.HasValue,Is.True,"correlated owner-only result");return seen.Value;}
        finally{Client.NearbyChanged-=Saw;}
    }
    JsonElement PeerPage(byte page)=>State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray()
        .Single(p=>p.GetProperty("index").GetByte()==page).GetProperty("items").Clone();
    [Test]
    public void ActualUdpPlaceUsesReceivedIdentityDraggedCellAndRotationWithoutLocalMutation()
    {
        var magazine=new Item(6,17,58){magLoadedRound="5.56x45mm NATO"};var e=Spawn(magazine);Open();var a=Address();
        var bag=Server.Transactions.InventoryForTest(Client.PlayerId);var pocket=bag.items[2];
        Assert.That((pocket.width,pocket.height),Is.EqualTo(((byte)5,(byte)3)));
        pocket.addItem(0,0,0,new Item(13));Pump();a=Address();
        Assert.That(Operate(Operation(a,2,"Place",page:2,toX:0,toY:0,toRot:1)),Is.EqualTo(NearbyResult.NoRoom),"occupied cell never swaps with ground");
        var place=Operation(Address(),3,"Place",page:2,toX:4,toY:0,toRot:1);
        Assert.That(Action(_peer,"nearbyOperation",place).GetProperty("queued").GetBoolean(),Is.True);
        Assert.That(Server.WorldItems.TryGet(e.NetIdValue,out _),Is.True,"queued intent is not a pickup");
        Assert.That(PeerPage(2).GetArrayLength(),Is.EqualTo(1),"the dashboard still shows only accepted owner cells");
        Pump();Assert.That(Client.Nearby.Result,Is.EqualTo(NearbyResult.Transferred));
        var jar=pocket.getItem(4,0);Assert.That((jar.x,jar.y,jar.rot),Is.EqualTo(((byte)4,(byte)0,(byte)1)));
        foreach(var field in typeof(Item).GetFields())Assert.That(field.GetValue(jar.item),Is.EqualTo(field.GetValue(magazine)),field.Name);
        Assert.That(Server.WorldItems.TryGet(e.NetIdValue,out _),Is.False);
        var drawn=PeerPage(2).EnumerateArray().Single(i=>i.GetProperty("id").GetUInt16()==6);
        Assert.That((drawn.GetProperty("x").GetByte(),drawn.GetProperty("y").GetByte(),drawn.GetProperty("rot").GetByte()),
            Is.EqualTo(((byte)4,(byte)0,(byte)1)),"native pages receive the accepted rotated cell");
        Assert.That(PeerPage(PlayerInventory.AREA).GetArrayLength(),Is.Zero);
        Action(_peer,"nearbyOperation",place);Pump();
        Assert.That(bag.items.SelectMany(p=>p.items).Count(j=>j.item.id==6),Is.EqualTo(1),"exact replay cannot duplicate");
    }
    [Test]
    public void ActualUdpSplitOfGroundMoneyLandsInNamedOwnedAndViewedCellsConservingValue()
    {
        var raw=new Item(1055,7,76);var e=Spawn(raw);var crate=Server.Inventories.ServerRegisterCrate(new NetId(60000),2,2,Vector3.zero);
        Action(_peer,"openStorage",new{netId=60000});Action(_host,"openStorage",new{netId=60000});Pump();Open();
        var a=Address();Assert.That(Client.Nearby.Rows.Select(r=>r.Jar.item.amount),Is.EqualTo(new ushort[]{500,200}));
        Assert.That(Operate(Operation(a,2,"Split",page:2,toX:1,toY:1,amount:500)),Is.EqualTo(NearbyResult.Denied),"the whole stack is a move");
        Assert.That(Operate(Operation(Address(),3,"Split",page:2,toX:1,toY:1,amount:75)),Is.EqualTo(NearbyResult.Transferred));
        var bag=Server.Transactions.InventoryForTest(Client.PlayerId);
        Assert.That(bag.items[2].getItem(1,1).item.amount,Is.EqualTo(75));Assert.That((raw.id,raw.amount),Is.EqualTo(((ushort)1056,(ushort)625)));
        Assert.That(Operate(Operation(Address(),4,"Split",page:2,toX:1,toY:1,amount:25)),Is.EqualTo(NearbyResult.Transferred));
        Assert.That(bag.items[2].getItem(1,1).item.amount,Is.EqualTo(100),"same-ID cell merges");
        Assert.That(Operate(Operation(Address(),5,"Split",page:7,toX:1,toY:0,amount:30,crate:60001)),Is.EqualTo(NearbyResult.Denied));
        Assert.That(Operate(Operation(Address(),6,"Split",page:7,toX:1,toY:0,amount:30,crate:60000)),Is.EqualTo(NearbyResult.Transferred));
        Assert.That(crate.Storage.getItem(1,0).item.amount,Is.EqualTo(30));
        var other=Call(_host,"inventoryView").GetProperty("tiles").GetProperty("cells").EnumerateArray()
            .Single(c=>c.GetProperty("page").GetByte()==7);
        Assert.That(other.GetProperty("tile").GetProperty("amount").GetUInt16(),Is.EqualTo(30),"other viewer repainted");
        Assert.That(raw.amount+bag.getItemCount(Currency.StackId)+crate.Storage.items.Sum(j=>j.item.amount),Is.EqualTo(700));
        Assert.That(bag.items[PlayerInventory.AREA].getItemCount(),Is.Zero);
    }
    [Test]
    public void ActualUdpEmptyZeroesOnlyTheExactGroundCanAndRefusesOtherRows()
    {
        var can=new Item(28){fuelLevel=55};var e=Spawn(can);Spawn(new Item(13));Open();
        var rows=Call(_peer,"inventoryView").GetProperty("items").EnumerateArray().Where(r=>r.GetProperty("address").GetProperty("page").GetByte()==8).ToArray();
        Assert.That(rows[0].GetProperty("actions").EnumerateArray().Select(b=>b.GetProperty("action").GetString()),Is.EqualTo(new[]{"emptyFuel","transfer","close"}));
        Assert.That(rows[1].GetProperty("actions").EnumerateArray().Select(b=>b.GetProperty("action").GetString()),Is.EqualTo(new[]{"transfer","close"}));
        Assert.That(Operate(Operation(Address(1),2,"EmptyFuel")),Is.EqualTo(NearbyResult.Denied));
        Wall();Assert.That(Operate(Operation(Address(),3,"EmptyFuel")),Is.EqualTo(NearbyResult.Denied),"transfer-time LOS");
        Assert.That(can.fuelLevel,Is.EqualTo(55));
        Wall(sourceWorld:false);   // replaced by small/transparent non-world geometry: clear again
        Assert.That(Operate(Operation(Address(),4,"EmptyFuel")),Is.EqualTo(NearbyResult.Transferred));
        Assert.That(can.fuelLevel,Is.Zero);Assert.That(Server.WorldItems.TryGet(e.NetIdValue,out _),Is.True);
        Assert.That(Client.Nearby.Rows[0].Jar.item.fuelLevel,Is.Zero,"accepted projection repaints");
    }
    [Test]
    public void PublishedProtocol76PeersAreRefusedAtTheHandshake()
    {
        var old=new NetClientSession(new SDG.NetTransport.Udp.UdpClientTransport("127.0.0.1",_port),"v76",protocolVersion:76,contentHash:UnturnedGodot.NetContent.Hash);
        try{old.Connect();for(int i=0;i<100&&old.State!=NetSessionState.Disconnected;i++){old.Tick();Call(_host,"tick");Thread.Sleep(1);}
            Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));Assert.That(old.RejectServerVersion,Is.EqualTo(NetProtocol.Version));}
        finally{old.Disconnect();}
    }
    [Test]
    public void NativeVerticalAndInvalidStuckDirectionsKeepFiniteMatchingModelSamples()
    {
        var geometry=new WorldGeometry();var item=Spawn(new Item(4));
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Content(),"items/items_manifest.json")));
        var center=manifest.RootElement.GetProperty("4").GetProperty("center").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        foreach(float sign in new[]{1f,-1f})
        {
            var adapter=new NearbyGeometry(Content(),geometry,_=>new(new[]{0f,sign,0f},null,null));
            var points=adapter.Samples(item);
            Assert.That(points.All(p=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z)),Is.True);
            Assert.That((points[0]-(item.Pos+new Vector3(sign*center[1],-sign*center[2],-center[0]))).magnitude,Is.LessThan(.00001f));
        }
        var fallback=new NearbyGeometry(Content(),geometry,_=>null).Samples(item);
        foreach(var invalid in new[]{new[]{0f,0f,0f},new[]{float.NaN,0f,0f},new[]{float.PositiveInfinity,1f,0f}})
        {
            var adapter=new NearbyGeometry(Content(),geometry,_=>new(invalid,null,null));
            Assert.That(adapter.Samples(item),Is.EqualTo(fallback),"invalid direction follows native drop pose");
        }
        var euler=new[]{.2f,.7f,-.3f};
        var expected=new NearbyGeometry(Content(),geometry,_=>new(null,null,euler)).Samples(item);
        Assert.That(new NearbyGeometry(Content(),geometry,_=>new(new float[3],null,euler)).Samples(item),Is.EqualTo(expected));
    }

}
