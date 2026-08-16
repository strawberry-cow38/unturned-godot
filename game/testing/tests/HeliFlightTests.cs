using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // ROTARY-WING FLIGHT (VoX 2026-08-15: a Rust-style minicopter, plus a Huey variant).
    //
    // The claims worth pinning are the ones a screenshot cannot tell apart. "It moved upward" passes on a
    // machine with no rotor sim at all if the spawn drop happens to bounce, and it passes on one that
    // ignores collective entirely and just floats. So each check below is written against something that
    // has to be FALSE in the broken version:
    //
    //   - it rests ON the ground rather than sinking through it or launching off it (the thing a wheelless
    //     VehicleBody3D most plausibly gets wrong, since the shared settle test counts wheel contacts and a
    //     helicopter has none);
    //   - a cold rotor cannot lift even at full collective -- thrust goes as spool SQUARED, so "engine on"
    //     and "able to fly" are deliberately not the same instant;
    //   - lift is along the BODY up axis, which is the entire Rust feel: tilting is how you translate. The
    //     test tilts the airframe and asserts it accelerates the way it is LEANING, not the way it is facing.
    public sealed class HeliFlightTests : GameTest
    {
        public override string Name => "vehicle.heli_flight";
        public override double TimeoutSimSeconds => 120;

        static Vehicle Spawn(Node world, string name, Vector3 at)
        {
            var v = Vehicle.BuildByName(name);
            world.AddChild(v);
            v.GlobalPosition = at;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            // ---- 1. IT IS A HELICOPTER AND IT IS BUILT
            var h = Spawn(World, "minicopter", new Vector3(0f, 1.6f, 0f));
            T.Check("the minicopter spec builds as a rotary wing", h.IsHeli);
            T.Check($"...with a spun-down rotor at rest (spool {h.RotorSpool:0.###})", Mathf.IsZeroApprox(h.RotorSpool));
            T.Check("...and both rotor pivots exist", h.FindChild("Rotor", false, false) != null && h.FindChild("TailRotor", false, false) != null);
            // NOT a car: the wheeled path must be gone, or it inherits brakes/steering that make no sense.
            T.Check("...and carries no wheels", h.GetChildCount() > 0 && h.FindChild("Wheel0", true, false) == null);

            // ---- 2. IT SETTLES ON ITS SKIDS. Dropped from 1.6 m it must come to rest ABOVE the ground plane
            // and STAY there -- not sink through (no wheel colliders to stop it) and not get flung.
            yield return Ticks(180);
            float rest = h.GlobalPosition.Y;
            T.Check($"it settles above the ground rather than sinking through ({rest:0.###} m)", rest > 0.2f && rest < 3f);
            yield return Ticks(120);
            T.Check($"...and stays put once settled (drift {Mathf.Abs(h.GlobalPosition.Y - rest):0.####} m over 120 ticks)",
                Mathf.Abs(h.GlobalPosition.Y - rest) < 0.05f);

            // ---- 3. A COLD ROTOR CANNOT FLY. Full collective with the engine off must not lift it: thrust
            // scales with spool SQUARED, so this is the check that separates "powered" from "switched on".
            for (int i = 0; i < 60; i++) { h.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"full collective with the engine OFF does not lift it ({h.GlobalPosition.Y - rest:+0.###;-0.###;0} m)",
                h.GlobalPosition.Y < rest + 0.15f);
            T.Check($"...because the rotor never spooled (spool {h.RotorSpool:0.###})", h.RotorSpool < 0.05f);

            // ---- 4. IT CLIMBS. Engine on, hold the collective up, and it must gain real altitude.
            h.EngineOn = true;   // fuel is NOT cheated on: the specs carry 200/2000 units against a ~1.4/s burn,
                                 // which outlasts this whole suite. Vehicle.InfiniteFuel is STATIC, and setting a
                                 // static cheat flag here would leak into every later test in the same boot.
            for (int i = 0; i < 400; i++) { h.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float climbed = h.GlobalPosition.Y - rest;
            T.Check($"the rotor spools up under power (spool {h.RotorSpool:0.###})", h.RotorSpool > 0.9f);
            T.Check($"and it climbs on collective ({climbed:0.##} m in 8 s)", climbed > 6f);
            // Bounded: a thrust-to-weight bug (or gravity not applying) reads as "it climbs" too, just far
            // too fast. 8 s of net ~7 m/s^2 is ~220 m; anything past that is not a helicopter.
            T.Check($"...at a plausible rate, not rocketing ({climbed:0.##} m)", climbed < 260f);

            // ---- 5. CUTTING COLLECTIVE DESCENDS. Sticky throttle: S has to wind it back down.
            //
            // Asserted on VERTICAL VELOCITY, not on altitude after a fixed window. The first cut of this used
            // altitude and failed at +19.62 m while the model was working perfectly: 8 s of climb leaves it
            // doing ~20 m/s upward, and cutting power does not teleport that momentum away -- it coasts up for
            // another second and a half before it starts down. "Is it descending" is the actual claim; "has it
            // ended up lower than it started" is a claim about how long I happened to wait.
            for (int i = 0; i < 240; i++) { h.DriveHeli(-1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"the collective winds back down to idle ({h.DebugHeliInput.X:0.##})", h.DebugHeliInput.X < 0.05f);
            T.Check($"...and it is descending under gravity ({h.LinearVelocity.Y:0.##} m/s)", h.LinearVelocity.Y < -1f);
            float top = h.GlobalPosition.Y;
            yield return Ticks(150);
            T.Check($"...losing real altitude ({h.GlobalPosition.Y - top:0.##} m over 3 s)", h.GlobalPosition.Y < top - 5f);

            // ---- 6. THE STICKY THROTTLE. Rust's collective HOLDS where you left it; it is not a held button.
            // Release everything and it must keep flying on the power already set, not fall out of the sky.
            var fresh = Spawn(World, "minicopter", new Vector3(40f, 30f, 0f));
            fresh.EngineOn = true;
            for (int i = 0; i < 260; i++) { fresh.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float held = fresh.DebugHeliInput.X;
            for (int i = 0; i < 60; i++) { fresh.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"collective holds its setting with no input ({held:0.##} -> {fresh.DebugHeliInput.X:0.##})",
                Mathf.IsEqualApprox(fresh.DebugHeliInput.X, held, 0.01f));

            // ---- 7. LIFT FOLLOWS THE AIRFRAME, NOT THE WORLD. Bank it hard and hold: it must accelerate
            // SIDEWAYS, toward the side it is leaning. This is what makes it a helicopter rather than a
            // drone that strafes -- and a model applying thrust along world-up passes every check above but
            // fails this one, which is why it is here.
            var lean = Spawn(World, "minicopter", new Vector3(-60f, 60f, 0f));
            lean.EngineOn = true;
            for (int i = 0; i < 260; i++) { lean.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            lean.LinearVelocity = Vector3.Zero;
            float x0 = lean.GlobalPosition.X;
            for (int i = 0; i < 200; i++) { lean.DriveHeli(0f, 0f, 0f, 0.45f, 0.02); yield return Ticks(1); }
            // Measured off the BODY UP AXIS, not off an Euler angle. Euler Z is a mirror waiting to happen --
            // it depends on extraction order and on Godot's roll sign convention, and the first cut of this
            // check asserted the wrong one and failed against a model that was behaving correctly. up.X is the
            // quantity the flight model literally multiplies thrust by, so comparing against it is comparing
            // against the mechanism instead of against my memory of a convention.
            float leanX = lean.GlobalTransform.Basis.Y.X;
            float moved = lean.GlobalPosition.X - x0;
            T.Check($"roll input banks the airframe (up axis tilted {Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(lean.GlobalTransform.Basis.Y.Y, -1f, 1f))):0.#} deg)",
                Mathf.Abs(leanX) > 0.15f);
            // The SIGN is the claim: it slides toward the low side of the bank. Magnitude alone would pass on a
            // machine that drifts for any reason at all, including thrust applied along WORLD up plus a nudge.
            T.Check($"...and it accelerates toward the bank, not away ({moved:+0.##;-0.##;0} m, up.X {leanX:+0.##;-0.##;0})",
                Mathf.Sign(moved) == Mathf.Sign(leanX) && Mathf.Abs(moved) > 1.5f);

            // ---- 8. YAW. A/D must turn the nose, and it is a separate axis from roll.
            var spin = Spawn(World, "minicopter", new Vector3(-120f, 60f, 0f));
            spin.EngineOn = true;
            for (int i = 0; i < 260; i++) { spin.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float yaw0 = spin.GlobalTransform.Basis.GetEuler().Y;
            for (int i = 0; i < 120; i++) { spin.DriveHeli(0f, 1f, 0f, 0f, 0.02); yield return Ticks(1); }
            float yawed = Mathf.RadToDeg(Mathf.Wrap(spin.GlobalTransform.Basis.GetEuler().Y - yaw0, -Mathf.Pi, Mathf.Pi));
            T.Check($"yaw input turns the nose ({yawed:0.#} deg in 2.4 s)", Mathf.Abs(yawed) > 15f);

            // ---- 8b. AXIS CONVENTIONS, pinned. An inverted axis flies perfectly well -- it climbs, banks,
            // turns and translates -- it is just backwards, so every check above passes on a machine with
            // pitch upside down. These name the intended direction in terms of the body basis:
            //   pitch +1 = nose UP    (forward vector -Z rises)
            //   roll  +1 = bank RIGHT (up vector tilts toward +X)
            //   yaw   +1 = nose RIGHT
            var conv = Spawn(World, "minicopter", new Vector3(-200f, 80f, 0f));
            conv.EngineOn = true;
            for (int i = 0; i < 260; i++) { conv.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            for (int i = 0; i < 90; i++) { conv.DriveHeli(0f, 0f, 1f, 0f, 0.02); yield return Ticks(1); }
            float noseY = -conv.GlobalTransform.Basis.Z.Y;   // forward is -Z; its Y component is how far the nose points up
            T.Check($"pitch +1 raises the nose ({noseY:+0.##;-0.##;0} of forward.Y)", noseY > 0.1f);

            var conv2 = Spawn(World, "minicopter", new Vector3(-260f, 80f, 0f));
            conv2.EngineOn = true;
            for (int i = 0; i < 260; i++) { conv2.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            for (int i = 0; i < 90; i++) { conv2.DriveHeli(0f, 0f, 0f, 1f, 0.02); yield return Ticks(1); }
            T.Check($"roll +1 banks right ({conv2.GlobalTransform.Basis.Y.X:+0.##;-0.##;0} of up.X)", conv2.GlobalTransform.Basis.Y.X > 0.1f);

            var conv3 = Spawn(World, "minicopter", new Vector3(-320f, 80f, 0f));
            conv3.EngineOn = true;
            for (int i = 0; i < 260; i++) { conv3.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            Vector3 fwd0 = -conv3.GlobalTransform.Basis.Z;
            for (int i = 0; i < 90; i++) { conv3.DriveHeli(0f, 1f, 0f, 0f, 0.02); yield return Ticks(1); }
            Vector3 fwd1 = -conv3.GlobalTransform.Basis.Z;
            // turning RIGHT = the forward vector swings clockwise seen from above = fwd0 x fwd1 points DOWN
            float turnSign = fwd0.Cross(fwd1).Y;
            T.Check($"yaw +1 turns the nose right ({turnSign:+0.###;-0.###;0})", turnSign < -0.02f);

            // ---- 9. THE HUEY flies the same model off its own spec + the real retail mesh.
            var huey = Spawn(World, "huey", new Vector3(120f, 3f, 0f));
            T.Check("the huey spec builds as a rotary wing", huey.IsHeli);
            T.Check($"...with the retail airframe, not the procedural frame ({huey.DisplayName})", huey.DisplayName == "Huey");
            huey.EngineOn = true;
            float hy = huey.GlobalPosition.Y;
            for (int i = 0; i < 400; i++) { huey.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"the huey climbs too ({huey.GlobalPosition.Y - hy:0.##} m)", huey.GlobalPosition.Y - hy > 4f);

            yield break;
        }
    }
}
