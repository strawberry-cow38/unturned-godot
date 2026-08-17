using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE TWO LIFT MULTIPLIERS: effective translational lift, and ground effect.
    //
    // Both make the rotor produce more thrust for the same collective, and both are easy to "verify" with a
    // check that cannot fail. "It climbs near the ground" passes on a machine with plenty of thrust and no
    // ground effect at all; "it accelerates as it speeds up" passes on any helicopter. So every check below is
    // written as a COMPARISON between two states of the same airframe, where only the quantity under test
    // differs -- and the two that matter most are the ones about what the multipliers must NOT do.
    //
    // The ordering check is the reason this suite exists. Both multipliers are applied ABOVE the dead-tail
    // clamp in StepHeli, because that clamp is an absolute ceiling encoding a signed-off rule ("dead tail
    // should also have the same effect as killmain of preventing gaining height"). Multiply after it and a
    // dead-tail machine climbs straight back through it -- in ground effect, precisely when the pilot is
    // closest to walking away. That is a one-line ordering mistake that no magnitude check would notice.
    //
    // TEETH CONFIRMED, not assumed: with the multipliers moved below the clamp instead of above it, the
    // dead-tail machine climbs +2.3 m and is still going up at +0.24 m/s at the end of the window, and
    // exactly the two ordering checks go red while everything else here stays green.
    public sealed class HeliLiftTests : GameTest
    {
        public override string Name => "vehicle.heli_lift";
        public override double TimeoutSimSeconds => 240;

        static Vehicle Spawn(Node world, string name, Vector3 at)
        {
            var v = Vehicle.BuildByName(name);
            world.AddChild(v);
            v.GlobalPosition = at;
            v.DebugNoTurbulence = true;
            v.EngineOn = true;
            return v;
        }

        // Vertical acceleration this tick, with gravity and heave damping backed out, so the number is the
        // rotor's contribution alone. Comparing raw climb rates instead would fold in whatever vertical speed
        // the machine happened to already have.
        static float RotorLift(Vehicle v, float prevVy, float dt) =>
            (v.LinearVelocity.Y - prevVy) / dt + 9.8f + 0.45f * prevVy;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            // ---- 1. GROUND EFFECT EXISTS, AND ONLY NEAR THE GROUND. A control pair on one airframe: the
            // same machine, same collective, same attitude, at two heights. Either reading alone is
            // meaningless -- it is the DIFFERENCE that is the claim.
            var low = Spawn(World, "huey", new Vector3(0f, 2.2f, 0f));
            var high = Spawn(World, "huey", new Vector3(200f, 400f, 0f));
            for (int i = 0; i < 460; i++)
            {
                low.DriveHeli(0f, 0f, 0f, 0f, 0.02); high.DriveHeli(0f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
            }
            float lowGe = low.DebugGroundEffect, highGe = high.DebugGroundEffect;
            // THE HIGH SUBJECT NEEDS A POSITIVE CONTROL BEFORE ITS READING MEANS ANYTHING. DebugGroundEffect
            // is a cached field whose INITIAL value is 1.0, so "it reads 1.0 at altitude" is also what a
            // machine whose StepHeli never ran would report -- and so would a broken probe, a null space
            // state, or a ray of any length at all, since Cheeseman-Bennett tends to 1.0 anyway. Spool is only
            // non-zero if the rotor sim actually ran on THIS body, so it distinguishes "correctly found
            // nothing" from "never looked".
            T.Check($"the high subject's flight model really ran, so its reading is a measurement (spool {high.RotorSpool:0.###})",
                high.RotorSpool > 0.95f);
            T.Check($"a helicopter near the deck is in ground effect (factor {lowGe:0.###} at {low.GlobalPosition.Y:0.#} m)",
                lowGe > 1.02f);
            T.Check($"...and one at altitude is not ({highGe:0.###} at {high.GlobalPosition.Y:0} m)",
                Mathf.IsEqualApprox(highGe, 1f, 0.001f));

            // ---- 2. AND IT DECAYS WITH HEIGHT rather than switching on and off. Two readings INSIDE the
            // effect: a step function passes every check above and fails this one.
            //
            // Driven, not merely spawned and left. The first cut called no DriveHeli on this subject, so it
            // free-fell through the 30 ticks and reported the factor at ~4.7 m while the comment described the
            // 6.5 m it was spawned at -- the assertion still held, but the spacing between the two readings
            // was set by an undeclared fall rather than by the test.
            var mid = Spawn(World, "huey", new Vector3(600f, 6.5f, 0f));
            for (int i = 0; i < 30; i++) { mid.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float midGe = mid.DebugGroundEffect;
            T.Check($"ground effect falls off with height ({lowGe:0.###} at {low.GlobalPosition.Y:0.#} m vs {midGe:0.###} at {mid.GlobalPosition.Y:0.#} m)",
                midGe < lowGe - 0.01f && midGe > 1.001f);
            // Bounded above by the R/2 clamp, which is the pole guard. Now that the probe measures from the
            // ROTOR HUB rather than the fuselage origin, a parked machine no longer pins to this clamp -- it
            // reports its own geometry -- so this bound stops being trivially satisfied and starts meaning
            // something.
            T.Check($"...and never exceeds the Cheeseman-Bennett clamp ({lowGe:0.###} against the 1.334 pole guard at R/2)",
                lowGe < 1.334f);

            // ---- 3. TRANSLATIONAL LIFT: the same airframe makes more thrust moving than hovering. Measured
            // on the ROTOR's contribution with gravity and damping removed, at altitude so ground effect is
            // out of the picture and the two readings differ in speed alone.
            var etl = Spawn(World, "huey", new Vector3(1000f, 600f, 0f));
            for (int i = 0; i < 460; i++) { etl.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            etl.LinearVelocity = new Vector3(0f, etl.LinearVelocity.Y, 0f);
            yield return Ticks(2);
            float pv = etl.LinearVelocity.Y;
            etl.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1);
            float hover = RotorLift(etl, pv, 0.02f);
            // Only the horizontal is written, so the 3-D speed barely moves and StepHeli's crash detector --
            // which measures full 3-D deceleration -- cannot fire on the assignment.
            var fwd = new Vector3(-etl.GlobalTransform.Basis.Z.X, 0f, -etl.GlobalTransform.Basis.Z.Z).Normalized();
            etl.LinearVelocity = new Vector3(fwd.X * 20f, etl.LinearVelocity.Y, fwd.Z * 20f);
            yield return Ticks(2);
            pv = etl.LinearVelocity.Y;
            etl.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1);
            float translating = RotorLift(etl, pv, 0.02f);
            T.Check($"translating out of the hover makes more lift ({translating:0.##} vs {hover:0.##} m/s^2, ratio {translating / hover:0.###})",
                translating > hover * 1.02f);
            // BOUNDED, and the bound is TIGHT on purpose. EtlGain is 0.05, so the designed ratio is 1.05. An
            // earlier version of this check read "< 1.10" against a comment claiming a designed 1.08 -- which
            // admitted 1.08 itself, the exact value the EtlGain docstring exists to rule out. A bound whose
            // job is to exclude a value has to actually exclude it.
            T.Check($"...by the intended margin, not more ({translating / hover:0.###} against a designed 1.05)",
                translating / hover < 1.07f);

            // ---- 4. AND IT DOES NOT DELETE THE HANDS-OFF SINK. Hands-off lift is 9.016; ETL raises it to
            // 9.467 against a g of 9.8, for a settled sink of 0.74 m/s. This is the check that goes red first
            // if anyone raises EtlGain, and it goes red for a reason that has nothing to do with speed.
            //
            // THE WINDOW IS 10 s BECAUSE 4 s MEASURED A TRANSIENT. Zeroing vy while the collective is still at
            // full opens the window with about 1.4 m/s of climb, which decays on a 1/HeliHeaveDamp = 2.2 s
            // time constant -- so a 4 s window still carried ~23 % of it and reported +0.14 m/s on a machine
            // whose steady state was a sink. That misreading is what set EtlGain to 0.05 originally, for a
            // reason that turned out to be fiction. 10 s is 4.5 time constants, leaving ~1 %. The check now
            // asserts a MEANINGFUL sink rather than merely a negative one, so it cannot be passed by a
            // machine that is technically descending at a millimetre per second.
            var sink = Spawn(World, "huey", new Vector3(1400f, 600f, 0f));
            for (int i = 0; i < 460; i++) { sink.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            var sfwd = new Vector3(-sink.GlobalTransform.Basis.Z.X, 0f, -sink.GlobalTransform.Basis.Z.Z).Normalized();
            sink.LinearVelocity = new Vector3(sfwd.X * 20f, 0f, sfwd.Z * 20f);
            for (int i = 0; i < 500; i++) { sink.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"hands off AT SPEED still sinks -- ETL does not turn the spring-back into a hover ({sink.LinearVelocity.Y:0.##} m/s, flat {new Vector3(sink.LinearVelocity.X, 0f, sink.LinearVelocity.Z).Length():0.#} m/s)",
                sink.LinearVelocity.Y < -0.3f);

            // ---- 4b. A PARKED MACHINE STAYS PARKED, engine idling, sitting in its own ground effect.
            //
            // This is the invariant ground effect most plausibly breaks, and it did: at full strength the
            // effect multiplies the hands-off collective's 9.016 up to 12.0 against a g of 9.8, so the
            // helicopter you left on the pad slowly flies away. It surfaced as a failure in the TURBULENCE
            // test -- whose grounded subject stopped being grounded -- and a check that only catches this
            // sideways, in a suite about something else, is a sensor rather than an assertion. So it is
            // stated here, where the mechanism lives.
            var idle = Spawn(World, "minicopter", new Vector3(2200f, 1.2f, 0f));
            for (int i = 0; i < 400; i++) { idle.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"...and it is genuinely in ground effect while doing so (factor {idle.DebugGroundEffect:0.###})",
                idle.DebugGroundEffect > 1.05f);
            T.Check($"an idling helicopter sits on the ground instead of floating off it ({idle.GlobalPosition.Y:0.##} m, {idle.LinearVelocity.Y:+0.##;-0.##;0} m/s, contacts {idle.GetContactCount()})",
                idle.GetContactCount() > 0 && idle.LinearVelocity.Y < 0.05f);

            // ---- 5. THE ORDERING TEETH. A dead tail must prevent gaining height, and ground effect must not
            // buy it back. Full collective, in ground effect, tail dead: it may sink slowly, it may hold, but
            // it must not climb. Reverse the two lines in StepHeli that put the multipliers above the clamp
            // and this is the check that goes red.
            // SPOOLED AT IDLE, NOT AT FULL COLLECTIVE, and the velocity is zeroed before the window opens.
            // The first cut did neither and failed against correct code twice over: 5 s of full collective
            // carried the machine ~10 m up and clean OUT of ground effect (factor back to 1.000, so the test
            // was not testing ground effect at all), and it entered the measurement window with several m/s
            // of upward momentum, which a clamped rotor bleeds off over about a second -- so it coasted up
            // 9.87 m while behaving exactly as specified. heli_flight's descent test carries the same warning
            // in its own comments; measuring displacement across a momentum change measures the momentum.
            var dead = Spawn(World, "huey", new Vector3(1800f, 2.2f, 0f));
            for (int i = 0; i < 460; i++) { dead.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            dead.DamageTailRotor(1e9f);
            dead.LinearVelocity = Vector3.Zero;
            yield return Ticks(2);
            float y0 = dead.GlobalPosition.Y;
            T.Check($"the tail rotor is dead ({dead.TailRotorHealth:0})", dead.TailRotorDead);
            T.Check($"...and it IS in ground effect at {y0:0.#} m, so this is a real test of the ordering (factor {dead.DebugGroundEffect:0.###})",
                dead.DebugGroundEffect > 1.02f);
            for (int i = 0; i < 250; i++) { dead.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"a dead-tail machine cannot climb out on ground effect ({dead.GlobalPosition.Y - y0:+0.##;-0.##;0} m over 5 s at full collective, factor {dead.DebugGroundEffect:0.###})",
                dead.GlobalPosition.Y <= y0 + 0.5f);
            T.Check($"...and is not even climbing at the end of it ({dead.LinearVelocity.Y:+0.##;-0.##;0} m/s)",
                dead.LinearVelocity.Y <= 0.05f);

            yield break;
        }
    }
}
