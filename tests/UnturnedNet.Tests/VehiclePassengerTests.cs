using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public class VehiclePassengerTests
    {
        static uint Spawn(TransactionalHarness h, int seats = 2)
        {
            var e = h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(), 0, 0, new Vector3(1, 0, 0), h.Server.Session.CurrentTick);
            h.Server.Vehicles.ServerSetSeatCount(new NetId(e.NetIdValue), seats);
            return e.NetIdValue;
        }
        static ushort Occupant(TransactionalHarness h, uint id, int seat)
            => h.Server.Vehicles.SeatOccupant(new NetId(id), seat);
        static void Board(TransactionalHarness h, NetWorldClient c, uint id, byte seat)
        {
            c.SendEnterVehicle(id, seat);
            Assert.That(h.StepUntil(() => Occupant(h, id, seat) == c.PlayerId), Is.True, $"seat {seat} accepts its real wire request");
            h.Step(10);
        }

        [Test]
        public void OccupiedCarPassengerBoardsWithoutStealingDriver_AndBlockedExitKeepsBothSeats()
        {
            var h = new TransactionalHarness(7311).Connected("driver", "passenger", "racer");
            var a = h.Clients[0]; var b = h.Clients[1]; var racer = h.Clients[2];
            uint id = Spawn(h); Board(h, a, id, 0);
            ushort driverAtEvent = 0; byte enteredSeat = 255;
            b.VehicleEntered += e => {
                if (e.PlayerId != b.PlayerId) return;
                enteredSeat = e.Seat;
                if (b.Vehicles.TryGet(id, out var car)) driverAtEvent = car.DriverPlayerId;
            };
            long rejected = h.Server.Commands.Diag.ValidationRejected;
            b.SendEnterVehicle(id, 1); racer.SendEnterVehicle(id, 1);
            Assert.That(h.StepUntil(() => Occupant(h, id, 1) == b.PlayerId), Is.True, "passenger can board while driver remains seated");
            h.Step(15);
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected), "second claimant for the occupied passenger seat is rejected");
            Assert.That(enteredSeat, Is.EqualTo(1));
            Assert.That(driverAtEvent, Is.EqualTo(a.PlayerId), "reliable passenger-enter fact must never temporarily replace the driver");
            foreach (var c in h.Clients)
            {
                Assert.That(c.Vehicles.SeatOccupant(new NetId(id), 0), Is.EqualTo(a.PlayerId));
                Assert.That(c.Vehicles.SeatOccupant(new NetId(id), 1), Is.EqualTo(b.PlayerId));
            }
            bool refused = false, exited = false; Vector3 exitSpot = Vector3.zero;
            b.VehicleExitRefused += _ => refused = true;
            b.VehicleExited += e => { if (e.PlayerId == b.PlayerId) { exited = true; exitSpot = e.Pos; } };
            h.Server.VehicleHost.ResolveExit = (_, seat) => (false, new Vector3(2, 0, 1));
            b.SendExitVehicle(); Assert.That(h.StepUntil(() => refused), Is.True, "passenger exit reaches the door validator");
            Assert.That(Occupant(h, id, 0), Is.EqualTo(a.PlayerId));
            Assert.That(Occupant(h, id, 1), Is.EqualTo(b.PlayerId));
            h.Server.VehicleHost.ResolveExit = (_, seat) => (true, new Vector3(2, 0, 1));
            b.SendExitVehicle(); Assert.That(h.StepUntil(() => exited), Is.True);
            h.Step(10);
            Assert.That(exitSpot, Is.EqualTo(new Vector3(2, 0, 1)));
            Assert.That(Occupant(h, id, 1), Is.Zero);
            Assert.That(Occupant(h, id, 0), Is.EqualTo(a.PlayerId));
            Assert.That(b.Vehicles.SeatOccupant(new NetId(id), 1), Is.Zero);
            Assert.That(b.Vehicles.SeatOccupant(new NetId(id), 0), Is.EqualTo(a.PlayerId));
        }

        [Test]
        public void PassengerFollowsVehicleButCannotDriveWalkReportOwnPoseOrConsoleTeleport()
        {
            var h = new TransactionalHarness(7312).Connected("driver", "passenger");
            var a = h.Clients[0]; var b = h.Clients[1]; uint id = Spawn(h);
            Board(h, a, id, 0); Board(h, b, id, 1);
            Assert.That(h.Server.VehicleHost.IsDriver(b.PlayerId), Is.False, "passenger never receives driver authority");
            long rejected = h.Server.Commands.Diag.ValidationRejected;
            b.SendDriveInput(id, 1, 1, false);
            b.SendVehicleState(id, new Vector3(500, 0, 0), Vector3.zero, Vector3.zero, Vector3.zero, 0, 1, 1, false, 1, 0);
            b.SendMoveInput(0, 1, 0);
            b.SendPlayerState(new Vector3(50, 0, 0), 0, 0, Vector3.zero, 0, true, 0);
            string console = null; b.ConsoleResult += e => console = e.Text;
            b.SendConsole("tp 99 0 99"); h.Step(15);
            Assert.That(h.Server.Commands.Diag.ValidationRejected - rejected, Is.GreaterThanOrEqualTo(4));
            Assert.That(console, Does.Contain("exit the vehicle"));
            var moved = new Vector3(10, 0, 5);
            h.Server.Vehicles.ServerPublish(new NetId(id), moved, Vector3.zero, Vector3.zero, Vector3.zero, 0, 0, 600, 10000, 0, h.Server.Session.CurrentTick);
            h.Step(5);
            Assert.That(h.Server.Players.TryGetByOwner(b.PlayerId, out var passenger), Is.True);
            Assert.That((passenger.Pos - PlayerReplication.Quantize(moved)).magnitude, Is.LessThan(0.05), "passenger's authoritative avatar follows the same vehicle as the driver");
        }

        [Test]
        public void PassengerEventsNeverMutateTheDriverSlot()
        {
            var replicas = new VehicleReplication(); var id = new NetId(1);
            replicas.ServerSpawn(id, 0, 0, Vector3.zero, 0);
            replicas.ServerSetDriver(id, 11, 0);
            replicas.ApplyEntered(new VehicleEnteredEvent { NetId = 1, PlayerId = 22, Seat = 1 }, 1);
            Assert.That(replicas.SeatOccupant(id, 0), Is.EqualTo(11));
            Assert.That(replicas.SeatOccupant(id, 1), Is.EqualTo(22));
            replicas.ApplyExited(new VehicleExitedEvent { NetId = 1, PlayerId = 22 }, 2);
            Assert.That(replicas.SeatOccupant(id, 0), Is.EqualTo(11));
            Assert.That(replicas.SeatOccupant(id, 1), Is.Zero);
        }
    }
}
