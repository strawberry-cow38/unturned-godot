using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests;

public sealed class RespawnPoseTests
{
    [Test] public void PublishedVersion72CannotJoinTheExtendedReliableEventStream()
    {
        Assert.That(NetProtocol.Version,Is.GreaterThanOrEqualTo(73));
        var h=new NetSimHarness(48313);var old=h.AddClient("old finite",version:72);old.Connect();
        Assert.That(h.StepUntil(()=>old.State==NetSessionState.Disconnected,100),Is.True);
        Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));
    }
    static NetPakReader Reader(byte[] data)
    { var r = new NetPakReader(); r.SetBufferSegment(data, data.Length); return r; }

    [Test]
    public void OptionalPoseIsBoundedAndLegacyRespawnPayloadIsUnchanged()
    {
        var pose = new RespawnPoseEvent { Tick = uint.MaxValue, Yaw = -46 };
        var bytes = NetMessagePak.Pack(ReplicationIds.EventRespawnPose, pose.Write)[1..];
        Assert.That(RespawnPoseEvent.TryRead(Reader(bytes), out var decoded), Is.True);
        Assert.That(decoded.Tick, Is.EqualTo(uint.MaxValue));
        Assert.That(decoded.Yaw, Is.EqualTo(NetQuantization.QuantizeDegrees(-46, NetQuantization.YawBits)));
        for (int i = 0; i < bytes.Length; i++)
        {
            Assert.That(RespawnPoseEvent.TryRead(Reader(bytes[..i]), out decoded), Is.False);
            Assert.That(decoded, Is.EqualTo(default(RespawnPoseEvent)));
        }
        pose.Yaw = float.NaN;
        Assert.Throws<ArgumentException>(() => NetMessagePak.Pack(62, pose.Write));
        Assert.That(NetMessagePak.Pack(ReplicationIds.EventPlayerRespawned,
            new PlayerRespawnedEvent { PlayerId = 0x1234 }.Write)[1..], Is.EqualTo(new byte[] { 0x34, 0x12 }));
    }

    [Test]
    public void PoseWaitsForSelfRespawnRejectsDuplicatesAndCannotLeakAcrossReconnect()
    {
        var h = new TransactionalHarness(48311).Connected("owner", "other");
        var owner = h.Clients[0]; var other = h.Clients[1];
        var received = new List<RespawnPoseEvent>(); owner.RespawnPose += received.Add;
        void Pose(uint tick) => owner.Events.TryDispatch(NetMessagePak.Pack(62, new RespawnPoseEvent { Tick = tick, Yaw = 90 }.Write));
        void Respawn(ushort id) => owner.Events.TryDispatch(NetMessagePak.Pack(ReplicationIds.EventPlayerRespawned,
            new PlayerRespawnedEvent { PlayerId = id }.Write));
        Pose(100); Assert.That(received, Is.Empty);
        Respawn(other.PlayerId); Assert.That(received, Is.Empty);
        Respawn(owner.PlayerId); Assert.That(received.Count, Is.EqualTo(1));
        Pose(100); Respawn(owner.PlayerId);
        Pose(99); Respawn(owner.PlayerId); Assert.That(received.Count, Is.EqualTo(1));
        Pose(101); owner.Disconnect(); owner.Connect(); Respawn(owner.PlayerId);
        Assert.That(received.Count, Is.EqualTo(1));
        Pose(1); Respawn(owner.PlayerId);
        Assert.That(received.Count, Is.EqualTo(2), "a new session has its own tick range");
    }

    [Test]
    public void RealRespawnPublishesPoseOnlyToOwnerAndOnlyWhenHostAdapterOptsIn()
    {
        var h = new TransactionalHarness(48312).Connected("owner", "other");
        var owner = h.Clients[0]; var other = h.Clients[1];
        var order = new List<string>(); var facts = new List<RespawnPoseEvent>(); int foreign = 0;
        owner.PlayerRespawned += e => { if (e.PlayerId == owner.PlayerId) order.Add("respawn"); };
        owner.RespawnPose += e => { order.Add("pose"); facts.Add(e); };
        other.RespawnPose += _ => foreign++;
        void DieAndReturn()
        {
            h.Server.Combat.DamagePlayerExternal(owner.PlayerId, 500, 0);
            h.Step(3);
            Assert.That(h.Server.Combat.ServerRequestRespawn(owner.PlayerId, h.Server.Session.CurrentTick), Is.True);
            h.Step(5);
        }
        DieAndReturn(); Assert.That(order, Is.EqualTo(new[] { "respawn" })); Assert.That(facts, Is.Empty);
        h.Server.Combat.PlayerRespawned += (id, tick) => h.Server.Combat.PublishRespawnYaw(id, 137.25f, tick);
        DieAndReturn();
        Assert.That(order, Is.EqualTo(new[] { "respawn", "respawn", "pose" }));
        Assert.That(facts.Count, Is.EqualTo(1)); Assert.That(foreign, Is.Zero);
        Assert.That(h.Server.Players.TryGetByOwner(owner.PlayerId, out var player), Is.True);
        Assert.That(player.YawDegrees, Is.EqualTo(facts[0].Yaw));
    }
}
