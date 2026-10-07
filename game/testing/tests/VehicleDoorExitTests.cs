using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // OUT THROUGH YOUR OWN DOOR, OR NOT AT ALL (strawberry 2026-10-04: "rework vehicles to kick you out in the spot where
    // the respective door is, instead of just 'wherevers free'. if your 'door' is blocked and you try to exit, refuse and
    // give feedback to the user").
    //
    // Before this every seat left by one point, 2.4 m off the RIGHT flank at the vehicle's origin, and a blocked point
    // fanned round the car to anywhere free. So each check below is aimed at a way the old code would pass a weaker test:
    //  - "the driver got out" passed with the driver climbing out of the passenger side -> assert the SIDE, per seat;
    //  - "a passenger got out on the right" passed with the rear passenger stepping out by the front door -> assert the Z;
    //  - "a wall stops you" passed with the fan walking you round to the far side -> assert you are STILL SEATED;
    //  - each of the two block probes is given a case only IT can catch (a thin fence: path only; a post: room only),
    //    so deleting either one turns a check red instead of being covered for by the other.
    public sealed class VehicleDoorExitTests : GameTest
    {
        public override string Name => "vehicle.door_exit";
        public override double TimeoutSimSeconds => 60;

        /// <summary>A static box in `car`'s local frame, on the world layer: walls, fences and posts.</summary>
        StaticBody3D Block(Vehicle car, Vector3 localCenter, Vector3 size)
        {
            var b = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            World.AddChild(b);
            b.GlobalTransform = new Transform3D(car.GlobalTransform.Basis.Orthonormalized(), car.ToGlobal(localCenter));
            return b;
        }

        static void Still(Vehicle v) { v.LinearVelocity = Vector3.Zero; v.AngularVelocity = Vector3.Zero; }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            var car = Vehicle.BuildByName("sedan");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(60f, 1.2f, 0f);
            yield return Ticks(45);   // settle onto the ground, and let the 1:1 hull land
            Still(car);
            var p = Rigs.Player(World, car.GlobalPosition + new Vector3(0f, 0f, 6f));
            yield return Ticks(2);

            // ---- 1. EACH SEAT ITS OWN DOOR. The sedan's seats: 0 front-left (driver), 1 front-right, 2 rear-left, 3 rear-right.
            float boxHalf = car.HullSize.X * 0.5f;
            foreach (int seat in new[] { 0, 1, 2, 3 })
            {
                var st = car.SeatLocal(seat);
                p.EnterVehicle(car, seat);
                T.Check($"seat {seat}: seated", p.IsDriving && p.SeatIndex == seat);
                yield return Ticks(2);
                bool out_ = p.TryExitVehicle();
                var local = car.ToLocal(p.GlobalPosition);
                T.Check($"seat {seat}: got out ({p.LastExitVerdict}, {car.LastExitProbe})", out_ && !p.IsDriving);
                T.Check($"seat {seat}: on its OWN side, clear of the hull (local x {local.X:0.00}, seat x {st.X:0.00}, hull half-width {boxHalf:0.00})",
                    Mathf.Sign(local.X) == Mathf.Sign(st.X) && Mathf.Abs(local.X) > boxHalf);
                T.Check($"seat {seat}: level with its seat, not the front door (local z {local.Z:0.00} vs seat z {st.Z:0.00})",
                    Mathf.Abs(local.Z - st.Z) < 0.3f);
                T.Check($"seat {seat}: and not flung (|x| {Mathf.Abs(local.X):0.00} m)", Mathf.Abs(local.X) < boxHalf + 1.2f);
                yield return Ticks(2);
                Still(car);
            }

            // ---- 2. A WALL FLUSH AGAINST THE LEFT FLANK: both left seats are REFUSED and stay put; the right ones still go.
            var leftDoor = car.DoorExitsLocal(0)[0];
            var wall = Block(car, new Vector3(leftDoor.X, 1.0f, 0f), new Vector3(0.6f, 3f, 9f));
            yield return Ticks(2);
            foreach (int seat in new[] { 0, 2 })
            {
                p.EnterVehicle(car, seat);
                yield return Ticks(2);
                var before = p.GlobalPosition;
                bool out_ = p.TryExitVehicle();
                T.Check($"wall: seat {seat} is REFUSED ({p.LastExitVerdict}, {car.LastExitProbe})",
                    !out_ && p.LastExitVerdict != Vehicle.ExitVerdict.Clear);
                T.Check($"wall: ...and is still in seat {seat}, not walked round to the other side", p.IsDriving && p.SeatIndex == seat && !car.SeatFree(seat));
                T.Check("wall: ...and did not move", p.GlobalPosition.DistanceTo(before) < 0.05f);
                p.TrySwitchSeat(1);   // free the left seat for the next round...
                T.Check("wall: the right-hand seat still gets out", p.TryExitVehicle() && car.ToLocal(p.GlobalPosition).X > boxHalf);
                yield return Ticks(2);
                Still(car);
            }

            // ---- 3. FORCED EXITS ARE NEVER REFUSED. Dying in the driver's seat against the same wall still puts you
            // outside the car -- somewhere you fit -- rather than leaving a corpse holding the seat.
            p.EnterVehicle(car, 0);
            yield return Ticks(2);
            p.TakeDamage(10000f);
            yield return Ticks(2);
            T.Check($"forced: dying against the wall still empties the seat (driving {p.IsDriving}, seat free {car.SeatFree(0)})",
                !p.IsDriving && car.SeatFree(0));
            var deadLocal = car.ToLocal(p.GlobalPosition);
            T.Check($"forced: ...and not inside the wall (local x {deadLocal.X:0.00}, wall from {leftDoor.X - 0.3f:0.00} to {leftDoor.X + 0.3f:0.00})",
                deadLocal.X > leftDoor.X + 0.3f || deadLocal.X < leftDoor.X - 0.3f);
            wall.QueueFree();
            p.QueueFree();
            yield return Ticks(2);
            p = Rigs.Player(World, car.GlobalPosition + new Vector3(0f, 0f, 6f));
            yield return Ticks(2);

            // ---- 4. EACH PROBE HAS A CASE ONLY IT CATCHES.
            // A thin fence between the hull and the door spot: there is room to stand beyond it, but the way out
            // crosses it. Only the PATH ray sees this.
            // The door spot sits radius + clearance outboard of the measured hull edge, so the capsule's inner side is
            // `clearance` off the hull: the fence goes in the middle of that gap, touching neither.
            float seatZ = car.SeatLocal(0).Z;
            var fenceX = leftDoor.X + Vehicle.ExitPlayerRadius + Vehicle.ExitHullClearance * 0.5f;
            var fence = Block(car, new Vector3(fenceX, 1.0f, seatZ), new Vector3(0.04f, 2.5f, 1.6f));
            yield return Ticks(2);
            p.EnterVehicle(car, 0);
            yield return Ticks(2);
            bool fenceOut = p.TryExitVehicle();
            T.Check($"fence: refused as DoorBlocked -- the way-out ray caught it ({p.LastExitVerdict}, {car.LastExitProbe})",
                !fenceOut && p.IsDriving && p.LastExitVerdict == Vehicle.ExitVerdict.DoorBlocked);
            fence.QueueFree();
            yield return Ticks(2);
            // A post where you would stand, but off the line from the seat (0.25 m along the car, inside the capsule's
            // 0.35 radius; the way-out ray and the ground probe both run at the seat's own z): the path is clear and the
            // room is not. Only the CAPSULE sees this.
            var post = Block(car, new Vector3(leftDoor.X - 0.1f, 1.0f, seatZ + 0.25f), new Vector3(0.2f, 2.5f, 0.2f));
            yield return Ticks(2);
            bool postOut = p.TryExitVehicle();
            T.Check($"post: refused as NoRoom -- the room test caught it ({p.LastExitVerdict}, {car.LastExitProbe})",
                !postOut && p.IsDriving && p.LastExitVerdict == Vehicle.ExitVerdict.NoRoom);
            post.QueueFree();
            yield return Ticks(2);
            T.Check("clear again: out", p.TryExitVehicle() && !p.IsDriving);
            car.QueueFree();
            yield return Ticks(2);

            // ---- 5. A CENTRELINE SEAT HAS NO SIDE: the quad steps off the left (retail: even seats left first), and
            // off the right when the left is walled -- refused only when both are.
            var quad = Vehicle.BuildByName("quad");
            World.AddChild(quad);
            quad.GlobalPosition = new Vector3(120f, 1.0f, 0f);
            yield return Ticks(45);
            Still(quad);
            T.Check($"quad: its rider sits on the centreline (x {quad.SeatLocal(0).X:0.00})", Mathf.Abs(quad.SeatLocal(0).X) <= Vehicle.ExitCentrelineX);
            var qp = Rigs.Player(World, quad.GlobalPosition + new Vector3(0f, 0f, 6f));
            yield return Ticks(2);
            qp.EnterVehicle(quad, 0);
            yield return Ticks(2);
            T.Check($"quad: off the LEFT by default ({quad.LastExitProbe})", qp.TryExitVehicle() && quad.ToLocal(qp.GlobalPosition).X < 0f);
            var qDoors = quad.DoorExitsLocal(0);
            T.Check($"quad: two doors to try ({qDoors.Count})", qDoors.Count == 2);
            var qLeft = Block(quad, new Vector3(qDoors[0].X, 1.0f, 0f), new Vector3(0.6f, 3f, 5f));
            yield return Ticks(2);
            qp.EnterVehicle(quad, 0);
            yield return Ticks(2);
            T.Check($"quad: left walled -> off the RIGHT ({quad.LastExitProbe})", qp.TryExitVehicle() && quad.ToLocal(qp.GlobalPosition).X > 0f);
            var qRight = Block(quad, new Vector3(qDoors[1].X, 1.0f, 0f), new Vector3(0.6f, 3f, 5f));
            yield return Ticks(2);
            qp.EnterVehicle(quad, 0);
            yield return Ticks(2);
            bool qOut = qp.TryExitVehicle();
            T.Check($"quad: both walled -> refused, still aboard ({qp.LastExitVerdict}, {quad.LastExitProbe})", !qOut && qp.IsDriving);
            qLeft.QueueFree(); qRight.QueueFree(); quad.QueueFree(); qp.QueueFree();
            yield return Ticks(2);

            // ---- 6. THE BUS HAS ONE REAL DOOR. The back row leaves by the bi-fold doorway at the front right, not
            // through the side of the bus beside its seat.
            var bus = Vehicle.BuildByName("bus");
            World.AddChild(bus);
            bus.GlobalPosition = new Vector3(-120f, 1.5f, 0f);
            yield return Ticks(45);
            Still(bus);
            Vehicle.AccessZone? doorway = null;
            foreach (var z in bus.AccessZones) if (z.Kind == Vehicle.AccessKind.BiFold) doorway = z;
            T.Check("bus: has its bi-fold doorway", doorway.HasValue);
            var bp = Rigs.Player(World, bus.GlobalPosition + new Vector3(0f, 0f, 9f));
            yield return Ticks(2);
            int back = bus.SeatCount - 1;
            bp.EnterVehicle(bus, back);
            yield return Ticks(2);
            bool busOut = bp.TryExitVehicle();
            var bl = bus.ToLocal(bp.GlobalPosition);
            if (doorway.HasValue)
                T.Check($"bus: the back row (seat {back}, z {bus.SeatLocal(back).Z:0.0}) steps out at the doorway " +
                        $"(local {bl.X:0.00},{bl.Z:0.00}; doorway z {doorway.Value.Center.Z:0.00}) ({bus.LastExitProbe})",
                    busOut && bl.X > 0f && Mathf.Abs(bl.Z - doorway.Value.Center.Z) < 0.6f);
            bus.QueueFree(); bp.QueueFree();
            yield return Ticks(2);
        }
    }
}
