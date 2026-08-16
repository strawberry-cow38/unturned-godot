using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // VEHICLE SEATS (strawberry 2026-08-16: "first we need vehicle seats. switch between them with the function
    // keys. only F1 is the drivers seat", "passengers can hold weapons").
    //
    // Seat positions are extracted from each prefab's Seats/Seat_* empties (tools/dump_vehicle_seats.py). The
    // checks below are split between the two things that can go wrong with that, because they fail in opposite
    // directions and only one of them is visible:
    //
    //   - the DATA being wrong (seats in the wrong order, or off the vehicle). The prefab hands seats back in
    //     TREE order, which is not index order -- the sedan returns Seat_3 before Seat_2 -- so an extractor that
    //     forgets to sort seats the driver in the back of half the fleet, and every one of those vehicles still
    //     drives perfectly. Nothing about that is visible from outside the car.
    //   - the RULES being wrong (a passenger steering, a passenger losing their gun, two people in one seat).
    public sealed class VehicleSeatTests : GameTest
    {
        public override string Name => "vehicle.seats";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            // ---- 1. THE DATA. Every spec builds with at least one seat, seat 0 first.
            foreach (var key in Vehicle.SpecNames)
            {
                var v = Vehicle.BuildByName(key);
                World.AddChild(v);
                T.Check($"{key}: has at least one seat", v.SeatCount >= 1);

                // Seats live ON the vehicle. A seat that missed its parent transform lands at the origin or
                // metres away, and the number that catches it is the seat's offset against the body's own box --
                // not an absolute threshold, which would fail the bus for being a bus.
                var box = v.WorldMeshAabb().Size;
                float reach = Mathf.Max(4f, box.Length());
                bool onBoard = true;
                for (int i = 0; i < v.SeatCount; i++) if (v.SeatLocal(i).Length() > reach) onBoard = false;
                T.Check($"{key}: all {v.SeatCount} seat(s) sit on the vehicle (reach {reach:0.#} m)", onBoard);

                // No two seats in the same place. Duplicate coordinates are what a copy-paste in the table looks
                // like, and two passengers occupying one bench renders as one passenger.
                bool distinct = true;
                for (int i = 0; i < v.SeatCount && distinct; i++)
                    for (int j = i + 1; j < v.SeatCount && distinct; j++)
                        if (v.SeatLocal(i).DistanceTo(v.SeatLocal(j)) < 0.05f) distinct = false;
                T.Check($"{key}: its seats are distinct positions", distinct);

                v.QueueFree();
            }
            yield return Ticks(1);

            // Known multi-seat vehicles, so "everything has one seat" cannot pass this suite. Without this the
            // whole section above is satisfied by a table that silently fell back to single-seat for the lot.
            var bus = Vehicle.BuildByName("bus"); World.AddChild(bus); bus.GlobalPosition = new Vector3(-200f, 2f, 0f);
            T.Check($"the bus carries a busload, not a driver ({bus.SeatCount} seats)", bus.SeatCount >= 8);
            var hind = Vehicle.BuildByName("hind"); World.AddChild(hind); hind.GlobalPosition = new Vector3(-240f, 2f, 0f);
            T.Check($"the hind has its gunner as well as its pilot ({hind.SeatCount} seats)", hind.SeatCount >= 2);
            // A Mi-24 is flown from the REAR cockpit, gunner in the nose -- so seat 0 must sit further back than
            // seat 1. This reads as an off-by-one until you know the aircraft, which is exactly why it is pinned.
            T.Check($"...and it is flown from the rear cockpit (pilot z {hind.SeatLocal(0).Z:0.##}, gunner z {hind.SeatLocal(1).Z:0.##})",
                hind.SeatLocal(0).Z > hind.SeatLocal(1).Z);
            var mini = Vehicle.BuildByName("minicopter"); World.AddChild(mini); mini.GlobalPosition = new Vector3(-280f, 2f, 0f);
            T.Check($"the minicopter stays single-seat as asked ({mini.SeatCount})", mini.SeatCount == 1);
            yield return Ticks(1);

            // ---- 2. THE RULES.
            // The section-1 vehicles are freed and this one is parked well clear of them. They were previously
            // left at the world origin, which is where this sedan spawned too: it was wedged inside a bus and a
            // Hind, and reported 0.06 m/s under full engine force. That reads exactly like a broken driver gate.
            bus.QueueFree(); hind.QueueFree(); mini.QueueFree();
            yield return Ticks(2);
            var car = Vehicle.BuildByName("sedan");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(60f, 1.2f, 0f);
            yield return Ticks(30);

            var p = Rigs.Player(World, car.GlobalPosition + new Vector3(2f, 0f, 0f));
            yield return Ticks(2);
            p.EnterVehicle(car);
            T.Check("entering an empty car puts you in the driver's seat", p.IsDriving && p.SeatIndex == 0 && p.IsDriver);
            T.Check("...and that seat now reads as taken", !car.SeatFree(0));

            T.Check("F2 moves you to a passenger seat", p.TrySwitchSeat(1) && p.SeatIndex == 1);
            T.Check("...and you are no longer the driver", !p.IsDriver && p.IsDriving);
            T.Check("...the driver's seat is free again", car.SeatFree(0));
            T.Check("...and the seat you moved into is not", !car.SeatFree(1));

            // Vacating the wheel must stop the car rather than leave it running with nobody driving.
            T.Check("...and the engine stops when the wheel is vacated", !car.EngineOn);

            // A passenger holding W must not drive. Asserted on the vehicle actually moving, because
            // "DriveVehicle returned early" is a claim about my own code path, while "the car did not move" is
            // the claim that matters.
            // Does the throttle DO anything from the back seat? Measured as the difference the input makes in
            // ONE seat with ONE engine state -- passenger idling vs passenger holding W -- because every other
            // framing of this check measured something else:
            //
            //   9.5 m   -- the suite had laid no ground and the car was in free fall.
            //   2.1 m   -- the car was still rolling after the drop.
            //   3.56 vs 1.06 m -- the two windows ran back to back, so the first inherited leftover motion.
            //   0.06 m/s under full engine force -- the car was spawned inside the bus from section 1.
            //   and then it passed with the gate DELETED, because vacating the wheel switches the engine off,
            //   so the passenger was stopped by the ignition and the seat rule was never under test at all.
            //
            // An idling car creeps, so no absolute threshold means anything. The difference the key makes does.
            var park = car.GlobalPosition;
            float SpeedNow() { var lv = car.LinearVelocity; lv.Y = 0f; return lv.Length(); }

            car.LinearVelocity = Vector3.Zero; car.AngularVelocity = Vector3.Zero; car.GlobalPosition = park;
            car.EngineOn = true; car.Wake();
            for (int i = 0; i < 40; i++) yield return Ticks(1);
            for (int i = 0; i < 90; i++) yield return Ticks(1);
            float passIdle = SpeedNow();

            car.LinearVelocity = Vector3.Zero; car.AngularVelocity = Vector3.Zero; car.GlobalPosition = park;
            car.EngineOn = true; car.Wake();
            for (int i = 0; i < 40; i++) yield return Ticks(1);
            p.ScriptedDrive = new Vector2(0f, 1f);
            for (int i = 0; i < 90; i++) yield return Ticks(1);
            float passThrottle = SpeedNow();
            T.Check($"...and the engine really was running for those windows (engine {car.EngineOn})", car.EngineOn);
            T.Check($"holding the throttle in a passenger seat changes nothing -- engine running either way " +
                    $"(idle {passIdle:0.##} m/s vs throttle {passThrottle:0.##} m/s)",
                Mathf.Abs(passThrottle - passIdle) < 0.5f);

            T.Check("F1 takes the wheel back", p.TrySwitchSeat(0) && p.IsDriver);
            T.Check("...restarting the engine", car.EngineOn);

            car.LinearVelocity = Vector3.Zero; car.AngularVelocity = Vector3.Zero; car.GlobalPosition = park;
            car.Wake();
            for (int i = 0; i < 40; i++) yield return Ticks(1);
            for (int i = 0; i < 90; i++) yield return Ticks(1);
            float asDriver = SpeedNow();
            p.ScriptedDrive = null;

            // ...and the same key in the driver's seat obviously does something, or the check above passes on a
            // build where the throttle is broken for everyone.
            T.Check($"the same throttle drives from the front seat ({asDriver:0.##} m/s vs {passThrottle:0.##} m/s as a passenger)",
                asDriver > passThrottle * 3f + 1f);

            // ---- 3. THE SEATED BODY MOVES WITH THE SEAT (strawberry: "make the different seats actually move
            // the player's seated position"). Asserted on SeatBodyLocal rather than on the rendered body,
            // because the 3rd-person rig is not built in a headless run -- so a check that read the body's
            // transform would pass on a machine that draws every passenger on the driver's lap.
            T.Check($"seat 0's body sits at the hand-tuned driver offset ({car.SeatBodyLocal(0)})",
                car.SeatBodyLocal(0) == car.SeatOffset);
            bool bodiesDiffer = true;
            for (int i = 0; i < car.SeatCount && bodiesDiffer; i++)
                for (int j = i + 1; j < car.SeatCount && bodiesDiffer; j++)
                    if (car.SeatBodyLocal(i).DistanceTo(car.SeatBodyLocal(j)) < 0.05f) bodiesDiffer = false;
            T.Check($"every seat draws the body somewhere different ({car.SeatCount} seats)", bodiesDiffer);
            // The passenger offsets must be the extracted seats SHIFTED by the driver's tuning, not raw: a
            // passenger placed at the bare prefab point sits lower than the driver in the same car.
            var lift = car.SeatOffset - car.SeatLocal(0);
            T.Check($"...and passengers inherit the driver's tuning rather than the raw prefab point (lift {lift})",
                car.SeatBodyLocal(1).IsEqualApprox(car.SeatLocal(1) + lift));
            // ...and the BODY actually lands there. Read back from the rendered rig in the vehicle's local frame,
            // so this fails if PlayerController stops calling SeatBodyLocal even though the table stays perfect.
            // A null here is a FAILURE, not a skip: "no body in a headless run" and "body drawn in the wrong
            // seat" must not report the same way.
            // Third person, or there is no seated body to place -- the placement branch early-returns in FP,
            // which had this check reading a body still parked at the world origin.
            p.DriveFP = false;
            T.Check("F2 again for the body check", p.TrySwitchSeat(1) && p.SeatIndex == 1);
            yield return Ticks(6);
            var seated1 = p.DebugSeatedBodyLocal;
            T.Check($"the 3rd-person body exists and reports a seat position ({seated1})", seated1.HasValue);
            if (seated1.HasValue)
                T.Check($"...and it is drawn in the seat we moved to, not the driver's " +
                        $"(body {seated1.Value} vs driver seat {car.SeatBodyLocal(0)})",
                    seated1.Value.DistanceTo(car.SeatBodyLocal(1)) < 0.35f &&
                    seated1.Value.DistanceTo(car.SeatBodyLocal(0)) > 0.3f);
            T.Check("back to the wheel", p.TrySwitchSeat(0));
            yield return Ticks(6);
            var seated0 = p.DebugSeatedBodyLocal;
            if (seated0.HasValue)
                T.Check($"...and back at the wheel it is drawn in the driver's seat again ({seated0.Value})",
                    seated0.Value.DistanceTo(car.SeatBodyLocal(0)) < 0.35f);
            p.DriveFP = true;

            // The bus is the one where getting this wrong is most visible.
            var bus2 = Vehicle.BuildByName("bus"); World.AddChild(bus2); bus2.GlobalPosition = new Vector3(-400f, 2f, 0f);
            T.Check($"the bus seats its back row metres behind its driver " +
                    $"(driver z {bus2.SeatBodyLocal(0).Z:0.##}, back row z {bus2.SeatBodyLocal(bus2.SeatCount - 1).Z:0.##})",
                bus2.SeatBodyLocal(bus2.SeatCount - 1).Z - bus2.SeatBodyLocal(0).Z > 3f);
            bus2.QueueFree();
            yield return Ticks(1);

            // ---- 4. PASSENGERS CAN ACTUALLY USE THE WEAPON THEY ARE HOLDING (strawberry: "passengers can hold
            // weapons"). The first cut of the seat work only kept the viewmodel SHOWN for a passenger, which
            // looks identical to the feature working and is not it: Fire() refused on `_driving != null`
            // regardless of seat, so a passenger held a gun they could never shoot. Asserted on Fire()'s return
            // rather than on the viewmodel being visible, because visible was already true when it was broken.
            p.DriveFP = true;
            T.Check("the DRIVER cannot fire -- hands on the wheel", p.IsDriver && !p.Fire());
            T.Check("move to a passenger seat", p.TrySwitchSeat(1) && p.IsPassenger);
            yield return Ticks(2);
            T.Check("...and a passenger CAN fire", p.Fire());
            // In third person the aim source is the orbiting chase cam rather than the player's look, which is
            // the "stray tracer flies straight south" bug the old blanket guard was covering up. Refused on
            // purpose, so that hole does not reopen under a different name.
            p.DriveFP = false;
            yield return Ticks(2);
            T.Check("...but not from the orbiting third-person cam", !p.Fire());
            p.DriveFP = true;
            T.Check("back to the wheel for the rest", p.TrySwitchSeat(0));
            yield return Ticks(2);

            // Refusals. A seat index past the end, and the seat you are already in.
            T.Check("a seat that does not exist is refused", !p.TrySwitchSeat(99));
            T.Check("...as is the seat you are already in", !p.TrySwitchSeat(0));

            // Two occupants cannot share a seat. Built by hand rather than with a second player rig, because the
            // claim is about the occupancy set, not about a second controller.
            car.OccupiedSeats.Add(2);
            T.Check("a seat someone else is in is refused", !p.TrySwitchSeat(2));
            T.Check("...and you keep the seat you had", p.SeatIndex == 0 && p.IsDriver);
            car.OccupiedSeats.Remove(2);

            yield break;
        }
    }
}
