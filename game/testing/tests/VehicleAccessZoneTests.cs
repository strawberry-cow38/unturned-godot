using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // VEHICLE ACCESS ZONES (strawberry 2026-08-25: "entering a vehicle at the seat you're looking at. kill the
    // lookat for the whole car, change it for a collider on each 'door'... add volumes for the hood and trunk
    // too", and then "some vehicles may not have trunks, consider that too").
    //
    // This suite exists because the FIRST cut of the feature shipped with correct zone geometry that never ran.
    // The zone test lived inside the look scan's no-collider fallback branch, which only executes when nothing
    // else won the frame -- and a car you are stood in front of has a real hull collider, so it always won on
    // the sphere probe and the zone code was dead. Every press fell through to the driver's seat, which is
    // indistinguishable from the behaviour the feature replaced.
    //
    // So the checks below are deliberately split, because the two halves fail in ways that look identical from
    // the outside and only one of them is about geometry:
    //
    //   - the ZONES themselves: does a sedan get a door per seat, a hood and a boot, and does a quad correctly
    //     get no boot at all?
    //   - the RESOLUTION: aim a real player at a real car through the real look scan and ask which zone came
    //     back. Asserting on Vehicle.ResolveAccess alone would have passed against the broken build -- the maths
    //     was never the thing that was wrong.
    public sealed class VehicleAccessZoneTests : GameTest
    {
        public override string Name => "vehicle.access";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            // ---- 1. THE ZONES.
            var car = Vehicle.BuildByName("sedan");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(60f, 1.2f, 0f);
            yield return Ticks(30);

            int doors = 0, hoods = 0, trunks = 0;
            foreach (var z in car.AccessZones)
            {
                if (z.Kind == Vehicle.AccessKind.Door) doors++;
                else if (z.Kind == Vehicle.AccessKind.Hood) hoods++;
                else if (z.Kind == Vehicle.AccessKind.Trunk) trunks++;
            }
            T.Check($"the sedan gets a door per seat ({doors} doors, {car.SeatCount} seats)", doors == car.SeatCount);
            T.Check($"...and exactly one hood ({hoods})", hoods == 1);
            T.Check($"...and exactly one boot ({trunks})", trunks == 1);

            // Every door must belong to a DIFFERENT seat. A build that names them all seat 0 satisfies the count
            // above and reproduces the exact bug this suite was written for.
            var seen = new HashSet<int>();
            bool oneEach = true;
            foreach (var z in car.AccessZones)
                if (z.Kind == Vehicle.AccessKind.Door && !seen.Add(z.Seat)) oneEach = false;
            T.Check($"...and each door is a different seat ({seen.Count} distinct)", oneEach && seen.Count == doors);

            // Doors sit OUTBOARD, on both flanks. All four on one side is what a dropped sign looks like, and
            // the car still enters fine when it happens.
            bool left = false, right = false;
            foreach (var z in car.AccessZones)
                if (z.Kind == Vehicle.AccessKind.Door) { if (z.Center.X > 0f) right = true; else left = true; }
            T.Check("...and there are doors down both flanks", left && right);

            // "Some vehicles may not have trunks" -- asserted on a vehicle that genuinely has no boot to speak
            // of, so the derivation is doing the work rather than every vehicle getting one by default.
            var quad = Vehicle.BuildByName("quad"); World.AddChild(quad); quad.GlobalPosition = new Vector3(120f, 1.2f, 0f);
            yield return Ticks(10);
            int qTrunks = 0;
            foreach (var z in quad.AccessZones) if (z.Kind == Vehicle.AccessKind.Trunk) qTrunks++;
            T.Check($"a quad gets no boot ({qTrunks})", qTrunks == 0);
            quad.QueueFree();
            yield return Ticks(2);

            // ---- 2. THE RESOLUTION. A real player, aimed through the real look scan, at a real car.
            // Standing distance is set by LookReach (2.6 m), not by taste: the eye-ray simply stops before that,
            // so a test parked at a comfortable-looking 3 m focuses nothing and reports the feature broken.
            // The look scan is gated on a captured mouse, and headless REFUSES to capture -- setting MouseMode
            // leaves it Visible. Without this override the scan never runs and every check below reports "no
            // zone", blaming the feature for a harness condition.
            PlayerController.DebugForceLookScan = true;
            var p = Rigs.Player(World, car.GlobalPosition + new Vector3(1.8f, 0f, 0f));
            // FIRST person, because DebugLookAt aims the CAMERA while the scan traces from the SHOULDER. In third
            // person those are different points, so an aim laid exactly on a door leaves the trace a few degrees
            // low -- far enough that it passed under the car entirely and focused nothing. That parallax is real
            // in gameplay too, but it is not what this suite is measuring.
            p.DriveFP = true;
            yield return Ticks(4);

            p.DebugLookAt(car.GlobalPosition);
            yield return Ticks(6);
            // Before asking WHICH zone, prove the look scan runs at all in this harness. "No zone" and "the scan
            // never executed" produce identical output downstream, and only one of them is a bug in the feature.
            T.Check($"the look scan focuses the car at all (focus {(p.DebugFocusVehicle == null ? "null" : p.DebugFocusVehicle.DisplayName)},"
                    + $" eye {p.DebugEye.Origin.DistanceTo(car.GlobalPosition):0.#} m away)", p.DebugFocusVehicle == car);

            var hitSeats = new HashSet<int>();
            int matched = 0, tried = 0;
            foreach (var z in car.AccessZones)
            {
                // Stand outboard of the zone, on whichever axis it sits off the hull centre, and look straight
                // at it. Derived rather than hardcoded per kind so a change to where hood/boot volumes sit does
                // not quietly turn this into a test of the doors three times over.
                Vector3 off = z.Center - car.AccessBoxCenter;
                Vector3 outward = Mathf.Abs(off.X) >= Mathf.Abs(off.Z)
                    ? new Vector3(Mathf.Sign(off.X), 0f, 0f)
                    : new Vector3(0f, 0f, Mathf.Sign(off.Z));
                Vector3 world = car.GlobalTransform * z.Center;
                // TeleportTo, NOT GlobalPosition: the controller re-applies its render-interp snapshot every
                // 50 Hz tick, so a raw position write is snapped straight back and the player silently never
                // moves. Every aim then resolves from wherever it first stood -- which reads as the feature
                // picking one door for everything.
                p.TeleportTo(car.GlobalTransform * (z.Center + outward * 1.6f) with { Y = car.GlobalPosition.Y });
                yield return Ticks(4);
                // Aim TWICE. DebugLookAt derives pitch from the camera's CURRENT position, and the camera is
                // repositioned in _Process -- so the first call after a teleport computes the angle from where
                // the camera still was, which put the ray into the ground short of the car. The second call sees
                // the settled camera and lands on the target.
                p.DebugLookAt(world);
                yield return Ticks(2);
                p.DebugLookAt(world);
                // The look scan runs in _Process while this harness steps _PhysicsProcess, so the focus read one
                // tick after aiming can be the PREVIOUS aim's answer. Settle before reading.
                yield return Ticks(6);

                tried++;
                bool ok = p.DebugFocusVehicle == car && p.DebugFocusAccessValid
                          && p.DebugFocusAccess.Kind == z.Kind && p.DebugFocusAccess.Seat == z.Seat;
                if (ok) matched++;
                if (ok && z.Kind == Vehicle.AccessKind.Door) hitSeats.Add(z.Seat);
                Vector3 want = car.GlobalTransform * (z.Center + outward * 1.6f) with { Y = car.GlobalPosition.Y };
                T.Check($"aiming at the {z.Kind}{(z.Kind == Vehicle.AccessKind.Door ? " for seat " + z.Seat : "")} resolves it"
                        + $" (got {(p.DebugFocusAccessValid ? p.DebugFocusAccess.Kind.ToString() + " seat " + p.DebugFocusAccess.Seat : "NO ZONE")};"
                        + $" stood {p.GlobalPosition.Snapped(Vector3.One * 0.1f)}; target {world.Snapped(Vector3.One * 0.1f)};"
                        + $" ray {p.DebugLookOrigin.Snapped(Vector3.One * 0.1f)} -> {p.DebugLookEnd.Snapped(Vector3.One * 0.1f)};"
                        + $" car y {car.GlobalPosition.Y:0.##})", ok);
            }
            T.Check($"every zone on the car was reachable by aiming at it ({matched}/{tried})", matched == tried && tried >= 3);

            // The teeth. Against the broken build every one of the checks above returns "no zone" and the player
            // lands in seat 0 regardless -- so pin that at least two DIFFERENT seats were reached, which is the
            // one thing the old whole-hull behaviour could never do.
            T.Check($"...and aiming at different doors reached different seats ({hitSeats.Count})", hitSeats.Count >= 2);

            PlayerController.DebugForceLookScan = false;
            T.Check("the look-scan override is left off for the suites after this one", !PlayerController.DebugForceLookScan);

            yield break;
        }
    }
}
