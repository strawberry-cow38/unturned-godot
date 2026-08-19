using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHY DOES THE CONTAINER SHIP CAPSIZE AND REFUSE TO TURN? (strawberry 2026-08-18: "it tends to capsize and is
    // almost impossible to turn.") Both symptoms are reported about the SAME hull, so before touching a handling
    // number this measures the two things a boat's handling is actually made of, on the ship AND on the runabout as
    // a control -- because "the ship turns slowly" is only a bug if a boat that handles fine turns faster for a
    // reason the ship doesn't share.
    //
    //   1. THE BUOYANCY FORCE CURVE vs DEPTH. Sweep the hull down through the water a slice at a time and read the
    //      net vertical acceleration. A healthy hull crosses zero once, steeply. A flat stretch is a hull with no
    //      idea where it floats, and a hull with no heave stiffness has no ROLL stiffness either -- same voxels.
    //   2. THE ROLL RESTORING CURVE vs HEEL. Hold the hull at a heel angle, let go for one tick, read the angular
    //      acceleration. This is the number that decides "rights itself" vs "capsizes", and it is measured, not
    //      derived: the 8-voxel Archimedes model has two discontinuous guards in it and I am not going to predict
    //      their sum with algebra.
    //   3. YAW AUTHORITY. Full rudder from a straight run, and read the steady turn rate reached.
    //
    // A PROBE, NOT A GATE: it prints curves and asserts only what has to be true for the vehicle to be usable at
    // all. The bounds worth gating on get written after there are numbers to write them against.
    public sealed class BoatHullProbe : GameTest
    {
        public override string Name => "vehicle.boat_hull_probe";
        public override double TimeoutSimSeconds => 600;

        const float Dt = 0.02f;   // the rig steps at 50 Hz

        static Vehicle Float(Node w, string name, Vector3 at)
        {
            var v = Vehicle.BuildByName(name);
            w.AddChild(v);
            v.GlobalPosition = at;
            v.EngineOn = true;
            return v;
        }

        // Park the hull at an exact pose with zero velocity, let the transform flush to the physics space, then
        // step ONE tick and read what the water did. Velocity is re-zeroed on every settling tick so the reading
        // is the restoring force alone -- with velocity present the per-voxel damping (-v * 0.1 * mass, and this
        // hull multiplies it by 4) would dominate whatever the buoyancy is doing.
        IEnumerable<Step> Pose(Vehicle v, float y, float rollDeg)
        {
            for (int i = 0; i < 3; i++)
            {
                v.GlobalTransform = new Transform3D(new Basis(Vector3.Back, Mathf.DegToRad(rollDeg)), new Vector3(v.GlobalPosition.X, y, 0f));
                v.LinearVelocity = Vector3.Zero; v.AngularVelocity = Vector3.Zero;
                yield return Ticks(1);
            }
        }

        public override IEnumerable<Step> Run()
        {
            // EXPLORATION: prints the full heave/roll curves the shipping numbers were read off. Gated because
            // 125 s on every L1 run buys nothing vehicle.boat_hull does not already assert -- but kept in-tree
            // and runnable, because the next person to touch a hull needs the CURVE, not my summary of it.
            if (System.Environment.GetEnvironmentVariable("UG_BOATSWEEP") != "1")
            { T.Check("SKIPPED (set UG_BOATSWEEP=1 to sweep)", true); yield break; }

            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Terrain.HasWater = true; Terrain.SeaLevelY = 0f;   // flat test sea at Y0, same as Main's boat scene
            try
            {
                var ship = Float(World, "ship", new Vector3(0f, 2f, 0f));
                var runa = Float(World, "runabout", new Vector3(600f, 2f, 0f));
                yield return Ticks(2);

                GD.Print($"[BOAT] ship mass={ship.Mass:0} inertia={ship.Inertia} com={ship.CenterOfMass}");
                GD.Print($"[BOAT] runa mass={runa.Mass:0} inertia={runa.Inertia} com={runa.CenterOfMass}");

                // ---- 1. WHERE DOES IT SETTLE, LEFT ALONE?
                for (int i = 0; i < 1500; i++) { ship.Drive(0f, 0f, false); runa.Drive(0f, 0f, false); yield return Ticks(1); }
                float shipRest = ship.GlobalPosition.Y, runaRest = runa.GlobalPosition.Y;
                GD.Print($"[BOAT] settled 30s: ship y={shipRest:0.00} roll={RollOf(ship):0.0}deg   runabout y={runaRest:0.00} roll={RollOf(runa):0.0}deg");

                // ---- 2. HEAVE CURVE. Net vertical accel against depth. Zero crossing = the float height; the
                // WIDTH of the near-zero region is the thing being looked for.
                foreach (var (v, tag, lo, hi, step) in new[] { (ship, "ship", -9f, 3f, 0.5f), (runa, "runabout", -2.5f, 1.5f, 0.25f) })
                {
                    var sb = new System.Text.StringBuilder();
                    for (float y = hi; y >= lo - 0.001f; y -= step)
                    {
                        foreach (var s in Pose(v, y, 0f)) yield return s;
                        yield return Ticks(1);
                        sb.Append($"{y:0.00}:{v.LinearVelocity.Y / Dt:+0.0;-0.0}  ");
                    }
                    GD.Print($"[BOAT-HEAVE] {tag} y:accel(m/s2)  {sb}");
                }

                // ---- 3. ROLL CURVE. Restoring angular accel about the hull's own forward axis, per heel angle.
                // NEGATIVE = rights itself (opposes the +roll), POSITIVE = the water is pushing it further over.
                foreach (var (v, tag, rest) in new[] { (ship, "ship", shipRest), (runa, "runabout", runaRest) })
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (float deg in new[] { 2f, 5f, 10f, 15f, 20f, 30f, 40f, 50f, 60f })
                    {
                        foreach (var s in Pose(v, rest, deg)) yield return s;
                        yield return Ticks(1);
                        float about = v.AngularVelocity.Dot(v.GlobalTransform.Basis.Z) / Dt;   // roll axis = body +Z
                        sb.Append($"{deg:0}deg:{about:+0.000;-0.000}  ");
                    }
                    GD.Print($"[BOAT-ROLL] {tag} heel:restoring(rad/s2, negative=rights)  {sb}");
                }

                // ---- 4. YAW AUTHORITY. Straight run to speed, then full rudder held, reading the turn rate it
                // actually reaches -- not the torque applied, which is the number that looks fine.
                foreach (var (v, tag, rest) in new[] { (ship, "ship", shipRest), (runa, "runabout", runaRest) })
                {
                    foreach (var s in Pose(v, rest, 0f)) yield return s;
                    for (int i = 0; i < 750; i++) { v.Drive(1f, 0f, false); yield return Ticks(1); }
                    float straight = v.LinearVelocity.Length();
                    for (int i = 0; i < 1500; i++) { v.Drive(1f, 1f, false); yield return Ticks(1); }
                    float yawRate = Mathf.RadToDeg(Mathf.Abs(v.AngularVelocity.Y));
                    GD.Print($"[BOAT-YAW] {tag} straight={straight:0.0}m/s  full-rudder 30s: yaw={yawRate:0.00}deg/s " +
                             $"(360 in {(yawRate > 0.01f ? 360f / yawRate : 9999f):0}s)  heel={RollOf(v):0.0}deg  spd={v.LinearVelocity.Length():0.0}m/s");
                }

                T.Check("probe ran (see [BOAT-*] lines)", true);
            }
            finally { Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea; }
        }

        // Heel angle: how far the hull's own up-axis has fallen away from world up.
        internal static float RollOf(Vehicle v) => Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(v.GlobalTransform.Basis.Y.Dot(Vector3.Up), -1f, 1f)));
    }

    // DOES RESOLUTION EXPLAIN IT? The probe above found the ship's upright to be an UNSTABLE equilibrium (positive
    // restoring out to 20 deg) sitting on a 2.5 m band of zero heave stiffness. The suspect is the voxel grid: the
    // source's 2-slices-per-axis puts one buoyancy point per 10 x 5.5 x 33 m block, so the ship's entire waterplane
    // is two points across a 20 m beam and the vertical spacing (5.5 m) is larger than its own draft -- the lower
    // deck saturates, the upper deck is still in the air, and in between NOTHING varies with depth or heel.
    //
    // That is a hypothesis about a cause, so it gets tested by VARYING the suspected cause and nothing else: same
    // hull, same lift, same damping, slice count swept. If resolution is the cause the dead band shrinks and the
    // small-heel restoring turns negative as slices go up. If it does not, the cause is somewhere else and I have
    // saved myself shipping a fix aimed at the wrong thing.
    public sealed class BoatSliceSweep : GameTest
    {
        public override string Name => "vehicle.boat_slice_sweep";
        public override double TimeoutSimSeconds => 900;
        const float Dt = 0.02f;

        IEnumerable<Step> Pose(Vehicle v, float x, float y, float rollDeg)
        {
            for (int i = 0; i < 3; i++)
            {
                v.GlobalTransform = new Transform3D(new Basis(Vector3.Back, Mathf.DegToRad(rollDeg)), new Vector3(x, y, 0f));
                v.LinearVelocity = Vector3.Zero; v.AngularVelocity = Vector3.Zero;
                yield return Ticks(1);
            }
        }

        public override IEnumerable<Step> Run()
        {
            // EXPLORATION, not a gate: ~4 and ~7 minutes of sim respectively. Kept in-tree and runnable
            // (UG_BOATSWEEP=1) because the shipping numbers were read off these curves and the next person to
            // change a hull needs the curve, not my summary of it. The skip is NAMED so a green run cannot be
            // mistaken for a run: a check called "SKIPPED" that passes is telling the truth.
            if (System.Environment.GetEnvironmentVariable("UG_BOATSWEEP") != "1")
            { T.Check("SKIPPED (set UG_BOATSWEEP=1 to sweep)", true); yield break; }
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Terrain.HasWater = true; Terrain.SeaLevelY = 0f;
            try
            {
                int col = 0;
                // (slices, extra BuoyLift). The odd/even prediction is tested by 5 and 7 -- if the dead band is
                // really the equilibrium landing on a deck boundary, every ODD count is clean and the number 3 is
                // not special. The dy rungs then chase the RETAIL DRAFT: Main.cs places the static Alberton
                // reference hull with its keel 4.8 m under, and the shipping ship measures 2.44 m, so the "-3.0
                // matches the 4.8 m draft" note on the spec is wrong by 2.4 m -- it was eyeballed against a hull
                // that was heeled 27 deg at the time, which puts the waterline up the hull side where it looks right.
                foreach (var (slices, dy) in new[] { (3, 0f), (5, 0f), (7, 0f), (3, 1f), (3, 2f), (3, 2.5f), (3, 3f) })
                {
                    float x = col++ * 400f;
                    System.Environment.SetEnvironmentVariable("UG_BUOYSLICES", slices.ToString());
                    System.Environment.SetEnvironmentVariable("UG_BUOYDY", dy.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var v = Vehicle.BuildByName("ship");
                    World.AddChild(v);
                    v.GlobalPosition = new Vector3(x, 2f, 0f);
                    v.EngineOn = true;
                    yield return Ticks(2);

                    // settle, untouched
                    for (int i = 0; i < 1500; i++) { v.Drive(0f, 0f, false); yield return Ticks(1); }
                    float rest = v.GlobalPosition.Y, restRoll = BoatHullProbe.RollOf(v);

                    // heave: count the rungs of the sweep whose net accel is inside +/-0.25 m/s2 -> the dead band,
                    // in metres, directly comparable between slice counts because the step is the same.
                    int flat = 0; float firstFlat = 0f, lastFlat = 0f;
                    for (float y = 1f; y >= -9.001f; y -= 0.25f)
                    {
                        foreach (var s in Pose(v, x, y, 0f)) yield return s;
                        yield return Ticks(1);
                        if (Mathf.Abs(v.LinearVelocity.Y / Dt) < 0.25f) { if (flat == 0) firstFlat = y; lastFlat = y; flat++; }
                    }

                    // roll restoring at the settled draft, small heel only -- that is the band that decides whether
                    // upright is stable, and it is the band the ship currently gets wrong.
                    var sb = new System.Text.StringBuilder();
                    foreach (float deg in new[] { 5f, 10f, 20f })
                    {
                        foreach (var s in Pose(v, x, rest, deg)) yield return s;
                        yield return Ticks(1);
                        sb.Append($"{deg:0}deg:{v.AngularVelocity.Dot(v.GlobalTransform.Basis.Z) / Dt:+0.000;-0.000}  ");
                    }
                    GD.Print($"[BOAT-SLICE] {slices}^3={slices * slices * slices,3} vox dy={dy:+0.0;-0.0} | settled y={rest:0.00} (target -4.80) heel={restRoll:0.0}deg | " +
                             $"deadband={flat * 0.25f:0.00}m ({firstFlat:0.00}..{lastFlat:0.00}) | roll {sb}");
                    v.QueueFree();
                    yield return Ticks(2);
                }
                System.Environment.SetEnvironmentVariable("UG_BUOYSLICES", null); System.Environment.SetEnvironmentVariable("UG_BUOYDY", null);
                T.Check("slice sweep ran (see [BOAT-SLICE] lines)", true);
            }
            finally { System.Environment.SetEnvironmentVariable("UG_BUOYSLICES", null); System.Environment.SetEnvironmentVariable("UG_BUOYDY", null); Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea; }
        }
    }

    // HOW MUCH RUDDER DOES A 66 m HULL NEED? Measured, because the quantity that matters is the steady TURN RATE
    // reached, not the torque applied -- and the torque is the number that already looks fine. The per-voxel water
    // damping resists yaw with the same r^2 the inertia has, so the two nearly cancel and the terminal rate is
    // roughly proportional to TurnScale; "roughly" is why this is a sweep and not a division.
    //
    // The runabout runs as a CONTROL at its untouched default. TurnScale is per-spec and defaults to 1, so the
    // runabout and the APC cannot have moved -- but "cannot have moved" is a claim about code I just edited, and
    // the cheap way to hold myself to it is to measure the boat that was already tuned and liked.
    public sealed class BoatTurnSweep : GameTest
    {
        public override string Name => "vehicle.boat_turn_sweep";
        public override double TimeoutSimSeconds => 900;

        IEnumerable<Step> Circle(Vehicle v, string tag)
        {
            for (int i = 0; i < 750; i++) { v.Drive(1f, 0f, false); yield return Ticks(1); }   // 15 s straight to speed
            float straight = v.LinearVelocity.Length();
            float worstHeel = 0f;
            for (int i = 0; i < 1500; i++)                                                     // 30 s of full rudder
            {
                v.Drive(1f, 1f, false);
                worstHeel = Mathf.Max(worstHeel, BoatHullProbe.RollOf(v));
                yield return Ticks(1);
            }
            float yaw = Mathf.RadToDeg(Mathf.Abs(v.AngularVelocity.Y));
            GD.Print($"[BOAT-TURN] {tag} straight={straight:0.0}m/s | yaw={yaw:0.00}deg/s (360 in {(yaw > 0.01f ? 360f / yaw : 9999f):0}s) " +
                     $"| worst heel in the turn={worstHeel:0.0}deg | turn spd={v.LinearVelocity.Length():0.0}m/s");
        }

        public override IEnumerable<Step> Run()
        {
            // EXPLORATION, not a gate: ~4 and ~7 minutes of sim respectively. Kept in-tree and runnable
            // (UG_BOATSWEEP=1) because the shipping numbers were read off these curves and the next person to
            // change a hull needs the curve, not my summary of it. The skip is NAMED so a green run cannot be
            // mistaken for a run: a check called "SKIPPED" that passes is telling the truth.
            if (System.Environment.GetEnvironmentVariable("UG_BOATSWEEP") != "1")
            { T.Check("SKIPPED (set UG_BOATSWEEP=1 to sweep)", true); yield break; }
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Terrain.HasWater = true; Terrain.SeaLevelY = 0f;
            try
            {
                int col = 0;
                foreach (float scale in new[] { 1f, 8f, 15f, 20f, 30f, 50f })
                {
                    System.Environment.SetEnvironmentVariable("UG_BOATTURN", scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var v = Vehicle.BuildByName("ship");
                    World.AddChild(v);
                    v.GlobalPosition = new Vector3(col++ * 900f, 2f, 0f);
                    v.EngineOn = true;
                    yield return Ticks(2);
                    for (int i = 0; i < 750; i++) { v.Drive(0f, 0f, false); yield return Ticks(1); }   // settle first
                    foreach (var st in Circle(v, $"ship  scale={scale,4:0}")) yield return st;
                    v.QueueFree();
                    yield return Ticks(2);
                }
                System.Environment.SetEnvironmentVariable("UG_BOATTURN", null);

                var r = Vehicle.BuildByName("runabout");
                World.AddChild(r);
                r.GlobalPosition = new Vector3(-900f, 2f, 0f);
                r.EngineOn = true;
                yield return Ticks(2);
                for (int i = 0; i < 750; i++) { r.Drive(0f, 0f, false); yield return Ticks(1); }
                foreach (var st in Circle(r, "runabout CONTROL")) yield return st;

                T.Check("turn sweep ran (see [BOAT-TURN] lines)", true);
            }
            finally { System.Environment.SetEnvironmentVariable("UG_BOATTURN", null); Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea; }
        }
    }

    // THE GATE. strawberry, 2026-08-18: "it tends to capsize and is almost impossible to turn." Both were real and
    // both were the same root cause -- see Spec.BuoySlices on _ship. These are the properties that had to change,
    // asserted as MEASURED quantities rather than as "the constants are still what I set them to".
    //
    // EVERY BOUND HERE WAS WRITTEN AFTER SEEING THE NUMBER, AND TEETH-CHECKED BY PUTTING THE BUG BACK
    // (BuoySlices 3->2, BuoyLift -0.7->-3.0). Broken readings in brackets on each check. That matters more than
    // usual here because the failure was not a crash or a wrong constant -- the old hull floated, drove, and
    // responded to the rudder. It was simply stable at 27 degrees of heel instead of at 0, which no check that
    // asks "did it build / does it float / did the input arrive" can see.
    //
    // The runabout runs as a CONTROL, because the voxel-damping normalisation changed a line every boat executes.
    // Be precise about what supports what. The evidence that 2-slice hulls did not move is the probe run BEFORE
    // and AFTER that edit coming back digit-identical (settles -0.27, yaw 57.96 -> 57.96, same roll curve to
    // three places) -- not these two checks, which cannot fail from that edit at all: the normalisation is
    // 8f / _buoys.Length, which is exactly 1 for an 8-voxel hull, so it is a no-op on the runabout BY
    // CONSTRUCTION. What these checks are for is the NEXT change, and they do have teeth against it: in the
    // teeth-check run they fired at 53.75 deg/s and y -2.17 when the buoy geometry shifted underneath them.
    //
    // Its numbers are pinned UNCHANGED including the mildly-wrong ones -- it settles at 6.2 deg of heel and is
    // faintly unstable at 2-5 deg (+0.003, +0.008), the same bug as the ship at about a fiftieth of the size,
    // small enough that nobody has complained. If someone fixes it later this fires and makes them say so on
    // purpose, rather than the ship's tuning quietly riding along on it.
    public sealed class BoatHullTests : GameTest
    {
        public override string Name => "vehicle.boat_hull";
        public override double TimeoutSimSeconds => 400;
        const float Dt = 0.02f;

        IEnumerable<Step> Pose(Vehicle v, float x, float y, float rollDeg)
        {
            for (int i = 0; i < 3; i++)
            {
                v.GlobalTransform = new Transform3D(new Basis(Vector3.Back, Mathf.DegToRad(rollDeg)), new Vector3(x, y, 0f));
                v.LinearVelocity = Vector3.Zero; v.AngularVelocity = Vector3.Zero;
                yield return Ticks(1);
            }
        }

        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Terrain.HasWater = true; Terrain.SeaLevelY = 0f;
            try
            {
                var ship = Vehicle.BuildByName("ship"); World.AddChild(ship);
                ship.GlobalPosition = new Vector3(0f, 2f, 0f); ship.EngineOn = true;
                var runa = Vehicle.BuildByName("runabout"); World.AddChild(runa);
                runa.GlobalPosition = new Vector3(600f, 2f, 0f); runa.EngineOn = true;
                yield return Ticks(2);

                // ---- SETTLE, UNTOUCHED. No input at all: this is the state the bug produced on its own.
                for (int i = 0; i < 1500; i++) { ship.Drive(0f, 0f, false); runa.Drive(0f, 0f, false); yield return Ticks(1); }
                float rest = ship.GlobalPosition.Y, heel = BoatHullProbe.RollOf(ship);

                T.Check($"ship settles UPRIGHT with no input: heel {heel:0.0} deg [broken: 26.7]", heel < 2f);
                T.Check($"ship settles at the retail Alberton draft: keel {-rest:0.00} m under, target 4.80 [broken: 2.44]",
                    Mathf.Abs(rest + 4.80f) < 0.35f);

                // ---- ROLL IS RESTORING AT SMALL HEEL. The SIGN is the whole check: positive means the water is
                // pushing it further over, which is what "tends to capsize" actually was. Each angle carries a
                // POSITIVE CONTROL -- if the pose silently failed to take, the hull would be sitting upright and
                // a reading of ~0 would sail through a naive "is it negative" bound at some later refactor.
                foreach (float deg in new[] { 5f, 10f, 20f })
                {
                    foreach (var st in Pose(ship, 0f, rest, deg)) yield return st;
                    float actual = BoatHullProbe.RollOf(ship);
                    yield return Ticks(1);
                    float restoring = ship.AngularVelocity.Dot(ship.GlobalTransform.Basis.Z) / Dt;
                    T.Check($"the hull really is heeled {deg:0} deg for that reading (measured {actual:0.0})", Mathf.Abs(actual - deg) < 1.5f);
                    T.Check($"heeled {deg:0} deg the water RIGHTS it: {restoring:+0.000;-0.000} rad/s2, negative required " +
                            $"[broken: {(deg == 5f ? "+0.045" : deg == 10f ? "+0.101" : "+0.205")}]", restoring < -0.10f);
                }

                // ---- NO HEAVE DEAD BAND. Sweep the hull down through the water and find the longest unbroken
                // stretch where the net vertical force is ~nothing. A hull with one is a hull with no waterline,
                // and it is the same voxels that would otherwise supply the righting moment above.
                int run = 0, worst = 0;
                for (float y = rest + 3f; y >= rest - 4f; y -= 0.25f)
                {
                    foreach (var st in Pose(ship, 0f, y, 0f)) yield return st;
                    yield return Ticks(1);
                    if (Mathf.Abs(ship.LinearVelocity.Y / Dt) < 0.5f) { run++; worst = Mathf.Max(worst, run); } else run = 0;
                }
                T.Check($"no heave dead band: longest flat stretch {worst * 0.25f:0.00} m [broken: 3.00]", worst * 0.25f <= 0.5f);

                // ---- TURN. Bounded BOTH ways on purpose. A lower bound alone would pass a ship that had been
                // turned into a speedboat, which is the other way to fail "ship like but usable" (strawberry).
                foreach (var st in Pose(ship, 0f, rest, 0f)) yield return st;
                float worstHeel = 0f;
                for (int i = 0; i < 750; i++) { ship.Drive(1f, 0f, false); runa.Drive(1f, 0f, false); yield return Ticks(1); }
                float shipStraight = ship.LinearVelocity.Length();
                for (int i = 0; i < 1500; i++)
                {
                    ship.Drive(1f, 1f, false); runa.Drive(1f, 1f, false);
                    worstHeel = Mathf.Max(worstHeel, BoatHullProbe.RollOf(ship));
                    yield return Ticks(1);
                }
                float shipYaw = Mathf.RadToDeg(Mathf.Abs(ship.AngularVelocity.Y));
                float circle = shipYaw > 0.01f ? 360f / shipYaw : 9999f;
                GD.Print($"[BOAT-GATE] ship rest={rest:0.00} heel={heel:0.0} straight={shipStraight:0.0}m/s yaw={shipYaw:0.00}deg/s circle={circle:0}s");

                T.Check($"ship turns: 360 deg in {circle:0} s, ship-like but usable (15-60 s) [broken: 484]", circle > 15f && circle < 60f);
                T.Check($"turning does not lay her over: worst heel through a full-rudder circle {worstHeel:0.0} deg", worstHeel < 10f);
                T.Check($"the deeper hull did not cost top speed: {shipStraight:0.0} m/s straight [regressed to 7.0 before the damping was normalised]",
                    shipStraight > 12f);

                // ---- CONTROL: the tuned boat must not have moved.
                float runaYaw = Mathf.RadToDeg(Mathf.Abs(runa.AngularVelocity.Y));
                GD.Print($"[BOAT-GATE] runabout yaw={runaYaw:0.00}deg/s (control, expect 57.96)");
                T.Check($"CONTROL -- runabout rudder untouched by the per-voxel damping change: {runaYaw:0.00} deg/s, expected 57.96", Mathf.Abs(runaYaw - 57.96f) < 3f);

                T.Check($"CONTROL -- runabout still floats where it did: y {runa.GlobalPosition.Y:0.00}, expected around -0.27",
                    Mathf.Abs(runa.GlobalPosition.Y + 0.27f) < 0.6f);
            }
            finally { Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea; }
        }
    }
}
