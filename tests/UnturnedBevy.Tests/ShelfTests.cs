using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot.Net;
namespace UnturnedBevyTests;
/// <summary>Original StoreShelf take-one over actual UDP: a joined client's
/// `takeFromStorage` is CommandTakeFromStorage (45), validated by the server's
/// ServerTakeFromStorage -- StorageReach, the exact cell, and add-first (a
/// full bag leaves the item on the shelf) -- with each verdict counted, and
/// reflected back through the container's display digest and the owner's bag.</summary>
[TestFixture, NonParallelizable]
public sealed class ShelfTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    BridgeHost _host,_peer;int _port;
    NetWorldServer Server=>(NetWorldServer)typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_host);
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    void Pump(int n=40){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Thread.Sleep(1);}}
    [SetUp]public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,0,0}});
        Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
    }
    [TearDown]public void Cleanup(){_peer.Dispose();_host.Dispose();}
    JsonElement Shelf(BridgeHost h)=>State(h).GetProperty("containers").EnumerateArray().Single();
    (int cell,int item)[] Display(BridgeHost h)=>Shelf(h).GetProperty("display").EnumerateArray()
        .Select(c=>(c.GetProperty("cell").GetInt32(),c.GetProperty("itemId").GetInt32())).OrderBy(c=>c.Item1).ToArray();
    int Bag(ushort id)=>State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray().Where(p=>p.GetProperty("index").GetInt32()<7)
        .SelectMany(p=>p.GetProperty("items").EnumerateArray()).Count(i=>i.GetProperty("id").GetUInt16()==id);

    [Test]
    public void AJoinedClientTakesOnlyTheNamedCellInReachAndTheDigestDropsIt()
    {
        // The source map manifest row for the PEI gondola, registered through the production ContainerNetSync.
        Call(_host,"worldServices",new{containers=new[]{new{mesh="Shelf_1",table=6,display=true,label="Store Shelf",position=new[]{0f,0,-1.5f},yaw=0f}}});
        Pump();
        uint id=Shelf(_host).GetProperty("netId").GetUInt32();
        Assert.That(Server.Inventories.TryGetCrate(id,out var crate),Is.True);
        crate.Storage.clear();
        crate.Storage.addItem(3,1,0,new Item(13));
        crate.Storage.addItem(5,2,0,new Item(14));
        Pump();
        Assert.That(Shelf(_peer).GetProperty("width").GetInt32(),Is.EqualTo(8),"StoreShelf.GridDims: 8 slots");
        Assert.That(Display(_peer),Is.EqualTo(new[]{(11,13),(21,14)}),"the joined client sees the digest: y*Width+x");
        Assert.That(Bag(13),Is.Zero);

        Action(_peer,"console",new{text="teleport 30 0 30"});Pump();
        Action(_peer,"takeFromStorage",new{netId=id,x=3,y=1});Pump();
        Assert.That(Server.Transactions.Diag.ShelfTakesRejected,Is.EqualTo(1),"beyond StorageReach the server refuses the command");
        Assert.That(Display(_peer),Has.Length.EqualTo(2),"...and nothing moves");
        Assert.That(Bag(13),Is.Zero);

        Action(_peer,"console",new{text="teleport 0 0 0"});Pump();
        Action(_peer,"takeFromStorage",new{netId=id,x=4,y=1});Pump();
        Assert.That(Server.Transactions.Diag.ShelfTakesRejected,Is.EqualTo(2));
        Assert.That(Display(_peer),Has.Length.EqualTo(2),"an empty (stale) cell is not a licence to take the neighbour");

        Action(_peer,"takeFromStorage",new{netId=id,x=3,y=1});Pump();
        Assert.That(Server.Transactions.Diag.ShelfTakesApplied,Is.EqualTo(1));
        Assert.That(Display(_peer),Is.EqualTo(new[]{(21,14)}),"the accepted take drops only that cell");
        Assert.That(Bag(13),Is.EqualTo(1),"...into the taker's own bag");
        Assert.That(Bag(14),Is.Zero);
        Assert.That(State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray()
            .Where(p=>p.GetProperty("index").GetInt32()==PlayerInventory.STORAGE).Sum(p=>p.GetProperty("width").GetInt32()),Is.Zero,
            "a grab is not an open: the taker's STORAGE page stays empty");

        // ADD FIRST, REMOVE ONLY ON SUCCESS: with no room in the bag the item stays on the shelf.
        var bag=Server.Transactions.InventoryForTest(State(_peer).GetProperty("playerId").GetUInt16());
        for(byte page=PlayerInventory.SLOTS;page<PlayerInventory.OWNPAGES;page++)bag.items[page].loadSize(0,0);
        Action(_peer,"takeFromStorage",new{netId=id,x=5,y=2});Pump();
        Assert.That(Server.Transactions.Diag.ShelfTakesRejected,Is.EqualTo(3),"a full bag refuses the take");
        Assert.That(Display(_peer),Is.EqualTo(new[]{(21,14)}),"...and the item never leaves the shelf");
        Assert.That(crate.Storage.getItemCount(),Is.EqualTo(1));
    }
}
