using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE PLANE HAD NO TESTS AT ALL until this file, which is why both of the 2026-09-06 bugs shipped:
    // master, "planes seem to plummet out of the sky when flying inverted, or after gaining significant vertical
    // downward speed, which should be transferred to horizontal glide, but isnt ... maneuvers that should be
    // possible just drop you out of the sky."
    //
    // Both are read off the LIFT FORCE rather than off the eventual crash, because both are invisible for a
    // while from the outside: a plane making a quarter of its lift still flies level and only fails when you ask
    // it to recover, and an inverted plane being pushed downward looks exactly like an inverted plane diving.
    //
    // Each check is built as a CONTROL PAIR — two flights differing in one thing — so it cannot pass by the
    // feature doing nothing, and it cannot pass by the feature doing something arbitrary.
    public sealed class PlaneDiveTests : GameTest
    {
        public override string Name => "vehicle.plane_dive";
        public override double TimeoutSimSeconds => 120;

        const float Speed = 60f;   // well above the 16 m/s target, so liftFrac is at its 1.3 clamp in both attitudes

        /// <summary>Hold an attitude with the velocity ALONG THE NOSE (so angle of attack is ~0 and cannot itself
        /// explain a difference), run one tick, and report the lift the wing made. The only thing that differs
        /// between calls is where that nose is pointing in the world.</summary>
        IEnumerable<Step> LiftAt(float pitchDeg, float rollDeg, float speed, System.Action<Vector3> report)
        {
            var v = Vehicle.BuildByName("plane");
            World.AddChild(v);
            v.GlobalPosition = new Vector3(0f, 3000f, 0f);
            v.EngineOn = true; v.DebugInstantStart = true;
            var basis = new Basis(Vector3.Right, Mathf.DegToRad(pitchDeg)) * new Basis(Vector3.Forward, Mathf.DegToRad(rollDeg));
            v.GlobalTransform = new Transform3D(basis, v.GlobalPosition);
            v.AngularVelocity = Vector3.Zero;
            v.LinearVelocity = -basis.Z * speed;
            yield return Ticks(1);   // let _Ready/_PhysicsProcess settle -- the vehicle steps ITSELF, so this tick is not inert

            // RE-PIN THE STATE IMMEDIATELY BEFORE THE MEASURED TICK. The settling tick above is a full physics
            // step: gravity acts, the vehicle runs its own StepPlane, and the attitude and velocity I set have
            // both moved by the time anything is read. The first version of this test measured after that drift
            // and reported the DIVE making 70 % MORE lift than level flight -- not because of the fix, but
            // because the two attitudes had drifted to different angles of attack (~2.9 deg against ~0.2 deg).
            // The premise "same speed, same AoA, only direction differs" has to be true AT THE MOMENT OF
            // MEASUREMENT, not at the moment of setup.
            v.GlobalTransform = new Transform3D(basis, v.GlobalPosition);
            v.LinearVelocity = -basis.Z * speed;   // straight down the nose: AoA == 0 in every attitude below
            v.AngularVelocity = Vector3.Zero;
            v.DrivePlane(0f, 0f, 0f, 0f, 0.02);
            yield return Ticks(1);
            report(v.DebugPlaneLiftG);
            v.QueueFree();
            yield return Ticks(2);
        }

        public override IEnumerable<Step> Run()
        {
            Vector3 level = Vector3.Zero, dive = Vector3.Zero, upright = Vector3.Zero, inverted = Vector3.Zero;

            // ---- 1. SPEED BOUGHT BY A DIVE MUST BUY LIFT.
            //
            // ISOLATED ON SPEED ALONE: identical attitude, identical AoA, only the airspeed differs. My first
            // version of this compared a DIVE against LEVEL at one speed and it was measuring the wrong thing --
            // the dive came out 70 % higher, which is bank compensation reacting to b.Y.Y falling as the nose
            // pitches down, nothing to do with the bug. And with velocity along the nose, the nose component
            // EQUALS the true airspeed, so the change under test was invisible in that configuration.
            //
            // The real defect was a ceiling: liftFrac was min(v/target, 1.3)^2, so the otter's lift stopped
            // growing at 20.8 m/s and a 60 m/s dive had the lift authority of a 21 m/s cruise. Below is the
            // control pair that shows it -- 20 m/s is under the old ceiling, 45 m/s is far above it.
            foreach (var st in LiftAt(0f, 0f, 20f, x => level = x)) yield return st;
            foreach (var st in LiftAt(0f, 0f, 45f, x => dive = x)) yield return st;

            T.Check($"the wing makes lift at 20 m/s at all ({level.Length():0.###} g)", level.Length() > 0.1f);
            T.Check($"more than doubling the airspeed buys substantially more lift ({dive.Length():0.###} g at 45 m/s vs {level.Length():0.###} g at 20 m/s)",
                dive.Length() > level.Length() * 1.5f);
            // The old ceiling made these two nearly equal (1.5625 vs 1.69 -- an 8 % difference for 2.25x the
            // speed). Asserted explicitly so restoring a low cap fails here rather than only in the air.
            T.Check($"...and NOT merely the 8 % the old 1.3 ceiling allowed ({dive.Length() / Mathf.Max(0.001f, level.Length()):0.##}x)",
                dive.Length() / Mathf.Max(0.001f, level.Length()) > 1.5f);

            // ---- 2. INVERTED MUST NOT BE PUSHED HARDER THAN UPRIGHT.
            // Bank compensation multiplies lift by up to 1/cos(bank) to hold altitude in a turn. Inverted, the
            // old clamp floored a NEGATIVE cosine at +0.2, so it multiplied by 4 -- along body-up, which upside
            // down is straight at the ground. Rolling inverted did not cost lift, it bought four gravities of
            // downforce. The magnitudes must match; only the direction may flip.
            foreach (var st in LiftAt(0f, 0f, Speed, x => upright = x)) yield return st;
            foreach (var st in LiftAt(0f, 180f, Speed, x => inverted = x)) yield return st;

            // NOT exact equality, and the asymmetry is real rather than slop: the measured tick includes one
            // step of gravity, which gives an UPRIGHT wing a small positive angle of attack and an INVERTED one
            // a small negative angle. ~10 % apart is the physics being right. What must not happen is
            // AMPLIFICATION -- the old clamp made inverted ~4x upright, so this bound has teeth at 1.15 and
            // asserting 5 % would have been demanding a symmetry that ought not to exist.
            float ratio = inverted.Length() / Mathf.Max(0.001f, upright.Length());
            T.Check($"inverted is not amplified -- comparable magnitude, not multiplied ({inverted.Length():0.###} g vs {upright.Length():0.###} g, {ratio:0.##}x)",
                ratio < 1.15f);
            T.Check($"...and it is not AMPLIFIED downward -- no bank compensation past knife-edge ({inverted.Y:0.###} g)",
                inverted.Y > -upright.Length() * 1.05f);
            // Two-sided: it must still genuinely point downward when you are upside down, or the fix has just
            // deleted the physics rather than corrected the clamp.
            T.Check($"...but it DOES point down inverted, because the wing is upside down ({inverted.Y:0.###} g)",
                inverted.Y < 0f);

            yield break;
        }
    }
}
