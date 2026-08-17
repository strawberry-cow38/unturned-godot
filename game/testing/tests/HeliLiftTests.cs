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
            for (int i = 0; i < 220; i++)
            {
                low.DriveHeli(0f, 0f, 0f, 0f, 0.02); high.DriveHeli(0f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
            }
            float lowGe = low.DebugGroundEffect, highGe = high.DebugGroundEffect;
            T.Check($"a helicopter near the deck is in ground effect (factor {lowGe:0.###} at {low.GlobalPosition.Y:0.#} m)",
                lowGe > 1.02f);
            T.Check($"...and one at altitude is not ({highGe:0.###} at {high.GlobalPosition.Y:0} m)",
                Mathf.IsEqualApprox(highGe, 1f, 0.001f));
            T.Check($"...so the effect is the HEIGHT, not the airframe ({lowGe:0.###} vs {highGe:0.###})",
                lowGe > highGe + 0.02f);
            // It must also DECAY, not merely be on or off: a step function would pass all three above.
            T.Check($"...and it decays with height rather than switching (factor {lowGe:0.###} < the 1.34 hard floor at R/2)",
                lowGe < 1.34f);

            // ---- 2. IT DECAYS WITH HEIGHT, rather than being a switch. Two readings inside the effect, a few
            // metres apart: the higher one must be strictly weaker. A single "it is on near the ground"
            // reading passes on a step function, which is the shape this most plausibly gets wrong, and the
            // 1.34 bound above only catches the clamp -- not whether anything varies between the clamp and 1.
            var mid = Spawn(World, "huey", new Vector3(600f, 6.5f, 0f));
            yield return Ticks(30);
            float midGe = mid.DebugGroundEffect;
            T.Check($"ground effect falls off with height ({lowGe:0.###} at {low.GlobalPosition.Y:0.#} m vs {midGe:0.###} at {mid.GlobalPosition.Y:0.#} m)",
                midGe < lowGe - 0.01f && midGe > 1.001f);

            // ---- 3. TRANSLATIONAL LIFT: the same airframe makes more thrust moving than hovering. Measured
            // on the ROTOR's contribution with gravity and damping removed, at altitude so ground effect is
            // out of the picture and the two readings differ in speed alone.
            var etl = Spawn(World, "huey", new Vector3(1000f, 600f, 0f));
            for (int i = 0; i < 260; i++) { etl.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
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
            // BOUNDED. The gain is capped at 0.08 for a reason (see EtlGain), so a check that only asserts
            // "more" would pass on a value large enough to break the hands-off sink below.
            T.Check($"...by the intended margin, not more ({translating / hover:0.###} against a designed 1.08)",
                translating / hover < 1.10f);

            // ---- 4. AND IT DOES NOT DELETE THE HANDS-OFF SINK. The margin here is genuinely thin -- hands-off
            // lift is 9.016 and ETL raises it to 9.74 against a g of 9.8 -- so this is the check that fails
            // first if anyone nudges EtlGain, and it fails for a reason that has nothing to do with speed.
            var sink = Spawn(World, "huey", new Vector3(1400f, 600f, 0f));
            for (int i = 0; i < 260; i++) { sink.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            var sfwd = new Vector3(-sink.GlobalTransform.Basis.Z.X, 0f, -sink.GlobalTransform.Basis.Z.Z).Normalized();
            sink.LinearVelocity = new Vector3(sfwd.X * 20f, 0f, sfwd.Z * 20f);
            for (int i = 0; i < 200; i++) { sink.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"hands off AT SPEED still sinks -- ETL does not turn the spring-back into a climb ({sink.LinearVelocity.Y:0.##} m/s)",
                sink.LinearVelocity.Y < 0f);

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
            for (int i = 0; i < 260; i++) { dead.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
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
