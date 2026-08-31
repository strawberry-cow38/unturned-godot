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

    // `kill` is a NO-ARG verb -- the exact shape the guard above swallows silently. It lives above the arg guard
    // with the heli rotor commands, and this drives the REAL dispatch to prove it reaches the player and triggers
    // death, rather than being eaten and looking like "the console doesn't recognize the command" (strawberry's
    // phrasing when spawnMagnetableContainer was swallowed the same way).
    public sealed class ConsoleKillTests : GameTest
    {
        public override string Name => "console.kill";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var console = new DevConsole { Player = p };
            World.AddChild(console);
            yield return Ticks(2);

            T.Check($"the player starts alive (health {p.Health:0.#})", !p.IsDead);

            console.RunForTest("kill");
            yield return Ticks(2);
            T.Check("`kill` (no-arg) actually kills the player -- not swallowed by the arg guard", p.IsDead);

            console.QueueFree();
        }
    }

    // Vehicles used to drop onto the console's look-point, so looking down at your own feet dropped a multi-ton
    // RigidBody straight onto the player capsule; the first-tick depenetration then shoved the player DOWN through
    // the thin terrain collider to the -1030 kill plane (strawberry 2026-08-31). The fix pushes the spawn out to a
    // clear radius. Teeth: the geometry check below fails outright if the vehicle is dropped on top of the player.
    public sealed class VehicleSpawnNoTunnelTests : GameTest
    {
        public override string Name => "vehicle.spawn_clear_of_player";
        public override double TimeoutSimSeconds => 60;

        static Vehicle FindVehicle(Node root)
        {
            if (root is Vehicle v) return v;
            foreach (var c in root.GetChildren()) { var f = FindVehicle(c); if (f != null) return f; }
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var terrain = Terrain.CreateFlat(6, 6, withCollider: true);
            World.AddChild(terrain);
            yield return Ticks(2);
            float groundY = terrain.SurfaceHeightWorld(0f, 0f);
            var p = Rigs.Player(World, new Vector3(0f, groundY + 1f, 0f));
            var console = new DevConsole { Player = p };
            World.AddChild(console);

            // Look straight down so LookPoint() lands on the player's own feet -- the exact repro.
            PlayerController.DebugForceLookScan = true;
            p.DebugSetPitch(-88f);
            yield return Ticks(4);

            Vector3 lp = p.LookPoint();
            float look = new Vector2(lp.X - p.GlobalPosition.X, lp.Z - p.GlobalPosition.Z).Length();
            T.Check($"precondition: the look-point sits on top of the player ({look:0.#} m)", look < 4f);

            float startY = p.GlobalPosition.Y;
            console.RunForTest("veh offroader");   // a heavy 4-door; its drop used to tunnel the player
            yield return Ticks(4);

            var veh = FindVehicle(World);
            T.Check("a vehicle was spawned", veh != null);
            if (veh != null)
            {
                float d = new Vector2(veh.GlobalPosition.X - p.GlobalPosition.X, veh.GlobalPosition.Z - p.GlobalPosition.Z).Length();
                T.Check($"it spawned CLEAR of the player ({d:0.#} m), not dropped on top of them", d > 4f);
            }

            yield return Ticks(36);   // let the drop land + the solver settle
            T.Check($"the player was not shoved through the terrain (y {startY:0.#} -> {p.GlobalPosition.Y:0.#})", p.GlobalPosition.Y > groundY - 3f);
            T.Check("...nor killed by an out-of-bounds fall", !p.IsDead);

            PlayerController.DebugForceLookScan = false;
            if (veh != null && GodotObject.IsInstanceValid(veh)) veh.QueueFree();
            console.QueueFree();
        }
    }
}
