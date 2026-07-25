using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Landmine trap: a placed Deployable with IsTrap watches TrapTrigger for a zombie, then DetonateTrap() -- an AoE
    // blast (DamageTool.explode, already covered by the grenade tests) plus shattering the mine. This proves the piece
    // I added: the proximity ARMING/TRIGGER. A zombie outside TrapTrigger leaves it armed; a zombie inside detonates it.
    public class LandmineArmsAndDetonates : GameTest
    {
        public override string Name => "trap.landmine";
        public override IEnumerable<Step> Run()
        {
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            yield return Ticks(2);
            T.Check("landmine placed + armed (not yet exploded)", mine != null && !mine.DebugExploded);
            if (mine == null) yield break;

            // a zombie OUTSIDE the trigger radius must NOT set it off
            var far = new ZombieController();
            World.AddChild(far);
            far.GlobalPosition = new Vector3(5f, 0f, 0f);        // 5 m > 1.4 m trigger
            yield return Ticks(2);
            mine.DebugTrapCheck();
            T.Check("a zombie out of range leaves it armed", !mine.DebugExploded);

            // a zombie INSIDE the trigger radius detonates it
            var near = new ZombieController();
            World.AddChild(near);
            near.GlobalPosition = new Vector3(0.8f, 0f, 0f);     // 0.8 m < 1.4 m trigger
            yield return Ticks(2);
            mine.DebugTrapCheck();
            T.Check("a zombie in range detonates the mine", mine.DebugExploded);
        }
    }
}
