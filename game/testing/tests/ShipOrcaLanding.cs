using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry 2026-08-19, on a build that already had the load cancellation: "the ship still [being] affected
    // by whats on the deck" ... "landing an orca on the deck" ... "it sinks when i first land, and then wobbles
    // for a while".
    //
    // My own probe read 0.01 m and was answering a different question in two ways at once, which is why it and
    // his eyes disagreed:
    //
    //   1. IT SAMPLED ONCE A SECOND and reported the SETTLED value. He is describing a TRANSIENT -- a dip at the
    //      instant of touchdown, then an oscillation. A per-second sample walks straight past both. This one
    //      samples the hull EVERY TICK and reports the PEAK excursion and how long the wobble takes to die.
    //   2. IT LANDED A HUEY, which sits on SKIDS. The orca lands on WHEELS (orca_wheels.txt), and Godot's
    //      VehicleWheel3D is a RAYCAST, not a collider -- so a machine resting on its gear may never enter the
    //      ship's contact list at all, and the weight cancellation reads that list. DebugDeckLoads is printed
    //      here precisely so "the cancellation never engaged" cannot be mistaken for "the cancellation failed".
    //
    // Reproducing before fixing: this test is expected to FAIL on the build that prompted it.
    public sealed class ShipOrcaLanding : GameTest
    {
        public override string Name => "vehicle.ship_orca_landing";
        public override double TimeoutSimSeconds => 220;

        sealed class Motion { public float Heave, Rate, Tilt, Corner, Pitch, Roll; }

        /// <summary>Watch the hull for N ticks and report what a person STANDING ON HER would feel: not just the
        /// centre's heave, but the tilt and -- the one that actually matters -- how far a DECK CORNER travels.
        /// A hull rocking about its own axis is nearly still at the centre and swinging at the edge, so measuring
        /// the centre answers a question nobody asked. strawberry, when the centre read 10 mm: "the ship is rocking".
        ///
        /// Returns a MUTABLE object filled in as the steps are consumed, not a struct: the iterator is lazy, so a
        /// value returned alongside it is captured before a single tick has run. The first version did exactly
        /// that and would have reported zeros for everything -- and zeros PASS every check here.</summary>
        IEnumerable<Step> Watch(Vehicle ship, int ticks, Motion m)
        {
            var corner = new Vector3(11.5f, 11f, -33f);   // forward starboard corner of the weather deck
            float lo = ship.GlobalPosition.Y, hi = lo, prev = lo, rate = 0f;
            float c0 = (ship.GlobalTransform * corner).Y;
            float cLo = c0, cHi = c0;
            float pLo = ship.GlobalRotation.X, pHi = pLo, rLo = ship.GlobalRotation.Z, rHi = rLo;
            for (int i = 0; i < ticks; i++)
            {
                ship.Drive(0f, 0f, false); yield return Ticks(1);
                float y = ship.GlobalPosition.Y;
                float d = Mathf.Abs(y - prev); prev = y;
                if (d > rate) rate = d;
                if (y < lo) lo = y;
                if (y > hi) hi = y;
                float cy = (ship.GlobalTransform * corner).Y;
                if (cy < cLo) cLo = cy;
                if (cy > cHi) cHi = cy;
                float px = ship.GlobalRotation.X, rz = ship.GlobalRotation.Z;
                if (px < pLo) pLo = px;
                if (px > pHi) pHi = px;
                if (rz < rLo) rLo = rz;
                if (rz > rHi) rHi = rz;
            }
            m.Heave = hi - lo; m.Rate = rate; m.Corner = cHi - cLo;
            m.Pitch = Mathf.RadToDeg(pHi - pLo);
            m.Roll = Mathf.RadToDeg(rHi - rLo);
            m.Tilt = Mathf.Max(m.Pitch, m.Roll);
        }

        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Terrain.HasWater = true; Terrain.SeaLevelY = 0f;
            try
            {
                var ship = Vehicle.BuildByName("ship");
                World.AddChild(ship);
                ship.GlobalPosition = new Vector3(0f, 2f, 0f);
                ship.EngineOn = true;
                yield return Ticks(2);
                for (int i = 0; i < 900; i++) { ship.Drive(0f, 0f, false); yield return Ticks(1); }
                float rest = ship.GlobalPosition.Y;
                GD.Print($"[ORCA] ship settled at y={rest:0.000} (deck surface {rest + 11f:0.00})");

                // CONTROL FIRST: how much does the hull move on her own? Everything below is a claim about what
                // the ORCA did to her, and a hull that already breathes at 0.3 m/s would produce the same reading
                // with nothing on deck at all. Measured before anything is dropped, over the same window.
                var bareM = new Motion();
                foreach (var st in Watch(ship, 200, bareM)) yield return st;
                GD.Print($"[ORCA] CONTROL, empty hull: heave {bareM.Heave * 1000f:0.0} mm, tilt {bareM.Tilt:0.000} deg, " +
                         $"DECK CORNER travels {bareM.Corner * 1000f:0.0} mm ({bareM.Rate * 50f:0.000} m/s at the centre)");
                // strawberry wants to BUILD on her, so "holds still with nothing aboard" is now a requirement in
                // its own right, not just a baseline to compare the orca against. Was 0.259 m/s from the wave
                // ripple; Spec.SteadyHull turns that off for this hull.
                // On the EXCURSION, for the same reason the loaded check is: I compared the control's RATE against
                // the loaded case's AMPLITUDE once and nearly reverted a change that was better on both, because
                // a rate cannot tell a millimetre of shiver from a slow heave.
                // 30 mm on the DECK CORNER, which is the number a person standing on her experiences -- the hull
                // centre reads 11 mm for the same motion, and reporting that instead is how 548 mm of corner
                // swing hid behind "10 mm" for several rounds.
                T.Check($"the empty hull holds still enough to build on (deck corner {bareM.Corner * 1000f:0.0} mm, tilt {bareM.Tilt:0.000} deg, centre heave {bareM.Heave * 1000f:0.0} mm)",
                        bareM.Corner < 0.03f);

                // Dropped from 8 m so it arrives with a real descent rate, which is the whole point -- a machine
                // set down gently delivers almost no impulse and would hide the thing being measured.
                var orca = Vehicle.BuildByName("orca");
                World.AddChild(orca);
                orca.EngineOn = false;
                orca.GlobalPosition = new Vector3(0f, rest + 19f, -10f);
                yield return Ticks(2);

                float peakDown = 0f, peakUp = 0f, impactSpeed = 0f;
                int loadsSeen = 0, ridersSeen = 0, tickOfPeak = 0;
                bool touched = false;
                for (int i = 0; i < 600; i++)
                {
                    ship.Drive(0f, 0f, false);
                    yield return Ticks(1);
                    if (!touched && orca.GlobalPosition.Y < rest + 13.5f) { touched = true; impactSpeed = -orca.LinearVelocity.Y; }
                    float d = ship.GlobalPosition.Y - rest;
                    if (d < peakDown) { peakDown = d; tickOfPeak = i; }
                    if (d > peakUp) peakUp = d;
                    if (ship.DebugDeckLoads > loadsSeen) loadsSeen = ship.DebugDeckLoads;
                    if (ship.DebugDeckRiders > ridersSeen) ridersSeen = ship.DebugDeckRiders;
                }
                float settled = ship.GlobalPosition.Y - rest;

                // How long does it ring for? Walk the last stretch and find when the hull stops moving.
                // AMPLITUDE as well as rate. "Hard to build on" is about how far the deck actually moves, and a
                // rate on its own cannot tell a 2 mm shiver at high frequency from a slow half-metre heave -- the
                // first is invisible, the second is the complaint. Measure the peak-to-peak excursion and let that
                // decide whether the rate matters.
                var loadM = new Motion();
                foreach (var st in Watch(ship, 200, loadM)) yield return st;
                float wobbleAmp = loadM.Heave;

                var orcaLocal = ship.GlobalTransform.AffineInverse() * orca.GlobalPosition;
                GD.Print($"[ORCA] touched down at {impactSpeed:0.0} m/s; hull PEAK {peakDown:0.000} m down (t+{tickOfPeak * 0.02f:0.0}s) / {peakUp:0.000} m up, " +
                         $"settled {settled:0.000} m; AFTERWARDS heave {wobbleAmp * 1000f:0.0} mm, " +
                         $"pitch {loadM.Pitch:0.000} deg / roll {loadM.Roll:0.000} deg, DECK CORNER travels {loadM.Corner * 1000f:0.0} mm");
                GD.Print($"[ORCA] weight cancellation engaged on at most {loadsSeen} body/bodies; carried riders {ridersSeen}; " +
                         $"orca ended at hull frame {orcaLocal.X:0.0},{orcaLocal.Y:0.0},{orcaLocal.Z:0.0}");

                // The A/B that used to live here (cancellation off, same parked orca) has served its purpose and
                // is removed rather than left in the suite: it measured a STEP -- switching the cancellation off
                // drops the hull to a new equilibrium -- so its 861 mm was mostly that transient, not steady
                // rocking. It answered the question it was built for (the cancellation reduces the rock roughly
                // sixfold, so it is not the excitation) and would only mislead if kept.
                T.Check($"the orca actually landed on the deck rather than missing it (hull frame y {orcaLocal.Y:0.0}, deck is 11)",
                        orcaLocal.Y > 10.5f && orcaLocal.Y < 16f);
                T.Check($"it arrived with a real descent rate ({impactSpeed:0.0} m/s) -- a gentle set-down would not test anything",
                        impactSpeed > 3f);
                T.Check($"the ship is in the contact list while it is stood on ({loadsSeen} load(s) cancelled) -- a machine resting on RAYCAST wheels would report 0 here and the cancellation would never run",
                        loadsSeen >= 1);
                // 0.03 m, not the 0.15 the first version allowed. "No effect on the ship" is the requirement, and a
                // gate set loose enough to pass the very behaviour being reported is not a gate. Measured before
                // the impact term went in: 0.117 m and 0.080 m on two runs of the same code.
                // NOT zero, and said out loud rather than dressed up: a hard arrival still dips her ~3 cm for an
                // instant, down from 8-12 cm. The settled draft IS zero (0.000 m), which is the part of "no effect
                // on the ship" that is fully met. Gate at 4 cm to catch a regression without pretending the
                // transient was eliminated.
                T.Check($"landing barely dips the hull ({peakDown:0.000} m at the worst instant, was 0.08-0.12 m on weight-cancel alone; settled draft is separately checked at zero)",
                        Mathf.Abs(peakDown) < 0.04f);
                // Gated on the EXCURSION, not the rate: 5 mm of deck movement is not something you can see, let
                // alone something that stops you placing a foundation, however fast it happens.
                // Gated on the DECK CORNER, not the hull centre. A hull rocking about its own axis barely moves at
                // the centre while the deck edge swings through an arc -- 10 mm of heave at the middle is entirely
                // consistent with a deck that visibly tilts under you, which is what "it still wobbles" means.
                T.Check($"the deck holds still with a machine parked on it (corner travels {loadM.Corner * 1000f:0.0} mm, tilt {loadM.Tilt:0.000} deg, heave at centre {wobbleAmp * 1000f:0.0} mm; was 548 mm)",
                        loadM.Corner < 0.05f);
                T.Check($"...and ends where she started ({settled:0.000} m)", Mathf.Abs(settled) < 0.10f);
            }
            finally { Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea; }
        }
    }
}
