using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>MEASURE a car, so a handling change can be argued from numbers instead of from feel.
    ///
    /// Cars had no physics test at all -- every vehicle.* test before this one was a boat, a helicopter, a
    /// rope tow or a ship. So the mass model, the drivetrain and the brakes could all be changed and the
    /// suite would stay green while the car got worse, which is the same blind spot the ship's inertia
    /// measurement was written to close.
    ///
    /// This reports rather than asserts hard numbers: top speed, the time to reach it, the stopping distance
    /// from the footbrake and from the handbrake, and the steady-state turn radius. The only CHECKS are
    /// sanity floors -- a car that cannot reach half its spec speed, or cannot stop, or cannot turn, is
    /// broken in a way no tuning argument can excuse. Compare the printed numbers across a change.</summary>
    public class CarHandlingProbe : GameTest
    {
        public override string Name => "vehicle.car_handling";
        public override double TimeoutSimSeconds => 420;   // top speed is ~20 m/s now and takes ~50 s of sim to reach

        const float Dt = 0.02f;
        // THE BRAKE AND TURN PHASES RUN AT A FIXED REFERENCE SPEED, not at a fraction of top speed.
        // They used to re-accelerate to 95% of measured top before each test, which was fine when top speed
        // was hard-capped at 12.5 m/s and arrived in 7 s. With a real drivetrain the jeep pulls to 20 m/s over
        // ~50 s, so three re-accelerations blew the sim timeout and every later phase ran on a car that never
        // got moving -- the handbrake "stopped" it in 0.00 m because it was already stationary.
        // A fixed reference also keeps these numbers comparable across drivetrain changes, which is the whole
        // point of a probe: it is the same manoeuvre at the same speed before and after.
        const float RefSpeed = 12f;

        /// <summary>How much faster the car is rotating than its steering angle COMMANDS. Ackermann says a
        /// steer angle d at speed v yields yaw = v*tan(d)/wheelbase; a gripping car tracks that (<=1x, and
        /// under 1 is understeer), while a car whose rear has let go pivots past it. That is the signature of
        /// a handbrake turn, and unlike a raw yaw rate it does not depend on how long the manoeuvre ran.</summary>
        static float Oversteer(Vehicle v)
        {
            float wb = v.WheelbaseForTest;
            if (wb <= 0.1f) return 0f;
            float sp = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
            float geo = sp * Mathf.Tan(Mathf.DegToRad(Mathf.Abs(v.SteerAngleDegrees))) / wb;
            return geo > 0.05f ? Mathf.Abs(v.AngularVelocity.Y) / geo : 0f;   // ignore near-straight moments, where the ratio is noise
        }

        static IEnumerable<Step> AccelTo(Vehicle v, float target, int maxTicks = 2500)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                v.Drive(1f, 0f, false);
                yield return Step.Ticks(1);
                if (Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z)) >= target) break;
            }
        }

        static Vehicle Spawn(Node w, string name, Vector3 at)
        {
            var v = Vehicle.BuildByName(name);
            w.AddChild(v);
            v.GlobalPosition = at;
            // A car spawns PARKED, BRAKED and with the engine OFF, and Drive() zeroes the throttle unless
            // EngineOn -- so a probe that only calls Drive() measures a top speed of exactly 0.00 m/s and
            // reads like the car is broken. It is the harness that was broken; the sanity floor caught it.
            v.EngineOn = true;
            v.Wake();
            v.Brake = 0f;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            string car = System.Environment.GetEnvironmentVariable("UG_CAR") ?? "jeep";   // UG_CAR=semi to probe a MULTI-SHAPE hull
            var v = Spawn(World, car, new Vector3(0f, 1.5f, 0f));
            yield return Ticks(200);   // outlast the 2.5 s spawn grace so it has settled on its wheels first
            v.EngineOn = true; v.Wake(); v.Brake = 0f;

            // ---- ACCELERATION: full throttle until the speed stops rising, so top speed is MEASURED as the
            // point where drive force meets drag, not read off the spec.
            float top = 0f, tTop = 0f; float t = 0f;
            int flat = 0;
            for (int i = 0; i < 3000 && flat < 100; i++)
            {
                v.Drive(1f, 0f, false);
                yield return Ticks(1);
                t += Dt;
                float sp = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
                if (sp > top + 0.01f) { top = sp; tTop = t; flat = 0; } else flat++;
            }
            GD.Print($"[car] {car}: TOP SPEED {top:0.00} m/s ({top * 3.6f:0.0} km/h), reached in {tTop:0.0} s");
            T.Check($"the car actually accelerates (top {top:0.00} m/s)", top > 3f);

            // Everything below runs at RefSpeed (or the car's own top, if it is slower than that).
            float refSpeed = Mathf.Min(RefSpeed, top * 0.9f);
            for (int i = 0; i < 400 && v.LinearVelocity.Length() > refSpeed; i++) { v.Drive(-1f, 0f, false); yield return Ticks(1); }
            foreach (var st in AccelTo(v, refSpeed)) yield return st;

            // ---- FOOTBRAKE: distance from top speed to a stop.
            // Through Drive(), not by poking Brake: negative throttle while rolling forward IS the foot brake
            // (see Drive's footBrake), so the probe exercises the pedal a player actually presses.
            var p0 = v.GlobalPosition; float tb = 0f;
            for (int i = 0; i < 1500 && v.LinearVelocity.Length() > 0.5f; i++)
            { v.Drive(-1f, 0f, false); yield return Ticks(1); tb += Dt; }
            float footDist = v.GlobalPosition.DistanceTo(p0);
            GD.Print($"[car] {car}: FOOTBRAKE from {refSpeed:0.00} m/s -> {footDist:0.0} m in {tb:0.0} s");
            T.Check($"the footbrake stops the car ({footDist:0.0} m)", footDist > 0.05f && footDist < 400f);

            // ---- HANDBRAKE, measured the same way from the same entry speed, so the two are comparable.
            // strawberry: "the handbrake SUCKS". A handbrake that only differs by a scale factor stops in
            // roughly the proportion of that factor and slides not at all; the yaw it produces is the tell.
            foreach (var st in AccelTo(v, refSpeed)) yield return st;
            float entry = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
            // WITH STEERING HELD. A handbrake pull in a straight line cannot rotate anything, however the
            // brakes are wired -- locking the rears symmetrically produces no yaw moment at all. The rotation
            // in a handbrake turn comes from the FRONTS still gripping and steering while the rears let go, so
            // the input has to include lock or the probe measures 0.00 rad/s no matter what the code does. It
            // did exactly that, twice, and the second time it was measuring a working handbrake.
            var h0 = v.GlobalPosition; float th = 0f, maxYaw = 0f, handOver = 0f;
            for (int i = 0; i < 1500 && v.LinearVelocity.Length() > 0.5f; i++)
            {
                v.Drive(0f, 1f, true);
                yield return Ticks(1); th += Dt;
                maxYaw = Mathf.Max(maxYaw, Mathf.Abs(v.AngularVelocity.Y));
                handOver = Mathf.Max(handOver, Oversteer(v));
            }
            float handDist = v.GlobalPosition.DistanceTo(h0);
            GD.Print($"[car] {car}: HANDBRAKE+lock from {entry:0.00} m/s -> {handDist:0.0} m in {th:0.0} s, peak yaw {maxYaw:0.00} rad/s");
            T.Check($"the handbrake stops the car ({handDist:0.0} m)", handDist > 0.05f && handDist < 400f);

            // CONTROL: the same manoeuvre on the FOOTBRAKE. The handbrake is only doing its job if it rotates
            // the car MORE than this -- an absolute yaw number on its own would pass on any car that merely
            // turns while slowing down.
            foreach (var st in AccelTo(v, refSpeed)) yield return st;
            float footYaw = 0f, footOver = 0f;
            for (int i = 0; i < 1500 && v.LinearVelocity.Length() > 0.5f; i++)
            {
                v.Drive(-1f, 1f, false);
                yield return Ticks(1);
                footYaw = Mathf.Max(footYaw, Mathf.Abs(v.AngularVelocity.Y));
                footOver = Mathf.Max(footOver, Oversteer(v));
            }
            GD.Print($"[car] {car}: FOOTBRAKE+lock peak yaw {footYaw:0.00} rad/s (handbrake {maxYaw:0.00}) | OVERSTEER foot {footOver:0.00}x hand {handOver:0.00}x");
            // COMPARED AS OVERSTEER, not as raw yaw rate. A raw-yaw comparison is a comparison of two
            // manoeuvres whose DURATIONS differ, so it moves whenever brake strength moves: dropping
            // FootBrakeScale 6 -> 1.5 let the control leg corner for longer and its peak yaw rose 0.68 -> 0.87
            // purely from spending more time at full lock, failing a check about the handbrake. What actually
            // defines a handbrake turn is the car rotating FASTER than its steering geometry commands -- the
            // rears let go and it pivots past Ackermann. That ratio is immune to how long either leg lasts.
            T.Check($"the handbrake breaks the rear away more than braking does ({handOver:0.00}x vs {footOver:0.00}x Ackermann)",
                handOver > footOver);

            // ---- TURN RADIUS: hold full lock at a steady throttle and measure the circle from the yaw rate,
            // r = v / omega. Taken from the RATE rather than by fitting a path, so a drifting car still
            // reports the radius it is actually carving.
            v.Brake = 0f;
            for (int i = 0; i < 200; i++) { v.Drive(1f, 0f, false); yield return Ticks(1); }
            float rSum = 0f; int rN = 0;
            for (int i = 0; i < 400; i++)
            {
                v.Drive(1f, 1f, false);
                yield return Ticks(1);
                if (i < 150) continue;   // let the yaw rate settle before sampling
                float sp = Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z));
                float om = Mathf.Abs(v.AngularVelocity.Y);
                if (om > 0.01f && sp > 0.5f) { rSum += sp / om; rN++; }
            }
            float radius = rN > 0 ? rSum / rN : -1f;
            GD.Print($"[car] {car}: TURN RADIUS {radius:0.0} m at full lock ({rN} samples)");
            T.Check($"the car turns ({radius:0.0} m radius)", radius > 0f && radius < 200f);

            // ---- TURN-IN, the transient. The steady-state radius above is set by steering geometry and the
            // tyre model and is almost blind to the inertia tensor: pinning the jeep's inertia changed it from
            // (0,0,0) collider-derived to (1677, 2002, 612) and top speed, both stopping distances and the
            // radius came back BYTE-IDENTICAL. That is not "the change did nothing", it is
            // [[match_the_metric_to_the_symptom]] -- yaw inertia is a resistance to CHANGING rotation, so it
            // only shows in how fast the car takes a set, never in where it ends up. Measured as the time from
            // a step of full lock to 90 % of the steady yaw rate, plus how long the yaw takes to die once the
            // wheel is straightened.
            v.Brake = 0f;
            for (int i = 0; i < 300; i++) { v.Drive(1f, 0f, false); yield return Ticks(1); }   // straighten + settle
            float steady = 0f;
            for (int i = 0; i < 300; i++) { v.Drive(1f, 1f, false); yield return Ticks(1); if (i > 150) steady = Mathf.Max(steady, Mathf.Abs(v.AngularVelocity.Y)); }
            for (int i = 0; i < 300; i++) { v.Drive(1f, 0f, false); yield return Ticks(1); }   // back to straight
            float tIn = -1f; float el = 0f;
            for (int i = 0; i < 400; i++)
            {
                v.Drive(1f, 1f, false); yield return Ticks(1); el += Dt;
                if (tIn < 0f && Mathf.Abs(v.AngularVelocity.Y) >= steady * 0.9f) { tIn = el; break; }
            }
            float tOut = -1f; el = 0f;
            for (int i = 0; i < 400; i++)
            {
                v.Drive(1f, 0f, false); yield return Ticks(1); el += Dt;
                if (tOut < 0f && Mathf.Abs(v.AngularVelocity.Y) <= steady * 0.1f) { tOut = el; break; }
            }
            GD.Print($"[car] {car}: TURN-IN to 90% of {steady:0.000} rad/s in {tIn:0.000} s, yaw decays to 10% in {tOut:0.000} s");
            T.Check($"the car takes a set in finite time (turn-in {tIn:0.000} s)", tIn > 0f);

            // ---- INERTIA + MASS, printed so a mass-model change is visible directly rather than inferred
            // from the handling numbers above.
            GD.Print($"[car] {car}: mass {v.Mass:0.0} kg, inertia ({v.Inertia.X:0.0}, {v.Inertia.Y:0.0}, {v.Inertia.Z:0.0}), CoM {v.CenterOfMass}");
            T.Check($"inertia is authored, not left at zero ({v.Inertia})", v.Inertia.LengthSquared() > 0f);
        }
    }
}
