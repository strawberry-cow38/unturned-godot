using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // "add back the ability to toggle zombies off on singleplayer" (master 2026-09-02). The old noZombies
    // parameter went out with the zombie rewrite, taking UG_DEDICATED_NOZOMBIES with it.
    //
    // TWO LEGS ON PURPOSE. A test that only builds with the flag SET and asserts "no ZombieChunkField"
    // passes just as happily if Playable never spawns zombies in the harness at all -- which is exactly
    // the trap I walked into verifying this by hand: I booted --dedicated to check the toggle and got
    // silence from BOTH runs, because the spawn site lives inside `if (mode == WorldMode.Playable)` and a
    // dedicated server has no zombies to suppress. The control leg is what makes the OFF leg mean anything.
    public class ZombieToggleSingleplayer : GameTest
    {
        public override string Name => "zombie.toggle_singleplayer";
        public override double TimeoutSimSeconds => 90;

        static IEnumerable<Node> AllNodes(Node n)
        {
            yield return n;
            foreach (var c in n.GetChildren())
                foreach (var d in AllNodes(c))
                    yield return d;
        }

        int CountFields()
        {
            int n = 0;
            foreach (var x in AllNodes(World)) if (x is ZombieChunkField) n++;
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            string mapRoot = (System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR")?.TrimEnd('\\', '/')
                               ?? "/home/ec2-user/unturned") + "/Maps/PEI";
            if (!System.IO.Directory.Exists(mapRoot + "/Landscape/Heightmaps"))
            {
                T.Check("SKIPPED -- no real Unturned install found (set UG_UNTURNED_DIR)", true);
                yield break;
            }

            // A real map build writes Terrain's statics globally; leaving them set sank ten later tests
            // once already (see ladder.real_world_repro). Restore them whatever happens.
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            var oldActive = Terrain.Active; string oldMapDir = Terrain.MapDir;
            string oldEnv = System.Environment.GetEnvironmentVariable("UG_NOZOMBIES");
            try
            {
                // CONTROL: no flag -> singleplayer builds its horde. Without this passing, the OFF leg is vacuous.
                System.Environment.SetEnvironmentVariable("UG_NOZOMBIES", null);
                T.Check("with no flag set, ZombiesDisabled reads false", !WorldBuilder.ZombiesDisabled);
                var t1 = WorldBuilder.BuildFullWorld(World, WorldMode.Playable, mapRoot, "placements.txt",
                                                     syncLoad: true, activeHoliday: "NONE");
                yield return Until(() => t1.IsCompleted, 60);
                T.Check("control world built", t1.Result.Ready);
                int on = CountFields();
                T.Check($"CONTROL: singleplayer spawns the horde with no flag ({on} ZombieChunkField)", on == 1);

                foreach (var c in World.GetChildren()) c.QueueFree();
                yield return Ticks(3);

                // TREATMENT: same build, flag set -> no field at all.
                System.Environment.SetEnvironmentVariable("UG_NOZOMBIES", "1");
                T.Check("with UG_NOZOMBIES=1, ZombiesDisabled reads true", WorldBuilder.ZombiesDisabled);
                var t2 = WorldBuilder.BuildFullWorld(World, WorldMode.Playable, mapRoot, "placements.txt",
                                                     syncLoad: true, activeHoliday: "NONE");
                yield return Until(() => t2.IsCompleted, 60);
                T.Check("toggled world built (the world still comes up, it just has no horde)", t2.Result.Ready);
                int off = CountFields();
                T.Check($"UG_NOZOMBIES=1 builds the same world with NO horde ({off} ZombieChunkField)", off == 0);
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("UG_NOZOMBIES", oldEnv);
                Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
                Terrain.Active = oldActive; Terrain.MapDir = oldMapDir;
            }
        }
    }
}
