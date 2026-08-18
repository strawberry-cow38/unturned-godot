using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE `heliphys` KNOBS (VoX 2026-08-17: "so we are essentially applying max speeds to the helis? Can we test
    // removing those please"). Two things need proving and they are different things:
    //
    //   1. THE DEFAULTS CHANGE NOTHING. A debug knob that quietly shifts the shipping flight model is worse than
    //      no knob, because every subsequent playtest is measuring the knob. So the first check is a control:
    //      untouched, terminal fall is still g/HeliHeaveDamp = 21.8 m/s.
    //   2. THE KNOBS ACTUALLY BITE. A toggle that parses, prints a confident status line and does nothing to the
    //      physics is exactly the failure that looks like success -- so each one is flown, not just set.
    //
    // Terminal fall is the cleanest probe available: engine off, no lift, no horizontal velocity, so the vertical
    // axis is the only thing acting and the answer is a closed form to compare against.
    public sealed class HeliPhysKnobTests : GameTest
    {
        public override string Name => "vehicle.heli_phys_knobs";
        public override double TimeoutSimSeconds => 400;

        // Dropped from high enough that terminal is actually reached: the time constant is 1/damping, so the
        // shaft-aligned case (half the damping at 45 deg) needs ~4.4 s per e-fold and a long way to fall.
        const float DropHeight = 4000f;
        const float SettleSeconds = 26f;
        const int SettleTicks = (int)(SettleSeconds / 0.02f);   // the rig steps at 50 Hz

        Vehicle DropAt(float pitchDeg)
        {
            var v = Vehicle.BuildByName("huey");
            World.AddChild(v);
            v.GlobalPosition = new Vector3(0f, DropHeight, 0f);
            v.EngineOn = false;          // no rotor, no lift: the vertical axis is heave damping and gravity only
            v.LinearVelocity = Vector3.Zero;
            v.AngularVelocity = Vector3.Zero;
            v.GlobalTransform = new Transform3D(
                new Basis(Vector3.Right, Mathf.DegToRad(pitchDeg)), v.GlobalPosition);
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            // TRY/FINALLY, because these statics are GLOBAL and the restore used to be the last two statements of
            // the iterator body -- i.e. on the SUCCESS PATH ONLY. TestHost abandons the enumerator on a watchdog
            // timeout or an exception, so a bail-out anywhere after the HeaveDampScale = 3f below would have left
            // it at 3 for the rest of the boot. Tests run in name order, so vehicle.heli_sling, heli_speed,
            // heli_turbulence and npc_heli all follow this one and would have flown in a world with a third of
            // the vertical resistance -- npc_heli's height check would then pass for entirely the wrong reason.
            try
            {
            // ---- 1. CONTROL: defaults are the shipping calibration.
            Vehicle.HeaveDampScale = 1f; Vehicle.DragScale = 1f;
            Vehicle.BackstopEnabled = true; Vehicle.ShaftAlignedDescent = false;

            var level = DropAt(0f);
            yield return Ticks(SettleTicks);
            float levelFall = -level.LinearVelocity.Y;
            float fallMaxSpec = level.FallMaxMps;   // read BEFORE the free; a freed node's properties are not safe to touch
            level.QueueFree();
            yield return Ticks(2);

            T.Check($"defaults are untouched: terminal fall {levelFall:0.#} m/s against the calibrated g/0.45 = 21.8",
                Mathf.Abs(levelFall - 21.8f) < 1.5f);

            // ---- 2. WORLD-ALIGNED IS ATTITUDE-BLIND. This is the behaviour VoX reported feeling ("it doesn't
            // feel like it falls fast enough" in a dive) and it is the CONTROL for the shaft check below: without
            // it, "the diving one fell faster" could just mean diving falls faster for some unrelated reason.
            var diveOff = DropAt(-45f);
            yield return Ticks(SettleTicks);
            float diveOffFall = -diveOff.LinearVelocity.Y;
            diveOff.QueueFree();
            yield return Ticks(2);

            T.Check($"world-aligned: a 45 deg dive falls the SAME as level ({diveOffFall:0.#} vs {levelFall:0.#} m/s) -- the artefact being fixed",
                Mathf.Abs(diveOffFall - levelFall) < 1.5f);

            // ---- 3. SHAFT-ALIGNED DESCENT BITES. cos^2(45) = 0.5, so half the resistance and ~2x terminal fall.
            Vehicle.ShaftAlignedDescent = true;
            var diveOn = DropAt(-45f);
            yield return Ticks(SettleTicks);
            float diveOnFall = -diveOn.LinearVelocity.Y;
            diveOn.QueueFree();
            yield return Ticks(2);

            T.Check($"shaft-aligned: the same 45 deg dive now falls at {diveOnFall:0.#} m/s, against {diveOffFall:0.#} world-aligned (expect ~2x, cos^2(45)=0.5)",
                diveOnFall > diveOffFall * 1.5f);

            // THE cos^2 TERM ITSELF, which the 45 deg case above NEVER TOUCHES. cos^2(45) = 0.500 is BELOW the
            // Huey's floor of 0.544, so Mathf.Max picks the floor and both checks above read the floor value --
            // they would pass unchanged with the cos^2 factor deleted outright. The floor takes over past 42.5 deg
            // on this airframe, so the entire 0-42.5 band, which is all normal flying, was untested and the drop
            // angle sat 2.5 deg the wrong side of the only line that mattered.
            // At 30 deg: cos^2 = 0.750, comfortably above the floor, so this reads the real term.
            var diveShallow = DropAt(-30f);
            yield return Ticks(SettleTicks);
            float shallowFall = -diveShallow.LinearVelocity.Y;
            diveShallow.QueueFree();
            yield return Ticks(2);

            float shallowExpect = 9.8f / (0.45f * 0.75f);   // 29.0 m/s
            T.Check($"the cos^2 term is what's acting at 30 deg (fall {shallowFall:0.#} m/s, cos^2=0.75 predicts {shallowExpect:0.#}; the floor would give {fallMaxSpec:0})",
                Mathf.Abs(shallowFall - shallowExpect) < 2.0f);
            // BOUNDED BOTH WAYS, AND THE UPPER BOUND IS THE ENVELOPE ITSELF. The previous version allowed
            // fallMaxSpec * 1.02, i.e. it PASSED on 40.8 m/s -- a state VehicleReplication rejects outright. A
            // check whose pass band includes the failure is not checking anything. The fall now targets
            // FallEnvelopeMargin * FallMax, so require it strictly inside the cap and still clearly engaged.
            T.Check($"terminal fall stays strictly INSIDE the MP envelope while still being raised ({diveOnFall:0.#} m/s vs HeliFallMax {fallMaxSpec:0})",
                diveOnFall < fallMaxSpec && diveOnFall > fallMaxSpec * 0.80f);

            GD.Print($"[HELIPHYS] level={levelFall:0.0}m/s  dive45_world={diveOffFall:0.0}m/s  dive45_shaft={diveOnFall:0.0}m/s  " +
                     $"ratio={diveOnFall / Mathf.Max(diveOffFall, 0.01f):0.00}x  fallMaxSpec={fallMaxSpec:0}m/s");

            // ---- 3b. INVERTED, UNDER POWER. The floor used to be derived from g alone, which is only right
            // while gravity is the only thing pushing down. Upside-down it is not: the tilt loss clamps at zero
            // so the rotor keeps 45 % of its thrust and it is applied along b.Y, which points at the ground.
            // The g-only floor let this reach 58 m/s against a 40 m/s FallMax -- a violation the feature
            // INTRODUCED, since the same attitude with the toggle off sits at 32.
            var inv = Vehicle.BuildByName("huey");
            World.AddChild(inv);
            inv.GlobalPosition = new Vector3(500f, DropHeight, 0f);
            inv.EngineOn = true; inv.DebugInstantStart = true; inv.SpawnRotorRunning();
            inv.DebugNoTurbulence = true;
            inv.LinearVelocity = Vector3.Zero; inv.AngularVelocity = Vector3.Zero;
            inv.GlobalTransform = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(137f)), inv.GlobalPosition);
            for (int i = 0; i < SettleTicks; i++) { inv.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float invFall = -inv.LinearVelocity.Y;
            GD.Print($"[HELIPHYS] inverted137_powered={invFall:0.0}m/s (FallMax {fallMaxSpec:0}; g-only floor gave 58)");
            T.Check($"inverted under power stays inside the fall envelope ({invFall:0.#} m/s against HeliFallMax {fallMaxSpec:0}) -- the server checks this with ZERO slack",
                invFall < fallMaxSpec);
            inv.QueueFree();
            yield return Ticks(2);

            // ---- 4. CLIMB IS UNTOUCHED BY THE SHAFT TOGGLE, which is the whole reason it is descent-only:
            // the same factor on the climb side busts HeliClimbMax, checked server-side with ZERO slack.
            var climber = Vehicle.BuildByName("huey");
            World.AddChild(climber);
            climber.GlobalPosition = new Vector3(200f, 500f, 0f);
            climber.EngineOn = true; climber.DebugInstantStart = true; climber.SpawnRotorRunning();
            climber.DebugNoTurbulence = true;
            for (int i = 0; i < 900; i++) { climber.DriveHeli(1f, 0f, 0f, 0f, 1.0 / 60.0); yield return Ticks(1); }
            float climbRate = climber.LinearVelocity.Y;
            T.Check($"shaft toggle does NOT touch the climb side: terminal climb {climbRate:0.#} m/s still inside HeliClimbMax {climber.ClimbMaxMps:0}",
                climbRate > 0f && climbRate <= climber.ClimbMaxMps);
            climber.QueueFree();
            yield return Ticks(2);

            // ---- 5. THE CONSOLE VERB IS REACHABLE. NoArgVerbs has silently swallowed a bare command before --
            // the guard printed a generic usage line and it read as "the console doesn't know that command".
            Vehicle.HeaveDampScale = 3f;   // something obviously not the default, so `reset` has work to do
            var console = new DevConsole();
            World.AddChild(console);
            yield return Ticks(2);
            console.RunForTest("heliphys");                 // bare: must report, not error
            console.RunForTest("heliphys reset");
            yield return Ticks(2);

            // `reset` means the SHIPPING default, not "everything off" -- shaft-aligned descent has been the
            // default since VoX asked for it, so a reset that turned it off would quietly undo his setting.
            T.Check($"`heliphys reset` restores the shipping calibration (heave x{Vehicle.HeaveDampScale:0.##}, shaft {Vehicle.ShaftAlignedDescent} -- expected on)",
                Mathf.Abs(Vehicle.HeaveDampScale - 1f) < 0.001f && Vehicle.ShaftAlignedDescent
                && Mathf.Abs(Vehicle.DragScale - 1f) < 0.001f && Vehicle.BackstopEnabled);

            }
            finally
            {
                // Restore the SHIPPING DEFAULTS on every exit path, not just the happy one.
                Vehicle.HeaveDampScale = 1f; Vehicle.DragScale = 1f;
                Vehicle.BackstopEnabled = true; Vehicle.ShaftAlignedDescent = true;
            }
        }
    }
}
