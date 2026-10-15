using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public sealed class ResourceContactTests
    {
        static NetPakReader Reader(byte[] bytes) { var reader = new NetPakReader(); reader.SetBufferSegment(bytes, bytes.Length); return reader; }
        static ResourceContactEvent Hit => new() { Index = ushort.MaxValue, Tick = 155,
            Position = new(123.125f, 12.5f, -444.75f), Health = 14, Damage = 21 };
        static ResourceContactEvent Fall => new() { Kind = ResourceContactKind.Felled, Index = 9, Tick = 156,
            Position = new(3, 2, -1), Direction = new Vector3(1, 0, -2).normalized, TrunkLength = 15.25f, DebrisSeconds = 11 };
        static byte[] Raw(ResourceContactEvent value) => NetMessagePak.Pack(ReplicationIds.EventResourceContact, writer => {
            writer.WriteUInt8((byte)value.Kind); writer.WriteUInt8((byte)value.Material);
            writer.WriteUInt16(value.Index); writer.WriteUInt32(value.Tick); NetWire.WritePos(writer, value.Position);
            if (value.Kind == ResourceContactKind.Hit) { writer.WriteFloat(value.Health); writer.WriteFloat(value.Damage); }
            else { NetWire.WriteDir(writer, value.Direction); writer.WriteFloat(value.TrunkLength); writer.WriteFloat(value.DebrisSeconds); }
        });

        [Test]
        public void HitAndFallRetainIdentityTimingAndSourceValuesOnTheSharedPositionGrid()
        {
            foreach (var value in new[] { Hit, Fall })
            {
                var bytes = NetMessagePak.Pack(ReplicationIds.EventResourceContact, value.Write);
                Assert.That(ResourceContactEvent.TryRead(Reader(bytes[1..]), out var read), Is.True);
                Assert.That(read.Kind, Is.EqualTo(value.Kind)); Assert.That(read.Index, Is.EqualTo(value.Index));
                Assert.That(read.Tick, Is.EqualTo(value.Tick)); Assert.That(read.Position, Is.EqualTo(value.Position));
                Assert.That(read.Health, Is.EqualTo(value.Health)); Assert.That(read.Damage, Is.EqualTo(value.Damage));
                Assert.That(read.TrunkLength, Is.EqualTo(value.TrunkLength)); Assert.That(read.DebrisSeconds, Is.EqualTo(value.DebrisSeconds));
                Assert.That((read.Direction - value.Direction).magnitude, Is.LessThan(0.001f));
                for (int length = 0; length < bytes.Length - 1; length++)
                    Assert.That(ResourceContactEvent.TryRead(Reader(bytes[1..(length + 1)]), out _), Is.False, $"truncated {value.Kind} at {length}");
            }
            var ore = Fall; ore.Material = ResourceContactMaterial.Ore; ore.DebrisSeconds = 0;
            Assert.That(ResourceContactEvent.TryRead(Reader(Raw(ore)[1..]), out _), Is.True);
        }

        [Test]
        public void MalformedFactsCannotReachPresentationHandlers()
        {
            var bad = new List<ResourceContactEvent>();
            var value = Hit; value.Health = float.NaN; bad.Add(value);
            value = Hit; value.Health = -1; bad.Add(value);
            value = Hit; value.Damage = float.PositiveInfinity; bad.Add(value);
            value = Hit; value.Damage = -1; bad.Add(value);
            value = Fall; value.TrunkLength = 0; bad.Add(value);
            value = Fall; value.TrunkLength = float.NaN; bad.Add(value);
            value = Fall; value.DebrisSeconds = -1; bad.Add(value);
            value = Fall; value.DebrisSeconds = float.PositiveInfinity; bad.Add(value);
            value = Fall; value.Direction = Vector3.zero; bad.Add(value);
            value = Fall; value.Direction = new(0, 1, 0); bad.Add(value);
            value = Fall; value.Material = ResourceContactMaterial.Ore; bad.Add(value); // ore has no falling debris
            var bytes = bad.Select(Raw).ToList();
            var tag = Raw(Hit); tag[1] = 2; bytes.Add(tag);
            tag = Raw(Hit); tag[2] = 2; bytes.Add(tag);
            int applied = 0;
            var events = new EventRegistry();
            events.Register<ResourceContactEvent>(ReplicationIds.EventResourceContact, ResourceContactEvent.TryRead, _ => applied++);
            foreach (var malformed in bytes) events.TryDispatch(malformed);
            Assert.That(applied, Is.Zero); Assert.That(events.Diag.MalformedSkipped, Is.EqualTo(bytes.Count));
            value = Hit; value.Position.x = float.NaN;
            Assert.Throws<ArgumentException>(() => NetMessagePak.Pack(ReplicationIds.EventResourceContact, value.Write));
        }

        [Test]
        public void OrderedContactsAreOnceOnlyAndDoNotReplaceTheLastingResourceBitmap()
        {
            TransactionalFixtures.RegisterAssets();
            var world = new TransactionalHarness(7630).Connected("first", "second");
            world.Server.Resources.ServerInit(10, world.Server.Session.CurrentTick);
            var first = new List<ResourceContactEvent>(); var second = new List<ResourceContactEvent>();
            world.Clients[0].ResourceContact += first.Add; world.Clients[1].ResourceContact += second.Add;
            world.Server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventResourceContact, Hit.Write));
            world.Server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventResourceContact, Fall.Write));
            world.Step(40);
            Assert.That(first.Select(e => e.Kind), Is.EqualTo(new[] { ResourceContactKind.Hit, ResourceContactKind.Felled }));
            Assert.That(second.Count, Is.EqualTo(2));
            Assert.That(world.Server.Resources.IsAlive(9), Is.True, "A cosmetic fact is not a harvest operation");
            world.Server.Transactions.SetResourceAlive(9, false); world.Step(10);
            var late = world.AddClient("late"); var lateEvents = new List<ResourceContactEvent>(); late.ResourceContact += lateEvents.Add;
            world.Step(35);
            Assert.That(late.Resources.IsAlive(9), Is.False); Assert.That(lateEvents, Is.Empty, "Late joins get a stump, not replayed fall history");
            Assert.That(first.Count, Is.EqualTo(2)); Assert.That(second.Count, Is.EqualTo(2));
        }

        [Test]
        public void Version62IsRefusedInsteadOfSilentlyDroppingResourceContacts()
        {
            Assert.That(ReplicationIds.EventResourceContact, Is.EqualTo(54));
            Assert.That(NetProtocol.Version, Is.GreaterThanOrEqualTo(63));
            var world = new NetSimHarness(seed: 7631); var old = world.AddClient("old resource view", version: 62); old.Connect();
            Assert.That(world.StepUntil(() => old.State == NetSessionState.Disconnected, 100), Is.True);
            Assert.That(old.RejectReason, Is.EqualTo(NetRejectReason.VersionMismatch));
            Assert.That(old.RejectServerVersion, Is.EqualTo(NetProtocol.Version)); Assert.That(world.Server.Peers, Is.Empty);
        }
    }
}
