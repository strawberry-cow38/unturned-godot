using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class RemoteMapBindingsTests
{
    BridgeHost _host, _remote;
    string _holiday;
    ushort _port;
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new VectorJson(), new ByteArrayJson() } };
    static string Root()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "content"))) return Path.Combine(dir.FullName, "game", "content");
        throw new DirectoryNotFoundException("game/content");
    }
    static JsonElement Call(BridgeHost host, string op, object request = null) => JsonSerializer.SerializeToElement(
        host.Handle(op, JsonSerializer.SerializeToElement(request ?? new { })), Options);
    static JsonElement State(BridgeHost host) => JsonSerializer.SerializeToElement(host.State(), Options);
    NetWorldServer Server => (NetWorldServer)typeof(BridgeHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host);
    object Map => new { contentRoot = Root(), mapName = "PEI", mapRoot = Path.Combine(Root(), "test-map-no-retail-required") };
    [SetUp] public void Setup()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
        _holiday = Environment.GetEnvironmentVariable("UG_HOLIDAY");
        _host = new(); _remote = new();
    }
    [TearDown] public void Cleanup()
    { _remote.Dispose(); _host.Dispose(); Environment.SetEnvironmentVariable("UG_HOLIDAY", _holiday); }
    void Listen(string holiday = "NONE")
    {
        Environment.SetEnvironmentVariable("UG_HOLIDAY", holiday);
        Call(_host, "init", new { contentRoot = Root(), mapId = "PEI", listenPort = _port, spawn = new[] { 0, 35, 0 } });
    }
    void Join(string localHoliday = "NONE")
    {
        Environment.SetEnvironmentVariable("UG_HOLIDAY", localHoliday);
        Call(_remote, "init", new { contentRoot = Root(), mapId = "PEI", connectHost = "127.0.0.1", connectPort = _port });
        Pump(24);
    }
    void Pump(int count = 24)
    { for (int i = 0; i < count; i++) { Call(_remote, "tick"); Call(_host, "tick"); Thread.Sleep(1); } }
    static Dictionary<int, JsonElement> Rows(JsonElement result) => result.GetProperty("objects").EnumerateArray().ToDictionary(e => e.GetProperty("sourceIndex").GetInt32());

    [Test]
    public void AcceptedHolidayAndFullManifestOrderingMatchRealHostIdsWithoutCreatingAuthority()
    {
        Listen("CHRISTMAS");
        var expected = Call(_host, "mapServices", Map);
        Join("NONE");
        Assert.That(State(_remote).GetProperty("network").GetProperty("holiday").GetString(), Is.EqualTo("CHRISTMAS"));
        Assert.That(State(_host).GetProperty("network").GetProperty("holiday").GetString(), Is.EqualTo("CHRISTMAS"));
        int count = Server.Containers.Count, fixtures = Server.Deployables.Count;
        var actual = Call(_remote, "mapBindings", Map);
        Assert.That(actual.GetProperty("readOnly").GetBoolean(), Is.True);
        Assert.That(actual.GetProperty("complete").GetBoolean(), Is.True, actual.GetRawText()[..200]);
        Assert.That(actual.GetProperty("resourceCount").GetInt32(), Is.EqualTo(expected.GetProperty("resourceCount").GetInt32()));
        Assert.That(actual.GetProperty("objectDoors").GetRawText(), Is.EqualTo(expected.GetProperty("objectDoors").GetRawText()));
        Assert.That(actual.GetProperty("seats").GetRawText(), Is.EqualTo(expected.GetProperty("seats").GetRawText()));
        var expectedRows = Rows(expected); var actualRows = Rows(actual);
        Assert.That(actualRows.Keys, Is.EquivalentTo(expectedRows.Keys));
        foreach (int source in expectedRows.Keys)
            foreach (string field in new[] { "containerNetId", "fixtureNetId", "objectDoorNetId", "seatNetIds" })
                Assert.That(actualRows[source].GetProperty(field).GetRawText(), Is.EqualTo(expectedRows[source].GetProperty(field).GetRawText()), $"row{source} {field}");
        Assert.That(Server.Containers.Count, Is.EqualTo(count)); Assert.That(Server.Deployables.Count, Is.EqualTo(fixtures));
        Assert.That(Call(_remote, "diagnostics").GetProperty("host").GetBoolean(), Is.False);
        // A reader's local clock must not quietly replace the accepted holiday.
        var local = MapServices.Parse(JsonSerializer.SerializeToElement(Map), "NONE");
        Assert.That(local.Resources.Length, Is.Not.EqualTo(actual.GetProperty("resources").GetArrayLength()));
        Assert.Throws<ArgumentException>(() => Call(_remote, "mapBindings", new { mapName = "Washington" }));
    }

    [Test]
    public void LateFixtureReplicasRebindAndAmbiguousCoLocatedIdsStayUnset()
    {
        Listen(); Join();
        var before = Call(_remote, "mapBindings", Map);
        Assert.That(before.GetProperty("complete").GetBoolean(), Is.False);
        Assert.That(before.GetProperty("unboundContainers").GetInt32(), Is.GreaterThan(0));
        var expected = Call(_host, "mapServices", Map); Pump(30);
        var after = Call(_remote, "mapBindings", Map);
        Assert.That(after.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(after.GetProperty("revision").GetInt64(), Is.GreaterThan(before.GetProperty("revision").GetInt64()));
        var source = Server.Containers.All.First();
        Server.Containers.ServerRegisterFixture(Server.Ids.Mint(), source.KindId, source.Pos, source.YawDegrees,
            source.Width, source.Height, Server.Session.CurrentTick);
        Pump(30);
        var ambiguous = Call(_remote, "mapBindings", Map);
        Assert.That(ambiguous.GetProperty("ambiguousContainers").GetInt32(), Is.GreaterThan(0));
        var row = Rows(expected).First(r => r.Value.GetProperty("containerNetId").ValueKind == JsonValueKind.Number
            && r.Value.GetProperty("containerNetId").GetUInt32() == source.NetIdValue).Key;
        Assert.That(Rows(ambiguous)[row].GetProperty("containerNetId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(Server.Containers.Count, Is.EqualTo(expected.GetProperty("containerCount").GetInt32() + 1));
    }

    [Test]
    public void SavedWorldRetainsItsHolidayAfterLocalCalendarOverrideChanges()
    {
        Listen("CHRISTMAS");
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "holiday-" + Guid.NewGuid() + ".json");
        try
        {
            Call(_host, "save", new { path });
            Environment.SetEnvironmentVariable("UG_HOLIDAY", "NONE");
            Call(_host, "load", new { path });
            Assert.That(State(_host).GetProperty("network").GetProperty("holiday").GetString(), Is.EqualTo("CHRISTMAS"));
            Join("HALLOWEEN");
            Assert.That(State(_remote).GetProperty("network").GetProperty("holiday").GetString(), Is.EqualTo("CHRISTMAS"));
        }
        finally { File.Delete(path); }
    }
}
