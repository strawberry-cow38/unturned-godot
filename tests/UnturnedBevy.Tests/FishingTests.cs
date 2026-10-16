using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot.Net;
namespace UnturnedBevyTests;
/// <summary>The v80 server-side fishing catch over actual UDP: a joined peer casts, the listen host's
/// FishingServices owns the bite timer, the catch window, the challenge and the reward, and the peer learns
/// its line's state from Event78 and sees the fish arrive in its replicated bag.</summary>
[TestFixture, NonParallelizable]
public sealed class FishingTests
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, IncludeFields=true, Converters={new VectorJson(),new ByteArrayJson()} };
    const ushort Rod = 503;
    static readonly ushort[] Fish = { 1349, 504, 505, 1351, 1959 };
    BridgeHost _host,_peer;int _port;
    static string Content(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"game/content")))return Path.Combine(d.FullName,"game/content");throw new DirectoryNotFoundException();}
    static JsonElement J(object v)=>JsonSerializer.SerializeToElement(v,Options);
    static JsonElement Call(BridgeHost h,string op,object args=null)=>J(h.Handle(op,J(args??new{})));
    static JsonElement State(BridgeHost h)=>J(h.State());
    static void Action(BridgeHost h,string name,object args)=>Call(h,"action",new{name,args});
    readonly List<(byte State,ushort Caught)> _seen=new();
    void Pump(int n=10){for(int i=0;i<n;i++){Call(_peer,"tick");Call(_host,"tick");Collect();Thread.Sleep(1);}}
    void Collect()
    {
        foreach(var e in State(_peer).GetProperty("events").EnumerateArray())
            if(e.GetProperty("kind").GetString()=="fishingState")
            {
                long seq=e.GetProperty("seq").GetInt64();if(seq<=_lastSeq)continue;_lastSeq=seq;
                _seen.Add((e.GetProperty("data").GetProperty("state").GetByte(),e.GetProperty("data").GetProperty("caughtItemId").GetUInt16()));
            }
    }
    long _lastSeq;
    [SetUp]public void Setup()
    {
        using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_port=((IPEndPoint)socket.LocalEndPoint).Port;}
        _host=new();_peer=new();Call(_host,"init",new{contentRoot=Content(),listenPort=_port,allowCheats=true,spawn=new[]{0,2,0}});
        Call(_host,"worldServices",new{hasWater=true,seaLevel=0f});
        _lastSeq=0;Call(_peer,"init",new{contentRoot=Content(),connectHost="127.0.0.1",connectPort=_port});Pump(24);
        _seen.Clear();_lastSeq=0;
    }
    [TearDown]public void Cleanup(){_peer.Dispose();_host.Dispose();}
    T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o);
    FishingServices Services=>Field<FishingServices>(_host,"_fishing");
    ushort PeerId=>State(_peer).GetProperty("playerId").GetUInt16();
    static (int Page,int X,int Y) Cell(BridgeHost h,ushort item)
    {
        foreach(var p in State(h).GetProperty("inventory").GetProperty("pages").EnumerateArray())
            foreach(var i in p.GetProperty("items").EnumerateArray())
                if(i.GetProperty("id").GetUInt16()==item)return(p.GetProperty("index").GetInt32(),i.GetProperty("x").GetInt32(),i.GetProperty("y").GetInt32());
        throw new InvalidOperationException($"no {item}");
    }
    void HoldRod(){Action(_peer,"console",new{text=$"give {Rod}"});Pump();var c=Cell(_peer,Rod);Action(_peer,"holdItem",new{page=c.Page,x=c.X,y=c.Y});Pump(20);}
    /// <summary>Every cast is answered (Line, or Idle for a refusal): wait for that answer.</summary>
    void Cast(float x,float y,float z){int before=_seen.Count;Action(_peer,"fishingCast",new{bobber=new[]{x,y,z},strength=.5f});for(int n=0;n<100&&_seen.Count==before;n++)Pump(1);}
    int FishInBag=>State(_peer).GetProperty("inventory").GetProperty("pages").EnumerateArray().SelectMany(p=>p.GetProperty("items").EnumerateArray())
        .Count(i=>Fish.Contains(i.GetProperty("id").GetUInt16()));
    uint Experience=>State(_peer).GetProperty("skills").GetProperty("experience").GetUInt32();

    [Test]
    public void TheServerRefusesForgedCastsAndLandsAFishOnlyThroughItsOwnWindowAndChallenge()
    {
        // Stand on the shore, 1 m above the sea, so each refusal below has exactly one cause.
        Action(_peer,"console",new{text="teleport 0 1 0"});Pump(40);
        Assert.That(Field<NetWorldServer>(_host,"_server").Players.TryGetByOwner(PeerId,out var me)&&me.Pos.y<1.5f&&Math.Abs(me.Pos.x)<0.5f,Is.True,"the server has the caster on the shore");
        Cast(5,0,-5);
        Assert.That(_seen.Last(),Is.EqualTo(((byte)FishingStateEvent.Idle,(ushort)0)),"no rod in the hand: refused");
        Assert.That(Services.LineCount,Is.Zero);
        HoldRod();
        Cast(200,0,0);
        Assert.That(Services.LineCount,Is.Zero,"beyond the cast reach");
        Cast(5,1.5f,-5);
        Assert.That(Services.LineCount,Is.Zero,"1.5 m off the sea plane (within the 2 m height bound): not water");
        Action(_peer,"fishingInput",new{kind=FishingInputCommand.Press});Pump();
        Assert.That(FishInBag,Is.Zero,"a press with no line catches nothing");

        uint xp=Experience;
        Cast(5,0,-5);
        Assert.That(_seen.Last().State,Is.EqualTo(FishingStateEvent.Line),"the server's line is in the water");
        Action(_peer,"fishingInput",new{kind=FishingInputCommand.Press});Pump();
        Assert.That(_seen.Last().State,Is.EqualTo(FishingStateEvent.Idle),"pressed before any bite: reeled in empty");
        Assert.That(FishInBag,Is.Zero);

        Cast(5,0,-5);
        for(int n=0;n<800&&_seen.Last().State!=FishingStateEvent.Bite;n++)Pump(1);
        Assert.That(_seen.Last().State,Is.EqualTo(FishingStateEvent.Bite),"the server's bite timer (6-14 s)");
        Pump(55);   // WARNING_DURATION: the window opens 1 s after the bite
        Action(_peer,"fishingInput",new{kind=FishingInputCommand.Press});Pump(2);
        Assert.That(_seen.Last().State,Is.EqualTo(FishingStateEvent.Challenge),"Rod_Fishing's catch challenge");
        // Track the fish with the server's own cursor: hold to pull up when the fish is above it.
        // The command lands a tick or two later, so steer on where the cursor will be.
        bool up=true;float last=-1;
        for(int n=0;n<3000&&_seen.Last().State==FishingStateEvent.Challenge;n++)
        {
            var sim=Services.SimFor(PeerId);if(sim==null)break;
            float centre=sim.ChallengeCursorPos+sim.ChallengeCursorSizeNorm*0.5f,v=last<0?0:centre-last;last=centre;
            bool want=sim.ChallengeFishPos>centre+v*4f;
            if(want!=up){Action(_peer,"fishingInput",new{kind=want?FishingInputCommand.Press:FishingInputCommand.Release});up=want;}
            Pump(1);
        }
        Pump(10);
        var landed=_seen.Last();
        Assert.That(landed.State,Is.EqualTo(FishingStateEvent.Idle));
        Assert.That(Fish,Does.Contain(landed.Caught),"a fish from the PEI table");
        Assert.That(FishInBag,Is.EqualTo(1),"granted into the authoritative bag and replicated");
        Assert.That(Experience,Is.EqualTo(xp+3),"Rod_Fishing Reward_Experience 3 on the server's skills");
    }

    [Test]
    public void PuttingTheRodAwayEndsTheLine()
    {
        HoldRod();Cast(5,0,-5);
        Assert.That(Services.LineCount,Is.EqualTo(1));
        Action(_peer,"console",new{text="give 13"});Pump();
        var c=Cell(_peer,13);Action(_peer,"holdItem",new{page=c.Page,x=c.X,y=c.Y});Pump(20);
        Assert.That(Services.LineCount,Is.Zero,"ClearFisher on any other equip");
        Assert.That(_seen.Last().State,Is.EqualTo(FishingStateEvent.Idle));
    }
}
