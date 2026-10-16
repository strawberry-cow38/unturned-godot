using System;
using NUnit.Framework;
using SDG.NetPak;
using UnityEngine;
using UnturnedGodot.Net;
namespace UnturnedNet.Tests;
/// <summary>v80 fishing wire: the cast (bobber position + gauge strength), the line input and the caster-only
/// state event round-trip, refuse truncation and out-of-range kinds/states, and the version bump keeps a v79
/// peer (which would drop 103/104/78) out.</summary>
[TestFixture]
public sealed class FishingWireTests
{
    static NetPakReader Reader(byte[] bytes) { var r = new NetPakReader(); r.SetBufferSegment(bytes, bytes.Length); return r; }
    static byte[] Pack(Action<NetPakWriter> write) => NetMessagePak.Pack(1, write)[1..];
    [Test]
    public void CastInputAndStateRoundTripAndRefuseTruncationAndOutOfRangeValues()
    {
        var cast = new FishingCastCommand { Bobber = new Vector3(12.5f, 25.6f, -40.25f), Strength = 0.5f };
        var bytes = Pack(cast.Write);
        Assert.That(FishingCastCommand.TryRead(Reader(bytes), out var c), Is.True);
        Assert.That((c.Bobber - cast.Bobber).magnitude, Is.LessThan(0.02f));
        Assert.That(c.Strength, Is.EqualTo(128 / 255f).Within(1e-6));
        for (int n = 0; n < bytes.Length; n++) Assert.That(FishingCastCommand.TryRead(Reader(bytes[..n]), out _), Is.False);
        Assert.That(FishingCastCommand.TryRead(Reader(Pack(new FishingCastCommand { Strength = 7f }.Write)), out c) && c.Strength == 1f, Is.True, "clamped to 1");

        foreach (byte kind in new[] { FishingInputCommand.Press, FishingInputCommand.Release, FishingInputCommand.Stop })
        {
            Assert.That(FishingInputCommand.TryRead(Reader(Pack(new FishingInputCommand { Kind = kind }.Write)), out var i), Is.True);
            Assert.That(i.Kind, Is.EqualTo(kind));
        }
        Assert.That(FishingInputCommand.TryRead(Reader(Pack(w => w.WriteUInt8(3))), out _), Is.False);

        var state = new FishingStateEvent { State = FishingStateEvent.Idle, CaughtItemId = 1349 };
        bytes = Pack(state.Write);
        Assert.That(FishingStateEvent.TryRead(Reader(bytes), out var s) && s.State == 0 && s.CaughtItemId == 1349, Is.True);
        for (int n = 0; n < bytes.Length; n++) Assert.That(FishingStateEvent.TryRead(Reader(bytes[..n]), out _), Is.False);
        Assert.That(FishingStateEvent.TryRead(Reader(Pack(w => { w.WriteUInt8(4); w.WriteUInt16(0); })), out _), Is.False);

        Assert.That((ReplicationIds.CommandFishingCast, ReplicationIds.CommandFishingInput, ReplicationIds.EventFishingState),
            Is.EqualTo(((byte)103, (byte)104, (byte)78)));
        Assert.That(NetProtocol.Version, Is.GreaterThanOrEqualTo(80));
    }
}
