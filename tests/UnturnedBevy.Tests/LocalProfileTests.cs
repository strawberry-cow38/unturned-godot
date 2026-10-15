using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class LocalProfileTests
{
    BridgeHost _host, _remote;
    string _directory;
    readonly Dictionary<string, string> _environment = new();
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };
    static string Root()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return dir.FullName;
        throw new DirectoryNotFoundException("game/content");
    }
    static JsonElement Call(BridgeHost host, string op, object args = null) => JsonSerializer.SerializeToElement(
        host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), JsonOptions);
    static T Private<T>(BridgeHost host, string name) => (T)typeof(BridgeHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
    string Config => Path.Combine(_directory, "profile.cfg");
    string Content => Path.Combine(Root(), "game", "content");
    string Picture => Path.Combine(Root(), "bevy", "src", "character", "nameplate", "tests", "avatar.png");
    NetWorldClient Peer => Private<NetWorldClient>(_remote, "_client");
    NetWorldServer Server => Private<NetWorldServer>(_host, "_server");
    [SetUp] public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "unturned-profile-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        foreach (string name in new[] { "UG_USERNAME", "UG_FACE", "UG_PROFILE_PNG" })
        { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, null); }
        _host = new(); _remote = new();
    }
    [TearDown] public void Cleanup()
    {
        _remote.Dispose(); _host.Dispose();
        foreach (var pair in _environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        Directory.Delete(_directory, true);
    }
    void Pump(int count = 20)
    { for (int i = 0; i < count; i++) { Call(_remote, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }

    [Test]
    public void ImportOriginalConfigOnceAndPreserveOtherSectionsOnChoice()
    {
        string legacy = Path.Combine(_directory, "legacy.cfg");
        File.WriteAllText(legacy, "[character]\nface=19\n");
        var read = Call(_remote, "localProfile", new { path = Config, legacyPath = legacy });
        Assert.That(read.GetProperty("face").GetInt32(), Is.EqualTo(19));
        Assert.That(_remote.State(), Is.Null, "Preferences never create authority");
        File.AppendAllText(Config, "\n[other]\nkeep=42\n");
        Call(_remote, "setLocalFace", new { face = 31 });
        Assert.That(File.ReadAllText(Config), Does.Contain("keep=42"));
        File.WriteAllText(legacy, "[character]\nface=2\n");
        using var reopened = new BridgeHost();
        var reload = Call(reopened, "localProfile", new { path = Config, legacyPath = legacy });
        Assert.That(reload.GetProperty("face").GetInt32(), Is.EqualTo(31), "Imported preferences now belong to Bevy");
    }

    [TestCase("-5", 0)] [TestCase("31", 31)] [TestCase("32", 0)] [TestCase("999", 0)]
    public void EnvironmentFaceUsesOriginalClampBeforeSavedPreference(string forced, int expected)
    {
        File.WriteAllText(Config, "[character]\nface=7\n");
        Environment.SetEnvironmentVariable("UG_FACE", forced);
        var profile = Call(_remote, "localProfile", new { path = Config });
        Assert.That(profile.GetProperty("face").GetInt32(), Is.EqualTo(expected));
        Call(_remote, "setLocalFace", new { face = 5 });
        Assert.That(File.ReadAllText(Config), Does.Contain("face=5"));
        using var reopened = new BridgeHost();
        Assert.That(Call(reopened, "localProfile", new { path = Config }).GetProperty("face").GetInt32(), Is.EqualTo(expected));
    }

    [Test]
    public void FreshFaceIsPersistedWithinOriginalFreeRangeAndBrokenAvatarIsBestEffort()
    {
        Environment.SetEnvironmentVariable("UG_USERNAME", "  [i\u00admg] A\u0301lice\r\n  ");
        string oversized = Path.Combine(_directory, "oversized.png");
        using (var file = File.Create(oversized)) file.SetLength(ProfileRules.MaxAvatarBytes + 1L);
        Environment.SetEnvironmentVariable("UG_PROFILE_PNG", oversized);
        var profile = Call(_remote, "localProfile", new { path = Config });
        int face = profile.GetProperty("face").GetInt32();
        Assert.That(face, Is.InRange(0, 9));
        Assert.That(profile.GetProperty("name").GetString(), Is.EqualTo(ProfileRules.SanitizeName(Environment.GetEnvironmentVariable("UG_USERNAME"))));
        Assert.That(profile.GetProperty("hasAvatar").GetBoolean(), Is.False);
        Assert.That(profile.GetProperty("avatarNotice").GetString(), Does.Contain("exceeds"));
        Assert.That(File.ReadAllText(Config), Does.Contain($"face={face}"));
        using var reopened = new BridgeHost();
        Assert.That(Call(reopened, "localProfile", new { path = Config }).GetProperty("face").GetInt32(), Is.EqualTo(face));
    }

    [Test]
    public void IntegratedSessionPublishesConfiguredProfileButLegacyHarnessKeepsFaceZero()
    {
        Environment.SetEnvironmentVariable("UG_FACE", "23");
        Environment.SetEnvironmentVariable("UG_PROFILE_PNG", Picture);
        Call(_host, "localProfile", new { path = Config, name = "Configured" });
        Call(_host, "init", new { contentRoot = Content, useLocalProfile = true });
        for (int i = 0; i < 16; i++) Call(_host, "tick");
        var client = Private<NetWorldClient>(_host, "_client");
        Assert.That(client.Profiles.TryGet(client.PlayerId, out var profile), Is.True);
        Assert.That(profile.Face, Is.EqualTo(23));
        Assert.That(profile.Name, Is.EqualTo("Configured"));
        Assert.That(profile.AvatarHash, Is.EqualTo(ProfileRules.AvatarHash(File.ReadAllBytes(Picture))));
        Call(_host, "init", new { contentRoot = Content });
        client = Private<NetWorldClient>(_host, "_client");
        Assert.That(client.Profiles.TryGet(client.PlayerId, out profile), Is.True);
        Assert.That(profile.Face, Is.Zero);
        Assert.That(profile.AvatarHash, Is.Zero);
    }

    [Test]
    public void ActualUdpJoinAndRejoinPublishPictureAndFaceThroughAcceptedProfileCommand()
    {
        ushort port;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port; }
        Environment.SetEnvironmentVariable("UG_PROFILE_PNG", Picture);
        Environment.SetEnvironmentVariable("UG_FACE", "21");
        Call(_remote, "localProfile", new { path = Config, name = "Picture owner" });
        Call(_host, "init", new { contentRoot = Content, listenPort = port, name = "Host" });
        object join = new { contentRoot = Content, connectHost = "127.0.0.1", connectPort = port, useLocalProfile = true };
        Call(_remote, "init", join); Pump(120);
        Assert.That(Peer.State, Is.EqualTo(NetSessionState.Connected));
        ushort id = Peer.PlayerId;
        Assert.That(Server.Profiles.TryGet(id, out var accepted), Is.True);
        Assert.That((accepted.Name, accepted.Face), Is.EqualTo(("Picture owner", (byte)21)));
        ulong hash = ProfileRules.AvatarHash(File.ReadAllBytes(Picture));
        Assert.That(accepted.AvatarHash, Is.EqualTo(hash));
        Assert.That(Call(_host, "profileAvatar", new { playerId = id, avatarHash = hash }).GetProperty("status").GetString(), Is.EqualTo("ready"));
        Call(_remote, "setLocalFace", new { face = 29 });
        Assert.That(accepted.Face, Is.EqualTo(21), "Local preference does not directly mutate accepted state");
        Pump();
        Assert.That(accepted.Face, Is.EqualTo(29));
        Assert.That(Peer.Profiles.TryGet(id, out var replica), Is.True);
        Assert.That(replica.Face, Is.EqualTo(29));
        Call(_remote, "disconnect");
        for (int i = 0; i < 10; i++) Call(_host, "tick");
        Call(_remote, "init", join); Pump(30);
        Assert.That(Server.Profiles.TryGet(Peer.PlayerId, out accepted), Is.True);
        Assert.That((accepted.Face, accepted.AvatarHash), Is.EqualTo(((byte)29, hash)));
    }
}
