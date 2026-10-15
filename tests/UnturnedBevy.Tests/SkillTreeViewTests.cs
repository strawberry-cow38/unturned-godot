using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class SkillTreeViewTests
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() } };
    static JsonElement Call(BridgeHost host, string op, object args = null) => JsonSerializer.SerializeToElement(host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), Json);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), Json);
    static JsonElement Tree(BridgeHost host) => State(host).GetProperty("skills").GetProperty("tree");
    static JsonElement Node(BridgeHost host, string id) => Tree(host).GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == id);
    static void Action(BridgeHost host, string name, object args) => Call(host, "action", new { name, args });
    static void Learn(BridgeHost host, string id) => Action(host, "learnSkillNode", new { nodeId = id });
    static void Pump(BridgeHost host, BridgeHost peer = null, int count = 12)
    { for (int i = 0; i < count; i++) { Call(host, "tick"); if (peer != null) { Call(peer, "tick"); Thread.Sleep(1); } } }
    static string Content()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException();
    }

    [Test]
    public void RealUdpLearnIsAcceptedOwnerOnlyAndOriginalReadModelExplainsCrossColumnRequirements()
    {
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)reservation.LocalEndPoint!).Port; reservation.Close();
        using var host = new BridgeHost(); using var peer = new BridgeHost();
        Call(host, "init", new { contentRoot = Content(), name = "GraphHost", listenPort = port, allowCheats = true, spawn = new[] { 0, 0, 0 } });
        Call(peer, "init", new { contentRoot = Content(), name = "GraphOwner", connectHost = "127.0.0.1", connectPort = port }); Pump(host, peer, 45);
        Assert.That(Tree(peer).GetProperty("nodes").GetArrayLength(), Is.EqualTo(15));
        Assert.That(Node(peer, "heavy_lifter").GetProperty("reason").GetString(), Is.EqualTo("Needs 20 XP."));
        Action(peer, "console", new { text = "xp 300" }); Pump(host, peer);
        Assert.That(Tree(peer).GetProperty("experience").GetUInt32(), Is.EqualTo(300));
        Learn(peer, "salvage");
        Assert.That(Node(peer, "salvage").GetProperty("learned").GetBoolean(), Is.False, "request is not an optimistic purchase");
        Pump(host, peer);
        Assert.That(Node(peer, "salvage").GetProperty("learned").GetBoolean(), Is.True);
        Assert.That(Tree(peer).GetProperty("experience").GetUInt32(), Is.EqualTo(280));
        Assert.That(Node(peer, "wiring").GetProperty("canTake").GetBoolean(), Is.True);
        Learn(peer, "salvage"); Learn(peer, "improvised_armour"); Pump(host, peer);
        Assert.That(Tree(peer).GetProperty("experience").GetUInt32(), Is.EqualTo(280));
        Assert.That(Node(peer, "improvised_armour").GetProperty("reason").GetString(), Is.EqualTo("Needs Sharpening."));
        Learn(peer, "sharpening"); Pump(host, peer);
        Assert.That(Node(peer, "improvised_armour").GetProperty("reason").GetString(), Is.EqualTo("Needs Heavy Lifter."));
        Learn(peer, "heavy_lifter"); Pump(host, peer); Learn(peer, "improvised_armour"); Pump(host, peer);
        Assert.That(Node(peer, "improvised_armour").GetProperty("learned").GetBoolean(), Is.True);
        Assert.That(Tree(peer).GetProperty("experience").GetUInt32(), Is.EqualTo(140));
        Assert.That(Node(host, "salvage").GetProperty("learned").GetBoolean(), Is.False);
        Assert.That(Tree(host).GetProperty("experience").GetUInt32(), Is.Zero);
        var client = (NetWorldClient)typeof(BridgeHost).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer)!;
        Assert.That(client.Skills.Count, Is.EqualTo(1), "remote receives only its owner block");
        string file = Path.Combine(Path.GetTempPath(), $"bevy-skill-udp-rejoin-{Guid.NewGuid():N}.json");
        try
        {
            Call(host, "save", new { path = file }); Call(peer, "disconnect"); Pump(host);
            Call(host, "load", new { path = file });
            Call(peer, "init", new { contentRoot = Content(), name = "GraphOwner", connectHost = "127.0.0.1", connectPort = port });
            Pump(host, peer, 45);
            Assert.That(Node(peer, "improvised_armour").GetProperty("learned").GetBoolean(), Is.True, "real UDP rejoin adopts the saved graph");
            Assert.That(Tree(peer).GetProperty("experience").GetUInt32(), Is.EqualTo(140));
            Assert.That(Node(host, "salvage").GetProperty("learned").GetBoolean(), Is.False);
        }
        finally { File.Delete(file); }
    }

    [Test]
    public void BridgeSaveLoadRestoresLearnedGraphAndXpWithoutReapplyingDescriptiveGrants()
    {
        using var host = new BridgeHost(); Call(host, "init", new { contentRoot = Content(), spawn = new[] { 0, 0, 0 } });
        Action(host, "console", new { text = "xp 100" }); Pump(host); Learn(host, "heavy_lifter"); Pump(host);
        string file = Path.Combine(Path.GetTempPath(), $"bevy-skill-tree-{Guid.NewGuid():N}.json");
        try
        {
            Call(host, "save", new { path = file }); Learn(host, "steady_hands"); Pump(host);
            Assert.That(Node(host, "steady_hands").GetProperty("learned").GetBoolean(), Is.True);
            Call(host, "load", new { path = file }); Pump(host);
            Assert.That(Node(host, "heavy_lifter").GetProperty("learned").GetBoolean(), Is.True);
            Assert.That(Node(host, "steady_hands").GetProperty("learned").GetBoolean(), Is.False);
            Assert.That(Tree(host).GetProperty("experience").GetUInt32(), Is.EqualTo(80));
            Assert.That(Node(host, "heavy_lifter").GetProperty("grants")[0].GetString(), Is.EqualTo("Carry weight +15"));
            Assert.That(State(host).GetProperty("skills").GetProperty("groups").EnumerateArray().SelectMany(g => g.EnumerateArray()).All(s => s.GetProperty("level").GetByte() == 0), Is.True);
        }
        finally { File.Delete(file); }
    }
}
