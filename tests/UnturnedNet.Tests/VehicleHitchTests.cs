using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;
using UnityEngine;
namespace UnturnedNet.Tests;

[TestFixture]
public sealed class VehicleHitchTests
{
    static NetPakReader Reader(byte[] bytes) { var r = new NetPakReader(); r.SetBufferSegment(bytes, bytes.Length); return r; }
    static byte[] Pack(Action<NetPakWriter> write) => NetMessagePak.Pack(1, write)[1..];
    [Test]
    public void HitchRowsAreAtomicHashedAndRemovalClearsOnlyTheDistinctRelationship()
    {
        var source = new VehicleReplication(); var copy = new VehicleReplication();
        for (uint id = 1; id <= 3; id++) source.ServerSpawn(new(id), 0, 0, Vector3.zero, 0, 10);
        Assert.That(source.ServerSetCoupledTrailer(1, 2, 1), Is.True);
        Assert.That(source.ServerSetCoupledTrailer(3, 2, 1), Is.False);
        Assert.That(source.ServerSetCoupledTrailer(2, 1, 1), Is.False);
        Assert.That(source.ServerSetCoupledTrailer(3, 3, 1), Is.False);
        var bytes = Pack(w => source.WriteFull(w, new ReplicationContext(1, 1, Vector3.zero)));
        copy.ReadSnapshot(Reader(bytes), true);
        Assert.That(copy.TryGetCoupledCab(2, out var cab), Is.True);
        Assert.That(cab.NetIdValue, Is.EqualTo(1)); Assert.That(cab.TowedNetId, Is.Zero, "rope state remains distinct");
        Assert.That(copy.StateHash(), Is.EqualTo(source.StateHash()));
        for (int n = 0; n < bytes.Length; n++)
        { copy.ReadSnapshot(Reader(bytes[..n]), true); Assert.That(copy.StateHash(), Is.EqualTo(source.StateHash())); }
        source.ServerRemove(new(2), 2);
        copy.ReadSnapshot(Reader(Pack(w => source.WriteDelta(w, new ReplicationContext(1, 2, Vector3.zero), 1))), false);
        Assert.That(copy.StateHash(), Is.EqualTo(source.StateHash()));
        Assert.That(copy.TryGetCoupledCab(2, out _), Is.False);
    }
    [Test]
    public void HitchIntentAndBothPairTailsRejectTruncationWithoutPartialValues()
    {
        var command = new VehicleHitchCommand { TrailerNetId = 2, Origin = new(1, 2, 3), Direction = Vector3.forward };
        var bytes = Pack(command.Write);
        Assert.That(VehicleHitchCommand.TryRead(Reader(bytes), out _), Is.True);
        for (int n=0;n<bytes.Length;n++) { Assert.That(VehicleHitchCommand.TryRead(Reader(bytes[..n]), out var c), Is.False); Assert.That(c.TrailerNetId, Is.Zero); }
        var pair = new VehiclePairPose { NetId = 2, Pos = new(4,5,6), LinVel = new(1,2,3), AngVel = new(0,.5f,0), YawDegrees = 90 };
        var state = new VehicleStateCommand { NetId = 1, Trailer = pair };
        bytes = Pack(state.Write); Assert.That(VehicleStateCommand.TryRead(Reader(bytes), out var decoded), Is.True);
        Assert.That(decoded.Trailer.Value.NetId, Is.EqualTo(2));
        for (int n=0;n<bytes.Length;n++) { Assert.That(VehicleStateCommand.TryRead(Reader(bytes[..n]), out decoded), Is.False); Assert.That(decoded.NetId, Is.Zero); }
        var recovery = new VehicleRecovEvent { NetId = 1, Trailer = pair, RecovCounter = 7 };
        bytes = Pack(recovery.Write); Assert.That(VehicleRecovEvent.TryRead(Reader(bytes), out var fact), Is.True);
        Assert.That(fact.Trailer.Value.Pos, Is.EqualTo(pair.Pos));
        for (int n=0;n<bytes.Length;n++) { Assert.That(VehicleRecovEvent.TryRead(Reader(bytes[..n]), out fact), Is.False); Assert.That(fact.NetId, Is.Zero); }
        pair.Pos = new(float.NaN,0,0); Assert.Throws<ArgumentException>(()=>Pack(w=>VehiclePairPose.WriteOptional(w,pair)));
        Assert.That(ReplicationIds.CommandVehicleHitch, Is.EqualTo(83));
    }
    static (TransactionalHarness h, NetWorldClient owner, uint cab, uint trailer) Pair()
    {
        var h = new TransactionalHarness(7483).Connected("driver", "observer"); var owner = h.Clients[0];
        uint Spawn(Vector3 p,float speed) {
            var e=h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(),0,0,p,h.Server.Session.CurrentTick,speed);
            h.Server.Vehicles.ServerPublishVitals(new(e.NetIdValue),100,6000,10000,false,h.Server.Session.CurrentTick);
            return e.NetIdValue;
        }
        var cab=Spawn(new(1,0,0),20); var trailer=Spawn(new(1,0,3),0);
        Assert.That(h.Server.Vehicles.ServerSetCoupledTrailer(cab,trailer,h.Server.Session.CurrentTick),Is.True);
        h.Server.VehicleHost.CoupledPoseAllowed=(_,_,cmd)=>(cmd.Trailer.Value.Pos-cmd.Pos-new Vector3(0,0,3)).sqrMagnitude<=1.6f*1.6f;
        owner.SendEnterVehicle(cab);
        Assert.That(h.StepUntil(()=>h.Server.VehicleHost.IsDriver(owner.PlayerId)),Is.True);
        return(h,owner,cab,trailer);
    }
    static VehicleReplication.VehicleEntity Entity(TransactionalHarness h,uint id) { Assert.That(h.Server.Vehicles.TryGet(id,out var e),Is.True);return e; }
    static void Send(NetWorldClient c,uint cab,Vector3 at,VehiclePairPose? trailer,byte ack=0)=>c.SendVehicleState(cab,at,Vector3.zero,new(3,0,0),Vector3.zero,0,1,0,false,1,ack,trailer);
    [Test]
    public void AcceptedDriverPairReachesObserverAndHandsBothBodiesBackOnExit()
    {
        var(h,c,cab,trailer)=Pair();
        var pose=new VehiclePairPose{NetId=trailer,Pos=new(1.2f,0,3),LinVel=new(3,0,0)};
        Send(c,cab,new(1.2f,0,0),pose); h.Step(12);
        Assert.That(Entity(h,cab).Pos.x,Is.EqualTo(1.2f).Within(.01));
        Assert.That(Entity(h,trailer).Pos.x,Is.EqualTo(1.2f).Within(.01),"zero trailer SpeedMax does not reject the pair");
        Assert.That(h.Server.VehicleHost.IsPredictedDriven(trailer),Is.True);
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(trailer,out var predicted),Is.True);
        Assert.That(predicted.Pos,Is.EqualTo(Entity(h,trailer).Pos));
        Assert.That(h.StepUntil(()=>h.Clients[1].Vehicles.StateHash()==h.Server.Vehicles.StateHash()),Is.True);
        h.Server.VehicleHost.ServerExit(c.PlayerId);
        Assert.That(h.Server.VehicleHost.IsPredictedDriven(cab),Is.False);
        Assert.That(h.Server.VehicleHost.IsPredictedDriven(trailer),Is.False);
        Send(c,cab,new(2,0,0),new VehiclePairPose{NetId=trailer,Pos=new(2,0,3)});h.Step(12);
        Assert.That(Entity(h,trailer).Pos.x,Is.EqualTo(1.2f).Within(.01),"ex-driver cannot overwrite host takeover pose");
    }
    [Test]
    public void MissingForeignOrSeparatedPairRollsBackBothAndRequiresTheLatestAck()
    {
        var(h,c,cab,trailer)=Pair(); var events=new List<VehicleRecovEvent>(); c.VehicleRecov+=events.Add;
        Send(c,cab,new(1.2f,0,0),null);h.Step(12);
        Assert.That(events.Count,Is.EqualTo(1));Assert.That(events[0].Trailer.Value.NetId,Is.EqualTo(trailer));
        Assert.That(Entity(h,cab).Pos.x,Is.EqualTo(1));Assert.That(Entity(h,trailer).Pos.x,Is.EqualTo(1));
        var valid=new VehiclePairPose{NetId=trailer,Pos=new(1.2f,0,3)};
        Send(c,cab,new(1.2f,0,0),valid);h.Step(8);Assert.That(Entity(h,cab).Pos.x,Is.EqualTo(1),"old ack frozen");
        valid.Pos=new(50,0,3);Send(c,cab,new(1.2f,0,0),valid,events[^1].RecovCounter);h.Step(8);
        Assert.That(events.Count,Is.EqualTo(2));Assert.That(Entity(h,cab).Pos.x,Is.EqualTo(1));
        valid.Pos=new(1.2f,0,3);valid.NetId=999;Send(c,cab,new(1.2f,0,0),valid,events[^1].RecovCounter);h.Step(8);
        Assert.That(events.Count,Is.EqualTo(3));
        valid.NetId=trailer;Send(c,cab,new(1.2f,0,0),valid,events[^1].RecovCounter);h.Step(8);
        Assert.That(Entity(h,cab).Pos.x,Is.EqualTo(1.2f).Within(.01));Assert.That(Entity(h,trailer).Pos.x,Is.EqualTo(1.2f).Within(.01));
        h.Server.Vehicles.ServerSetCoupledTrailer(cab,0,h.Server.Session.CurrentTick);
        Send(c,cab,new(1.4f,0,0),valid,events[^1].RecovCounter);h.Step(8);
        Assert.That(events.Count,Is.EqualTo(4));Assert.That(events[^1].Trailer,Is.Null,"late old-link frame cannot revive detached pair");
    }
    [Test]
    public void HitchDispatchRequiresInstalledAuthorityAndConnectionOwner()
    {
        var h=new TransactionalHarness(7484).Connected("on foot");var c=h.Clients[0];int count=0;
        var intent=new VehicleHitchCommand{TrailerNetId=2,Direction=Vector3.forward};
        c.SendVehicleHitch(intent);h.Step(8);Assert.That(count,Is.Zero);
        h.Server.VehicleHitchRequested=(owner,_)=>{Assert.That(owner,Is.EqualTo(c.PlayerId));count++;};
        h.Server.VehicleHitchAllowed=(_,_)=>false;c.SendVehicleHitch(intent);h.Step(8);Assert.That(count,Is.Zero);
        h.Server.VehicleHitchAllowed=(_,_)=>true;c.SendVehicleHitch(intent);h.Step(8);Assert.That(count,Is.EqualTo(1));
    }
    [TestCase(false)]
    [TestCase(true)]
    public void DeathAndDisconnectedOwnerReleaseBothLastAcceptedBodies(bool disconnect)
    {
        var(h,c,cab,trailer)=Pair();
        Send(c,cab,new(1.3f,0,0),new VehiclePairPose{NetId=trailer,Pos=new(1.3f,0,3),LinVel=new(3,0,0)});h.Step(8);
        var last=Entity(h,trailer).Pos;
        Assert.That(h.Server.VehicleHost.IsPredictedDriven(trailer),Is.True);
        if(disconnect)c.Disconnect();else h.Server.Combat.DamagePlayerExternal(c.PlayerId,1000);
        Assert.That(h.StepUntil(()=>!h.Server.VehicleHost.IsDriver(c.PlayerId),600),Is.True);
        Assert.That(h.Server.VehicleHost.IsPredictedDriven(trailer),Is.False);
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(trailer,out _),Is.False);
        Assert.That(Entity(h,trailer).Pos,Is.EqualTo(last));
        Assert.That(Entity(h,cab).CoupledTrailerNetId,Is.EqualTo(trailer),"leaving the cab does not unlatch a physical hitch");
    }
    [Test]
    public void OriginBarrierTranslatesBothAdoptedPosesBeforeHostTakesOver()
    {
        var(h,c,cab,trailer)=Pair();
        Send(c,cab,new(1.3f,0,0),new VehiclePairPose{NetId=trailer,Pos=new(1.3f,0,3),LinVel=new(3,0,0)});h.Step(8);
        var delta=new Vector3(-256,0,256);
        WorldRebase.Translate(h.Server,c,delta);
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(cab,out var a),Is.True);
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(trailer,out var b),Is.True);
        Assert.That(a.Pos,Is.EqualTo(Entity(h,cab).Pos));
        Assert.That(b.Pos,Is.EqualTo(Entity(h,trailer).Pos),"raw adoption must share the replica's new origin");
        Assert.That(b.LinVel,Is.EqualTo(new Vector3(3,0,0)));
        h.Server.VehicleHost.ServerExit(c.PlayerId);
        Assert.That(Entity(h,trailer).Pos,Is.EqualTo(b.Pos));
    }

    [Test]
    public void RelinkingTheSameTrailerCannotRestoreItsPreviousDrivenPose()
    {
        var(h,c,cab,trailer)=Pair();
        Send(c,cab,new(1.3f,0,0),new VehiclePairPose{NetId=trailer,Pos=new(1.3f,0,3),LinVel=new(3,0,0)});h.Step(8);
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(trailer,out var old),Is.True);
        long tick=h.Server.Session.CurrentTick;
        Assert.That(h.Server.Vehicles.ServerSetCoupledTrailer(cab,0,tick),Is.True);
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(trailer,out _),Is.False);
        h.Server.Vehicles.ServerAdoptDriverState(new(trailer),new(1.7f,0,3),Vector3.zero,Vector3.zero,Vector3.zero,0,0,tick);
        Assert.That(h.Server.Vehicles.ServerSetCoupledTrailer(cab,trailer,tick),Is.True);
        Assert.That(h.Server.VehicleHost.IsPredictedDriven(trailer),Is.True,"the linked trailer stays held until the owner's next pair");
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(trailer,out var linked),Is.True);
        Assert.That(linked.Pos,Is.EqualTo(Entity(h,trailer).Pos),"same-ID relink must hold the newly accepted snap, not the old raw cache");
        Assert.That(linked.Pos,Is.Not.EqualTo(old.Pos));Assert.That(linked.LinVel,Is.EqualTo(Vector3.zero));
        Send(c,cab,new(1.4f,0,0),new VehiclePairPose{NetId=trailer,Pos=new(1.4f,0,3),LinVel=new(3,0,0)});h.Step(8);
        Assert.That(h.Server.VehicleHost.TryGetPredictedState(trailer,out var fresh),Is.True);
        Assert.That(fresh.Pos,Is.EqualTo(Entity(h,trailer).Pos));Assert.That(fresh.LinVel,Is.EqualTo(new Vector3(3,0,0)));
    }

    [Test]
    public void PriorProtocol73CannotDecodeTheNewPairLayout()
    {
        var net=new NetSimHarness(7485);var old=net.AddClient("before hitch",version:73);old.Connect();
        Assert.That(net.StepUntil(()=>old.State==NetSessionState.Disconnected,100),Is.True);
        Assert.That(old.RejectReason,Is.EqualTo(NetRejectReason.VersionMismatch));
    }

}
