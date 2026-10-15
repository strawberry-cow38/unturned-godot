using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class NpcViewTests
{
    BridgeHost _host, _remote;
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() } };
    static string Content()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException();
    }
    static JsonElement Call(BridgeHost h, string op, object data = null) => JsonSerializer.SerializeToElement(h.Handle(op, JsonSerializer.SerializeToElement(data ?? new { })), Options);
    static JsonElement State(BridgeHost h) => JsonSerializer.SerializeToElement(h.State(), Options);
    static T Field<T>(BridgeHost h, string name) => (T)typeof(BridgeHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h);
    NetWorldServer Server => Field<NetWorldServer>(_host, "_server");
    ushort Owner => Field<NetWorldClient>(_remote, "_client").PlayerId;
    [SetUp] public void Setup() { _host = new(); _remote = new(); }
    [TearDown] public void Cleanup() { _remote.Dispose(); _host.Dispose(); }
    void Join()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        Call(_host, "init", new { contentRoot = Content(), listenPort = port });
        Call(_remote, "init", new { contentRoot = Content(), connectHost = "127.0.0.1", connectPort = port }); Pump();
        Assert.That(State(_remote).GetProperty("connected").GetBoolean(), Is.True);
    }
    void Pump(int n = 20) { for (int i = 0; i < n; i++) { Call(_remote, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    void Act(string name, object args) => Call(_remote, "action", new { name, args });
    JsonElement View(object args = null) => Call(_remote, "npcView", args);

    [Test]
    public void RealUdpDialogueUsesAcceptedNavigationAndOriginalFilteredIndicesAfterEventAck()
    {
        Join();
        Act("npcTalk", new { dialogue = 59 });
        Assert.That(View().GetProperty("openDialogue").GetInt32(), Is.Zero, "A queued command cannot navigate the presentation");
        Pump();
        var view = View();
        Assert.That(view.GetProperty("dialogue").GetProperty("responses").EnumerateArray().Select(x => x.GetProperty("index").GetInt32()), Is.EqualTo(new[] { 1, 3 }));
        Act("npcChoose", new { dialogue = 59, response = 0 }); Pump();
        Assert.That(View().GetProperty("openDialogue").GetInt32(), Is.EqualTo(59), "The hidden trade branch is refused");
        Act("npcChoose", new { dialogue = 59, response = 1 }); Pump();
        Assert.That(View().GetProperty("dialogue").GetProperty("pages").GetArrayLength(), Is.EqualTo(2));
        long seq = State(_remote).GetProperty("events").EnumerateArray().Max(x => x.GetProperty("seq").GetInt64());
        Call(_remote, "snapshot", new { ackEvents = seq });
        Assert.That(View().GetProperty("openDialogue").GetInt32(), Is.EqualTo(60), "Accepted dialogue survives transient event consumption");
        Act("npcClose", new { }); Pump(); Act("npcTalk", new { dialogue = 59 }); Pump();
        Assert.That(View().GetProperty("dialogue").GetProperty("responses")[0].GetProperty("index").GetInt32(), Is.Zero, "Accepted flag reward reveals original branch zero");
        Call(_remote, "init", new { contentRoot = Content() });
        Assert.That(View().GetProperty("revision").GetInt32(), Is.Zero, "A new session cannot inherit a previous server's NPC state");
    }

    [Test]
    public void RealBatchedVendorFactSurvivesAckWithoutReopeningOnUnrelatedState()
    {
        Join(); Server.Npcs.For(Owner).SetFlag(348, 3); Pump();
        Act("npcTalk", new { dialogue = 87 }); Pump();
        Act("npcChoose", new { dialogue = 87, response = 5 }); Pump();
        var view = View();
        Assert.That(view.GetProperty("vendorRevision").GetInt32(), Is.EqualTo(1));
        string vendor = view.GetProperty("vendorOpened").GetString();
        Assert.That(vendor, Is.EqualTo("d98e2980ad3d47518c08b9e1df98f7ef"));
        var trade = View(new { vendor }).GetProperty("trade");
        Assert.That(trade.GetProperty("name").GetString(), Is.EqualTo("Coalition Aircraft Hangar"));
        Assert.That(trade.GetProperty("selling").EnumerateArray().All(x => x.GetProperty("vehicle").GetBoolean() && !x.GetProperty("enabled").GetBoolean()), Is.True);
        Server.Npcs.For(Owner).SetFlag(999, 2); Pump();
        Assert.That(View().GetProperty("vendorRevision").GetInt32(), Is.EqualTo(1));
        Assert.That(Server.Npcs.For(Owner).PendingVendor, Is.Empty);
    }

    [Test]
    public void QuestReadModelUsesAcceptedFlagsAndSourceAutoTrackWhileTradeUsesReplicaWallet()
    {
        Join(); var npc = Server.Npcs.For(Owner);
        npc.SetQuestStatus(157, ENpcQuestStatus.Active); npc.SetFlag(156, 3); Pump();
        var quest = View().GetProperty("tracker");
        Assert.That(quest.GetProperty("objectives")[0].GetProperty("text").GetString(), Is.EqualTo("Cleanup 3/9 Barnacles"));
        Assert.That(quest.GetProperty("status").GetString(), Is.EqualTo("active"));
        npc.SetFlag(156, 12); Pump();
        Assert.That(View().GetProperty("tracker").GetProperty("status").GetString(), Is.EqualTo("ready"));
        Assert.That(View().GetProperty("tracker").GetProperty("objectives")[0].GetProperty("text").GetString(), Is.EqualTo("Cleanup 9/9 Barnacles"));
        var vendor = NpcCatalog.VendorByKey("Chef_Fresh_Food_Market");
        Server.Inventories.TryGet(Owner, out var bag); Assert.That(Currency.Pay(bag.Inventory, 400), Is.True); Server.Inventories.ServerMarkDirty(Owner); Pump();
        int price = vendor.Selling[0].Cost, id = vendor.Selling[0].Item, before = bag.Inventory.getItemCount((ushort)id);
        Assert.That(View(new { vendor = vendor.Guid }).GetProperty("wallet").GetInt32(), Is.EqualTo(400));
        Act("npcTrade", new { vendor = vendor.Guid, sell = false, index = 0, count = 1 }); Pump();
        Assert.That(bag.Inventory.getItemCount((ushort)id), Is.EqualTo(before + 1));
        Assert.That(View(new { vendor = vendor.Guid }).GetProperty("wallet").GetInt32(), Is.EqualTo(400 - price));
        npc.SetQuestStatus(157, ENpcQuestStatus.Completed); Pump();
        Assert.That(View(new { pinnedQuest = 157 }).GetProperty("tracker").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(View().GetProperty("quests")[0].GetProperty("status").GetString(), Is.EqualTo("completed"));
    }

    [Test]
    public void PlacedRetailAndCustomCharactersKeepAllFieldsAndSourceSanitizedDisplayName()
    {
        Call(_host, "init", new { contentRoot = Content() });
        string root = Path.Combine(Path.GetTempPath(), "bevy-npc-placement-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "objects")); Directory.CreateDirectory(Path.Combine(root, "spawns"));
        File.WriteAllText(Path.Combine(root, "objects", "placements.txt"), "");
        File.WriteAllText(Path.Combine(root, "spawns", "editor_PEI_npcs.txt"), "Chef 1 2 -3 47\nBevy_Read_Test 4 5 -6 90\n");
        string previous = Godot.ProjectSettings.ContentRoot;
        try
        {
            Godot.ProjectSettings.ContentRoot = root;
            NpcCatalog.SaveCustom(new NpcCharacterDef { Id = 60000, Key = "Bevy_Read_Test", Name = " [img] A\u0301lice\r\n ",
                Shirt = 230, Pants = 231, Hat = 239, Vest = 12, Mask = 13, Glasses = 14, Backpack = 15,
                Face = 30, Skin = "#4B3D31", Hair = "#191919", Dialogue = 59, Shop = "Chef_Fresh_Food_Market" });
            var placed = MapServices.Parse(JsonSerializer.SerializeToElement(new { contentRoot = root, mapName = "PEI" })).Spec.GetProperty("npcs");
            Assert.That(placed.GetArrayLength(), Is.EqualTo(2));
            Assert.That(placed[0].GetProperty("position")[2].GetSingle(), Is.EqualTo(-3), "Authored NPC positions are already in engine coordinates");
            Assert.That(placed[0].GetProperty("character").GetProperty("hat").GetInt32(), Is.EqualTo(239));
            var custom = placed[1].GetProperty("character");
            Assert.That(custom.EnumerateObject().Count(), Is.EqualTo(16));
            Assert.That(custom.GetProperty("displayName").GetString(), Is.EqualTo(ProfileRules.SanitizeName(" [img] A\u0301lice\r\n ")));
            Assert.That(custom.GetProperty("shop").GetString(), Is.EqualTo("Chef_Fresh_Food_Market"));
            Assert.That(custom.GetProperty("face").GetInt32(), Is.EqualTo(30));
            var catalog = Call(_host, "npcCatalog").GetProperty("characters").EnumerateArray().First(c => c.GetProperty("key").GetString() == "Bevy_Read_Test");
            Assert.That(catalog.GetRawText(), Is.EqualTo(custom.GetRawText()), "Map and console character metadata share one DTO");
        }
        finally {
            Godot.ProjectSettings.ContentRoot = previous; Directory.Delete(root, true);
            ((System.Collections.IDictionary)typeof(NpcCatalog).GetField("_charsByKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).Remove("Bevy_Read_Test");
            ((System.Collections.IDictionary)typeof(NpcCatalog).GetField("_chars", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).Remove(60000);
            ((HashSet<string>)typeof(NpcCatalog).GetField("_custom", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).Remove("Bevy_Read_Test");
        }
    }
}
