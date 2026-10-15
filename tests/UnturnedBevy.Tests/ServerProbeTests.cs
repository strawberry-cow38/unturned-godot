using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class ServerProbeTests
{
    BridgeHost _bridge;
    Socket _socket;
    int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
    JsonElement Call(string op, object request) => JsonSerializer.SerializeToElement(
        _bridge.Handle(op, JsonSerializer.SerializeToElement(request)), Options);
    [SetUp] public void Setup()
    {
        _bridge = new();
        _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _socket.ReceiveTimeout = 2500;
    }
    [TearDown] public void Cleanup() { _bridge.Dispose(); _socket.Dispose(); }
    long Start() => Call("serverProbeStart", new { host = "127.0.0.1", port = Port }).GetProperty("token").GetInt64();
    (byte[] Data, EndPoint From) Receive()
    {
        var data = new byte[1400]; EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int length = _socket.ReceiveFrom(data, ref from);
        return (data[..length], from);
    }
    byte[] Reply(byte[] request, ulong? version = null)
    {
        var reply = new byte[20]; "UGSR"u8.CopyTo(reply); request.AsSpan(4, 4).CopyTo(reply.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(10), 12);
        BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(12), version ?? NetContent.Hash);
        return reply;
    }
    JsonElement Complete(long token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 3000)
        {
            var reply = Call("serverProbePoll", new { token });
            if (!reply.GetProperty("pending").GetBoolean()) return reply;
            Thread.Sleep(2);
        }
        throw new AssertionException("probe did not complete");
    }

    [Test]
    public void OneWorkerIsNonblockingAndSupersededResultsCannotLeakIntoNewToken()
    {
        var watch = Stopwatch.StartNew(); long first = Start();
        Assert.That(watch.ElapsedMilliseconds, Is.LessThan(750), "source socket waits1500ms; start must return before it");
        var request = Receive();
        Assert.That(request.Data.Length, Is.EqualTo(1200));
        Assert.That(Encoding.ASCII.GetString(request.Data, 0, 4), Is.EqualTo("UGSQ"));
        Assert.That(Call("serverProbePoll", new { token = first }).GetProperty("pending").GetBoolean(), Is.True);
        long second = Start();
        Assert.That(Call("serverProbePoll", new { token = first }).GetProperty("cancelled").GetBoolean(), Is.True);
        Assert.That(_socket.Poll(10_000, SelectMode.SelectRead), Is.False, "new query queues behind the sole worker");
        _socket.SendTo(Reply(request.Data, 1), request.From);
        var deadline = Stopwatch.StartNew();
        while (!_socket.Poll(1000, SelectMode.SelectRead) && deadline.ElapsedMilliseconds < 2500)
        { Call("serverProbePoll", new { token = second }); Thread.Sleep(2); }
        var newer = Receive();
        _socket.SendTo(Reply(newer.Data), newer.From);
        var status = Complete(second).GetProperty("status");
        Assert.That(status.GetProperty("compatible").GetBoolean(), Is.True, "discarded old mismatch must not contaminate replacement");
        Assert.That(_bridge.State(), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourceRejectsWrongNonceAndKeepsValidCoreWhenOptionalTailIsTruncated(bool wrongNonce)
    {
        long token = Start(); var request = Receive(); var bytes = Reply(request.Data);
        if (wrongNonce) bytes[4] ^= 1;
        else bytes = [..bytes, NetProtocol.Version, 3, 100, 0, 20, (byte)'P', (byte)'E', (byte)'I'];
        _socket.SendTo(bytes, request.From);
        var status = Complete(token).GetProperty("status");
        Assert.That(status.GetProperty("ok").GetBoolean(), Is.EqualTo(!wrongNonce));
        if (!wrongNonce)
        {
            Assert.That(status.GetProperty("map").GetString(), Is.EqualTo("PEI"));
            Assert.That(status.GetProperty("players").GetInt32(), Is.EqualTo(3));
            Assert.That(status.GetProperty("passworded").GetBoolean(), Is.True);
            Assert.That(status.GetProperty("gamemode").GetString(), Is.Empty);
        }
    }

    [Test]
    public void SourceTextAndContentRefusalArePreservedAndCancelInvalidatesLateResult()
    {
        long token = Start(); var request = Receive(); var bytes = new List<byte>(Reply(request.Data, NetContent.Hash ^ 1));
        bytes.AddRange([NetProtocol.Version, 1, 0, 0]);
        foreach (string text in new[] { "PEI\n\u200b MAP", "Survival", "hello\tworld" })
        { byte[] value = Encoding.UTF8.GetBytes(text); bytes.Add((byte)value.Length); bytes.AddRange(value); }
        _socket.SendTo(bytes.ToArray(), request.From);
        var status = Complete(token).GetProperty("status");
        Assert.That(status.GetProperty("map").GetString(), Is.EqualTo("PEI MAP"));
        Assert.That(status.GetProperty("motd").GetString(), Is.EqualTo("hello world"));
        Assert.That(status.GetProperty("compatible").GetBoolean(), Is.False);
        Assert.That(status.GetProperty("refusal").GetString(), Is.EqualTo(NetRejectText.Describe(NetRejectReason.ContentMismatch)));
        token = Start(); request = Receive();
        Call("serverProbeCancel", new { token }); _socket.SendTo(Reply(request.Data), request.From);
        Assert.That(Complete(token).GetProperty("cancelled").GetBoolean(), Is.True);
        Assert.That(SourceServerMenu.PingGateRefusal(50, 51), Does.Contain("allows 50"));
        Assert.That(SourceServerMenu.PingGateRefusal(50, 50), Is.Null);
    }

    [Test]
    public void SilentTimeoutIsReachabilityFailureAndSourceAttemptPolicyIsExposed()
    {
        var start = Call("serverProbeStart", new { host = "127.0.0.1", port = Port });
        long token = start.GetProperty("token").GetInt64();
        Receive(); // Real bound endpoint deliberately gives no answer.
        var status = Complete(token).GetProperty("status");
        Assert.That(status.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(status.GetProperty("refusal").GetString(), Is.EqualTo(NetRejectText.Describe(NetRejectReason.None)));
        Assert.That(start.GetProperty("connectAttempts").GetInt32(), Is.EqualTo(4));
        Assert.That(start.GetProperty("rejectRetryLimit").GetInt32(), Is.EqualTo(4));
        Assert.That(start.GetProperty("linkTimeoutSeconds").GetSingle(), Is.EqualTo(30));
        Assert.That(NetRejectText.ShouldRetryReject(NetRejectReason.ServerFull, 3, SourceServerMenu.RejectRetryLimit), Is.True);
        Assert.That(NetRejectText.ShouldRetryReject(NetRejectReason.ServerFull, 4, SourceServerMenu.RejectRetryLimit), Is.False);
    }

    [Test]
    public void MalformedEndpointIsRejectedWithoutCreatingAWorkerOrWorld()
    {
        Assert.Throws<ArgumentException>(() => Call("serverProbeStart", new { host = "", port = Port }));
        Assert.Throws<ArgumentException>(() => Call("serverProbeStart", new { host = "   ", port = Port }));
        foreach (int port in new[] { -1, 0, 65536 })
            Assert.Throws<ArgumentException>(() => Call("serverProbeStart", new { host = "127.0.0.1", port }));
        Assert.That(_bridge.State(), Is.Null);
        Assert.That(Call("serverProbePoll", new { token = 1 }).GetProperty("cancelled").GetBoolean(), Is.True);
    }
}
