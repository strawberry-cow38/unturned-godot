using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>MEASURE how tippy a car is, because "top heavy" is a feeling and a fix needs a number.
    ///
    /// Master 2026-09-06: "fix a lot of vehicles being really top heavy and easy to flip (roadster, quad,
    /// sedan, vans, etc)". CarHandlingProbe measures speed, braking and turn radius and says nothing at all
    /// about roll, so the whole mass model could be changed and every car could get tippier with the suite
    /// still green -- the same blind spot that probe was itself written to close for the drivetrain.
    ///
    /// Two manoeuvres, because "flips" has two causes and only one of them is cornering:
    ///   TURN  -- full lock at a fixed reference speed. Peak roll here is grip-driven weight transfer.
    ///   KERB  -- a one-shot upward impulse under the LEFT wheels while running straight, which is what
    ///            clipping a kerb, a rock or the lip of a slope actually does. This is the one that flips
    ///            cars in practice: the static rollover threshold of these hulls is near 2 g, so no tyre can
    ///            corner them over, but a one-sided vertical hit does not care about the threshold.
    ///
    /// The impulse is scaled by the vehicle's own mass, so the same manoeuvre means the same thing to a quad
    /// and to a van and the numbers are comparable down the fleet.
    ///
    /// A/B it in ONE build with UG_ANTIROLL=0 (bars off) against the default. That switch is the entire point
    /// of the probe: it reports rather than asserting tuned values, and the only hard CHECK is the one no
    /// tuning argument can excuse -- a car that ends a manoeuvre on its roof.</summary>
    public class RolloverProbe : GameTest
    {
        public override string Name => "vehicle.rollover";
        public override double TimeoutSimSeconds => 900;

        const float Dt = 0.02f;
        const float RefSpeed = 12f;         // same reference speed CarHandlingProbe brakes and turns at
        const float KerbDeltaV = 2.0f;      // the kerb strike, as the vertical delta-v it would give the whole car

        /// <summary>Roll only -- the body's RIGHT axis tipping out of horizontal. Deliberately not the angle
        /// between body-up and world-up, which a nose-down car reports as roll it does not have.</summary>
        static float RollDegrees(Vehicle v)
            => Mathf.Abs(Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(v.GlobalTransform.Basis.X.Dot(Vector3.Up), -1f, 1f))));

        static bool Inverted(Vehicle v) => v.GlobalTransform.Basis.Y.Y < 0f;

        static Vehicle Spawn(Node w, string name, Vector3 at)
        {
            var v = Vehicle.BuildByName(name);
            w.AddChild(v);
            v.GlobalPosition = at;
            v.EngineOn = true;   // a car spawns parked, braked and off; Drive() zeroes throttle unless EngineOn
            v.Wake();
            v.Brake = 0f;
            return v;
        }

        IEnumerable<Step> AccelTo(Vehicle v, float target, int maxTicks = 1200)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                v.Drive(1f, 0f, false);
                yield return Ticks(1);
                if (Mathf.Abs(v.LinearVelocity.Dot(-v.GlobalTransform.Basis.Z)) >= target) break;
            }
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            string only = System.Environment.GetEnvironmentVariable("UG_CAR");
            string[] cars = string.IsNullOrEmpty(only)
                ? new[] { "roadster", "quad", "sedan", "van" }   // the ones master named
                : new[] { only };
            bool bars = System.Environment.GetEnvironmentVariable("UG_ANTIROLL") != "0";
            GD.Print($"[roll] anti-roll bars {(bars ? "ON" : "OFF")}");

            // FLEET SURVEY FIRST, and it costs no sim time: build every wheeled spec and print its static
            // rollover threshold. This is what says which vehicles the CoM floor actually moves -- "only the
            // quad" is a claim, and this is the check on it.
            foreach (string n in Vehicle.SpecNames)
            {
                Vehicle sv = null;
                try { sv = Vehicle.BuildByName(n); } catch { }
                if (sv == null) continue;
                World.AddChild(sv);
                if (sv.RolloverThresholdForTest > 0f)
                    GD.Print($"[roll:fleet] {n,-12} SSF {sv.RolloverThresholdForTest:0.00} g{(sv.RolloverThresholdForTest <= 1.001f ? "  <- AT THE FLOOR (moved)" : "")}");
                sv.QueueFree();
            }
            yield return Ticks(5);

            float worstTurn = 0f, worstKerb = 0f;
            var flipped = new List<string>();

            foreach (string car in cars)
            {
                var v = Spawn(World, car, new Vector3(0f, 1.5f, 0f));
                GD.Print($"[roll] {car,-9} SSF {v.RolloverThresholdForTest:0.00} g | grip {v.WheelFrictionForTest(0):0.00} | halftrack/comH from spec");
                yield return Ticks(200);                       // outlast the 2.5 s spawn grace and settle on the springs
                v.EngineOn = true; v.Wake(); v.Brake = 0f;

                // ---- TURN: full lock at the reference speed, peak roll while it holds the corner.
                foreach (var st in AccelTo(v, RefSpeed)) yield return st;
                float turnPeak = 0f;
                for (int i = 0; i < 180; i++)
                {
                    v.Drive(1f, 1f, false);
                    yield return Ticks(1);
                    turnPeak = Mathf.Max(turnPeak, RollDegrees(v));
                }
                bool turnFlip = Inverted(v);

                // ---- KERB: straighten out, get back to speed, then hit the left side from below.
                for (int i = 0; i < 120; i++) { v.Drive(0f, 0f, false); yield return Ticks(1); }
                foreach (var st in AccelTo(v, RefSpeed)) yield return st;

                var up = v.GlobalTransform.Basis.Y;
                var com = v.ToGlobal(v.CenterOfMass);
                int left = 0;
                for (int i = 0; i < v.WheelCountForTest; i++) if (v.WheelLocalPosForTest(i).X < 0f) left++;
                if (left > 0)
                {
                    float per = v.Mass * KerbDeltaV / left;
                    for (int i = 0; i < v.WheelCountForTest; i++)
                    {
                        var lp = v.WheelLocalPosForTest(i);
                        if (lp.X < 0f) v.ApplyImpulse(up * per, v.ToGlobal(lp) - com);
                    }
                }
                float kerbPeak = 0f;
                for (int i = 0; i < 150; i++)
                {
                    v.Drive(1f, 0f, false);
                    yield return Ticks(1);
                    kerbPeak = Mathf.Max(kerbPeak, RollDegrees(v));
                }
                bool kerbFlip = Inverted(v);

                GD.Print($"[roll] {car,-9} mass {v.Mass,6:0} kg | TURN peak {turnPeak,5:0.0} deg{(turnFlip ? " INVERTED" : "")}"
                         + $" | KERB peak {kerbPeak,5:0.0} deg{(kerbFlip ? " INVERTED" : "")}");
                worstTurn = Mathf.Max(worstTurn, turnPeak);
                worstKerb = Mathf.Max(worstKerb, kerbPeak);
                if (turnFlip || kerbFlip) flipped.Add(car);

                v.QueueFree();
                yield return Ticks(5);
            }

            GD.Print($"[roll] WORST turn {worstTurn:0.0} deg, WORST kerb {worstKerb:0.0} deg, flipped: "
                     + (flipped.Count == 0 ? "none" : string.Join(", ", flipped)));

            // The only hard check. Peak roll is a number to COMPARE across the UG_ANTIROLL A/B; ending a
            // manoeuvre upside down is a failure on its own terms, at any tuning.
            T.Check($"no car ended a manoeuvre on its roof ({(flipped.Count == 0 ? "none" : string.Join(",", flipped))})",
                    flipped.Count == 0);
            // A car that never rolls AT ALL means the probe measured nothing -- a bar stiff enough to weld the
            // body to the road would pass the check above while making the cars feel dead.
            T.Check($"the manoeuvres actually disturbed the cars (worst kerb roll {worstKerb:0.0} deg)", worstKerb > 0.5f);
        }
    }
}
