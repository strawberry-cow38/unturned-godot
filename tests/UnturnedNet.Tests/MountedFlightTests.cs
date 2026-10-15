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
    public sealed class MountedFlightTests
    {
        static MountedFlightEvent Continued=>new(){Kind=MountedFlightKind.Continue,LaunchTick=10,Tick=12,Sequence=65535,
            Position=new(1,2,-3),Velocity=new(0,0,-220),RemainingSteps=118,Pierced=1};
        static MountedFlightEvent Retired=>new(){Kind=MountedFlightKind.Retire,LaunchTick=10,Tick=14,Sequence=65535,
            Position=new(1,2,-8),Velocity=new(0,0,-220),Reason=MountedFlightRetirement.Impact};
        static MountedFlightEvent Blast=>new(){Kind=MountedFlightKind.Blast,LaunchTick=10,Tick=14,Sequence=65535,Radius=12,
            Shot=new(){PlayerId=0,Gun="tank_cannon",Origin=new(1,2,-8),Dir=new(0,0,-1)}};
        static NetPakReader Read(byte[] bytes){var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r;}
        static byte[] Pack(MountedFlightEvent value)=>NetMessagePak.Pack(ReplicationIds.EventMountedFlight,value.Write)[1..];
        [Test]
        public void EveryUnionRejectsTruncationAndInvalidTimeGeometryOrTagsBeforeDispatch()
        {
            foreach(var fact in new[]{Continued,Retired,Blast})
            {
                var bytes=Pack(fact);Assert.That(MountedFlightEvent.TryRead(Read(bytes),out var parsed),Is.True);
                Assert.That((parsed.Kind,parsed.LaunchTick,parsed.Tick,parsed.Sequence),Is.EqualTo((fact.Kind,fact.LaunchTick,fact.Tick,fact.Sequence)));
                for(int n=0;n<bytes.Length;n++)Assert.That(MountedFlightEvent.TryRead(Read(bytes[..n]),out _),Is.False,$"{fact.Kind} prefix{n}");
            }
            var bad=Continued;bad.RemainingSteps=0;Assert.Throws<ArgumentException>(()=>Pack(bad));
            bad=Continued;bad.Pierced=3;Assert.Throws<ArgumentException>(()=>Pack(bad));
            bad=Continued;bad.Tick=9;Assert.Throws<ArgumentException>(()=>Pack(bad));
            bad=Continued;bad.Velocity.x=float.NaN;Assert.Throws<ArgumentException>(()=>Pack(bad));
            bad=Retired;bad.Reason=(MountedFlightRetirement)2;Assert.Throws<ArgumentException>(()=>Pack(bad));
            bad=Blast;bad.Radius=float.PositiveInfinity;Assert.Throws<ArgumentException>(()=>Pack(bad));
            bad=Blast;bad.Shot.Gun="../tank";Assert.Throws<ArgumentException>(()=>Pack(bad));
            var tag=Pack(Blast);tag[0]=3;Assert.That(MountedFlightEvent.TryRead(Read(tag),out _),Is.False);
            var wrapped=Continued;wrapped.LaunchTick=uint.MaxValue-2;wrapped.Tick=1;
            Assert.That(MountedFlightEvent.TryRead(Read(Pack(wrapped)),out _),Is.True,"Tick rollover remains a small valid age");
        }
        [Test]
        public void ReliableFactsRemainOrderedAndOnceOnlyAndNeverChangeClientVitals()
        {
            TransactionalFixtures.RegisterAssets();var h=new TransactionalHarness(7661).Connected("mounted observer");var peer=h.Clients[0];h.Step(10);
            var received=new List<MountedFlightKind>();peer.MountedFlight+=fact=>received.Add(fact.Kind);
            peer.CombatState.TryGet(peer.PlayerId,out var life);float before=life.HealthExact;
            foreach(var fact in new[]{Continued,Blast,Retired})h.Server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventMountedFlight,fact.Write));
            h.Step(80);Assert.That(received,Is.EqualTo(new[]{MountedFlightKind.Continue,MountedFlightKind.Blast,MountedFlightKind.Retire}));
            Assert.That(peer.CombatState.TryGet(peer.PlayerId,out life),Is.True);Assert.That(life.HealthExact,Is.EqualTo(before));
            Assert.That(h.Server.Combat.LiveBullets,Is.Zero,"Presentation facts do not manufacture authority rounds");
            var late=h.AddClient("late");var historical=new List<MountedFlightKind>();late.MountedFlight+=f=>historical.Add(f.Kind);late.Connect();h.Step(30);
            Assert.That(historical,Is.Empty,"Late joins do not replay already finished streaks and blasts");
        }
        [Test]
        public void Version65IsRejectedRatherThanSilentlyDroppingFlightRetirement()
        {
            Assert.That(NetProtocol.Version,Is.GreaterThanOrEqualTo(66));Assert.That(ReplicationIds.EventMountedFlight,Is.EqualTo(56));
            var h=new NetSimHarness(7662);var peer=h.AddClient("old flight",version:65);peer.Connect();
            Assert.That(h.StepUntil(()=>peer.State==NetSessionState.Disconnected,100),Is.True);
            Assert.That(peer.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));
        }
    }
}
