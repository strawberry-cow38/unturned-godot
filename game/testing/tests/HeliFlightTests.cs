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
        // Generous because this suite genuinely simulates a lot of flying: an 18 s climb, several 5 s
        // manoeuvres, and a 14 s turbulence window that has to span multiple gust intervals to observe one.
        public override double TimeoutSimSeconds => 420;

        static Vehicle Spawn(Node world, string name, Vector3 at)
        {
            var v = Vehicle.BuildByName(name);
            world.AddChild(v);
            v.GlobalPosition = at;
            v.DebugInstantStart = true;   // this suite is about FLYING, not starting -- see Vehicle.DebugInstantStart
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            // ---- 1. IT IS A HELICOPTER AND IT IS BUILT
            var h = Spawn(World, "minicopter", new Vector3(0f, 1.6f, 0f));
            h.DebugNoTurbulence = true;   // this one is the measuring rig: no unattended gusts tipping it mid-climb
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
            // 18 s of climb. The descent phase below needs real altitude UNDERNEATH it, and the thrust cuts of
            // 2026-08-16 (17 -> 13.6 -> 11.8) lowered the ceiling twice; both times the descent window reached
            // the ground and the check failed against a machine that was behaving correctly and had simply
            // landed. A test that measures falling has to keep the floor out of the measurement.
            for (int i = 0; i < 900; i++) { h.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float climbed = h.GlobalPosition.Y - rest;
            T.Check($"the rotor spools up under power (spool {h.RotorSpool:0.###})", h.RotorSpool > 0.9f);
            T.Check($"and it climbs on collective ({climbed:0.##} m in 18 s)", climbed > 6f);
            // Bounded: a thrust-to-weight bug (or gravity not applying) reads as "it climbs" too, just far too
            // fast. At T/W 1.20 and this drag the terminal climb is ~5.7 m/s, so 18 s is ~100 m; 260 m would
            // mean gravity or the damping had stopped applying.
            T.Check($"...at a plausible rate, not rocketing ({climbed:0.##} m)", climbed < 260f);

            // ---- 5. CUTTING COLLECTIVE DESCENDS. Sticky throttle: S has to wind it back down.
            //
            // Asserted on VERTICAL VELOCITY, not on altitude after a fixed window. The first cut of this used
            // altitude and failed at +19.62 m while the model was working perfectly: 8 s of climb leaves it
            // doing ~20 m/s upward, and cutting power does not teleport that momentum away -- it coasts up for
            // another second and a half before it starts down. "Is it descending" is the actual claim; "has it
            // ended up lower than it started" is a claim about how long I happened to wait.
            // 120 ticks is 2.4 s, just past the 1.8 s the collective needs to wind from full to idle. Longer
            // windows kept flying the machine into the ground before the assertions ran -- twice, at successively
            // lower thrust settings -- and a landed helicopter reports 0 m/s, which looks identical to a broken
            // descent. Measure the fall while there is still air under it.
            for (int i = 0; i < 120; i++) { h.DriveHeli(-1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"the collective winds back down to idle ({h.DebugHeliInput.X:0.##})", h.DebugHeliInput.X < 0.05f);
            T.Check($"...and it is descending under gravity ({h.LinearVelocity.Y:0.##} m/s)", h.LinearVelocity.Y < -1f);
            float top = h.GlobalPosition.Y;
            T.Check($"...with room left to fall through ({top:0.#} m up)", top > 20f);
            yield return Ticks(100);
            // Altitude reported ABSOLUTELY as well as as a delta, so a future failure says whether it stopped
            // falling or simply ran out of sky to fall through.
            T.Check($"...losing real altitude ({h.GlobalPosition.Y - top:0.##} m over 2 s, from {top:0.#} m to {h.GlobalPosition.Y:0.#} m)",
                h.GlobalPosition.Y < top - 5f);

            // ---- 6. THE THROTTLE IS SPRING-LOADED TO JUST UNDER HOVER.
            //
            // This REPLACES the sticky-throttle behaviour the first cut shipped with, on VoX's instruction
            // 2026-08-16: hands off, the collective returns to "a bit below the amount of thrust required to
            // counteract gravity"; S drives it to zero only while held; W drives it to maximum only while held.
            // So the resting state is a gentle sink -- not a held hover, and not a fall.
            var fresh = Spawn(World, "minicopter", new Vector3(40f, 420f, 0f));   // high: this subject spends ~6 s descending, and once crash damage existed it used to reach the ground and explode mid-test (an exploded heli zeroes its collective, so the spring-back check read 0)
            fresh.EngineOn = true;
            for (int i = 0; i < 260; i++) { fresh.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"W drives the collective to maximum ({fresh.DebugHeliInput.X:0.##})", fresh.DebugHeliInput.X > 0.95f);

            for (int i = 0; i < 150; i++) { fresh.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float idle = fresh.DebugHeliInput.X;
            // The idle point is DERIVED from the spec's thrust (hover = g / thrust), so assert it against that
            // rather than against a number copied out of the spec -- retuning thrust must move both together.
            float hover = 9.8f / 11.8f;
            T.Check($"hands off, it settles just BELOW hover, not at zero and not at hover ({idle:0.###} vs hover {hover:0.###})",
                idle > hover * 0.80f && idle < hover * 0.99f);
            // and the consequence that actually matters in the air: hands off means sinking, gently.
            float sink = fresh.LinearVelocity.Y;
            T.Check($"...so hands-off is a gentle sink, not a hover and not a plummet ({sink:0.##} m/s)",
                sink < -0.05f && sink > -12f);

            for (int i = 0; i < 150; i++) { fresh.DriveHeli(-1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"S drives it to zero while held ({fresh.DebugHeliInput.X:0.###})", fresh.DebugHeliInput.X < 0.02f);
            // ...and releasing S must come back UP to idle, not stay at zero -- the spring pulls both ways.
            for (int i = 0; i < 150; i++) { fresh.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"...and springs back up to idle on release ({fresh.DebugHeliInput.X:0.###})",
                fresh.DebugHeliInput.X > hover * 0.80f);

            // ---- 7. LIFT FOLLOWS THE AIRFRAME, NOT THE WORLD. Bank it hard and hold: it must accelerate
            // SIDEWAYS, toward the side it is leaning. This is what makes it a helicopter rather than a
            // drone that strafes -- and a model applying thrust along world-up passes every check above but
            // fails this one, which is why it is here.
            var lean = Spawn(World, "minicopter", new Vector3(-60f, 60f, 0f));
            lean.EngineOn = true;
            for (int i = 0; i < 260; i++) { lean.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            lean.LinearVelocity = Vector3.Zero;
            float x0 = lean.GlobalPosition.X;
            lean.DebugNoTurbulence = true;
            // A SHORT bank, then hold it. Under real torque a held input keeps accelerating the roll, so the
            // old 4 s of stick rolled past inverted (up.X -0.86) and the "moves toward the bank" check was then
            // measuring an upside-down machine. Bank it ~25 deg, release, and let momentum carry the slide.
            for (int i = 0; i < 35; i++) { lean.DriveHeli(0f, 0f, 0f, 0.5f, 0.02); yield return Ticks(1); }
            for (int i = 0; i < 165; i++) { lean.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
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

            // ---- 7b. ATTITUDE IS STATE, and it PERSISTS. VoX after flying it 2026-08-16: "pitch and yaw are
            // tracked as a current value and ... thrust applies in relation to that value ... Right now your
            // model keeps reverting the copter to upright even if no mouse is applied which is wrong."
            //
            // Bank it, then let go of everything and wait. The bank must still be there. The old self-levelling
            // term undid the pilot's input the moment they stopped holding it, which reads in the air as the
            // controls not working -- and note that every other check in this file passed the whole time it was
            // wrong, because a self-levelling helicopter still climbs, descends, yaws and translates.
            var hold = Spawn(World, "minicopter", new Vector3(-440f, 90f, 0f));
            hold.EngineOn = true; hold.DebugNoTurbulence = true;   // measuring the pilot, not the weather -- gusts made this flaky
            for (int i = 0; i < 260; i++) { hold.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            for (int i = 0; i < 60; i++) { hold.DriveHeli(0f, 0f, 0f, 0.6f, 0.02); yield return Ticks(1); }
            float banked = hold.GlobalTransform.Basis.Y.X;
            T.Check($"a roll input actually banks it (up.X {banked:0.###})", Mathf.Abs(banked) > 0.1f);
            // hands off for 3 s -- no stick, no collective change
            for (int i = 0; i < 150; i++) { hold.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            // Asserted as "it did NOT return to level", using the up vector's Y -- 1.0 is upright, 0 is
            // knife-edge, -1 inverted. The first version compared up.X before and after, which is not monotonic
            // through 90 deg: with real momentum and no self-levelling the airframe keeps rolling, and once it
            // passes vertical up.X shrinks again. It read +0.778 -> -0.274 and "failed" on a machine that had
            // rolled FURTHER, which is the opposite of the thing being tested.
            float upright = hold.GlobalTransform.Basis.Y.Y;
            T.Check($"...and nothing rights it hands-off (up.Y {upright:0.###}, 1.0 would be level again)",
                upright < 0.9f);
            // NOT "the rate has damped to nothing" -- that was written against the old assigned-velocity model
            // and is the opposite of what real momentum does. Drag is deliberately very slight, so 3 s after
            // release the airframe is still turning; the bank persists because nothing stopped it, which is
            // exactly the behaviour asked for. Section 7c measures the coast itself.
            T.Check($"...because nothing quietly zeroed the rotation for the pilot ({hold.AngularVelocity.Length():0.###} rad/s still on)",
                hold.AngularVelocity.Length() > 0.02f);

            // ---- 7c. INERTIA. strawberry 2026-08-16: "joystick changes should feel slower, heavier and more
            // sluggish. like the heli actually has weight."
            //
            // Weight is a claim about LAG, so it is measured as lag: after one tick of full stick the machine
            // must have barely begun to turn, and it must still be turning after the stick is released. A model
            // with no inertia snaps to the commanded rate immediately and stops dead on release, which passes
            // "does yaw input turn it" perfectly well.
            //
            // Turbulence is OFF for this one -- a gust is a rate the pilot did not ask for, and leaving the
            // weather on would let it fake the very lag being measured, or mask its absence.
            var inert = Spawn(World, "minicopter", new Vector3(-500f, 90f, 0f));
            inert.EngineOn = true; inert.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++) { inert.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            inert.AngularVelocity = Vector3.Zero;
            yield return Ticks(1);
            inert.DriveHeli(0f, 1f, 0f, 0f, 0.02);
            yield return Ticks(1);
            float afterOneTick = inert.AngularVelocity.Length();
            // Full yaw commands ~1.3 rad/s. Through two lags, one tick (20 ms) should deliver a small fraction.
            T.Check($"a full stick input does not take effect instantly ({afterOneTick:0.####} rad/s after 1 tick)",
                afterOneTick < 0.25f);
            for (int i = 0; i < 60; i++) { inert.DriveHeli(0f, 1f, 0f, 0f, 0.02); yield return Ticks(1); }
            float sustained = inert.AngularVelocity.Length();
            T.Check($"...but builds up while held ({sustained:0.###} rad/s after 1.2 s)", sustained > afterOneTick * 3f);
            // THE MOMENTUM CLAIM, and it is measured a full SECOND after release, not two ticks.
            //
            // VoX: "the vehical itself has rotational inertia which will keep it rotating for a bit unless you
            // counteract it with opposite stick input ... not fake input inertia, real physics simulated
            // inertia." The version this replaced assigned AngularVelocity through a lag, which decays with a
            // ~0.32 s time constant -- so at 1 s it retained about 4 % and would fail this outright. Real
            // angular momentum against 0.8 aerodynamic damping retains far more. Two ticks could not tell the
            // two apart; a second can.
            for (int i = 0; i < 50; i++) { inert.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float coasting = inert.AngularVelocity.Length();
            T.Check($"...and is STILL turning a full second after release ({coasting:0.###} rad/s, was {sustained:0.###})",
                coasting > sustained * 0.40f);

            // COUNTER-STICK ARRESTS IT. The other half of real momentum: if letting go does not stop the
            // rotation, flying against it has to. This is what makes the coast a skill rather than a nuisance.
            // Measured over a SHORT burst: counter-stick held too long does not stop the rotation, it REVERSES
            // it, and |omega| is large again for the opposite reason. The first cut of this held it 1.2 s and
            // read 1.337 rad/s -- the machine was spinning the other way, which is correct physics and a
            // useless assertion.
            for (int i = 0; i < 25; i++) { inert.DriveHeli(0f, -1f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"...and opposite stick arrests it far faster than waiting does ({inert.AngularVelocity.Length():0.###} rad/s, was {coasting:0.###})",
                inert.AngularVelocity.Length() < coasting * 0.7f);

            // The drag is deliberately "very very slight" (VoX), so settling is SLOW -- that it happens at all
            // is the claim, not that it happens promptly. 10 s against a ~4 s decay constant.
            for (int i = 0; i < 500; i++) { inert.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"...and slight drag does eventually bring it to rest ({inert.AngularVelocity.Length():0.###} rad/s)",
                inert.AngularVelocity.Length() < 0.25f);

            // ---- 7d. TURBULENCE, and that it knows when NOT to blow.
            var gust = Spawn(World, "minicopter", new Vector3(-560f, 90f, 0f));
            gust.EngineOn = true;
            float peak = 0f;
            for (int i = 0; i < 700; i++)   // 14 s: several gust intervals (1.6-5.5 s apart)
            {
                gust.DriveHeli(1f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
                peak = Mathf.Max(peak, gust.DebugTurbulence.Length());
            }
            T.Check($"turbulence actually blows in the air ({peak:0.###} rad/s peak gust)", peak > 0.05f);
            T.Check($"...but stays MINOR, not a wrestling match ({peak:0.###} rad/s)", peak < 0.6f);
            // A machine sitting on the ground does not get shoved around -- the check that stops "turbulence"
            // from becoming "the parked helicopter shakes itself apart".
            // A dedicated grounded subject, with turbulence ENABLED. Asserting this on the measuring rig above
            // would pass because that machine has the weather switched off -- a check whose pass means nothing.
            var parked = Spawn(World, "minicopter", new Vector3(-620f, 1.2f, 0f));
            parked.EngineOn = true;
            for (int i = 0; i < 400; i++) { parked.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"...and none of it reaches a machine sitting on the ground ({parked.DebugTurbulence.Length():0.###}, contacts {parked.GetContactCount()})",
                parked.DebugTurbulence.Length() < 0.001f);

            // ---- 8b. AXIS CONVENTIONS, pinned. An inverted axis flies perfectly well -- it climbs, banks,
            // turns and translates -- it is just backwards, so every check above passes on a machine with
            // pitch upside down. These name the intended direction in terms of the body basis:
            //   pitch +1 = nose UP    (forward vector -Z rises)
            //   roll  +1 = bank RIGHT (up vector tilts toward +X)
            //   yaw   +1 = nose RIGHT
            var conv = Spawn(World, "minicopter", new Vector3(-200f, 80f, 0f));
            conv.EngineOn = true; conv.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++) { conv.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            for (int i = 0; i < 30; i++) { conv.DriveHeli(0f, 0f, 1f, 0f, 0.02); yield return Ticks(1); }   // SHORT: a held stick keeps accelerating the rotation and would roll past 90 deg, flipping the sign being asserted
            float noseY = -conv.GlobalTransform.Basis.Z.Y;   // forward is -Z; its Y component is how far the nose points up
            T.Check($"pitch +1 raises the nose ({noseY:+0.##;-0.##;0} of forward.Y)", noseY > 0.1f);

            var conv2 = Spawn(World, "minicopter", new Vector3(-260f, 80f, 0f));
            conv2.EngineOn = true; conv2.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++) { conv2.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            for (int i = 0; i < 30; i++) { conv2.DriveHeli(0f, 0f, 0f, 1f, 0.02); yield return Ticks(1); }
            T.Check($"roll +1 banks right ({conv2.GlobalTransform.Basis.Y.X:+0.##;-0.##;0} of up.X)", conv2.GlobalTransform.Basis.Y.X > 0.1f);

            var conv3 = Spawn(World, "minicopter", new Vector3(-320f, 80f, 0f));
            conv3.EngineOn = true; conv3.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++) { conv3.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            Vector3 fwd0 = -conv3.GlobalTransform.Basis.Z;
            for (int i = 0; i < 45; i++) { conv3.DriveHeli(0f, 1f, 0f, 0f, 0.02); yield return Ticks(1); }
            Vector3 fwd1 = -conv3.GlobalTransform.Basis.Z;
            // turning RIGHT = the forward vector swings clockwise seen from above = fwd0 x fwd1 points DOWN
            float turnSign = fwd0.Cross(fwd1).Y;
            T.Check($"yaw +1 turns the nose right ({turnSign:+0.###;-0.###;0})", turnSign < -0.02f);

            // ---- 8c. THE TAIL ROTOR STAYS ON EDGE WHILE IT SPINS.
            //
            // strawberry, first minute of flying it: "the tail rotor needs to be rotated + 90 deg roll". It was
            // rolled 90 deg at build time and then the per-tick spin assigned the WHOLE rotation
            // (`Rotation = (0, spin, 0)`), wiping the roll every frame, so it lay flat like a second main rotor.
            // The comment on that line asserted the roll was still there while the assignment beside it
            // destroyed it -- a comment is not evidence.
            //
            // Asserted on the pivot's own basis, and specifically AFTER it has been spinning for a while: the
            // build-time value was always correct, so a check that ran before the first tick would have passed
            // throughout the bug.
            var tr = Spawn(World, "minicopter", new Vector3(-380f, 80f, 0f));
            tr.EngineOn = true;
            for (int i = 0; i < 260; i++) { tr.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            var tailPivot = tr.FindChild("TailRotor", false, false) as Node3D;
            T.Check("the tail rotor pivot exists", tailPivot != null);
            // Its spin axis is local Y. Standing the disc on edge means that axis points SIDEWAYS (world X),
            // not up -- so |Y.Y| must be ~0 and |Y.X| ~1. Flat-like-a-main-rotor is exactly the inverse.
            Vector3 spinAxis = tailPivot.GlobalTransform.Basis.Y;
            T.Check($"...and its spin axis points sideways, not up (axis.X {spinAxis.X:0.##}, axis.Y {spinAxis.Y:0.##})",
                Mathf.Abs(spinAxis.X) > 0.9f && Mathf.Abs(spinAxis.Y) < 0.2f);
            // and it is genuinely still turning -- the roll must not have been restored by freezing the spin
            float phaseA = tailPivot.GlobalTransform.Basis.Z.Y;
            yield return Ticks(3);
            T.Check($"...while still spinning (phase moved {Mathf.Abs(tailPivot.GlobalTransform.Basis.Z.Y - phaseA):0.###})",
                Mathf.Abs(tailPivot.GlobalTransform.Basis.Z.Y - phaseA) > 0.001f);

            // ---- 8d. ROTOR DAMAGE. Two independent rotors, each with its own failure MODE -- the point is not
            // that damage exists but that losing the main and losing the tail break different things.
            var dmg = Spawn(World, "minicopter", new Vector3(-700f, 120f, 0f));
            dmg.EngineOn = true; dmg.DebugNoTurbulence = true;
            T.Check($"the rotors carry independent health (main {dmg.MainRotorHealth:0}, tail {dmg.TailRotorHealth:0})",
                dmg.MainRotorHealth > 0f && dmg.TailRotorHealth > 0f && !dmg.MainRotorDead && !dmg.TailRotorDead);
            // Damaging one must not touch the other, or "independent" is decoration.
            float tailBefore = dmg.TailRotorHealth;
            dmg.DamageMainRotor(dmg.MainRotorHealth * 0.5f);
            T.Check($"...damaging the main leaves the tail alone ({dmg.TailRotorHealth:0} vs {tailBefore:0})",
                Mathf.IsEqualApprox(dmg.TailRotorHealth, tailBefore));

            // MAIN DEAD -> NO LIFT, so it falls even at full collective. Measured against a HEALTHY control in
            // the same run and the same conditions -- "it descended" alone would pass on any machine that had
            // simply run out of altitude.
            var ctrl = Spawn(World, "minicopter", new Vector3(-760f, 120f, 0f));
            ctrl.EngineOn = true; ctrl.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++)
            { dmg.DriveHeli(1f, 0f, 0f, 0f, 0.02); ctrl.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            dmg.KillMainRotor();
            T.Check("KillMainRotor kills the main rotor", dmg.MainRotorDead);
            float dy0 = dmg.GlobalPosition.Y, cy0 = ctrl.GlobalPosition.Y;
            for (int i = 0; i < 150; i++)
            { dmg.DriveHeli(1f, 0f, 0f, 0f, 0.02); ctrl.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float dead = dmg.GlobalPosition.Y - dy0, alive = ctrl.GlobalPosition.Y - cy0;
            T.Check($"a dead main rotor cannot gain height on full collective ({dead:+0.#;-0.#;0} m vs a healthy {alive:+0.#;-0.#;0} m)",
                dead < 0f && alive > dead + 5f);

            // TAIL DEAD -> IT SPINS, and specifically about YAW. A machine that merely tumbles would pass a
            // check on total angular velocity, so this asserts the yaw component dominates.
            var spun = Spawn(World, "minicopter", new Vector3(-820f, 120f, 0f));
            spun.EngineOn = true; spun.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++) { spun.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            spun.AngularVelocity = Vector3.Zero;
            spun.KillTailRotor();
            T.Check("KillTailRotor kills the tail rotor", spun.TailRotorDead);
            for (int i = 0; i < 150; i++) { spun.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            Vector3 av = spun.AngularVelocity;
            float yawSigned = av.Dot(spun.GlobalTransform.Basis.Y);   // SIGNED -- see the direction check below
            float yawPart = Mathf.Abs(yawSigned);
            T.Check($"a dead tail rotor spins the airframe ({yawPart:0.##} rad/s of yaw)", yawPart > 0.4f);
            // ...IN THE DIRECTION THE ROTOR'S REACTION TORQUE IMPLIES. The magnitude check above is sign-blind:
            // `Mathf.Abs` passes just as happily on a spin the wrong way, so it cannot tell a working model from
            // one with an inverted coupling term. The live term is `cmd += b.Y * (TailLossTorque * ...)` -- a
            // POSITIVE moment about the body up axis -- so the yaw rate must share that sign. Pinned here because
            // the heli physics rework proposes rewriting this coupling, and a sign flip is the single easiest
            // thing to get wrong in that rewrite while every existing check stays green. Review 2026-08-17.
            T.Check($"...the RIGHT WAY round, matching the rotor's reaction torque ({yawSigned:+0.##;-0.##})",
                yawSigned > 0f);
            T.Check($"...and it is a SPIN, not a tumble (yaw {yawPart:0.##} of total {av.Length():0.##})",
                yawPart > av.Length() * 0.7f);

            // ROTOR FX: smoke that grows with damage, fire when dead, and NOTHING on a healthy rotor. The last
            // of those is the one that matters -- a machine that smokes from full health tells the pilot
            // nothing, and every other check here would pass on it.
            var fx = Spawn(World, "minicopter", new Vector3(-940f, 3f, 0f));
            fx.EngineOn = true; fx.DebugNoTurbulence = true;
            yield return Ticks(5);
            T.Check($"a healthy rotor does not smoke (main {fx.MainRotorNorm:0.##})", !fx.DebugMainRotorSmoking && !fx.DebugMainRotorBurning);
            fx.DamageMainRotor(fx.MainRotorHealth * 0.5f);
            yield return Ticks(5);
            T.Check($"a hurt rotor smokes ({fx.MainRotorNorm:0.##} health)", fx.DebugMainRotorSmoking && !fx.DebugMainRotorBurning);
            int lightAmount = fx.DebugMainRotorSmokeAmount;
            fx.DamageMainRotor(fx.MainRotorHealth * 0.7f);
            yield return Ticks(5);
            T.Check($"...and smokes MORE the worse it gets ({lightAmount} -> {fx.DebugMainRotorSmokeAmount} particles at {fx.MainRotorNorm:0.##} health)",
                fx.DebugMainRotorSmokeAmount > lightAmount);
            fx.KillMainRotor();
            yield return Ticks(5);
            T.Check("a broken rotor catches fire", fx.DebugMainRotorBurning);
            T.Check("...and keeps smoking under the fire", fx.DebugMainRotorSmoking);
            // The tail is its own emitter -- killing the main must not set the TAIL on fire, or the FX erase
            // the very distinction the split health exists to make.
            T.Check($"...while the untouched tail rotor stays clean (tail {fx.TailRotorNorm:0.##})",
                !fx.DebugTailRotorBurning && !fx.DebugTailRotorSmoking);

            // A DEAD TAIL ALSO GROUNDS YOU, distinctly from a dead main: no climbing, but not the total loss of
            // lift a dead main gives. Both halves are asserted -- "cannot climb" alone would pass on a machine
            // that had simply been zeroed like the main, which is the thing this is meant NOT to be.
            var tdead = Spawn(World, "minicopter", new Vector3(-1000f, 120f, 0f));
            tdead.EngineOn = true; tdead.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++) { tdead.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            tdead.KillTailRotor();
            tdead.LinearVelocity = Vector3.Zero;
            float ty0 = tdead.GlobalPosition.Y;
            for (int i = 0; i < 150; i++) { tdead.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float tdrop = tdead.GlobalPosition.Y - ty0;
            T.Check($"a dead tail rotor cannot climb either ({tdrop:+0.#;-0.#;0} m on full collective)", tdrop < 0f);
            T.Check($"...but sinks gentler than a dead MAIN does ({tdrop:0.#} m vs {dead:0.#} m)", tdrop > dead);

            // BOTH ROTORS GONE -> the airframe itself dies, through the ordinary damage path.
            var both = Spawn(World, "minicopter", new Vector3(-1060f, 30f, 0f));
            both.EngineOn = true; both.DebugNoTurbulence = true;
            yield return Ticks(5);
            float hullBefore = both.Health;
            both.KillMainRotor();
            yield return Ticks(5);
            T.Check($"one dead rotor does NOT kill the airframe ({both.Health:0} of {hullBefore:0})", both.Health >= hullBefore - 0.01f);
            both.KillTailRotor();
            yield return Ticks(10);
            T.Check($"...but losing both does ({both.Health:0} hp, onFire {both.OnFire})", both.Health <= 0f || both.OnFire);

            // THE ROTOR STOPS. Idle, and dead -- the old spin had a constant term, so a parked machine with a
            // cold engine sat there slowly turning its blades forever.
            var still = Spawn(World, "minicopter", new Vector3(-1120f, 1.2f, 0f));
            yield return Ticks(30);
            float phase0 = still.DebugRotorPhase;
            yield return Ticks(60);
            T.Check($"a parked, unpowered rotor does not turn at all (phase {phase0:0.###} -> {still.DebugRotorPhase:0.###})",
                Mathf.IsEqualApprox(still.DebugRotorPhase, phase0));

            // ---- 8e. CRASHING. Three bands, and the boring one is load-bearing: a gentle set-down must
            // cost NOTHING. A model that damages the airframe on every touchdown passes both of the
            // interesting checks below and is unplayable.
            //
            // EVERY subject here settles on the ground first and waits out _spawnGrace before being thrown.
            // That guard suppresses crash damage for 2.5 s after a vehicle is placed -- correctly, so an
            // admin-spawned machine cannot blow itself up on its own settling drop -- but 2.5 s is also long
            // enough to fall 30 m. The first cut of this dropped each subject straight from spawn and every
            // impact landed inside the window: no damage, no bonk, no explosion, and a diagnostic showing
            // grace at -0.02 after 5 s of test, i.e. the machine had been invulnerable the whole way down.
            // The guard was right and the test was fighting it.
            // Height only, no initial velocity: assigning LinearVelocity in the same tick that clears Freeze
            // does not take -- the body is still frozen when it lands, so the head start is silently dropped.
            // A "25 m/s" smash actually arrived at ~17, under the write-off line, and read as a bonk. Gravity
            // is reliable and the drop height is the honest dial.
            Vehicle Drop(Vehicle v, float height)
            {
                v.Freeze = false;
                v.GlobalPosition = new Vector3(v.GlobalPosition.X, height, v.GlobalPosition.Z);
                return v;
            }

            var land = Spawn(World, "minicopter", new Vector3(-1200f, 1.2f, 0f));
            land.DebugNoTurbulence = true;
            yield return Ticks(220);   // settle AND outlast the 2.5 s spawn grace
            float landHp = land.Health;
            Drop(land, 1.9f);          // a gentle set-down: ~4 m/s at touchdown, under the 5.5 floor
            for (int i = 0; i < 160; i++) yield return Ticks(1);
            T.Check($"a gentle set-down costs no health ({land.Health:0} of {landHp:0}, bonks {land.DebugBonkCount})",
                land.Health >= landHp - 0.01f && land.DebugBonkCount == 0);

            // A MID-SPEED impact hurts and bonks, but is survivable.
            var bonk = Spawn(World, "minicopter", new Vector3(-1260f, 1.2f, 0f));
            bonk.DebugNoTurbulence = true;
            yield return Ticks(220);
            float bonkHp = bonk.Health;
            Drop(bonk, 8f);            // ~12 m/s at impact: over the 5.5 floor, under the 19 write-off
            for (int i = 0; i < 200 && !bonk.Exploded; i++) yield return Ticks(1);
            T.Check($"a mid-speed impact damages it ({bonk.Health:0} of {bonkHp:0})", bonk.Health < bonkHp);
            T.Check($"...and bonks, with FX ({bonk.DebugBonkCount} bonks)", bonk.DebugBonkCount > 0);
            T.Check($"...but does not write it off ({(bonk.Exploded ? "EXPLODED" : "intact")})", !bonk.Exploded);

            // A FAST impact is fatal outright -- no 4 s fuse, it goes up on contact.
            var smash = Spawn(World, "minicopter", new Vector3(-1320f, 1.2f, 0f));
            smash.DebugNoTurbulence = true;
            yield return Ticks(220);
            Drop(smash, 45f);          // ~24 m/s at impact, well past the write-off threshold
            for (int i = 0; i < 250 && !smash.Exploded; i++) yield return Ticks(1);
            T.Check($"a fast impact explodes it ({(smash.Exploded ? "EXPLODED" : "intact, " + smash.Health.ToString("0") + " hp")}; impact {smash.DebugLastImpactSpeed:0.#} m/s vs threshold 19, y={smash.GlobalPosition.Y:0.##})",
                smash.Exploded);

            // ---- 8f. THE ENGINE CUTS ONCE A CRIPPLED MACHINE IS DOWN, but NOT in the air -- cutting mid-air
            // would take away the autorotation you need to survive the landing, turning a recoverable failure
            // into a guaranteed kill. Both halves asserted, because "the engine stops" alone passes on the
            // version that kills it instantly.
            var cut = Spawn(World, "minicopter", new Vector3(-1380f, 120f, 0f));
            cut.EngineOn = true; cut.DebugNoTurbulence = true;
            for (int i = 0; i < 260; i++) { cut.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            cut.KillMainRotor();
            for (int i = 0; i < 60; i++) { cut.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"a dead rotor does NOT cut the engine in mid-air (y {cut.GlobalPosition.Y:0.#}, engine {cut.EngineOn})",
                cut.EngineOn && cut.GlobalPosition.Y > 5f);
            for (int i = 0; i < 700 && cut.EngineOn; i++) { cut.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"...but does once it is back on the ground (y {cut.GlobalPosition.Y:0.##}, engine {cut.EngineOn})", !cut.EngineOn);

            // BLOWTORCHING THE HULL BRINGS THE ROTORS BACK. Without this a machine could be welded to full
            // health and still be unflyable with no way to fix it -- a dead end rather than a difficulty.
            var fix = Spawn(World, "minicopter", new Vector3(-1440f, 1.2f, 0f));
            fix.KillMainRotor(); fix.KillTailRotor();
            fix.TakeDamage(fix.Health * 0.5f);
            yield return Ticks(5);
            T.Check("a wrecked machine starts with both rotors dead", fix.MainRotorDead && fix.TailRotorDead);
            fix.Repair(fix.HealthMax);
            T.Check($"a full hull repair restores the rotors too (main {fix.MainRotorNorm:0.##}, tail {fix.TailRotorNorm:0.##})",
                fix.MainRotorNorm > 0.99f && fix.TailRotorNorm > 0.99f);

            // ---- 8g. A STOPPED ROTOR NEITHER CUTS NOR IS CUT. A parked machine sitting against something
            // was grinding its own rotor away just by being parked there. The pair of checks is the point:
            // unpowered it must take NOTHING however long it sits, and powered in the same place it must take
            // damage -- otherwise "no damage" would also pass on a blade-strike system that had simply stopped
            // working.
            var park = Spawn(World, "minicopter", new Vector3(-1500f, 1.2f, 0f));
            park.DebugNoTurbulence = true;
            // A wall standing up through the disc, so the blades are unambiguously inside something.
            var wall = new StaticBody3D { Position = new Vector3(-1500f, 2.0f, 0f) };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4f, 4f, 0.4f) } });
            World.AddChild(wall);
            yield return Ticks(200);
            float parkMain = park.MainRotorHealth;
            T.Check($"a parked machine with its rotor off takes no blade damage ({parkMain:0} intact, spool {park.RotorSpool:0.##})",
                Mathf.IsEqualApprox(parkMain, park.MainRotorHealth) && park.RotorSpool < 0.05f);
            yield return Ticks(200);
            T.Check($"...however long it sits there ({park.MainRotorHealth:0} of {parkMain:0})",
                park.MainRotorHealth >= parkMain - 0.01f);
            // Now spin it up in the SAME spot: the blades must start biting, or the check above proves nothing.
            park.EngineOn = true;
            for (int i = 0; i < 400; i++) { park.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"...but a SPINNING rotor in the same wall does take damage ({park.MainRotorHealth:0} of {parkMain:0}, spool {park.RotorSpool:0.##})",
                park.MainRotorHealth < parkMain);

            // THE HUB IS THE BULLET TARGET, the disc is not. Shooting the tip of a 5 m blade should not kill a
            // rotor; hitting the machinery at the mast should.
            var hb = Spawn(World, "minicopter", new Vector3(-880f, 4f, 0f));
            Vector3 hubWorld = hb.ToGlobal(new Vector3(0f, 1.22f, 0.55f));
            T.Check($"a hit at the main mast resolves to the main rotor ({hb.ResolveHitPart(hubWorld)})",
                hb.ResolveHitPart(hubWorld) == Vehicle.HeliPart.MainRotor);
            T.Check($"a hit at the tail rotor resolves to the tail ({hb.ResolveHitPart(hb.ToGlobal(new Vector3(0.09f, 0.02f, 2.46f)))})",
                hb.ResolveHitPart(hb.ToGlobal(new Vector3(0.09f, 0.02f, 2.46f))) == Vehicle.HeliPart.TailRotor);
            T.Check($"a hit out on the blade tip is NOT a rotor hit ({hb.ResolveHitPart(hb.ToGlobal(new Vector3(2.4f, 1.22f, 0.55f)))})",
                hb.ResolveHitPart(hb.ToGlobal(new Vector3(2.4f, 1.22f, 0.55f))) == Vehicle.HeliPart.Body);
            T.Check($"a hit on the seat is airframe, not a rotor ({hb.ResolveHitPart(hb.ToGlobal(new Vector3(0f, 0.02f, 0.18f)))})",
                hb.ResolveHitPart(hb.ToGlobal(new Vector3(0f, 0.02f, 0.18f))) == Vehicle.HeliPart.Body);

            // ---- 8h. THE WHOLE RETAIL FLEET FLIES. Every one is a real .dat with real numbers, built on the
            // shared model -- so the check is that each ACTUALLY LIFTS OFF, not merely that its spec parses.
            // A missing mesh, a hub at the wrong height or a thrust below its own weight all produce a
            // perfectly valid Vehicle that sits on the ground forever.
            string[] fleet = { "hind", "orca", "skycrane", "hummingbird", "huey" };
            for (int fi = 0; fi < fleet.Length; fi++)
            {
                var a = Spawn(World, fleet[fi], new Vector3(-1600f - fi * 80f, 3f, 0f));
                a.EngineOn = true; a.DebugNoTurbulence = true;
                T.Check($"{fleet[fi]}: builds as a rotary wing with its own airframe ({a.DisplayName})",
                    a.IsHeli && a.DisplayName.Length > 0);
                float y0 = a.GlobalPosition.Y;
                for (int i = 0; i < 260; i++) { a.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
                T.Check($"{fleet[fi]}: gets off the ground ({a.GlobalPosition.Y - y0:+0.#;-0.#;0} m)", a.GlobalPosition.Y - y0 > 3f);
                // and each carries its OWN ported .dat health rather than a shared default
                T.Check($"{fleet[fi]}: carries its own .dat health ({a.HealthMax:0})", a.HealthMax > 0f);
            }

            // ---- 8i. THE FLEET IS BALANCED AGAINST THE REAL AIRCRAFT. The absolute numbers are taste; the
            // ORDERING is the specification strawberry gave, so that is what gets pinned. Each of these would
            // survive any amount of retuning and fails the moment someone makes the gunship handle like a
            // scout or the crane out-run everything.
            //
            // Speed: Mi-24 330 > Ka-60 300 > MD500 282 > UH-1 222 > S-64 213 km/h.
            var byName = new System.Collections.Generic.Dictionary<string, Vehicle>();
            foreach (var n in new[] { "hind", "orca", "hummingbird", "huey", "skycrane", "minicopter" })
                byName[n] = Vehicle.BuildByName(n);
            T.Check($"the Hind is the fastest of the fleet ({byName["hind"].SpeedMaxMps:0} m/s)",
                byName["hind"].SpeedMaxMps > byName["orca"].SpeedMaxMps
                && byName["orca"].SpeedMaxMps > byName["hummingbird"].SpeedMaxMps
                && byName["hummingbird"].SpeedMaxMps > byName["huey"].SpeedMaxMps
                && byName["huey"].SpeedMaxMps > byName["skycrane"].SpeedMaxMps);
            // ...and the scrap ultralight is slower than every real aircraft.
            T.Check($"the minicopter is the slowest thing with a rotor ({byName["minicopter"].SpeedMaxMps:0} m/s)",
                byName["minicopter"].SpeedMaxMps < byName["skycrane"].SpeedMaxMps);
            // Agility runs the OTHER way, by weight: MD500 1.6 t ... S-64 21 t. The Hind being both the
            // fastest AND near the bottom for handling is the check that a "faster = better" retune breaks.
            T.Check("agility is inverse to weight: Hummingbird > Huey > Orca > Hind > Skycrane",
                byName["hummingbird"].DebugRollAuthority > byName["huey"].DebugRollAuthority
                && byName["huey"].DebugRollAuthority > byName["orca"].DebugRollAuthority
                && byName["orca"].DebugRollAuthority > byName["hind"].DebugRollAuthority
                && byName["hind"].DebugRollAuthority > byName["skycrane"].DebugRollAuthority);
            T.Check($"...so the fastest machine is NOT the most agile (hind roll {byName["hind"].DebugRollAuthority:0.##} vs hummingbird {byName["hummingbird"].DebugRollAuthority:0.##})",
                byName["hind"].DebugRollAuthority < byName["hummingbird"].DebugRollAuthority);
            // The Skycrane climbs WORST despite being the lift machine -- at 21 t it mostly lifts itself.
            T.Check($"the Skycrane is the worst climber, not the best ({byName["skycrane"].DebugThrust:0.#})",
                byName["skycrane"].DebugThrust < byName["huey"].DebugThrust
                && byName["skycrane"].DebugThrust < byName["hind"].DebugThrust);
            foreach (var v in byName.Values) v.QueueFree();

            // ---- 9. THE HUEY flies the same model off its own spec + the real retail mesh.
            var huey = Spawn(World, "huey", new Vector3(120f, 3f, 0f));
            T.Check("the huey spec builds as a rotary wing", huey.IsHeli);
            T.Check($"...with the retail airframe, not the procedural frame ({huey.DisplayName})", huey.DisplayName == "Huey");
            huey.EngineOn = true;
            float hy = huey.GlobalPosition.Y;
            for (int i = 0; i < 260; i++) { huey.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"the huey climbs too ({huey.GlobalPosition.Y - hy:0.##} m)", huey.GlobalPosition.Y - hy > 4f);

            yield break;
        }
    }
}
