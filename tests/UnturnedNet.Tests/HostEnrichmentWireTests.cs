using System;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

/// <summary>v80 (row 15A) host-enrichment wire: System21 Environment, the System9 part-health tail,
/// Command101 ToolUse, and the version bump that keeps a v79 peer (which would misparse System9 and drop
/// 101/21) out.</summary>
[TestFixture]
public sealed class HostEnrichmentWireTests
{
    static NetPakReader Reader(byte[] bytes) { var r = new NetPakReader(); r.SetBufferSegment(bytes, bytes.Length); return r; }
    static byte[] Pack(Action<NetPakWriter> write) => NetMessagePak.Pack(1, write)[1..];
    static readonly ReplicationContext Ctx = new(1, 1, Vector3.zero);

    [Test]
    public void EnvironmentRoundTripsFullDeltaUnchangedAndHashes()
    {
        var host = new EnvironmentReplication(); var copy = new EnvironmentReplication();
        // Nothing configured: a full says so, a replica stays empty and hashes alike.
        copy.ReadSnapshot(Reader(Pack(w => host.WriteFull(w, Ctx))), true);
        Assert.That(copy.HasEnvironment, Is.False);
        Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()));

        host.ServerSet(-123456789, -7, 1, WeatherStage.Active, .5f, .25f, 1f, 10);
        var full = Pack(w => host.WriteFull(w, Ctx));
        copy.ReadSnapshot(Reader(full), true);
        Assert.That((copy.HasEnvironment, copy.Seed, copy.BaseDay, copy.WeatherType, copy.Stage),
            Is.EqualTo((true, -123456789, -7, 1, WeatherStage.Active)));
        Assert.That((copy.Blend, copy.Puddle, copy.Snow), Is.EqualTo(((byte)128, (byte)64, (byte)255)));
        Assert.That(copy.BlendAlpha, Is.EqualTo(128 / 255f));
        Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()));
        // Truncation never adopts half a block.
        for (int n = 0; n < full.Length; n++)
        {
            var partial = new EnvironmentReplication(); partial.ReadSnapshot(Reader(full[..n]), true);
            Assert.That(partial.HasEnvironment, Is.False, $"truncated at {n}");
        }

        // Unchanged: republishing the same quantized values does not dirty, and the delta is one bit.
        long stamped = host.LastChangedTick;
        host.ServerSet(-123456789, -7, 1, WeatherStage.Active, .5001f, .2501f, 1f, 20);
        Assert.That(host.LastChangedTick, Is.EqualTo(stamped), "a sub-quantum change is not a change");
        var unchanged = Pack(w => host.WriteDelta(w, Ctx, stamped));
        Assert.That(unchanged.Length, Is.EqualTo(1));
        copy.ReadSnapshot(Reader(unchanged), false);
        Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()), "an unchanged delta leaves the replica as it was");

        // Delta: clear sky (no type) after the weather ends.
        host.ServerSet(-123456789, -7, -1, WeatherStage.None, 0f, .2f, 0f, 30);
        copy.ReadSnapshot(Reader(Pack(w => host.WriteDelta(w, Ctx, stamped))), false);
        Assert.That((copy.WeatherType, copy.Stage, copy.Blend, copy.Snow), Is.EqualTo((-1, WeatherStage.None, (byte)0, (byte)0)));
        Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()));
        // The hash sees every field.
        var other = new EnvironmentReplication(); other.ServerSet(-123456789, -7, -1, WeatherStage.None, 0f, .2f, 1f / 255f, 30);
        Assert.That(other.StateHash(), Is.Not.EqualTo(host.StateHash()));
    }

    [Test]
    public void EnvironmentRidesTheRealSnapshotPlaneToAJoinedClient()
    {
        var h = new TransactionalHarness(8080).Connected("joiner"); var c = h.Clients[0];
        h.Server.Environment.ServerSet(42, 3, 0, WeatherStage.Active, 1f, .5f, 0f, h.Server.Session.CurrentTick);
        Assert.That(h.StepUntil(() => c.Environment.HasEnvironment && c.Environment.StateHash() == h.Server.Environment.StateHash(), 200), Is.True);
        Assert.That((c.Environment.Seed, c.Environment.BaseDay, c.Environment.WeatherType), Is.EqualTo((42, 3, 0)));
        Assert.That(ReplicationIds.SystemEnvironment, Is.EqualTo((byte)21));
    }

    [Test]
    public void VehiclePartTailIsOneBitWhenWholeAndRoundTripsEveryField()
    {
        var host = new VehicleReplication(); var copy = new VehicleReplication(); var id = new NetId(9);
        host.ServerSpawn(id, 21, 0, Vector3.zero, 0, 40);
        var whole = Pack(w => host.WriteFull(w, Ctx));
        copy.ReadSnapshot(Reader(whole), true);
        Assert.That(copy.TryGet(id, out var row), Is.True);
        Assert.That((row.EnginePart, row.MainRotorPart, row.TailRotorPart, row.EngineDrowned, row.FuseLit, row.BurnTick),
            Is.EqualTo((VehicleReplication.PartFull, VehicleReplication.PartFull, VehicleReplication.PartFull, false, false, (int?)null)));
        Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()));

        host.ServerSetParts(id, VehicleReplication.QuantizePart(.1f, 4000), VehicleReplication.QuantizePart(224, 450),
            VehicleReplication.QuantizePart(0, 280), true, true, -1500, 2);
        var bytes = Pack(w => host.WriteFull(w, Ctx));
        Assert.That(bytes.Length, Is.GreaterThan(whole.Length), "the tail costs bytes only when present");
        copy.ReadSnapshot(Reader(bytes), true); copy.TryGet(id, out row);
        Assert.That((row.EnginePart, row.MainRotorPart, row.TailRotorPart), Is.EqualTo(((byte)1, (byte)127, (byte)0)),
            "a live remainder never rounds to dead; an exact zero stays zero");
        Assert.That((row.EngineDrowned, row.FuseLit, row.BurnTick), Is.EqualTo((true, true, (int?)-1500)), "burn tick is signed");
        Assert.That(VehicleReplication.PartHealth(row.MainRotorPart, 450), Is.EqualTo(224f).Within(450f / 255f));
        Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()));
        for (int n = 0; n < bytes.Length; n++)
        {
            copy.ReadSnapshot(Reader(bytes[..n]), true);
            Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()), "a truncated tail must not replace the accepted view");
        }

        // Delta: only the changed field moves; a same-value republish does not dirty.
        long stamped = host.TryGet(id, out var e) ? e.LastChangedTick : -1;
        host.ServerSetParts(id, 1, 127, 0, true, true, -1500, 5);
        Assert.That(e.LastChangedTick, Is.EqualTo(stamped));
        host.ServerSetParts(id, 1, 127, 0, true, false, 2000, 6);
        copy.ReadSnapshot(Reader(Pack(w => host.WriteDelta(w, Ctx, stamped))), false); copy.TryGet(id, out row);
        Assert.That((row.FuseLit, row.BurnTick), Is.EqualTo((false, (int?)2000)));
        Assert.That(copy.StateHash(), Is.EqualTo(host.StateHash()));
        Assert.That(VehicleReplication.QuantizePart(5, 0), Is.EqualTo(VehicleReplication.PartFull), "no such part reads full");
        Assert.That(VehicleReplication.QuantizePart(4000, 4000), Is.EqualTo(VehicleReplication.PartFull));
        Assert.That(VehicleReplication.QuantizePart(3999.9f, 4000), Is.EqualTo((byte)254), "only an intact part reads full");
    }

    [Test]
    public void ToolUseCommandRoundTripsAndRefusesAnEmptyPayload()
    {
        foreach (bool active in new[] { true, false })
        {
            var bytes = Pack(new ToolUseCommand { Active = active }.Write);
            Assert.That(ToolUseCommand.TryRead(Reader(bytes), out var read), Is.True);
            Assert.That(read.Active, Is.EqualTo(active));
            Assert.That(ToolUseCommand.TryRead(Reader(Array.Empty<byte>()), out _), Is.False);
        }
        Assert.That(ReplicationIds.CommandToolUse, Is.EqualTo((byte)101));
    }

    [Test]
    public void PriorProtocol79IsRefusedBecauseItCannotParseTheNewVehicleTail()
    {
        Assert.That(NetProtocol.Version, Is.EqualTo((byte)80));
        var net = new NetSimHarness(8079); var old = net.AddClient("before host enrichment", version: 79); old.Connect();
        Assert.That(net.StepUntil(() => old.State == NetSessionState.Disconnected, 100), Is.True);
        Assert.That(old.RejectReason, Is.EqualTo(NetRejectReason.VersionMismatch));
        Assert.That(old.RejectServerVersion, Is.EqualTo(NetProtocol.Version));
    }
}
