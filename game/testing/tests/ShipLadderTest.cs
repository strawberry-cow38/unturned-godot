using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry 2026-08-19: "add a ladder to the back of the container ship."
    //
    // The ladder itself is the easy half. The half worth testing is that it stays climbable while she is MAKING
    // WAY, because climbing is its own movement stance and nothing that carries a player along a moving deck
    // reaches it: CharacterBody3D's own moving-floor handling and PlayerController's deck rotation both require
    // you to be STANDING on something, and StepLadder deliberately does not re-snap while climbing ("retail
    // snaps on ENTRY only"). Left alone, a climber rises straight up in world space, the hull sails out from
    // under them, and the probe misses within a tick or two.
    public sealed class ShipLadderTest : GameTest
    {
        public override string Name => "vehicle.ship_ladder";
        public override double TimeoutSimSeconds => 220;

        /// <summary>Put the player on the ladder: alongside its face at the same height as the look target, so
        /// the body-facing probe runs horizontally into it.</summary>
        IEnumerable<Step> Grab(PlayerController p, Vehicle ship)
        {
            // LOW on the ladder (hull y 5.5, just above the 4.79 waterline), with 5.5 m of it still to go before
            // the deck at 11. The first version started at 8.5 and the player simply FINISHED -- 3.21 m in three
            // seconds put them on the deck in STAND, so the leg that was supposed to test climbing under way was
            // testing standing on a deck instead, and the check that caught it ("still attached partway up") was
            // reporting a successful climb as a failure.
            p.TeleportTo(ship.GlobalTransform * new Vector3(0f, 5.5f, 34.6f));
            p.LookAt(ship.GlobalTransform * new Vector3(0f, 5.5f, 0f), Vector3.Up);   // level, straight at the hull
            yield return Until(() => p.Stance == EPlayerStance.CLIMB, 3.0);
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

                // The ladder must actually be there and be marked climbable, or every check below passes for
                // the wrong reason -- a player who never attaches simply reports "not climbing" all the way.
                Node3D lad = null;
                foreach (var c in ship.GetChildren())
                    if (c is StaticBody3D sb && sb.HasMeta(Ladder.Meta)) lad = sb;
                T.Check("the ship carries a ladder body the climb probe can resolve", lad != null);
                if (lad == null) yield break;
                var lp = ship.GlobalTransform.AffineInverse() * lad.GlobalPosition;
                GD.Print($"[LADDER] ship rest y={rest:0.00}; ladder at hull frame {lp.X:0.0},{lp.Y:0.0},{lp.Z:0.0}; " +
                         $"face axis {Ladder.FaceAxis(lad)}");
                T.Check($"...on the BACK of her, aft of the transom at z=33.75 (hull frame z {lp.Z:0.0})", lp.Z > 33.75f);
                T.Check($"...and its climbable face is horizontal, so Ladder.IsClimbable will not refuse it as sloped (face y {Ladder.FaceAxis(lad).Y:+0.00;-0.00})",
                        Mathf.Abs(Ladder.FaceAxis(lad).Y) <= Ladder.SlopeDot);

                var p = new PlayerController { CaptureMouse = false };
                World.AddChild(p);
                yield return Ticks(4);
                // Just aft of the ladder's outer face, above the waterline so the stance is not SWIM, looking
                // at the ship so the body-facing probe runs into the ladder.
                // 0.55 m off the face, not 0.75: the probe reaches exactly 0.75 m, so standing at the limit makes
                // the grab a coin-flip on floating point. And the look target is at the SAME height -- aiming at
                // the ship's ORIGIN points the body 14 deg downward (she floats with her origin below the
                // waterline), and the probe follows body facing, so it rakes down past the face entirely. Both of
                // those were my fixture, not the ladder: the first run reported the ladder present and correctly
                // oriented and the player simply never attached.
                foreach (var st in Grab(p, ship)) yield return st;
                T.Check($"a player at her stern can grab the ladder ({p.Stance})", p.Stance == EPlayerStance.CLIMB);
                if (p.Stance != EPlayerStance.CLIMB) yield break;

                // ---- 1. IT CLIMBS, stationary. The plain case, and the one a solid-box collider exists for.
                float y0 = p.GlobalPosition.Y;
                p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                for (int i = 0; i < 75; i++) { ship.Drive(0f, 0f, false); yield return Ticks(1); }
                float climbed = p.GlobalPosition.Y - y0;
                p.ScriptedInput = null;   // HOLD on the rungs for the leg below -- a climber who reaches the top
                                          // dismounts to STAND, and then the next leg measures the deck instead
                GD.Print($"[LADDER] stationary: climbed {climbed:0.00} m in 1.5 s (deck is at world y {rest + 11f:0.00}, player now {p.GlobalPosition.Y:0.00})");
                T.Check($"and climbs it ({climbed:0.00} m gained in 1.5 s)", climbed > 1.5f);
                T.Check($"still attached partway up, below the deck ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

                // ---- 2. IT STAYS CLIMBABLE UNDER WAY. Measured in the HULL's frame: in world coordinates a
                // climber being carried and a climber being left behind both just change position.
                var before = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                var shipFrom = ship.GlobalPosition;
                for (int i = 0; i < 250; i++) { ship.Drive(1f, 0f, false); yield return Ticks(1); }
                var after = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                float shipRan = (ship.GlobalPosition - shipFrom).Length();
                float slip = new Vector2(after.X - before.X, after.Z - before.Z).Length();
                GD.Print($"[LADDER] under way: ship ran {shipRan:0.0} m, climber slipped {slip:0.00} m across the hull " +
                         $"(hull frame {before.X:0.0},{before.Y:0.0},{before.Z:0.0} -> {after.X:0.0},{after.Y:0.0},{after.Z:0.0}), stance {p.Stance}");
                T.Check($"the ship actually got under way ({shipRan:0.0} m) -- else staying on the ladder proves nothing",
                        shipRan > 20f);
                T.Check($"the climber stays ON the ladder while she makes way (slipped {slip:0.00} m in the hull's frame)",
                        slip < 1.5f);
                T.Check($"...and is still climbing rather than swimming behind her ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

                // ---- 3. TEETH. Everything above is also true if the engine happened to carry them. Turn the
                // carry off and run the same leg: without it the hull leaves at 12 m/s and the probe loses the
                // ladder within a tick or two.
                PlayerController.DeckCarryEnabled = false;
                p.ScriptedInput = null;
                foreach (var st in Grab(p, ship)) yield return st;
                T.Check($"the control leg starts ON the ladder too ({p.Stance}) -- a climber who never grabbed it cannot be 'left behind'",
                        p.Stance == EPlayerStance.CLIMB);
                var offBefore = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                var offShipFrom = ship.GlobalPosition;
                for (int i = 0; i < 250; i++) { ship.Drive(1f, 0f, false); yield return Ticks(1); }
                var offAfter = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                float offShipRan = (ship.GlobalPosition - offShipFrom).Length();
                float offSlip = new Vector2(offAfter.X - offBefore.X, offAfter.Z - offBefore.Z).Length();
                GD.Print($"[LADDER] CARRY OFF, same leg: ship ran {offShipRan:0.0} m, climber slipped {offSlip:0.0} m (with it on: {slip:0.00} m), stance {p.Stance}");
                T.Check($"the ship moved just as far with the carry off ({offShipRan:0.0} m vs {shipRan:0.0}) -- like-for-like",
                        offShipRan > 20f);
                T.Check($"WITHOUT the carry the climber is left behind ({offSlip:0.0} m vs {slip:0.00} m) -- so the check above measures this code",
                        offSlip > Mathf.Max(5f, slip * 4f));
            }
            finally
            {
                PlayerController.DeckCarryEnabled = true;
                Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
            }
        }
    }
}
