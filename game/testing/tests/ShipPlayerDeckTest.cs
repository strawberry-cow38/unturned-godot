using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry 2026-08-19: "keep going for player collision" -- the half of the deck carry the vehicle-side
    // implementation deliberately could not do. A PlayerController is a CharacterBody3D whose controller rewrites
    // Velocity from input every tick, so the frame-shift the ship applies to rigid riders is erased before it
    // integrates; and a bare GlobalPosition write on it is undone one tick later by the render-interp snapshot.
    // It needs its own path inside the controller, which is what StepMoveOnce/DeckPlatformVelocity now is.
    //
    // Every leg here is measured in the HULL'S frame, because that is the only frame in which "did the player
    // stay where they were standing" is a question with an answer -- in world coordinates a player being carried
    // and a player standing on the seabed while the ship sails away both just change position.
    public sealed class ShipPlayerDeckTest : GameTest
    {
        public override string Name => "vehicle.ship_player_deck";
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
                GD.Print($"[PDECK] ship settled at y={rest:0.00}, deck surface ~{rest + 11f:0.00}");

                var p = new PlayerController { CaptureMouse = false };
                World.AddChild(p);
                yield return Ticks(4);
                p.TeleportTo(new Vector3(0f, rest + 12.5f, -10f));   // 1.5 m over the foredeck, forward of the deckhouse
                yield return Until(() => p.DebugOnDeck != null, 4.0);

                T.Check($"the player lands on the deck and is recognised as being aboard (on deck: {(p.DebugOnDeck != null ? p.DebugOnDeck.Name : "NO")})",
                        p.DebugOnDeck == ship);
                if (p.DebugOnDeck != ship) yield break;

                // Re-place the player at mid-deck before each leg. The first version did not, and its control
                // leg reported a beautiful 0.1 m of drift for a reason that had nothing to do with the feature:
                // the previous leg had run the player into the BOW RAIL, where they physically could not move
                // further and every subsequent number was "wedged", not "carried".
                IEnumerable<Step> Reset()
                {
                    p.TeleportTo(ship.GlobalTransform * new Vector3(0f, 12.5f, -8f));
                    yield return Until(() => p.IsOnFloor(), 4.0);
                    yield return Ticks(10);
                }

                // ---- 1. UNDER WAY. The player gives NO input: this is standing still on deck. Worth locking in
                // as a regression guard, but note what it is NOT evidence of -- CharacterBody3D carries its own
                // capsule along a moving floor, so this leg passes with the deck code entirely removed. That is
                // exactly why it was measured: the first implementation ADDED the deck velocity on top of this
                // and sent the player 88.6 m while the hull made 66.0, straight into the bow rail.
                var deckBefore = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                var shipFrom = ship.GlobalPosition; var pFrom = p.GlobalPosition;
                for (int i = 0; i < 300; i++) { ship.Drive(1f, 0f, false); yield return Ticks(1); }
                var deckAfter = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                float shipRan = (ship.GlobalPosition - shipFrom).Length();
                float pRan = (p.GlobalPosition - pFrom).Length();
                float drift = new Vector2(deckAfter.X - deckBefore.X, deckAfter.Z - deckBefore.Z).Length();
                GD.Print($"[PDECK] under way 6 s: ship {shipRan:0.0} m, player {pRan:0.0} m; player drifted {drift:0.00} m across the deck " +
                         $"(hull frame {deckBefore.X:0.0},{deckBefore.Y:0.0},{deckBefore.Z:0.0} -> {deckAfter.X:0.0},{deckAfter.Y:0.0},{deckAfter.Z:0.0})");

                T.Check($"the ship actually got under way ({shipRan:0.0} m) -- else every check below passes on a stationary ship",
                        shipRan > 25f);
                T.Check($"a player standing still on deck travels WITH the ship ({pRan:0.0} m against the hull's {shipRan:0.0} m)",
                        Mathf.Abs(pRan - shipRan) < 5f);
                T.Check($"...and holds their spot on the deck rather than sliding aft ({drift:0.00} m in the hull's frame)",
                        drift < 3f);

                // ---- 2. THROUGH A TURN, which is where the deck code actually earns its place. Nothing rotates
                // a CharacterBody3D with the floor it stands on, so a player on a turning hull keeps their WORLD
                // facing: half a circle later they are looking over the rail of a ship they are stood squarely on.
                foreach (var st in Reset()) yield return st;
                var turnDeckBefore = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                float shipYaw0 = ship.GlobalRotation.Y, playerYaw0 = p.GlobalRotation.Y;
                for (int i = 0; i < 500; i++) { ship.Drive(1f, 1f, false); yield return Ticks(1); }
                var turnDeckAfter = ship.GlobalTransform.AffineInverse() * p.GlobalPosition;
                float turnDrift = new Vector2(turnDeckAfter.X - turnDeckBefore.X, turnDeckAfter.Z - turnDeckBefore.Z).Length();
                float shipTurned = Mathf.RadToDeg(Mathf.Wrap(ship.GlobalRotation.Y - shipYaw0, -Mathf.Pi, Mathf.Pi));
                float playerTurned = Mathf.RadToDeg(Mathf.Wrap(p.GlobalRotation.Y - playerYaw0, -Mathf.Pi, Mathf.Pi));
                GD.Print($"[PDECK] through a 10 s turn: hull yawed {shipTurned:0.0} deg, player yawed {playerTurned:0.0} deg, " +
                         $"drifted {turnDrift:0.00} m across the deck");

                T.Check($"the hull actually turned during that leg ({shipTurned:0.0} deg)", Mathf.Abs(shipTurned) > 30f);
                T.Check($"the player turns WITH the deck (player {playerTurned:0.0} deg vs hull {shipTurned:0.0} deg)",
                        Mathf.Abs(playerTurned - shipTurned) < 12f);
                T.Check($"...and is still aboard afterwards (hull frame {turnDeckAfter.X:0.0},{turnDeckAfter.Y:0.0},{turnDeckAfter.Z:0.0}; deck is x+-11.5, z+-33.25)",
                        Mathf.Abs(turnDeckAfter.X) < 11.5f && Mathf.Abs(turnDeckAfter.Z) < 33.25f && turnDeckAfter.Y > 10.5f);

                // ---- 3. TEETH, aimed at the thing this code actually does. "The player turned 105 degrees" is
                // also what happens if the engine were rotating them for free, so run the SAME turn with the deck
                // code switched off: if it is doing the work, the player's facing now stays put in world terms.
                PlayerController.DeckCarryEnabled = false;
                foreach (var st in Reset()) yield return st;
                float offShipYaw0 = ship.GlobalRotation.Y, offPlayerYaw0 = p.GlobalRotation.Y;
                for (int i = 0; i < 500; i++) { ship.Drive(1f, 1f, false); yield return Ticks(1); }
                float offShipTurned = Mathf.RadToDeg(Mathf.Wrap(ship.GlobalRotation.Y - offShipYaw0, -Mathf.Pi, Mathf.Pi));
                float offPlayerTurned = Mathf.RadToDeg(Mathf.Wrap(p.GlobalRotation.Y - offPlayerYaw0, -Mathf.Pi, Mathf.Pi));
                GD.Print($"[PDECK] CARRY OFF, same turn: hull yawed {offShipTurned:0.0} deg, player yawed {offPlayerTurned:0.0} deg " +
                         $"(with it on, the player followed to within {Mathf.Abs(playerTurned - shipTurned):0.0} deg)");

                T.Check($"the hull turned just as far with the deck code off ({offShipTurned:0.0} deg vs {shipTurned:0.0}) -- the control is like-for-like",
                        Mathf.Abs(offShipTurned) > 30f);
                T.Check($"WITHOUT it the player does NOT follow the hull's heading (player {offPlayerTurned:0.0} deg against the hull's {offShipTurned:0.0}) -- so the check above is measuring this code, not the engine",
                        Mathf.Abs(offPlayerTurned - offShipTurned) > 20f);
            }
            finally
            {
                PlayerController.DeckCarryEnabled = true;
                Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
            }
        }
    }
}
