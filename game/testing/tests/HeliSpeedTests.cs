using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHAT HORIZONTAL SPEED DOES A HELICOPTER ACTUALLY REACH, and what LAW is resisting it?
    //
    // Nothing else in the repo measures either. vehicle.heli_flight's fleet section reads SpeedMaxMps off the
    // SPEC FIELD and pins the ordering -- that is the calibration's INPUT, not its output, and it stays green
    // with the achieved speed landing anywhere at all. So the horizontal flight model is currently untested by
    // construction, and any rework of it would be graded by a suite that cannot see it.
    //
    // TWO CHECKS OF DIFFERENT KIND, because each alone is passable by a wrong model:
    //
    //   - MAGNITUDE ("a committed dive reaches roughly spec, and does not blow through the MP envelope"). This
    //     is what catches an airframe that quietly can no longer reach its own advertised speed -- the exact
    //     failure a drag rework risks -- but it says nothing about the mechanism.
    //   - SHAPE. And the obvious shape check is a TRAP, which is worth writing down because I wrote it first:
    //     "acceleration is lower at high speed than at low speed" does NOT distinguish the old excess-spring
    //     model from quadratic drag, because the engine damping the old model relied on is ITSELF a linear
    //     drag. Acceleration already tapered before the rework. That check would have passed before and after
    //     and proved nothing.
    //     What actually separates the two laws is CONVEXITY. Sample acceleration at three evenly spaced
    //     speeds: under a linear law a(v) = A - c*v the two decrements are EQUAL, while under a quadratic law
    //     a(v) = A - k*v^2 the upper one is larger by (2*v1 + 3*step) / (2*v1 + step). The sample points are
    //     chosen from the machine's actual state rather than fixed fractions, so that prediction is computed
    //     below rather than quoted.
    //
    // RUN AGAINST THE PRE-REWORK MODEL ON PURPOSE, so the baseline is recorded rather than assumed. It came
    // back exactly as a check with teeth should: all 36 magnitude checks PASSED and convexity FAILED at a
    // ratio of 1.08, against a quadratic prediction of 1.51 and a linear prediction of 1.00. The old model was
    // measurably linear, so this suite's green state is not something it could have reached by accident.
    public sealed class HeliSpeedTests : GameTest
    {
        public override string Name => "vehicle.heli_speed";
        // Real flying: seven airframes each spool for 5.2 s then hold a 13 s dive, plus a three-window probe.
        // These rigs set DebugInstantStart, so the start-up gate is out of the picture -- it is a gameplay
        // constant this suite does not measure, and letting it set the windows would make every check here
        // silently depend on it.
        // 13 s is not padding -- quadratic drag reaches 99 % of terminal at 2.65 * v_terminal / a, which is
        // ~11 s for the slowest-converging airframe (the Hind). Longer windows cost L1 wall clock for nothing,
        // and L1's outer cap is a real constraint: this suite going in at 20 s dives pushed the whole phase
        // past its then-1200 s timeout (since raised to 1800), which surfaces as a core dump in an unrelated test.
        public override double TimeoutSimSeconds => 600;

        const float DiveDeg = 45f;    // committed dive: the fastest a machine can be flown, which is what the
                                      // server's envelope has to survive. Level flight cannot produce the peak.

        static Vehicle Spawn(Node world, string name, Vector3 at)
        {
            var v = Vehicle.BuildByName(name);
            world.AddChild(v);
            v.GlobalPosition = at;
            v.DebugNoTurbulence = true;
            v.DebugInstantStart = true;   // these rigs measure FLIGHT; the start-up gate has its own check   // a measuring rig: a gust inside the window is noise in the number
            // CLEAN AIRFRAME. The sky-crane deploys an electromagnet on a 9 m cable whenever it is airborne, and a
            // swinging load is a real aerodynamic and attitude disturbance -- at DiveDeg it pulled the nose up to 29
            // deg, outside this suite's +/-15 window, so the terminal speed being measured stopped being the
            // airframe's. Speed_Max and the _heliDragFwd derivation this suite validates are properties of the bare
            // machine, so the bare machine is what it flies. The slung case has its own suite (vehicle.heli_sling).
            v.DebugNoSling = true;
            v.EngineOn = true;
            return v;
        }

        // Nose-down angle in degrees, POSITIVE when diving. Taken from the body up axis projected on the flat
        // heading rather than an Euler angle, for the reason heli_flight already records: Euler extraction
        // order is a mirror waiting to happen, and b.Y is the vector the flight model multiplies thrust by.
        static float PitchDownDeg(Vehicle v)
        {
            Basis b = v.GlobalTransform.Basis;
            var fwd = new Vector3(-b.Z.X, 0f, -b.Z.Z);
            if (fwd.LengthSquared() < 1e-6f) return 0f;
            return Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(b.Y.Dot(fwd.Normalized()), -1f, 1f)));
        }

        // HOLD THE ATTITUDE ON THE STICK, the way a pilot does -- releasing does NOT hold it. The model
        // converges angular VELOCITY toward the commanded rate and then leans on AngularDamp 0.25 (a ~4 s
        // decay) to bleed the residual off, so letting go at the commanded rate keeps rotating for seconds.
        // The first cut of this rig pitched to 25 deg, released, and measured a TUMBLING helicopter that fell
        // 880 m in 30 s: full health, no crash, and a plausible-looking speed that meant nothing. PD rather
        // than P because a pure proportional term on a rate-commanded axis oscillates.
        static float HoldDive(Vehicle v, float targetDeg)
        {
            float phi = -PitchDownDeg(v);                                        // nose-UP convention: pitch input +1 raises the nose
            float rateUp = Mathf.RadToDeg(v.AngularVelocity.Dot(v.GlobalTransform.Basis.X));
            return Mathf.Clamp(0.06f * (-targetDeg - phi) - 0.020f * rateUp, -1f, 1f);
        }

        // HOLD ALTITUDE ON PITCH. "Level flight" is not an attitude, it is a CONSTRAINT: at full collective,
        // driving vertical speed to zero with the cyclic converges on exactly the steepest lean the machine can
        // sustain without descending -- which is the attitude LevelFlightAccel solves for and the one the drag
        // coefficient is derived against. So this controller finds the calibration's own operating point
        // instead of the test asserting it from the outside.
        // TRIM INTEGRATOR: vertical speed accumulates into a target ATTITUDE, which the proven attitude loop
        // then holds. Two earlier shapes both failed, and both failures were informative:
        //
        //   - driving the stick straight from vy SATURATES. At T/W 1.45 the Hind climbs hard on full
        //     collective, so the error pinned the stick nose-down and it kept rotating until vy finally
        //     reversed -- by then 65 deg down and falling. The minicopter, with three times the pitch
        //     authority, was fine: the signature of a gain tuned on one airframe.
        //   - a PROPORTIONAL outer loop cannot get there either. It settles wherever target = k*vy is
        //     self-consistent, which is a hover-and-climb, not level flight: measured 13.7 deg at +4.56 m/s
        //     when level flight for that airframe is ~31 deg. Steady-state error is what proportional control
        //     DOES; the fixed point just looked like convergence.
        //
        // Integral action has zero steady-state error by construction, which is the whole requirement here:
        // the attitude has to end up wherever vy = 0 exactly, and that angle is what the calibration is
        // derived against. The proportional term is kept for approach speed only.
        static float LevelTrim(Vehicle v, ref float trim, float dt)
        {
            trim = Mathf.Clamp(trim + v.LinearVelocity.Y * 0.60f * dt, 0f, 55f);
            return Mathf.Clamp(trim + v.LinearVelocity.Y * 1.5f, 0f, 55f);
        }

        static float FlatSpeed(Vehicle v) => new Vector3(v.LinearVelocity.X, 0f, v.LinearVelocity.Z).Length();

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            // ---- 1. EVERY AIRFRAME REACHES ITS OWN SPEC, AND STAYS INSIDE THE ENVELOPE.
            // Flown from high enough that 13 s of diving cannot reach the ground: a machine that lands reports
            // a flat speed near zero, which is indistinguishable from one that could never accelerate.
            string[] fleet = { "minicopter", "scoutcopter", "huey", "hind", "orca", "hummingbird", "skycrane" };
            for (int fi = 0; fi < fleet.Length; fi++)
            {
                var a = Spawn(World, fleet[fi], new Vector3(fi * 400f, 1400f, 0f));
                float spec = a.SpeedMaxMps;
                for (int i = 0; i < 260; i++) { a.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
                T.Check($"{fleet[fi]}: the rotor is at full spool before the run (spool {a.RotorSpool:0.###})",
                    a.RotorSpool > 0.95f);

                // PEAK, not final. The envelope validates every tick, so the number that matters is the
                // largest the sim ever produced, not wherever the window happened to stop.
                float peak = 0f, lastFlat = 0f;
                for (int i = 0; i < 650; i++)
                {
                    lastFlat = FlatSpeed(a);
                    a.DriveHeli(1f, 0f, HoldDive(a, DiveDeg), 0f, 0.02);
                    yield return Ticks(1);
                    peak = Mathf.Max(peak, FlatSpeed(a));
                }

                // A CONTROL ON THE MEASUREMENT, not a claim about flight: a machine that clipped the ground or
                // bonked something both bleeds speed and makes the number above a measurement of a collision.
                T.Check($"{fleet[fi]}: flew the window clean, so the speed below is flight and not a collision (hp {a.Health:0}/{a.HealthMax:0}, {a.GlobalPosition.Y:0} m up)",
                    a.Health >= a.HealthMax - 0.01f && !a.Exploded && a.GlobalPosition.Y > 80f);
                T.Check($"{fleet[fi]}: held the dive attitude ({PitchDownDeg(a):0.#} deg nose-down at exit)",
                    PitchDownDeg(a) > DiveDeg - 15f && PitchDownDeg(a) < DiveDeg + 15f);
                // BOUNDED BOTH WAYS. A lower bound alone passes on a model with no limiter at all; an upper
                // bound alone passes on an airframe too draggy to be worth flying.
                // The trailing diagnostics are the ones that actually resolved a failure during this rework and
                // are kept for the next one: k says whether the derivation moved, the attitude says whether the
                // rig flew what it meant to, and "still gaining" separates "too draggy" from "window too short"
                // -- which is the distinction that turned a wrong guess about this suite into a measurement.
                T.Check($"{fleet[fi]}: a committed dive reaches its spec top speed ({peak:0.#} of {spec:0.#} m/s = {peak / spec:0.##}x; k {a.DebugHeliDragK:0.#####} 1/m, {PitchDownDeg(a):0.#} deg nose-down, still gaining {(FlatSpeed(a) - lastFlat) / 0.02f:+0.##;-0.##} m/s^2 at the end)",
                    peak > spec * 0.90f);
                // The upper bound is the MP envelope's, not taste: VehicleReplication validates horizontal
                // motion against SpeedMaxMps * EnvelopeSlack (1.25), so a sim that produces more than that
                // rolls back a legitimate pilot doing nothing wrong.
                T.Check($"{fleet[fi]}: ...without producing a state the server would reject ({peak / spec:0.##}x vs the 1.25x envelope)",
                    peak < spec * 1.25f);
            }

            // ---- 2. THE LAW OF THE RESISTING FORCE, measured as convexity across three evenly spaced speeds.
            //
            // ONLY THE HORIZONTAL COMPONENT IS ASSIGNED, and that is load-bearing rather than tidy. The first
            // cut of this rig wrote the whole velocity vector, which ZEROES the vertical -- and StepHeli's
            // crash detector fires on _prevSpeed - curSpeed > 200*dt using the FULL 3-D speed, while I had
            // reasoned the assignments were safe because they raised the FLAT speed. In a 45 deg dive the
            // vertical component is the larger one, so dropping it read as a ~6 m/s single-tick deceleration,
            // bonked the rig, and damaged the airframe whose acceleration was being measured. Preserving the
            // vertical keeps the 3-D magnitude nearly unchanged, so no assignment trips the detector.
            var m = Spawn(World, "minicopter", new Vector3(-800f, 1400f, 0f));
            float vmax = m.SpeedMaxMps;
            for (int i = 0; i < 260; i++) { m.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            // Short: the attitude only has to be established, and every metre per second built here raises the
            // floor the first sample has to clear.
            for (int i = 0; i < 60; i++) { m.DriveHeli(1f, 0f, HoldDive(m, DiveDeg), 0f, 0.02); yield return Ticks(1); }

            // SAMPLE POINTS DERIVED FROM THE MACHINE'S ACTUAL STATE, not from fixed fractions of Speed_Max.
            // Evenly spaced is the only thing convexity needs, and choosing v1 just above the current speed is
            // what keeps every assignment an increase. The predicted ratios are then computed from the sample
            // points below rather than quoted from a spacing this rig might not have achieved.
            float v1 = FlatSpeed(m) + 1.5f;
            float step = (vmax * 0.88f - v1) / 2f;
            var accel = new float[3];
            var windowTilt = new float[3];
            for (int s = 0; s < 3; s++)
            {
                var fwd = new Vector3(-m.GlobalTransform.Basis.Z.X, 0f, -m.GlobalTransform.Basis.Z.Z).Normalized();
                float want = v1 + step * s;
                m.LinearVelocity = new Vector3(fwd.X * want, m.LinearVelocity.Y, fwd.Z * want);
                // Two ticks of settle, then a 10-tick window. The attitude is held throughout, so the thrust
                // vector is the same in all three windows and only the speed differs.
                m.DriveHeli(1f, 0f, HoldDive(m, DiveDeg), 0f, 0.02); yield return Ticks(1);
                m.DriveHeli(1f, 0f, HoldDive(m, DiveDeg), 0f, 0.02); yield return Ticks(1);
                float v0 = FlatSpeed(m);
                for (int i = 0; i < 10; i++) { m.DriveHeli(1f, 0f, HoldDive(m, DiveDeg), 0f, 0.02); yield return Ticks(1); }
                accel[s] = (FlatSpeed(m) - v0) / 0.20f;
                windowTilt[s] = PitchDownDeg(m);
            }

            float dLow = accel[0] - accel[1];    // acceleration lost going v1 -> v1+step
            float dHigh = accel[1] - accel[2];   // ...and v1+step -> v1+2*step
            // For a = A - c*v the two decrements are EQUAL. For a = A - k*v^2 the ratio is (2*v1 + 3*step) /
            // (2*v1 + step), which is why the sample points have to be known to state the prediction.
            float predQuad = (2f * v1 + 3f * step) / (2f * v1 + step);
            T.Check($"the probe rig survived all three windows (hp {m.Health:0.##}/{m.HealthMax:0.##}, {m.GlobalPosition.Y:0} m up)",
                m.Health >= m.HealthMax - 0.01f && !m.Exploded && m.GlobalPosition.Y > 80f);
            // THE PRECONDITION THE WHOLE COMPARISON RESTS ON, and it was previously only asserted in a comment.
            // The three windows are supposed to differ in SPEED alone; if the PD controller drifts between them
            // the accelerations differ because the thrust vector moved, and a shallowing drift inflates the
            // ratio -- a false PASS, in the same family as the tumbling rig this suite already got caught by.
            T.Check($"...at the same attitude in all three, so only the speed differed ({windowTilt[0]:0.#}, {windowTilt[1]:0.#}, {windowTilt[2]:0.#} deg nose-down)",
                Mathf.Abs(windowTilt[0] - windowTilt[2]) < 2.5f && Mathf.Abs(windowTilt[0] - windowTilt[1]) < 2.5f);
            // Both decrements must be real before their ratio means anything: if the machine sits at its cap
            // in all three windows the accelerations are all ~0 and the ratio is noise over noise.
            T.Check($"...and the three windows differ enough to compare ({accel[0]:0.##}, {accel[1]:0.##}, {accel[2]:0.##} m/s^2 at {v1:0.#}/{v1 + step:0.#}/{v1 + 2f * step:0.#} m/s)",
                dLow > 0.15f && dHigh > 0.15f);
            // THE CLAIM, graded against the MIDPOINT of the two predictions so it fires on which law is in
            // force rather than on how well the coefficient happens to be tuned.
            float ratio = dHigh / dLow;
            T.Check($"the resisting force is QUADRATIC in speed, not linear (decrement ratio {ratio:0.##}; quadratic predicts {predQuad:0.##} at these samples, linear predicts 1.00)",
                ratio > (1f + predQuad) * 0.5f);
            // ...AND IT IS NOT QUADRATIC PLUS A LITTLE LINEAR, which the ratio alone cannot say. For a mixed
            // law a = A - c*v - k*v^2 the ratio is 1 + 2*step/(c/k + u) with u = 2*v1 + step, so the midpoint
            // threshold above passes for ANY c/k < u -- which at these samples is a residual linear
            // coefficient up to about 0.25 s^-1. The stray damping this whole rework exists to remove is
            // 0.100 s^-1, so the check written to prove the fix would have stayed green if someone reverted
            // LinearDampMode to Combine and left the quadratic term in place.
            //
            // Inverting the same relation measures c directly instead of bounding it by proxy: it is exactly
            // zero for a pure quadratic law, and reads back the stray coefficient if one returns.
            float impliedLinear = m.DebugHeliDragK * (2f * step / Mathf.Max(ratio - 1f, 1e-4f) - (2f * v1 + step));
            T.Check($"...with no residual LINEAR term hiding underneath it (implied c {impliedLinear:0.####} s^-1; Godot's default_linear_damp is 0.1, and k is {m.DebugHeliDragK:0.#####})",
                impliedLinear < 0.05f);
            // Bounded above too. The lower bound alone is satisfied MORE easily by a cubic or quartic law than
            // by the quadratic one the message claims, so without this the check would endorse a higher power.
            T.Check($"...and is not a HIGHER power than quadratic (ratio {ratio:0.##} against a quadratic prediction of {predQuad:0.##})",
                ratio < predQuad * 1.20f);

            // ---- 3. THE DERIVATION'S OWN CLAIM, which until now nothing measured: LEVEL-FLIGHT terminal speed
            // is Speed_Max. That is the entire purpose of _heliDragFwd, and every other window in this suite is
            // a 45 deg dive -- which settles against the 1.15 BACKSTOP, not against drag. Halving the drag
            // coefficient left every check in this file green, because the backstop caught the difference. A
            // calibration whose only instrument is a limiter that overrides it is not measured at all.
            //
            // TEETH CONFIRMED: with the coefficient halved, both checks below go red (1.179x and 1.176x) while
            // every dive check in section 1 still passes -- which is precisely the hole they could not see.
            var levelFleet = new[] { "minicopter", "hind" };
            for (int li = 0; li < levelFleet.Length; li++)
            {
                string name = levelFleet[li];
                // Spaced by INDEX. The first cut spaced them by fleet.Length, which is a constant, so both
                // airframes spawned on the same spot and collided -- and a collision reads as a control
                // failure, which is exactly how it was first diagnosed.
                var lv = Spawn(World, name, new Vector3(-2000f - li * 500f, 1000f, 400f));
                float lvSpec = lv.SpeedMaxMps;
                for (int i = 0; i < 260; i++) { lv.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
                float yStart = lv.GlobalPosition.Y, trim = 0f;
                for (int i = 0; i < 1400; i++)
                {
                    float want = LevelTrim(lv, ref trim, 0.02f);
                    lv.DriveHeli(1f, 0f, HoldDive(lv, want), 0f, 0.02);
                    yield return Ticks(1);
                }
                float lvFlat = FlatSpeed(lv);
                // The altitude bound is a PRECONDITION, not a nicety: a machine that is quietly descending is
                // trading height for speed and its terminal number answers a different question.
                T.Check($"{name}: the level-flight rig actually held altitude ({lv.GlobalPosition.Y - yStart:+0.#;-0.#;0} m over 28 s, {lv.LinearVelocity.Y:+0.##;-0.##;0} m/s at the end, {PitchDownDeg(lv):0.#} deg nose-down)",
                    Mathf.Abs(lv.LinearVelocity.Y) < 1.0f && Mathf.Abs(lv.GlobalPosition.Y - yStart) < 200f);
                T.Check($"{name}: level-flight terminal speed IS Speed_Max, which is what the drag coefficient is derived to produce ({lvFlat:0.#} vs {lvSpec:0.#} m/s = {lvFlat / lvSpec:0.###}x, k {lv.DebugHeliDragK:0.#####})",
                    lvFlat > lvSpec * 0.92f && lvFlat < lvSpec * 1.08f);
            }

            // ---- 4. LATERAL IS DRAGGIER THAN FORE/AFT. This is the entire justification for deleting
            // ForeAftBoost/LateralBoost -- the asymmetry moved from thrust to drag -- and it had NO coverage:
            // every other flight in this suite is nose-forward, so only the alongFwd branch was ever exercised.
            // Setting HeliLateralDragRatio to 1.0, i.e. deleting the asymmetry outright, moved no check.
            //
            // Asserted as a RATIO between two subjects, because two absolute bounds would be satisfied by any
            // pair of coefficients I happened to pick. Both machines are held LEVEL so rotor thrust has no
            // horizontal component and drag is the only horizontal force acting.
            var faR = Spawn(World, "huey", new Vector3(-2600f, 900f, 0f));
            var latR = Spawn(World, "huey", new Vector3(-2600f, 900f, 300f));
            for (int i = 0; i < 260; i++)
            {
                faR.DriveHeli(0f, 0f, 0f, 0f, 0.02); latR.DriveHeli(0f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
            }
            var bf = faR.GlobalTransform.Basis;
            var fwdDir = new Vector3(-bf.Z.X, 0f, -bf.Z.Z).Normalized();
            var sideDir = new Vector3(bf.X.X, 0f, bf.X.Z).Normalized();
            faR.LinearVelocity = new Vector3(fwdDir.X * 20f, faR.LinearVelocity.Y, fwdDir.Z * 20f);
            latR.LinearVelocity = new Vector3(sideDir.X * 20f, latR.LinearVelocity.Y, sideDir.Z * 20f);
            yield return Ticks(2);
            float fa0 = FlatSpeed(faR), lat0 = FlatSpeed(latR);
            for (int i = 0; i < 10; i++)
            {
                faR.DriveHeli(0f, 0f, 0f, 0f, 0.02); latR.DriveHeli(0f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
            }
            float faDecel = (fa0 - FlatSpeed(faR)) / 0.20f, latDecel = (lat0 - FlatSpeed(latR)) / 0.20f;
            T.Check($"both drag probes actually decelerated, so the ratio below divides real numbers (fore/aft {faDecel:0.##}, lateral {latDecel:0.##} m/s^2)",
                faDecel > 0.2f && latDecel > 0.2f);
            T.Check($"sliding SIDEWAYS drags harder than flying forward, by the designed ratio ({latDecel / faDecel:0.##}x against a designed {2.5f:0.##}x)",
                latDecel > faDecel * 2.0f && latDecel < faDecel * 3.0f);

            yield break;
        }
    }
}
