using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class VehicleSeatSwitchTests
{
    static uint Spawn(TransactionalHarness h,int seats=3)
    {
        var e=h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(),0,0,new Vector3(1,0,0),h.Server.Session.CurrentTick);
        h.Server.Vehicles.ServerSetSeatCount(new NetId(e.NetIdValue),seats);
        h.Server.Vehicles.ServerPublishVitals(new NetId(e.NetIdValue),1000,600,10000,false,h.Server.Session.CurrentTick);
        return e.NetIdValue;
    }
    static ushort Seat(TransactionalHarness h,uint id,int seat)=>h.Server.Vehicles.SeatOccupant(new NetId(id),seat);
    static void Board(TransactionalHarness h,NetWorldClient owner,uint id,byte seat)
    {
        owner.SendEnterVehicle(id,seat);
        Assert.That(h.StepUntil(()=>Seat(h,id,seat)==owner.PlayerId),Is.True,"real wire seat request accepted");
        h.Step(12);
    }
    [Test]
    public void DriverSwitchReleasesAuthorityAndReliableObserversKeepExactlyOneSeat()
    {
        var h=new TransactionalHarness(9611).Connected("driver","observer");
        var a=h.Clients[0];var b=h.Clients[1];uint id=Spawn(h);
        Board(h,a,id,0);Board(h,b,id,2);
        bool observed=false;
        b.VehicleEntered+=e=> {if(e.PlayerId==a.PlayerId&&e.Seat==1) {
            observed=true;
            Assert.That(b.Vehicles.SeatOccupant(new NetId(id),0),Is.Zero,"reliable switch clears prior driver immediately");
            Assert.That(b.Vehicles.SeatOccupant(new NetId(id),1),Is.EqualTo(a.PlayerId));
            Assert.That(b.Vehicles.SeatOccupant(new NetId(id),2),Is.EqualTo(b.PlayerId));
        }};
        a.SendDriveInput(id,1,0,false);h.Step(4);
        Board(h,a,id,1);
        Assert.That(observed,Is.True);
        Assert.That(h.Server.VehicleHost.IsDriver(a.PlayerId),Is.False);
        Assert.That(h.Server.VehicleHost.IsSeated(a.PlayerId),Is.True);
        Assert.That(h.Server.VehicleHost.TryGetDriven(a.PlayerId,out _),Is.False);
        h.Server.Vehicles.TryGet(id,out var entity);
        Assert.That(entity.HasInput,Is.False,"departed driver cannot leave held throttle queued");
        long rejects=h.Server.Commands.Diag.ValidationRejected;
        a.SendVehicleState(id,new Vector3(400,0,0),Vector3.zero,Vector3.zero,Vector3.zero,0,1,0,false,1,0);
        h.Step(12);
        Assert.That(h.Server.Commands.Diag.ValidationRejected,Is.GreaterThan(rejects));
        Assert.That(entity.Pos.x,Is.LessThan(400));
        Board(h,a,id,0);
        Assert.That(h.Server.VehicleHost.IsDriver(a.PlayerId),Is.True);
        foreach(var peer in h.Clients) {
            Assert.That(peer.Vehicles.SeatOccupant(new NetId(id),0),Is.EqualTo(a.PlayerId));
            Assert.That(peer.Vehicles.SeatOccupant(new NetId(id),1),Is.Zero);
            Assert.That(peer.Vehicles.SeatOccupant(new NetId(id),2),Is.EqualTo(b.PlayerId));
        }
    }
    [Test]
    public void ExactSameVehicleSwitchRejectsOccupiedMissingSameAndAdjacentVehicleSeats()
    {
        var h=new TransactionalHarness(9612).Connected("driver","passenger");
        var a=h.Clients[0];var b=h.Clients[1];uint id=Spawn(h),other=Spawn(h);
        Board(h,a,id,0);Board(h,b,id,1);
        long rejected=h.Server.Commands.Diag.ValidationRejected;
        a.SendEnterVehicle(id,0);a.SendEnterVehicle(id,1);a.SendEnterVehicle(id,12);
        a.SendEnterVehicle(id,EnterVehicleCommand.AnySeat);a.SendEnterVehicle(other,0);
        h.Step(15);
        Assert.That(h.Server.Commands.Diag.ValidationRejected-rejected,Is.EqualTo(5));
        Assert.That(Seat(h,id,0),Is.EqualTo(a.PlayerId));Assert.That(Seat(h,id,1),Is.EqualTo(b.PlayerId));
        Assert.That(Seat(h,id,2),Is.Zero);Assert.That(Seat(h,other,0),Is.Zero);
        // Original wide/long vehicles have seats far from their hull origin.
        h.Server.VehicleHost.OccupantPoseOf=(_,seat)=>(new Vector3(20,3,0),0);
        h.Step(3);
        Board(h,a,id,2);
        Assert.That(Seat(h,id,0),Is.Zero,"existing accepted seat authorizes the in-cabin switch without external reach");
    }
    [Test]
    public void ReliableSeatFactMovesOnlyItsOwnerBeforeAnySnapshotAndIsIdempotent()
    {
        var replicas=new VehicleReplication();var id=new NetId(1);
        replicas.ServerSpawn(id,0,0,Vector3.zero,0);
        replicas.ServerSetSeatCount(id,3);replicas.ServerSetSeat(id,0,11,0);replicas.ServerSetSeat(id,2,22,0);
        var changed=new VehicleEnteredEvent {NetId=1,PlayerId=11,Seat=1};
        replicas.ApplyEntered(changed,1);replicas.ApplyEntered(changed,2);
        Assert.That(replicas.SeatOccupant(id,0),Is.Zero);Assert.That(replicas.SeatOccupant(id,1),Is.EqualTo(11));
        Assert.That(replicas.SeatOccupant(id,2),Is.EqualTo(22));
    }
}
