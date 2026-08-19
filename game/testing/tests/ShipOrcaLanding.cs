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
                float bare = 0f, barePrev = ship.GlobalPosition.Y;
                float bareLo = ship.GlobalPosition.Y, bareHi = ship.GlobalPosition.Y;
                for (int i = 0; i < 200; i++)
                {
                    ship.Drive(0f, 0f, false); yield return Ticks(1);
                    float y = ship.GlobalPosition.Y;
                    float dy = Mathf.Abs(y - barePrev);
                    barePrev = y;
                    if (dy > bare) bare = dy;
                    if (y < bareLo) bareLo = y;
                    if (y > bareHi) bareHi = y;
                }
                float bareAmp = bareHi - bareLo;
                GD.Print($"[ORCA] CONTROL, empty hull over the same window: {bare * 50f:0.000} m/s over {bareAmp * 1000f:0.0} mm peak-to-peak");
                // strawberry wants to BUILD on her, so "holds still with nothing aboard" is now a requirement in
                // its own right, not just a baseline to compare the orca against. Was 0.259 m/s from the wave
                // ripple; Spec.SteadyHull turns that off for this hull.
                // On the EXCURSION, for the same reason the loaded check is: I compared the control's RATE against
                // the loaded case's AMPLITUDE once and nearly reverted a change that was better on both, because
                // a rate cannot tell a millimetre of shiver from a slow heave.
                T.Check($"the empty hull actually holds still, because it is meant to be built on ({bareAmp * 1000f:0.0} mm peak-to-peak, at {bare * 50f:0.000} m/s)",
                        bareAmp < 0.02f);

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
                float wobble = 0f;
                float prevY = ship.GlobalPosition.Y;
                float loY = ship.GlobalPosition.Y, hiY = ship.GlobalPosition.Y;
                for (int i = 0; i < 200; i++)
                {
                    ship.Drive(0f, 0f, false); yield return Ticks(1);
                    float y = ship.GlobalPosition.Y;
                    float dy = Mathf.Abs(y - prevY);
                    prevY = y;
                    if (dy > wobble) wobble = dy;
                    if (y < loY) loY = y;
                    if (y > hiY) hiY = y;
                }
                float wobbleAmp = hiY - loY;

                var orcaLocal = ship.GlobalTransform.AffineInverse() * orca.GlobalPosition;
                GD.Print($"[ORCA] touched down at {impactSpeed:0.0} m/s; hull PEAK {peakDown:0.000} m down (t+{tickOfPeak * 0.02f:0.0}s) / {peakUp:0.000} m up, " +
                         $"settled {settled:0.000} m, residual motion {wobble * 50f:0.000} m/s over {wobbleAmp * 1000f:0.0} mm peak-to-peak");
                GD.Print($"[ORCA] weight cancellation engaged on at most {loadsSeen} body/bodies; carried riders {ridersSeen}; " +
                         $"orca ended at hull frame {orcaLocal.X:0.0},{orcaLocal.Y:0.0},{orcaLocal.Z:0.0}");

                T.Check($"the orca actually landed on the deck rather than missing it (hull frame y {orcaLocal.Y:0.0}, deck is 11)",
                        orcaLocal.Y > 10.5f && orcaLocal.Y < 16f);
                T.Check($"it arrived with a real descent rate ({impactSpeed:0.0} m/s) -- a gentle set-down would not test anything",
                        impactSpeed > 3f);
                T.Check($"the ship is in the contact list while it is stood on ({loadsSeen} load(s) cancelled) -- a machine resting on RAYCAST wheels would report 0 here and the cancellation would never run",
                        loadsSeen >= 1);
                // 0.03 m, not the 0.15 the first version allowed. "No effect on the ship" is the requirement, and a
                // gate set loose enough to pass the very behaviour being reported is not a gate. Measured before
                // the impact term went in: 0.117 m and 0.080 m on two runs of the same code.
                T.Check($"landing does not dunk the hull ({peakDown:0.000} m at the worst instant, not just once it settles; was 0.08-0.12 m on weight-cancel alone)",
                        Mathf.Abs(peakDown) < 0.03f);
                // Gated on the EXCURSION, not the rate: 5 mm of deck movement is not something you can see, let
                // alone something that stops you placing a foundation, however fast it happens.
                T.Check($"...and the deck does not visibly move afterwards ({wobbleAmp * 1000f:0.0} mm peak-to-peak, at {wobble * 50f:0.000} m/s; empty hull {bare * 50f:0.000} m/s)",
                        wobbleAmp < 0.02f);
                T.Check($"...and ends where she started ({settled:0.000} m)", Mathf.Abs(settled) < 0.10f);
            }
            finally { Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea; }
        }
    }
}
