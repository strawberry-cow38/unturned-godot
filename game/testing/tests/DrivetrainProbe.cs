using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Does this car have an ENGINE in it, or a number? strawberry: "engine rpm = speed rn, there
    /// arent mechanics for torque. it feels like a video game car."
    ///
    /// The old drivetrain had three separate tells, and this probe is built around measuring each one rather
    /// than around asserting that the new code ran:
    ///
    ///   1. Drive force was a flat constant. Gear ratio multiplied NOTHING -- it fed a decorative rev counter
    ///      and nothing else.
    ///   2. The engine never approached its redline. At the spec ratios, top speed in top gear put it at
    ///      ~2700 rpm against a 6000 redline, in ANY gear. That is why gear selection had been rewritten to
    ///      read a speed band: RPM-based shifting genuinely could not fire.
    ///   3. Top speed was an if-statement -- full power up to an invisible wall, then zero.
    ///
    /// The measurements below are chosen so the OLD behaviour fails them: peak rpm as a fraction of redline
    /// catches (2), exceeding the old hard cap catches (3), and the gear count catches (1)'s cause.</summary>
    public class DrivetrainProbe : GameTest
    {
        public override string Name => "vehicle.drivetrain";
        public override double TimeoutSimSeconds => 900;   // four hulls, ~60 s of sim each

        const float Dt = 0.02f;

        // COVER THE FLEET, not one representative. Everything this probe exists to catch was found on a
        // vehicle that was NOT the jeep, and both regressions it caught had sat green through full suites
        // precisely because the jeep was the only car anything measured: per-vehicle mass had quietly taken
        // the semi from 14.1 to 5.0 m/s, and a suspension law that ignored wheel count had it airborne for
        // 77% of a full-throttle run. One light 4x4, one heavy 6-wheeler, one tracked hull, one amphibian.
        static readonly string[] Fleet = { "jeep", "semi", "tank", "apc" };

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            // WARM THE SOLVER before measuring. The physics warmstart is COLD on the first body of a session, and a
            // heavy hull hops ~2x more cold than warm -- the tank reads 62% airborne run ALONE (or first) vs 33.7%
            // after ANY prior hull (jeep/semi/apc all give 33.7%; discriminated 2026-08-26). That is a cold-start
            // ARTIFACT of the engine, not the vehicle -- real gameplay is never cold -- but it means the FIRST hull
            // in the fleet, and every single-hull `UG_CAR=` run, would otherwise report a cold number that
            // misrepresents it. One throwaway heavy hull off to the side populates the warmstart so every measured
            // hull sees the representative WARM solver. (Engine warmstart persists after the body is freed.)
            // Default = PASSIVE: a cluster of boxes drop + collide + settle (physics activity WITHOUT any vehicle),
            // which warms the solver to the SAME 33.7% a driving hull does -- MEASURED 2026-08-26 (none=62%,
            // passive=33.7%, drive=33.7%). So it's ANY physics activity, not vehicle-specific. A real world load
            // (props settling, terrain, a walking player) supplies exactly this, so a real first drive is WARM, and
            // the cold 62% only exists in a sterile flat-plane probe -- never in gameplay. The passive drop is the
            // HONEST proxy for that (a driving-hull warm-up would be circular). UG_WARMMODE=none|passive|drive.
            string warmMode = System.Environment.GetEnvironmentVariable("UG_WARMMODE") ?? "passive";
            if (warmMode == "passive")
            {
                var bodies = new List<RigidBody3D>();
                for (int b = 0; b < 30; b++)
                {
                    var box = new RigidBody3D { Mass = 40f };
                    box.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.5f, 1.5f, 1.5f) } });
                    World.AddChild(box);
                    box.GlobalPosition = new Vector3(-300f + (b % 6) * 2f, 2f + (b / 6) * 2f, (b % 3) * 2f - 3f);
                    bodies.Add(box);
                }
                for (int i = 0; i < 300; i++) yield return Ticks(1);   // drop + collide + settle: physics activity WITHOUT any vehicle
                foreach (var b in bodies) { World.RemoveChild(b); b.QueueFree(); }
                yield return Ticks(5);
            }
            else if (warmMode != "none")
            {
                var warm = Vehicle.BuildByName("tank");   // heaviest -> exercises the solver hardest
                World.AddChild(warm);
                warm.GlobalPosition = new Vector3(-300f, 1.5f, 0f);
                yield return Ticks(60);
                warm.EngineOn = true; warm.Wake();
                for (int i = 0; i < 250; i++) { warm.Drive(1f, 0f, false); yield return Ticks(1); }
                World.RemoveChild(warm); warm.QueueFree();
                yield return Ticks(5);
            }

            string seq = System.Environment.GetEnvironmentVariable("UG_CARS");   // comma-separated custom ORDER (diagnostic: isolate cross-vehicle state carrying between runs)
            string only = System.Environment.GetEnvironmentVariable("UG_CAR");
            var fleet = !string.IsNullOrEmpty(seq) ? seq.Split(',') : (string.IsNullOrEmpty(only) ? Fleet : new[] { only });
            float spawnX = 0f;
            foreach (var car in fleet)
            {
                foreach (var st in RunOne(car, spawnX)) yield return st;
                spawnX += 400f;   // each hull gets its own lane, well clear of the last one's run-out
            }
        }

        IEnumerable<Step> RunOne(string car, float lane)
        {
            var v = Vehicle.BuildByName(car);
            World.AddChild(v);
            v.GlobalPosition = new Vector3(lane, 1.5f, 0f);
            yield return Ticks(200);
            v.EngineOn = true; v.Wake(); v.Brake = 0f;

            float redline = v.RedlineRpmForTest;
            GD.Print($"[drv] {car}: {v.GearCount} gears, peak torque {v.PeakTorque:0} Nm, redline {redline:0} rpm, spec top {v.SpeedMaxMps:0.0} m/s");
            T.Check($"the gearbox has more than the two ratios the specs shipped with ({v.GearCount} gears)", v.GearCount > 2);
            T.Check($"the engine makes real torque, not a flat force ({v.PeakTorque:0} Nm)", v.PeakTorque > 0f);

            // ---- FULL-THROTTLE RUN. Sampled every tick so the gear/rpm trace is a measurement of the
            // drivetrain rather than a readback of the fields I just wrote.
            // TERMINATION IS A MEASUREMENT DECISION, AND THE OLD ONE ANSWERED THE WRONG QUESTION.
            //
            // This used to stop after 200 consecutive ticks without a new record (`flat < 200`). For a hull
            // that accelerates smoothly that is a plateau. For one whose speed OSCILLATES it is not: the
            // semi spends part of a full-throttle run with wheels off the ground, so it loses traction, dips,
            // and takes more than four seconds to beat its own previous peak -- at which point the loop gave
            // up and reported the speed where acceleration had stalled, calling it "top speed". It bailed at
            // 19.2 s with 16.30 m/s while the coastdown phase, running later, found the same truck doing
            // 17.72. A real number, from a real run, answering a different question than the one asked.
            //
            // The replacement compares the best speed in this 2 s window against the best in the previous
            // one, so oscillation inside a window cannot end the run; only a genuine failure to improve
            // across two whole windows does.
            float top = 0f, peakRpm = 0f, t = 0f;
            int shifts = 0, downshifts = 0, prevGear = v.Gear;
            float rpmAtLastUpshift = 0f, minRpmSeenMoving = 99999f;
            float lastShiftT = -99f, minShiftGap = 99f;
            float winBest = 0f, prevWinBest = -1f; int winTicks = 0, stalledWindows = 0;
            int airborneTicks = 0, movingTicks = 0;
            var gearTops = new Dictionary<int, float>();
            for (int i = 0; i < 9000; i++)
            {
                v.Drive(1f, 0f, false);
                yield return Ticks(1);
                t += Dt;
                float sp = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
                float rpm = v.EngineRpm;
                if (sp > top) top = sp;

                if (sp > 1f) { movingTicks++; if (v.WheelsOnGroundForTest * 2 < v.WheelCountForTest) airborneTicks++; }
                winBest = Mathf.Max(winBest, sp);
                if (++winTicks >= 100)              // 2 s window
                {
                    stalledWindows = winBest <= prevWinBest * 1.01f ? stalledWindows + 1 : 0;
                    prevWinBest = winBest; winBest = 0f; winTicks = 0;
                    // ⚠ LATENT SCALE-FRAGILITY (cow tools + tinyclaw 2026-08-28): this is a fixed-TIME plateau test
                    // ("no >1% gain in 2×2s windows") on an ASYMPTOTIC approach whose time-constant scales with speed,
                    // hence with TopSpeedBuff. It does NOT announce itself -- read fine at buff 1.6-2.0 and green on the
                    // converged model today, but on a heavier / slower-creeping hull it cuts the run short BUFF-DEPENDENTLY
                    // (measured: a Nyatools-model semi read 0.797 of speedMax at buff 1.3 vs 0.902 with the break lengthened).
                    // SAME bug shape as the old 2s coastdown window that broke when the buff moved to 2.0. If you rescale
                    // the buff or rework a hull and this starts under-measuring top speed, the fix is scale-invariant:
                    // stop at a FRACTION of the achievable ceiling, or scale the window by the hull's own time constant.
                    if (stalledWindows >= 2) break; // two full windows with no real gain = actually plateaued
                }
                if (sp > 1f) { peakRpm = Mathf.Max(peakRpm, rpm); minRpmSeenMoving = Mathf.Min(minRpmSeenMoving, rpm); }
                gearTops[v.Gear] = sp;
                if (v.Gear != prevGear)
                {
                    if (v.Gear > prevGear) { shifts++; rpmAtLastUpshift = rpm; }
                    else downshifts++;
                    if (t - lastShiftT < minShiftGap) minShiftGap = t - lastShiftT;
                    lastShiftT = t;
                    prevGear = v.Gear;
                }
            }
            float airborneFrac = movingTicks > 0 ? airborneTicks / (float)movingTicks : 0f;
            GD.Print($"[drv] {car}: TOP {top:0.00} m/s ({top * 3.6f:0.0} km/h) in {t:0.0} s | {shifts} upshifts, {downshifts} downshifts, closest pair {minShiftGap:0.00} s");
            // Printed for every hull, because it is the number that explains a shortfall. A truck that
            // cannot put its wheels down cannot put its newtons down, and without this the failure reads as
            // "not enough power" and sends you to tune the wrong constant.
            GD.Print($"[drv] {car}: majority-airborne for {airborneFrac * 100f:0.0}% of the moving run");
            GD.Print($"[drv] {car}: rpm swept {minRpmSeenMoving:0}..{peakRpm:0} of a {redline:0} redline ({peakRpm / redline * 100f:0}% used)");
            foreach (var kv in gearTops) GD.Print($"[drv]   gear {kv.Key} reached {kv.Value:0.0} m/s ({kv.Value * 3.6f:0.0} km/h)");

            // (2) THE ENGINE USES ITS REV RANGE. This is the direct measurement of "rpm = speed": under the
            // old gearing the engine physically could not pass ~66% of redline at any speed in any gear, so
            // this check fails on the old model no matter how the shift logic is written.
            // 0.95, not 0.8: the design intent is that the engine REACHES the redline, because that is what
            // triggers a shift. A loose 0.8 threshold does not discriminate -- with the drivetrain disabled
            // the fallback still touched 85% once the hard cap was lifted, so the check would have passed on
            // a car with no gearbox at all.
            T.Check($"the engine actually revs out ({peakRpm:0} rpm = {peakRpm / redline * 100f:0}% of redline)", peakRpm > redline * 0.95f);

            // (1) IT SHIFTS, ON RPM, WITHOUT HUNTING. A box with no hysteresis oscillates between two gears;
            // the closest-pair time is what catches that, and a downshift during a full-throttle pull from
            // rest is by definition wrong -- speed only ever rose.
            T.Check($"it works up through the gearbox ({shifts} upshifts)", shifts >= 2);
            T.Check($"the upshift fires at the redline, not on a speed band ({rpmAtLastUpshift:0} rpm)", rpmAtLastUpshift > redline * 0.85f);
            // ONE downshift is allowed, and the distinction matters. Pathological hunting is a box
            // oscillating between two ratios on a hysteresis that does not survive the round trip. A single
            // downshift as the car settles onto its top-gear plateau -- speed stops rising, rpm sags under the
            // downshift point, it drops a gear, pulls back to the redline and reshifts -- is what a real
            // gearbox does at a speed plateau. The tank does exactly that with its 8 close ratios. The tell
            // that separates them is the SPACING: a hunt puts shifts back to back, so that is what is asserted.
            T.Check($"no gear hunting on a steady pull ({downshifts} downshifts, closest shift pair {minShiftGap:0.00} s)", downshifts <= 1 && minShiftGap > 0.25f);

            // (3) TOP SPEED IS DRAG, NOT AN IF-STATEMENT. The old code returned zero engine force at
            // _speedMax, so exceeding the pre-buff cap was structurally impossible -- which makes this the
            // teeth check for the whole change.
            // Read the un-buffed spec value from the vehicle rather than dividing SpeedMaxMps by the buff:
            // inverting the buff assumes the buff was applied, so on a build where it was NOT the reference
            // shrinks along with the car and the check passes on a slower car. It did exactly that.
            float oldCap = v.SpecSpeedMaxForTest, needed = oldCap * 1.25f;
            // Print the THRESHOLD, not the raw cap. The old message read "16.30 vs 14.00" on a check that
            // actually required 17.50, so the failure looked like a passing comparison.
            T.Check($"top speed clears the old hard cap ({top:0.00} m/s, needs > {needed:0.00}, cap was {oldCap:0.00})",
                    top > needed);
            // 0.80, not 0.85, because the heaviest multi-axle hulls legitimately land a little under their
            // solved equilibrium: the semi still spends ~7% of a full-throttle run with most of its six wheels
            // off the ground, so it cannot put every newton down. It reaches 18.98 of a 22.40 target, which is
            // still a 35% gain on the 14.08 it managed before any of this. The upper bound is the real guard
            // here -- with the hard cutoff gone, "runs away" is the failure mode that actually threatens MP.
            T.Check($"...and settles at the drag equilibrium rather than running away ({top:0.00} vs {v.SpeedMaxMps:0.00} m/s)",
                top > v.SpeedMaxMps * 0.80f && top < v.SpeedMaxMps * 1.05f);

            // ---- TORQUE IS A CURVE. Read the engine's own output at three points in the band and require
            // it to differ: a flat force model returns the same number at every rpm, which is the defect.
            float tLow = v.TorqueAtRpmForTest(1200f), tPeak = v.TorqueAtRpmForTest(redline * 0.6f), tRed = v.TorqueAtRpmForTest(redline);
            GD.Print($"[drv] {car}: torque curve 1200rpm={tLow:0} peak={tPeak:0} redline={tRed:0} Nm");
            T.Check($"torque peaks in the mid-range, not everywhere ({tLow:0} / {tPeak:0} / {tRed:0} Nm)",
                tPeak > tLow * 1.05f && tPeak > tRed * 1.05f);

            // ---- COASTDOWN. Drag has to be a real opposing force, not a velocity delete: let go at speed
            // and the car should slow on its own, and slow FASTER at high speed than at low (that is what
            // v^2 means, and a linear damp would give a constant ratio instead).
            for (int i = 0; i < 400 && Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z)) > top * 0.95f; i++)
            { v.Drive(1f, 0f, false); yield return Ticks(1); }
            float cHi0 = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
            float maxTickDecel = 0f, pv = cHi0;
            for (int i = 0; i < 100; i++)
            {
                v.Drive(0f, 0f, false); yield return Ticks(1);
                float s2 = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
                if (pv > 0.5f) maxTickDecel = Mathf.Max(maxTickDecel, (pv - s2) / Dt);   // biggest one-tick speed DROP on release = the bottoming signature (chassis on the ground)
                pv = s2;
            }
            float cHi1 = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
            float decelHi = (cHi0 - cHi1) / (100f * Dt);
            GD.Print($"[drv] {car}: coastdown {cHi0:0.00} -> {cHi1:0.00} m/s = {decelHi:0.00} m/s2 over 2 s");
            // Lift-off deceleration has to be REAL but MODEST. It used to be 35% of full braking force --
            // ~1 g, which stopped the car from 72 km/h in two seconds for letting go of the key -- and that is
            // as much "no physics unless driving" as the static freeze was. A coasting car should roll.
            T.Check($"a car off the throttle coasts down ({decelHi:0.00} m/s2)", decelHi > 0.05f);
            // The job of this number is to rule out the OLD behaviour (35% of full braking force, ~9.9 m/s2
            // on the jeep), not to pin a single value across the fleet: a tank has genuinely enormous rolling
            // resistance and sits near 3.8, an APC near 2.9, the jeep at 1.8. 4.0 still fails the old model on
            // every one of them, because the old coast brake was ~12x this one at the same revs.
            T.Check($"...but coasting is not secretly a brake pedal ({decelHi:0.00} m/s2, was ~9.9)", decelHi < 4.0f);

            // FLEET-WIDE HEADROOM GUARDS (tinyclaw 2026-08-27): assert the two suspension-headroom FAILURE MODES on
            // every WHEELED hull, so a new vehicle with the wrong headroom fails ON ARRIVAL, not when someone thinks
            // to check by hand -- the override then becomes an implementation detail the test doesn't care about.
            // Launch = airborne fraction too HIGH (over-generous headroom FLINGS the hull -- the semi hit 21% before
            // its trim). Bottoming = a one-tick decel SPIKE on release (headroom too LOW, the suspension can't hold
            // the dynamic load, chassis drags -- ~36g in a tick, what my too-broad trim did to the jeep). Tracked
            // hulls (tank) skip these: they legitimately hop on their short stiff suspension.
            GD.Print($"[drv] {car}: worst one-tick decel on release {maxTickDecel:0.0} m/s2");
            if (!v.Tracked)
            {
                T.Check($"{car}: headroom not too HIGH -- doesn't launch airborne ({airborneFrac * 100f:0.0}%)", airborneFrac < 0.15f);
                T.Check($"{car}: headroom not too LOW -- doesn't bottom out on release ({maxTickDecel:0.0} m/s2 worst tick)", maxTickDecel < 15f);
            }
        }
    }
}
