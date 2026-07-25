using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Landmine trap: a placed Deployable (IsTrap) is inert for TrapArmDelay (placer grace), then arms and watches
    // TrapTrigger for a zombie; a victim in range detonates it (AoE via DamageTool.explode, covered by the grenade
    // tests) and shatters the mine. Proves the pieces I added: the arming grace, the trigger radius, and detonation.
    public class LandmineArmsAndDetonates : GameTest
    {
        public override string Name => "trap.landmine";
        public override IEnumerable<Step> Run()
        {
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            var z = new ZombieController();
            World.AddChild(z);
            yield return Ticks(3);   // mesh/collision build + the zombie joins the "zombies" group
            T.Check("landmine placed (not yet exploded)", mine != null && !mine.DebugExploded);
            if (mine == null) yield break;

            z.GlobalPosition = new Vector3(0.8f, 0f, 0f);   // INSIDE the 1.4 m trigger (no Ticks after -> it stays put)

            // GRACE: a freshly-planted mine is inert -- a zombie in range must NOT set it off (else you blast yourself)
            mine.DebugTrapCheck();
            T.Check("placer grace: a fresh mine ignores a zombie in range", !mine.DebugExploded);

            mine.DebugAdvanceArm(2f);   // past TrapArmDelay -> armed

            // armed, but a zombie OUT of range still doesn't trigger it
            z.GlobalPosition = new Vector3(5f, 0f, 0f);     // 5 m > 1.4 m
            mine.DebugTrapCheck();
            T.Check("armed but zombie out of range: still armed", !mine.DebugExploded);

            // armed + a zombie IN range detonates it
            z.GlobalPosition = new Vector3(0.8f, 0f, 0f);
            mine.DebugTrapCheck();
            T.Check("armed + zombie in range detonates the mine", mine.DebugExploded);
        }
    }

    // The landmine also arms on a PLAYER (PvP + you can't just walk over your own field). This covers the DETECTION
    // path (TrapVictimNear via PlayerRegistry) without detonating -- the detonation is the same code the zombie test
    // already exercises, so there's no need to blast a bare test player through its damage/UI path.
    public class LandmineDetectsPlayer : GameTest
    {
        public override string Name => "trap.landmine_player";
        public override IEnumerable<Step> Run()
        {
            var player = new PlayerController();
            World.AddChild(player);                              // registers in PlayerRegistry on _EnterTree
            player.GlobalPosition = new Vector3(30f, 0f, 0f);    // far away
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            yield return Ticks(3);
            T.Check("placed", mine != null);
            if (mine == null) yield break;

            T.Check("a player far away is NOT a victim", !mine.DebugVictimNear());

            player.GlobalPosition = new Vector3(0.8f, 0f, 0f);   // inside the 1.4 m trigger
            T.Check("a player in range IS a victim", mine.DebugVictimNear());
        }
    }
}
