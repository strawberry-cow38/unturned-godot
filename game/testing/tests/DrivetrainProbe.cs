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
            string only = System.Environment.GetEnvironmentVariable("UG_CAR");
            var fleet = string.IsNullOrEmpty(only) ? Fleet : new[] { only };
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
            float top = 0f, peakRpm = 0f, t = 0f;
            int flat = 0, shifts = 0, downshifts = 0, prevGear = v.Gear;
            float rpmAtLastUpshift = 0f, minRpmSeenMoving = 99999f;
            float lastShiftT = -99f, minShiftGap = 99f;
            var gearTops = new Dictionary<int, float>();
            for (int i = 0; i < 6000 && flat < 200; i++)
            {
                v.Drive(1f, 0f, false);
                yield return Ticks(1);
                t += Dt;
                float sp = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
                float rpm = v.EngineRpm;
                if (sp > top + 0.01f) { top = sp; flat = 0; } else flat++;
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
            GD.Print($"[drv] {car}: TOP {top:0.00} m/s ({top * 3.6f:0.0} km/h) in {t:0.0} s | {shifts} upshifts, {downshifts} downshifts, closest pair {minShiftGap:0.00} s");
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
            float oldCap = v.SpecSpeedMaxForTest;
            T.Check($"top speed clears the old hard cap ({top:0.00} vs {oldCap:0.00} m/s)", top > oldCap * 1.25f);
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
            for (int i = 0; i < 100; i++) { v.Drive(0f, 0f, false); yield return Ticks(1); }
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
        }
    }
}
