using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // L1 tests for the zombie rewrite (docs/ZOMBIE_REWRITE_PLAN.md).
    //
    // The L0 suite proves the sim's logic against a mock navmesh. These prove the part L0 cannot: that
    // the sim is correctly wired to the ENGINE -- Godot's navigation server really answers the corridor
    // queries, rows really move on a real navmesh, and the view pool really lends rigs to the near ones.
    //
    // Deliberately built on a synthetic flat navmesh rather than PEI, so they boot in seconds alongside
    // the rest of L1 instead of paying for a full world load.
    static class NavSandbox
    {
        // A flat walkable square centred on the origin, registered as a real NavigationRegion3D.
        public static NavigationRegion3D Flat(Node3D world, float half = 120f)
        {
            var nm = new NavigationMesh();
            var verts = new Vector3[]
            {
                new Vector3(-half, 0f, -half), new Vector3(half, 0f, -half),
                new Vector3(half, 0f, half), new Vector3(-half, 0f, half),
            };
            nm.Vertices = verts;
            nm.AddPolygon(new int[] { 0, 1, 2, 3 });
            var region = new NavigationRegion3D { NavigationMesh = nm };
            world.AddChild(region);
            return region;
        }

        public static ZombieRegionBounds[] OneRegion(float half = 120f) =>
            new[] { new ZombieRegionBounds(-half, -half, half, half) };
    }

    // The headline claim of the rewrite: zombies walk without a physics body. Not "the sim says they
    // moved" -- their positions in the world change, on a real navmesh, driven by the real nav server.
    public class ZombieDirectorWalksWithNoBodies : GameTest
    {
        public override string Name => "zdirector.walks_without_bodies";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            NavSandbox.Flat(World);
            var dir = new ZombieDirector { MaxViews = 8, DebugPlayer = Vector3.Zero };
            World.AddChild(dir);
            var spawns = new List<Vector3>();
            for (int i = 0; i < 12; i++) spawns.Add(new Vector3(30f + i, 0f, 10f));
            dir.DebugBuild(NavSandbox.OneRegion(), spawns.ToArray());
            // This test is about MOVEMENT and engine wiring, not perception: let them see the player from
            // anywhere, so a failure means the navmesh path broke rather than that they spawned facing
            // the wrong way. Sight cones, line of sight and hearing have their own L0 suite.
            dir.Sim.Kinds[0].SightRange = 5000f;
            dir.Sim.Kinds[0].SightHalfAngleDeg = 180f;

            yield return Ticks(10);   // the navigation map merges its regions on a physics tick, not in _Ready
            var start = new Vector3[dir.Sim.Count];
            for (int i = 0; i < dir.Sim.Count; i++) start[i] = G(dir.Sim.PositionOf(i));

            yield return Ticks(250);  // 5 s of sim

            int moved = 0, closer = 0;
            for (int i = 0; i < dir.Sim.Count; i++)
            {
                var now = G(dir.Sim.PositionOf(i));
                if (start[i].DistanceTo(now) > 1f) moved++;
                if (now.Length() < start[i].Length() - 1f) closer++;
            }
            T.Check($"all 12 rows moved (got {moved})", moved == 12);
            T.Check($"all 12 closed on the player (got {closer})", closer == 12);
            // Lifetime, not per-tick: on any given tick the budget has usually drained and both per-tick
            // counters read 0, which says nothing about whether it ever pathed.
            T.Check($"the sim issued navmesh path queries (got {dir.Sim.TotalPathQueries})", dir.Sim.TotalPathQueries > 0);

            // The point of the whole exercise: none of this involved a physics body.
            T.Check("no CharacterBody3D exists anywhere under the director", CountDown<CharacterBody3D>(dir) == 0);
            T.Check("no NavigationAgent3D exists anywhere under the director", CountDown<NavigationAgent3D>(dir) == 0);
        }

        static Vector3 G(UnityEngine.Vector3 v) => new Vector3(v.x, v.y, v.z);

        static int CountDown<T>(Node n) where T : Node
        {
            int c = n is T ? 1 : 0;
            foreach (var ch in n.GetChildren()) c += CountDown<T>(ch);
            return c;
        }
    }

    // Rigs are lent, not owned. The pool must be capped, must go to the near ones, and must be handed
    // back when a zombie stops being worth drawing -- otherwise "views are scarce" is just a comment.
    public class ZombieDirectorLendsRigsAndTakesThemBack : GameTest
    {
        public override string Name => "zdirector.view_pool";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            NavSandbox.Flat(World);
            var cam = new Camera3D { Current = true, Fov = 60f, Far = 4000f };
            World.AddChild(cam);
            cam.GlobalPosition = Vector3.Zero;

            var dir = new ZombieDirector { MaxViews = 4, DebugPlayer = Vector3.Zero };
            World.AddChild(dir);
            var spawns = new List<Vector3>();
            for (int i = 0; i < 20; i++) spawns.Add(new Vector3(20f + i * 2f, 0f, 0f));   // all inside NearRange
            dir.DebugBuild(NavSandbox.OneRegion(), spawns.ToArray(), moveSpeed: 0.001f);  // hold position; this test is about views

            yield return Ticks(30);
            T.Check($"20 zombies, at most 4 rigs (got {dir.ViewsInUse})", dir.ViewsInUse <= 4);
            T.Check($"the pool is actually used (got {dir.ViewsInUse})", dir.ViewsInUse > 0);
            T.Check("rig nodes never exceed the cap", CountDown<RiggedCharacter>(dir) <= 4);

            // Walk the player far away: everyone drops out of the drawn tiers, and the rigs come back.
            dir.DebugPlayer = new Vector3(-3000f, 0f, -3000f);
            yield return Ticks(30);
            T.Check($"rigs released when nobody is worth drawing (got {dir.ViewsInUse})", dir.ViewsInUse == 0);
            T.Check("the sim still has every zombie", dir.Sim.Count == 20);
        }

        static int CountDown<T>(Node n) where T : Node
        {
            int c = n is T ? 1 : 0;
            foreach (var ch in n.GetChildren()) c += CountDown<T>(ch);
            return c;
        }
    }

    // Requirement 8, in-engine: a populated level with the player nowhere near it must tier everything
    // down to AMBIENT and stop doing per-zombie work.
    public class ZombieDirectorCostsNothingWhenYouAreElsewhere : GameTest
    {
        public override string Name => "zdirector.quiet_when_away";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            NavSandbox.Flat(World, 400f);
            var dir = new ZombieDirector { MaxViews = 16, DebugPlayer = new Vector3(-3000f, 0f, -3000f) };
            World.AddChild(dir);
            var spawns = new List<Vector3>();
            for (int i = 0; i < 200; i++) spawns.Add(new Vector3(100f + i % 20, 0f, 100f + i / 20));
            dir.DebugBuild(new[] { new ZombieRegionBounds(0f, 0f, 300f, 300f) }, spawns.ToArray());

            yield return Ticks(30);
            var s = dir.Sim.Stats;
            T.Check($"all 200 tiered to AMBIENT (got {s.Ambient})", s.Ambient == 200);
            T.Check($"no rigs lent (got {dir.ViewsInUse})", dir.ViewsInUse == 0);
            T.Check($"no path queries issued (got {s.PathQueries})", s.PathQueries == 0);
            T.Check($"per-tick scheduled work is a fraction of the fleet (got {s.Due} of 200)", s.Due <= 200 / 50 + 1);
        }
    }
}
