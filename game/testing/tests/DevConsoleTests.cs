using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE NO-ARG GUARD SWALLOWS VERBS SILENTLY.
    //
    // DevConsole runs every command through a guard that short-circuits any verb invoked with no argument unless it
    // is listed in NoArgVerbs. The list's own comment warns about this ("the guard silently swallows it and the verb
    // becomes unreachable"), and a 2026-08-16 review found four verbs already sitting underneath it. I then added
    // spawnMagnetableContainer and made the identical mistake: it built, it was in the autocomplete list, and it did
    // nothing. strawberry reported it as "i dont think the console is recognizing the command" -- which is exactly
    // what a swallowed verb looks like from the outside.
    //
    // Reading the array is not enough to catch this: the failure is the INTERACTION between a handler that takes no
    // argument and a list that has to be kept in step by hand. So this drives the real dispatch and asks whether the
    // world changed.
    public sealed class DevConsoleTests : GameTest
    {
        public override string Name => "console.spawn_verbs";
        public override double TimeoutSimSeconds => 60;

        // Count from the SCENE ROOT, not from World: with no PlayerController the console parents the spawn to
        // GetTree().Root (its `Player?.GetParent() ?? GetTree().Root` fallback), so a World-only count reports 0 for
        // a container that spawned perfectly well -- which is a test bug that looks exactly like the real one.
        static int CountIn(Node root)
        {
            int n = 0;
            void Walk(Node k) { if (k is MagnetableContainer) n++; foreach (var c in k.GetChildren()) Walk(c); }
            Walk(root);
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            // CLEAN UP WHAT THE CONSOLE SPAWNS. DevConsole parents its container to
            // `Player?.GetParent() ?? GetTree().Root`, and this test deliberately has no player -- so both
            // containers land in the SCENE ROOT, which outlives this test's World. Two 30 t boxes then sit
            // ~4.5 m down -Z at eye height for the REST OF THE BOOT, and that is the exact line every later
            // test shoots along: gun.damage_falloff, gun.playground_dummy and both destructible tests were
            // all firing into a container instead of their target (fired + aimed fine, round never landed),
            // and net.shell_drive could not walk past one. All five pass alone and failed in a full run.
            // Found by bisecting the run order down to console.* and validating it in isolation.
            try
            {
            Rigs.Ground(World);
            var console = new DevConsole();
            World.AddChild(console);
            yield return Ticks(2);

            T.Check("no containers in the world before the command", CountIn(World.GetTree().Root) == 0);

            // Typed exactly as a player would, mixed case and bare -- the two things that broke it.
            console.RunForTest("spawnMagnetableContainer");
            yield return Ticks(4);
            int after = CountIn(World.GetTree().Root);
            T.Check($"`spawnMagnetableContainer` with NO argument actually spawns one ({after} in the world)", after == 1);

            console.RunForTest("magcontainer");
            yield return Ticks(4);
            T.Check($"...and so does the short alias ({CountIn(World.GetTree().Root)} total)", CountIn(World.GetTree().Root) == 2);

            // A verb that genuinely does not exist must NOT spawn anything -- otherwise the check above would pass on
            // a console that spawned a container for any input at all.
            console.RunForTest("definitelynotarealverb");
            yield return Ticks(4);
            T.Check($"a nonsense verb spawns nothing ({CountIn(World.GetTree().Root)} still)", CountIn(World.GetTree().Root) == 2);
            }
            finally
            {
                // finally, not a tail call: a failed check above must not leave the containers behind, or one
                // red test here turns into five red tests scattered across the rest of the suite.
                var doomed = new List<MagnetableContainer>();
                void Walk(Node k) { if (k is MagnetableContainer mc) doomed.Add(mc); foreach (var c in k.GetChildren()) Walk(c); }
                Walk(World.GetTree().Root);
                foreach (var c in doomed) c.QueueFree();
            }
        }
    }
}
