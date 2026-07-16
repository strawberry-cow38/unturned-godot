using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --heartest: a zombie reacts to the LOUDEST+CLOSEST sound it can hear (salience = loudness - dist),
    // ignoring sounds outside its HearingRange sphere or too quiet to carry that far; and while committed to a loud
    // sound it stays on task unless something LOUDER shows up (master's hearing rework).
    public class ZombieHearSalience : GameTest
    {
        public override string Name => "zombie.hear_salience";
        public override IEnumerable<Step> Run()
        {
            var z = new ZombieController();
            World.AddChild(z);   // _Ready: joins the "zombies" group, HearingRange 48
            z.GlobalPosition = Vector3.Zero;
            yield return Ticks(1);

            // all Hear calls + the readback happen inside ONE tick, like the old inline test
            z.Hear(new Vector3(10, 0, 0), 12f);   // dist 10 <= 12 loud  -> heard, salience 2
            z.Hear(new Vector3(5, 0, 0), 6f);     // dist 5  <= 6  loud  -> heard, salience 1
            z.Hear(new Vector3(40, 0, 0), 48f);   // dist 40 <= 48 loud  -> heard, salience 8 (LOUD gunshot beats near footsteps)
            z.Hear(new Vector3(3, 0, 0), 2f);     // dist 3  >  2  loud  -> IGNORED (too quiet to carry)
            z.Hear(new Vector3(60, 0, 0), 64f);   // dist 60 >  48 range -> IGNORED (outside the ears)
            var (pos, sal) = z.DebugHeard();
            T.Check($"winner is the loud gunshot at (40,0,0) sal 8 (got {pos} sal {sal:0.##})",
                pos.DistanceTo(new Vector3(40, 0, 0)) < 0.01f && Mathf.Abs(sal - 8f) < 0.01f);

            // stay-on-task gate: committed to salience 8, a quieter footstep must NOT override, a louder shot must
            T.Check("ignores a quieter footstep while on task", !z.DebugWouldOverride(8f, new Vector3(5, 0, 0), 6f));
            T.Check("switches to a louder gunshot", z.DebugWouldOverride(8f, new Vector3(10, 0, 0), 48f));
        }
    }
}
