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
public sealed class ProfileReadTests
{
    BridgeHost _host, _remote;
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() },
    };
    // Actual decodable 128x128 RGBA PNG; source validates only its header.
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAABUUlEQVR4nO3SMQHAIADAsDELSEMw0kAGRxMFPTrm2ucj638dwFsGiDNAnAHiDBBngDgDxBkgzgBxBogzQJwB4gwQZ4A4A8QZIM4AcQaIM0CcAeIMEGeAOAPEGSDOAHEGiDNAnAHiDBBngDgDxBkgzgBxBogzQJwB4gwQZ4A4A8QZIM4AcQaIM0CcAeIMEGeAOAPEGSDOAHEGiDNAnAHiDBBngDgDxBkgzgBxBogzQJwB4gwQZ4A4A8QZIM4AcQaIM0CcAeIMEGeAOAPEGSDOAHEGiDNAnAHiDBBngDgDxBkgzgBxBogzQJwB4gwQZ4A4A8QZIM4AcQaIM0CcAeIMEGeAOAPEGSDOAHEGiDNAnAHiDBBngDgDxBkgzgBxBogzQJwB4gwQZ4A4A8QZIM4AcQaIM0CcAeIMEGeAOAPEGSDOAHEGiDNAnAHiDBBngDgDxF1zSAMkWsiGhQAAAABJRU5ErkJggg==");
    static string Root()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException("game/content");
    }
    static JsonElement Call(BridgeHost host, string op, object args = null) => JsonSerializer.SerializeToElement(
        host.Handle(op, JsonSerializer.SerializeToElement(args ?? new { })), JsonOptions);
    static T Private<T>(BridgeHost host, string field) => (T)typeof(BridgeHost).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
    NetWorldClient Peer => Private<NetWorldClient>(_remote, "_client");
    NetWorldServer Server => Private<NetWorldServer>(_host, "_server");
    [SetUp] public void Setup() { _host = new(); _remote = new(); }
    [TearDown] public void Cleanup() { _remote.Dispose(); _host.Dispose(); }
    void Join()
    {
        ushort port;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port; }
        Call(_host, "init", new { contentRoot = Root(), listenPort = port, mapId = "PEI", name = "Host" });
        Call(_remote, "init", new { contentRoot = Root(), connectHost = "127.0.0.1", connectPort = port, mapId = "PEI", name = "Remote" });
        Pump(120); // Also pass the original two-second profile command rate gate.
        Assert.That(Peer.State, Is.EqualTo(NetSessionState.Connected));
    }
    void Pump(int count = 16)
    { for (int n = 0; n < count; n++) { Call(_remote, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    JsonElement Read(BridgeHost receiver, ushort player, ulong hash) => Call(receiver, "profileAvatar", new { playerId = player, avatarHash = hash });

    [Test]
    public void OriginalRulesAreAvailableBeforeJoinAndDoNotCreateAuthority()
    {
        const string raw = "  [i\u00admg] A\u0301lice\r\n  ";
        var reply = Call(_host, "profileRules", new { name = raw });
        Assert.That(reply.GetProperty("name").GetString(), Is.EqualTo(ProfileRules.SanitizeName(raw)));
        Assert.That(reply.GetProperty("changed").GetBoolean(), Is.True);
        var rules = reply.GetProperty("rules");
        Assert.That(rules.GetProperty("minNameChars").GetInt32(), Is.EqualTo(ProfileRules.MinNameChars));
        Assert.That(rules.GetProperty("maxNameChars").GetInt32(), Is.EqualTo(ProfileRules.MaxNameChars));
        Assert.That(rules.GetProperty("avatarPixels").GetInt32(), Is.EqualTo(ProfileRules.AvatarPixels));
        Assert.That(rules.GetProperty("maxAvatarBytes").GetInt32(), Is.EqualTo(ProfileRules.MaxAvatarBytes));
        Assert.That(_host.State(), Is.Null);
        Assert.Throws<InvalidOperationException>(() => Read(_host, 1, 0));
    }

    [Test]
    public void AvatarQueryUsesActualUdpBytesAndAcceptedPlayerHashWithoutSnapshotPayload()
    {
        Join(); ushort id = Peer.PlayerId; ulong hash = ProfileRules.AvatarHash(Png);
        Assert.That(Read(_host, id, 0).GetProperty("status").GetString(), Is.EqualTo("none"));
        Assert.That(Peer.SendSetProfile("[Remote]", Png, 3), Is.True);
        Pump();
        foreach (var receiver in new[] { _host, _remote })
        {
            var reply = Read(receiver, id, hash);
            Assert.That(reply.GetProperty("status").GetString(), Is.EqualTo("ready"));
            Assert.That(reply.GetProperty("png").EnumerateArray().Select(e => e.GetByte()), Is.EqualTo(Png));
            Assert.That(Read(receiver, id, hash + 1).GetProperty("status").GetString(), Is.EqualTo("stale"));
            Assert.That(Read(receiver, id, hash + 1).GetProperty("png").ValueKind, Is.EqualTo(JsonValueKind.Null));
            string snapshot = JsonSerializer.Serialize(receiver.State(), JsonOptions);
            Assert.That(snapshot, Does.Not.Contain("\"png\""));
            var state = JsonSerializer.SerializeToElement(receiver.State(), JsonOptions);
            Assert.That(state.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "profileAvatar"
                && e.GetProperty("data").GetProperty("avatarHash").GetUInt64() == hash), Is.True);
        }
        Call(_remote, "disconnect"); Pump();
        Assert.That(Read(_remote, id, hash).GetProperty("status").GetString(), Is.EqualTo("missing"), "Terminal client replicas are not an active world");
        Assert.That(Read(_host, id, hash).GetProperty("status").GetString(), Is.EqualTo("missing"), "Cached bytes alone do not identify a departed player");
        Assert.That(Read(_host, id, hash).GetProperty("png").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void LateReliableAvatarRemainsPendingAndInvalidReplacementPreservesAcceptedBytes()
    {
        Join(); ushort id = Peer.PlayerId; ulong hash = ProfileRules.AvatarHash(Png);
        // Produce the real profile snapshot before its reliable avatar event,
        // as independent replication planes may arrive in either order.
        Assert.That(Server.Profiles.ServerApplyProfile(id, "Pending", Png, Server.Session.CurrentTick, out var verdict), Is.True);
        Assert.That(verdict, Is.EqualTo(ProfileRules.AvatarVerdict.Ok));
        Pump();
        Assert.That(Read(_remote, id, hash).GetProperty("status").GetString(), Is.EqualTo("pending"));
        Assert.That(Read(_remote, id, hash).GetProperty("png").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Peer.SendSetProfile("Arrived", Png); Pump();
        Assert.That(Read(_remote, id, hash).GetProperty("status").GetString(), Is.EqualTo("ready"));
        Pump(100);
        var invalid = (byte[])Png.Clone(); invalid[19] = 64;
        Peer.SendSetProfile("Rejected image", invalid); Pump();
        var kept = Read(_remote, id, hash);
        Assert.That(kept.GetProperty("status").GetString(), Is.EqualTo("ready"));
        Assert.That(kept.GetProperty("png").EnumerateArray().Select(e => e.GetByte()), Is.EqualTo(Png));
    }
}
