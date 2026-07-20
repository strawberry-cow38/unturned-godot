using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // Water/swim, in-engine: a player whose feet+1.25 body probe is under the ocean surface enters the SWIM
    // stance, has NO gravity (doesn't sink to the seabed), swims UP when jump is held, and takes no fall/drown
    // damage from being in the water. Retail model (PlayerMovement.cs:1134-1164, PlayerStance.cs:636-673).
    // The port's ocean is a single global plane at Terrain.SeaLevelY -- set here (STATIC, so reset at the end or
    // it would flip every later test's low-spawned player into swimming).
    public class PlayerSwimInWater : GameTest
    {
        public override string Name => "player.swim_in_water";
        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater;
            float oldSea = Terrain.SeaLevelY;
            Rigs.Ground(World);                       // floor at ~Y0 -- the "seabed" the player must NOT sink to
            Terrain.HasWater = true;
            Terrain.SeaLevelY = 8f;                   // ocean surface at Y8
            var p = Rigs.Player(World, new Vector3(0f, 2f, 0f));   // feet Y2: body(3.25)+eyes(3.75) both < 8 -> submerged
            yield return Ticks(6);

            T.Check($"submerged player entered SWIM (stance={p.Stance})", p.Stance == EPlayerStance.SWIM);

            // no gravity: a submerged idle player holds depth (neutral), does not sink to the seabed at Y0
            float ySub = p.GlobalPosition.Y;
            yield return Ticks(25);
            T.Check($"no gravity in water -- doesn't sink to the floor (Y {ySub:0.0} -> {p.GlobalPosition.Y:0.0})",
                    p.GlobalPosition.Y > ySub - 0.5f);

            // jump swims UP (free-swim vertical = 3 m/s)
            float yUp0 = p.GlobalPosition.Y;
            p.ScriptedJump = true;
            yield return Ticks(20);
            p.ScriptedJump = false;
            T.Check($"jump swims UP (Y {yUp0:0.0} -> {p.GlobalPosition.Y:0.0})", p.GlobalPosition.Y > yUp0 + 0.5f);

            T.Check($"no fall/drown damage from swimming (health {p.Health:0})", p.Health >= 99f);

            Terrain.HasWater = hadWater;   // MUST restore -- static leaks into every later test
            Terrain.SeaLevelY = oldSea;
        }
    }
}
