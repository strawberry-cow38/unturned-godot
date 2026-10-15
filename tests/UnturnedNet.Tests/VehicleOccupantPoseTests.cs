using System;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public sealed class VehicleOccupantPoseTests
    {
        [Test]
        public void HostSeatPreparationRunsOnceBeforeAllRidersAndInvalidPoseUsesExistingFallback()
        {
            var h=new TransactionalHarness(7651).Connected("driver","passenger");
            var vehicle=h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(),0,0,new(1,0,0),h.Server.Session.CurrentTick);
            var id=new NetId(vehicle.NetIdValue);h.Server.Vehicles.ServerSetSeatCount(id,2);
            h.Clients[0].SendEnterVehicle(id.Value,0);h.Clients[1].SendEnterVehicle(id.Value,1);h.Step(12);
            int prepared=0;bool invalid=false;
            h.Server.VehicleHost.BeforeOccupantStep=_=>prepared++;
            h.Server.VehicleHost.OccupantPoseOf=(_,seat)=>invalid?(new Vector3(float.NaN,0,0),90):(new Vector3(10+seat,2,3),90);
            h.Step(1);Assert.That(prepared,Is.EqualTo(1),"Two occupants must not double-step articulation");
            for(ushort owner=1;owner<=2;owner++)
            {
                Assert.That(h.Server.Players.TryGetByOwner(owner,out var rider),Is.True);
                Assert.That(rider.Pos,Is.EqualTo(new Vector3(9+owner,2,3)));Assert.That(rider.YawDegrees,Is.EqualTo(90).Within(.1));
                Assert.That(rider.Stance,Is.EqualTo(MoveInput.WireStanceSitting));
            }
            invalid=true;h.Step(1);h.Server.Players.TryGetByOwner(2,out var fallback);
            Assert.That(fallback.Pos,Is.EqualTo(vehicle.Pos),"Malformed host pose cannot contaminate replicated coordinates");
            h.Server.VehicleHost.OccupantPoseOf=null;h.Step(1);Assert.That(fallback.Pos,Is.EqualTo(vehicle.Pos));
            h.Server.Vehicles.ServerRemove(id,h.Server.Session.CurrentTick);h.Step(8);
            Assert.That(h.Server.VehicleHost.IsSeated(1)||h.Server.VehicleHost.IsSeated(2),Is.False,"Removed body cannot retain occupants");
        }

        [Test]
        public void GunnerRiseIsAtomicBoundedAndAChangeAlonePublishesADelta()
        {
            var source=new VehicleMountReplication();var replica=new VehicleMountReplication();
            var row=new VehicleMountsState(1,new[]{new VehicleMountState(0,0,60,0,true)},true,.24f);
            source.ServerSet(row,1);byte[] bytes=Pack(w=>source.WriteFull(w,new(1,1,Vector3.zero)));
            replica.ReadSnapshot(Read(bytes),true);Assert.That(replica.TryGet(1,out var view)&&view.GunnerRise==.24f,Is.True);
            source.ServerSet(row with{GunnerRise=.3f},2);bytes=Pack(w=>source.WriteDelta(w,new(2,1,Vector3.zero),1));ulong old=replica.StateHash();
            for(int n=0;n<bytes.Length;n++){replica.ReadSnapshot(Read(bytes[..n]),false);Assert.That(replica.StateHash(),Is.EqualTo(old));}
            replica.ReadSnapshot(Read(bytes),false);Assert.That(replica.StateHash(),Is.EqualTo(source.StateHash()));
            foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-.01f,1.01f})
            {
                Assert.Throws<ArgumentException>(()=>source.ServerSet(row with{GunnerRise=bad},3));
                byte[] forged=Pack(w=>{w.WriteBit(true);w.WriteUInt16(1);w.WriteUInt32(1);w.WriteUInt8(1);w.WriteBit(true);w.WriteFloat(bad);});
                ulong before=replica.StateHash();replica.ReadSnapshot(Read(forged),true);Assert.That(replica.StateHash(),Is.EqualTo(before));
            }
        }

        [Test]
        public void OldMountRowsWithoutRiseFailVersionHandshakeInsteadOfMisaligningAngles()
        {
            Assert.That(NetProtocol.Version,Is.GreaterThanOrEqualTo(65));var h=new NetSimHarness(7652);var old=h.AddClient("old mount rows",version:64);old.Connect();
            Assert.That(h.StepUntil(()=>old.State==NetSessionState.Disconnected,100),Is.True);
            Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));Assert.That(old.RejectServerVersion,Is.EqualTo(NetProtocol.Version));
        }
        static byte[] Pack(Action<NetPakWriter> write)=>NetMessagePak.Pack(0,write)[1..];
        static NetPakReader Read(byte[] bytes){var r=new NetPakReader();r.SetBufferSegment(bytes,bytes.Length);return r;}
    }
}
