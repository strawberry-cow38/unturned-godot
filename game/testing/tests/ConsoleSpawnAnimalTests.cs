using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // `spawnanimal <kind> [count]` (strawberry 2026-09-11: "how do i get a horse" -> "add it").
    //
    // Horses ride PEI's passive fauna tables, so before this the only way to see one was to walk the farmland
    // and wait for the streamer to pick one. The command builds through AnimalField.BuildAnimal, which is the
    // SAME path the streaming spawn uses -- and the point of the check below is that it stays that way. A
    // console spawn with its own private copy of "what an animal is made of" is how one of them ends up
    // missing the rig, the health or the species after somebody edits the other.
    public sealed class ConsoleSpawnAnimal : GameTest
    {
        public override string Name => "console.spawn_animal";
        public override double TimeoutSimSeconds => 30;

        int Agents()
        {
            int n = 0;
            foreach (var c in World.GetChildren())
                if (c is AnimalField f)
                    foreach (var a in f.GetChildren()) if (a is AnimalAgent) n++;
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            var player = Rigs.Player(World, new Vector3(0, 2, 0));
            // Terr null on purpose: BuildAnimal has to cope with a world that has no terrain, which is exactly
            // what a test rig and the main menu are. If it did not, this test would be the thing that found out.
            var field = new AnimalField { Player = player };
            World.AddChild(field);
            var console = new DevConsole { Player = player };
            World.AddChild(console);
            yield return Ticks(2);

            T.Check($"nothing is spawned to begin with (got {Agents()})", Agents() == 0);

            console.RunForTest("spawnanimal");
            T.Check($"the bare form lists what can be spawned (got '{console.LastEcho}')",
                    console.LastEcho.Contains("horse") && console.LastEcho.Contains("usage"));
            T.Check($"...and spawns nothing (got {Agents()})", Agents() == 0);

            console.RunForTest("spawnanimal wyvern");
            T.Check($"an unknown animal is refused, not substituted (got '{console.LastEcho}')",
                    console.LastEcho.Contains("no animal"));
            T.Check($"...and still spawns nothing (got {Agents()})", Agents() == 0);

            console.RunForTest("spawnanimal horse");
            yield return Ticks(2);
            T.Check($"`spawnanimal horse` puts one in the world (got {Agents()})", Agents() == 1);
            T.Check($"...and says so (got '{console.LastEcho}')", console.LastEcho.Contains("spawned 1 horse"));

            // It has to be an ACTUAL horse, not the fail-safe deer. SpeciesForAnimalId falls back to deer for an
            // unregistered id, so "an agent appeared" alone would pass on a command that spawned the wrong thing.
            AnimalAgent spawned = null;
            foreach (var a in field.GetChildren()) if (a is AnimalAgent ag) spawned = ag;
            T.Check($"the agent is the horse species, not the deer fallback (species {spawned?.Species})",
                    spawned != null && spawned.Species == AnimalCatalog.SpeciesForAnimalId(AnimalCatalog.HorseId));
            T.Check($"...and carries the horse's health, so it came through the shared builder (got {spawned?.Health:0})",
                    spawned != null && Mathf.IsEqualApprox(spawned.Health, 170f));

            // Count, and the guard on it. 12 is the cap; 13 must be refused rather than clamped, because a
            // silently clamped count is a command that did something other than what was typed.
            console.RunForTest("spawnanimal horse 3");
            yield return Ticks(2);
            T.Check($"a count spawns that many (got {Agents()}, expected 4)", Agents() == 4);

            console.RunForTest("spawnanimal horse 99");
            T.Check($"an out-of-range count is refused (got '{console.LastEcho}')", console.LastEcho.Contains("1-12"));
            T.Check($"...and spawns nothing more (got {Agents()}, still 4)", Agents() == 4);

            // ACROSS SEPARATE CALLS, which is the case an even split within one command gets wrong: each
            // single spawn takes i = 0 and they all land on the same arc. This is what caught it.
            console.RunForTest("spawnanimal horse");
            console.RunForTest("spawnanimal horse");
            yield return Ticks(2);
            T.Check($"four separate calls all spawned (got {Agents()}, expected 6)", Agents() == 6);

            // Spread, not a stack: animals dropped on one point would depenetrate into each other.
            var seen = new List<Vector3>();
            foreach (var a in field.GetChildren()) if (a is AnimalAgent ag) seen.Add(ag.GlobalPosition);
            float closest = float.MaxValue;
            for (int i = 0; i < seen.Count; i++)
                for (int j = i + 1; j < seen.Count; j++)
                    closest = Mathf.Min(closest, seen[i].DistanceTo(seen[j]));
            T.Check($"they are spread out rather than stacked on one spot (closest pair {closest:0.0} m)", closest > 1f);

            // And clear of the player, for the reason `vehicle` spawns at a radius: a body materialising inside
            // the player capsule gets depenetrated on the first tick, and a horse is big enough to do the shoving.
            float nearestToPlayer = float.MaxValue;
            foreach (var pos in seen) nearestToPlayer = Mathf.Min(nearestToPlayer, pos.DistanceTo(player.GlobalPosition));
            T.Check($"none of them land on the player (nearest {nearestToPlayer:0.0} m)", nearestToPlayer > 3f);
        }
    }
}
