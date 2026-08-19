using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry 2026-08-19: "make vehicles on the deck not completely sink the ship, and actual handle
    // relative to the ship. ie if i land a heli on a ship, it stays on the deck and moves with the ship."
    //
    // Measuring BEFORE designing, because two of the three things here might not be problems at all and I do
    // not want to fix a symptom I have not seen. Specifically: every vehicle in this game masses the same
    // GlobalMass 900, and the ship's Archimedes force is derived from ITS mass -- so a single heli is a 100%
    // load increase. Whether that "completely sinks" it or just settles it deeper is a question about the
    // heave stiffness the BuoySlices fix gave it, and the answer is a number, not an opinion.
    public sealed class ShipDeckProbe : GameTest
    {
        public override string Name => "vehicle.ship_deck_probe";
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
                for (int i = 0; i < 1200; i++) { ship.Drive(0f, 0f, false); yield return Ticks(1); }
                float restAlone = ship.GlobalPosition.Y;
                GD.Print($"[DECK] ship alone settles at y={restAlone:0.00}");

                // Where IS the deck, in world terms, right now? The collider stops at the hull box top; the
                // MESH deck is at y=11 local. Report both so the drop height is not a guess.
                GD.Print($"[DECK] collider box top (local y) = {11f:0.0}, ship y {restAlone:0.00} -> deck world y ~= {restAlone + 11f:0.00}");

                // Drop a heli onto the deck and watch what the hull does.
                var heli = Vehicle.BuildByName("huey");
                World.AddChild(heli);
                // OVER THE OPEN DECK, forward of the superstructure. This used to be z=+12, which is inside the
                // deckhouse footprint (z 10.4..25.75) -- harmless while the superstructure had no collision at
                // all, and an instant ejection the moment it got some. The probe was quietly dropping the heli
                // INTO the bridge, and then reporting "sank 0.00 m" because the load had bounced off into the sea.
                heli.GlobalPosition = new Vector3(0f, restAlone + 15f, -10f);
                heli.EngineOn = false;
                yield return Ticks(2);

                for (int i = 0; i < 10; i++)
                {
                    for (int k = 0; k < 50; k++) { ship.Drive(0f, 0f, false); yield return Ticks(1); }
                    GD.Print($"[DECK] t+{(i + 1) * 1.0f:0.0}s ship y={ship.GlobalPosition.Y:0.00} (was {restAlone:0.00}, " +
                             $"delta {ship.GlobalPosition.Y - restAlone:+0.00;-0.00})  heli y={heli.GlobalPosition.Y:0.00}  " +
                             $"heliRestingOnShip={(heli.GlobalPosition.Y > ship.GlobalPosition.Y + 8f ? "yes" : "NO")}");
                }

                // PENETRATION CREEP, named explicitly because it is the failure mode this feature actually has and
                // it is invisible in world coordinates -- rider and hull descend together, so both look fine right
                // up until the hull falls through the gap between two buoyancy voxel decks and goes 47 m under.
                // In the HULL'S frame it is obvious: a rider resting on the deck must hold its height.
                var deckY0 = (ship.GlobalTransform.AffineInverse() * heli.GlobalPosition).Y;
                float sank = restAlone - ship.GlobalPosition.Y;
                GD.Print($"[DECK] VERDICT: a single 900 kg vehicle sank the hull {sank:0.00} m");

                // Now get underway and see whether the heli comes with it.
                var heliStart = heli.GlobalPosition;
                var shipStart = ship.GlobalPosition;
                for (int i = 0; i < 500; i++) { ship.Drive(1f, 0f, false); yield return Ticks(1); }
                var shipMoved = ship.GlobalPosition - shipStart;
                var heliMoved = heli.GlobalPosition - heliStart;
                GD.Print($"[DECK] under way 10 s: ship moved {shipMoved.Length():0.0} m, heli moved {heliMoved.Length():0.0} m");
                GD.Print($"[DECK] heli still aboard? y={heli.GlobalPosition.Y:0.00} vs ship {ship.GlobalPosition.Y:0.00}; " +
                         $"lateral offset from ship {(new Vector3(heli.GlobalPosition.X - ship.GlobalPosition.X, 0f, heli.GlobalPosition.Z - ship.GlobalPosition.Z)).Length():0.0} m");

                // ---- GATES. The probe printed numbers; these are the ones that are allowed to regress.
                // strawberry 2026-08-19: "we should have the ship have an effect on other vehicles, but other
                // vehicles have no effect on the ship." Not "less", none -- so this is checked at centimetres,
                // not metres. 10.21 m unbounded -> 0.76 m with reserve buoyancy -> nothing, with the rider's
                // weight cancelled where it presses on the deck.
                T.Check($"a vehicle on deck has NO effect on the hull's draft (sank {sank:0.00} m; was 10.21 m unbounded, then 0.76 m on buoyancy alone)",
                        Mathf.Abs(sank) < 0.10f);
                T.Check($"...and it FOUND a new equilibrium rather than settling slowly forever (heli still on deck at t+10s, y={heli.GlobalPosition.Y:0.00} vs ship {ship.GlobalPosition.Y:0.00})",
                        heli.GlobalPosition.Y > ship.GlobalPosition.Y + 8f);
                T.Check($"the heli is CARRIED: it travelled with the hull ({heliMoved.Length():0.0} m against the ship's {shipMoved.Length():0.0} m)",
                        Mathf.Abs(heliMoved.Length() - shipMoved.Length()) < 5f);
                float aboardOff = new Vector3(heli.GlobalPosition.X - ship.GlobalPosition.X, 0f, heli.GlobalPosition.Z - ship.GlobalPosition.Z).Length();
                T.Check($"...and it is still ON the deck afterwards, not merely moving at a similar speed somewhere else ({aboardOff:0.0} m from the hull centre, deck half-length 33)",
                        aboardOff < 34f);

                var deckY1 = (ship.GlobalTransform.AffineInverse() * heli.GlobalPosition).Y;
                GD.Print($"[DECK] rider height in the HULL's frame: {deckY0:0.00} -> {deckY1:0.00} (deck plate is y=11)");
                T.Check($"the rider does not sink INTO the deck over time (hull-frame height {deckY0:0.00} -> {deckY1:0.00})",
                        Mathf.Abs(deckY1 - deckY0) < 0.30f && deckY1 > 10.9f);

                // ---- THROUGH A TURN. This is the case a velocity-match passes the straight-line check on and
                // then quietly fails: matching the hull's LINEAR velocity keeps station on a straight course and
                // slides steadily off the stern as soon as the ship puts the rudder over. Measured as the rider's
                // offset IN THE HULL'S OWN FRAME, which is the only frame where "did it slide on the deck" is a
                // question with an answer.
                var deckPosBefore = ship.GlobalTransform.AffineInverse() * heli.GlobalPosition;
                for (int i = 0; i < 900; i++) { ship.Drive(1f, 1f, false); yield return Ticks(1); }
                var deckPosAfter = ship.GlobalTransform.AffineInverse() * heli.GlobalPosition;
                float slide = (deckPosAfter - deckPosBefore).Length();
                float yawTurned = Mathf.RadToDeg(Mathf.Abs(ship.GlobalRotation.Y));
                GD.Print($"[DECK] through an 18 s turn (hull yawed ~{yawTurned:0} deg): rider slid {slide:0.00} m across the deck " +
                         $"(deck-frame {deckPosBefore.X:0.0},{deckPosBefore.Y:0.0},{deckPosBefore.Z:0.0} -> {deckPosAfter.X:0.0},{deckPosAfter.Y:0.0},{deckPosAfter.Z:0.0})");
                T.Check($"the hull actually TURNED during that leg ({yawTurned:0} deg) -- else the slide check below proves nothing",
                        yawTurned > 30f);
                T.Check($"the rider holds station THROUGH the turn, not just on a straight course (slid {slide:0.00} m in the hull's frame)",
                        slide < 4f);

                // ---- CONTROL: a HOVERING aircraft must NOT be dragged. Without this, "carry works" is
                // indistinguishable from "anything within 30 m of the ship gets towed", which would be a far
                // worse bug than the one being fixed and would feel awful to fly anywhere near a ship.
                var hover = Vehicle.BuildByName("huey");
                World.AddChild(hover);
                // IN THE SHIP'S OWN FRAME. A world-space offset was wrong the moment the turn leg above left the
                // hull pointing somewhere else -- it would have parked the control heli off the side of the ship,
                // where "it was not dragged along" is true of any implementation and proves nothing.
                // OVER THE FOREDECK, and the leg below is kept SHORT on purpose. A world-stationary object is not
                // stationary relative to the ship: the hull travels ~12 m/s, so anything parked over the deck
                // drifts aft through the hull's frame at that rate, and the superstructure -- which now has
                // collision up to y=22 -- eventually arrives and rams it. That is correct physics and a broken
                // control: it read as "the hover was dragged 44 m" when what actually happened was the deckhouse
                // hit it. Starting 32 m forward and running 3 s keeps it over open deck the whole time.
                hover.GlobalPosition = ship.GlobalTransform * new Vector3(0f, 16f, -32f);   // over the foredeck, never landed
                hover.GravityScale = 0f;                                                   // hold it up without flying it
                hover.LinearVelocity = Vector3.Zero; hover.AngularVelocity = Vector3.Zero;
                yield return Ticks(2);
                var hoverStart = hover.GlobalPosition;
                var shipXfAtHover = ship.GlobalTransform;
                var shipStart2 = ship.GlobalPosition;
                int maxRiders = 0;
                for (int i = 0; i < 150; i++)   // 3 s: far enough to prove the ship moved, short enough that the
                {                               // deckhouse never reaches the hovering aircraft
                    ship.Drive(1f, 0f, false); yield return Ticks(1);
                    if (ship.DebugDeckRiders > maxRiders) maxRiders = ship.DebugDeckRiders;
                }
                float hoverMoved = (hover.GlobalPosition - hoverStart).Length();
                // The DIRECT reading, so "was it dragged" stops being an inference from a distance. One rider is
                // the heli parked on the deck; two means the hovering one was being carried as well.
                GD.Print($"[DECK] most riders carried at once during the control leg: {maxRiders} (1 = only the parked heli)");
                T.Check($"the hovering aircraft was never counted as a rider (peak {maxRiders}, expected 1 -- the parked heli)",
                        maxRiders <= 1);
                float shipMoved2 = (ship.GlobalPosition - shipStart2).Length();
                GD.Print($"[DECK] CONTROL hovering heli: moved {hoverMoved:0.0} m while the ship moved {shipMoved2:0.0} m");
                T.Check($"the ship actually moved during the control leg ({shipMoved2:0.0} m) -- else 'the hover was not dragged' is vacuous",
                        shipMoved2 > 20f);
                // ...and it has to have been OVER THE DECK to begin with, or it was never a candidate for being
                // carried and the control is measuring nothing.
                var hoverLocal = shipXfAtHover.AffineInverse() * hoverStart;
                T.Check($"the control heli started INSIDE the deck-carry volume (hull frame {hoverLocal.X:0.0},{hoverLocal.Y:0.0},{hoverLocal.Z:0.0}; box is x+-11.5, y 11..17, z+-33.25)",
                        Mathf.Abs(hoverLocal.X) < 11.5f && hoverLocal.Y > 11f && hoverLocal.Y < 17f && Mathf.Abs(hoverLocal.Z) < 33.25f);
                T.Check($"a HOVERING aircraft over the deck is NOT dragged along ({hoverMoved:0.0} m against the ship's {shipMoved2:0.0} m)",
                        hoverMoved < shipMoved2 * 0.25f);

                // ---- LANDING ON A MOVING SHIP (strawberry asked for "considerations for landing on a moving
                // ship"). The hard version of it: the approach is deliberately NOT speed-matched. The lander is
                // left hanging at zero velocity while the hull sails underneath, so at the instant of touchdown
                // the deck is passing beneath it at full speed. If a touchdown flings anything, it is here.
                var lander = Vehicle.BuildByName("huey");
                World.AddChild(lander);
                lander.EngineOn = false;
                lander.GlobalPosition = ship.GlobalTransform * new Vector3(0f, 13.5f, -26f);   // 2.5 m over the foredeck
                lander.LinearVelocity = Vector3.Zero;                                          // NOT matched, on purpose
                lander.AngularVelocity = Vector3.Zero;
                yield return Ticks(2);
                float deckSpeed = ship.LinearVelocity.Length();
                for (int i = 0; i < 300; i++) { ship.Drive(1f, 0f, false); yield return Ticks(1); }
                var landLocal = ship.GlobalTransform.AffineInverse() * lander.GlobalPosition;
                float relSpeed = (lander.LinearVelocity - ship.LinearVelocity).Length();
                GD.Print($"[DECK] unmatched landing onto a deck doing {deckSpeed:0.0} m/s: settled at hull-frame " +
                         $"{landLocal.X:0.0},{landLocal.Y:0.0},{landLocal.Z:0.0}; closing speed against the hull now {relSpeed:0.00} m/s");
                T.Check($"the deck really was moving underneath it ({deckSpeed:0.0} m/s) -- landing on a stopped ship would prove nothing",
                        deckSpeed > 5f);
                T.Check($"an unmatched landing ends up ON the deck rather than flung off it (hull frame {landLocal.X:0.0},{landLocal.Y:0.0},{landLocal.Z:0.0})",
                        landLocal.Y > 10.9f && landLocal.Y < 14f && Mathf.Abs(landLocal.X) < 11.5f && Mathf.Abs(landLocal.Z) < 33.25f);
                // "Is it sliding down the deck" asked as a DISPLACEMENT over a couple of seconds rather than as
                // an instantaneous relative speed. The speed reading is noisy -- it caught the lander mid-settle
                // and read 0.19, 0.56 and 1.71 m/s on three runs of the same code -- and the thing actually worth
                // knowing is whether it ends up somewhere else on the ship, which is a distance.
                var slideFrom = ship.GlobalTransform.AffineInverse() * lander.GlobalPosition;
                for (int i = 0; i < 120; i++) { ship.Drive(1f, 0f, false); yield return Ticks(1); }
                var slideTo = ship.GlobalTransform.AffineInverse() * lander.GlobalPosition;
                float landerSlide = new Vector2(slideTo.X - slideFrom.X, slideTo.Z - slideFrom.Z).Length();
                GD.Print($"[DECK] ...and over the NEXT 2.4 s it moved {landerSlide:0.00} m across the deck (instantaneous relative speed was {relSpeed:0.00} m/s)");
                T.Check($"...and afterwards it holds its spot on the deck rather than sliding down it ({landerSlide:0.00} m over 2.4 s)",
                        landerSlide < 2.0f);

                // ---- ON THE BRIDGE ROOF, which is ABOVE the deck carry volume (y 11..17) and so was invisible
                // to the first version of the load cancellation -- it reused the deck box, so a machine sat on
                // top of the deckhouse at y=22 pressed on the hull with its full weight and nothing removed it.
                // strawberry's rule has no deck in it: other vehicles have no effect on the ship, wherever they
                // happen to be sitting on her.
                float beforeRoof = ship.GlobalPosition.Y;
                var roofer = Vehicle.BuildByName("huey");
                World.AddChild(roofer);
                roofer.EngineOn = false;
                roofer.GlobalPosition = ship.GlobalTransform * new Vector3(0f, 24f, 17f);   // 2 m over the bridge roof
                yield return Ticks(2);
                for (int i = 0; i < 400; i++) { ship.Drive(0f, 0f, false); yield return Ticks(1); }
                float roofSank = beforeRoof - ship.GlobalPosition.Y;
                var rooferLocal = ship.GlobalTransform.AffineInverse() * roofer.GlobalPosition;
                GD.Print($"[DECK] a vehicle on the BRIDGE ROOF (hull frame {rooferLocal.X:0.0},{rooferLocal.Y:0.0},{rooferLocal.Z:0.0}): hull moved {roofSank:0.00} m");
                T.Check($"it actually landed on the deckhouse rather than falling past it (hull-frame y {rooferLocal.Y:0.0}, roof is 22)",
                        rooferLocal.Y > 20f);
                T.Check($"a vehicle on the bridge roof has no effect on the hull either ({roofSank:0.00} m)",
                        Mathf.Abs(roofSank) < 0.10f);

                // ---- ALONGSIDE, NOT ABOARD. The hovering-heli control only covers things directly OVER the
                // deck. The other way "aboard" could misfire is a boat sitting against the hull SIDE at the
                // waterline: it is in sustained contact, which is half the rider test. The geometry says it
                // cannot qualify -- the carry box spans hull-local y 11..17 and x +-11.5, while the waterline
                // sits around y 4.8 -- but that is an argument, and the ship towing every boat that moors
                // against it would be a bad way to find out the argument was wrong.
                var alongside = Vehicle.BuildByName("runabout");
                World.AddChild(alongside);
                alongside.GlobalPosition = ship.GlobalTransform * new Vector3(12.5f, 5.5f, -6f);   // against the hull, at the waterline
                yield return Ticks(2);
                for (int i = 0; i < 100; i++) { ship.Drive(0f, 0f, false); yield return Ticks(1); }   // let it settle alongside
                var alongStart = alongside.GlobalPosition;
                var alongShipXf = ship.GlobalTransform;   // read the frame NOW: taking it after the run reports
                                                          // the boat's start position in the hull's END frame, which
                                                          // is a number about nothing

                var alongShipStart = ship.GlobalPosition;
                int alongMaxRiders = 0;
                for (int i = 0; i < 200; i++)
                {
                    ship.Drive(1f, 0f, false); yield return Ticks(1);
                    if (ship.DebugDeckRiders > alongMaxRiders) alongMaxRiders = ship.DebugDeckRiders;
                }
                float alongMoved = (alongside.GlobalPosition - alongStart).Length();
                float alongShipMoved = (ship.GlobalPosition - alongShipStart).Length();
                var alongLocal = alongShipXf.AffineInverse() * alongStart;   // in the hull frame it STARTED in
                GD.Print($"[DECK] ALONGSIDE control: a boat against the hull at {alongLocal.X:0.0},{alongLocal.Y:0.0},{alongLocal.Z:0.0} " +
                         $"moved {alongMoved:0.0} m while the ship made {alongShipMoved:0.0} m; peak riders {alongMaxRiders}");

                T.Check($"the ship moved during the alongside leg ({alongShipMoved:0.0} m) -- else it was never a chance to tow anything",
                        alongShipMoved > 15f);
                T.Check($"a boat ALONGSIDE the hull is never counted as cargo (peak riders {alongMaxRiders}, expected 2 -- the parked heli and the lander)",
                        alongMaxRiders <= 2);
                T.Check($"...and is not towed along by it ({alongMoved:0.0} m against the ship's {alongShipMoved:0.0} m)",
                        alongMoved < alongShipMoved * 0.3f);
            }
            finally { Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea; }
        }
    }
}
