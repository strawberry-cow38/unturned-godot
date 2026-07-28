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

    // Regression: the director wired the navmesh but never the EYES, so the sim kept its OpenField
    // default -- which answers "yes" to every sight test -- and zombies saw straight through walls. The
    // L0 suite covers the occlusion rules against a mock, which is precisely why nothing caught that no
    // real implementation was ever plugged in. This asserts the engine one is.
    public class ZombieDirectorRespectsWalls : GameTest
    {
        public override string Name => "zdirector.sight_blocked_by_walls";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            NavSandbox.Flat(World);
            var dir = new ZombieDirector { MaxViews = 4, DebugPlayer = new Vector3(0f, 0f, 20f) };
            World.AddChild(dir);
            dir.DebugBuild(NavSandbox.OneRegion(), new[] { Vector3.Zero });   // zombie at origin, facing +z at the player

            // A solid wall on the world-geometry layer, squarely between them.
            var wall = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(20f, 6f, 1f) } });
            World.AddChild(wall);
            wall.GlobalPosition = new Vector3(0f, 3f, 10f);

            yield return Ticks(30);
            T.Check($"a wall blocks sight (state {dir.Sim.StateOf(0)})", dir.Sim.StateOf(0) == ZombieState.Idle);

            wall.QueueFree();
            yield return Ticks(30);
            T.Check($"and with the wall gone it sees and chases (state {dir.Sim.StateOf(0)})",
                    dir.Sim.StateOf(0) == ZombieState.Pursue || dir.Sim.StateOf(0) == ZombieState.Attack);
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

    // Stage 6: the rewrite's zombies are sim ROWS with no collider, so every gameplay path that used to
    // find a zombie through the "zombies" node group -- bullets, melee, explosions, the sound bus --
    // addressed an empty set under --newzombies. They could not be shot, could not hear, dealt no
    // damage. These prove the replacement seams reach the sim, which is the thing a green L0 run cannot
    // tell you: the sim's combat surface was fully unit-tested and had zero callers for days.
    public class ZombieDirectorShotsReachSimRows : GameTest
    {
        public override string Name => "zdirector.shots_hit_rows";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            NavSandbox.Flat(World);
            var dir = new ZombieDirector { MaxViews = 4, DebugPlayer = Vector3.Zero };
            World.AddChild(dir);
            dir.DebugBuild(NavSandbox.OneRegion(), new[] { new Vector3(0f, 0f, 10f) });
            yield return Ticks(5);

            T.Check("a row exists to shoot at", dir.Sim.Count == 1);
            float hp0 = dir.Sim.HealthOf(0);

            // Fire from the origin straight down +Z at chest height. No collider exists anywhere on that
            // line, which is precisely why the physics ray used to pass through and do nothing.
            bool hit = dir.ShootRay(new Vector3(0f, 1.4f, 0f), new Vector3(0f, 0f, 1f), 30f, 40f, out bool killed);

            T.Check("the shot found a sim zombie with no collider", hit);
            T.Check("it lost health", dir.Sim.Count > 0 && dir.Sim.HealthOf(0) < hp0);
            T.Check("40 damage did not kill a 100 hp zombie", !killed);

            // ...and a wall-blocked shot must NOT land, or bullets would pass through cover.
            bool behindWall = dir.ShootSegment(new Vector3(0f, 1.4f, 0f), new Vector3(0f, 0f, 1f),
                                               30f, wallDistance: 2f, damage: 40f, out _, out _, out _);
            T.Check("a zombie further than the wall is not hit", !behindWall);
        }
    }

    public class ZombieDirectorHearsTheSoundBus : GameTest
    {
        public override string Name => "zdirector.hears_gunshots";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            NavSandbox.Flat(World);
            // A player IS registered, because a row with no player in its region tiers to Ambient and
            // returns before it can path anywhere -- so "it did not move" would prove nothing about
            // hearing. Sight is switched off instead, which leaves hearing as the only live channel.
            var dir = new ZombieDirector { MaxViews = 4, DebugPlayer = Vector3.Zero };
            World.AddChild(dir);
            // 20 m out: inside ZombieKind.HearingRange (32 m). At 40 m it simply cannot hear the shot,
            // and the first version of this test failed for that reason rather than for a wiring fault.
            dir.DebugBuild(NavSandbox.OneRegion(), new[] { new Vector3(0f, 0f, 20f) });
            dir.Sim.Kinds[0].SightRange = 0.01f;
            yield return Ticks(5);

            var before = dir.Sim.PositionOf(0);
            int heard = dir.Sim.Hear(new UnityEngine.Vector3(0f, 0f, 0f), 0f);   // control: silence is ignored
            T.Check("a silent emit is not heard", heard == 0);
            SoundBus.Emit(Tree, new Vector3(0f, 0f, 0f), 120f);    // a gunshot at the origin
            yield return Ticks(120);

            var after = dir.Sim.PositionOf(0);
            T.Check("a gunshot moved a blind zombie (it heard it)",
                    new Vector3(after.x, 0f, after.z).DistanceTo(new Vector3(before.x, 0f, before.z)) > 1f);
            T.Check("...and it moved TOWARD the noise", after.z < before.z - 1f);
        }
    }

    // Airdrop crate: proves the crate is actually LOOTABLE and actually BREAKABLE, in-engine. Written
    // after a verifier caught that AirdropCrate.TakeDamage had zero callers and the crate held no loot
    // at all -- a crate you cannot open is an orange box, and a destruction method nobody calls is the
    // exact shape of bug this repo has been paying for. L1 rather than L0 because spilling loot means
    // spawning real WorldItem nodes into a real tree.
    public class AirdropCrateIsLootableAndBreakable : GameTest
    {
        public override string Name => "airdrop.crate_loot_and_break";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            var crate = AirdropCrate.Spawn(World, new Vector3(0f, 0f, 0f));
            crate.Contents.AddRange(new ushort[] { 67, 67 });
            yield return Ticks(2);

            // Still in the air: looting must be refused, or a player could empty a crate mid-descent.
            T.Check("an airborne crate cannot be looted", crate.Open() == false);

            crate.MarkLanded();
            yield return Ticks(2);

            int before = CountWorldItems();
            T.Check("a landed crate opens", crate.Open());
            yield return Ticks(2);
            int after = CountWorldItems();
            T.Check("its contents reached the ground as real items", after > before);
            T.Check("and it cannot be looted twice", crate.Open() == false);

            // Breaking is a separate path and must also work -- this is the call TakeDamage never had.
            var other = AirdropCrate.Spawn(World, new Vector3(20f, 0f, 0f));
            other.Contents.Add(67);
            other.MarkLanded();
            yield return Ticks(2);
            int b2 = CountWorldItems();
            T.Check("a big hit destroys the crate", other.TakeDamage(9999f));
            yield return Ticks(2);
            T.Check("and breaking it spills the supplies rather than deleting them",
                    CountWorldItems() > b2);
        }

        int CountWorldItems()
        {
            int n = 0;
            foreach (var c in World.GetChildren()) Count(c, ref n);
            return n;
        }

        void Count(Node node, ref int n)
        {
            if (node is WorldItem) n++;
            foreach (var c in node.GetChildren()) Count(c, ref n);
        }
    }


}
