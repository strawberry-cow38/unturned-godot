using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE INFINITE WORLD, ON THE PLAYER'S PATH (strawberry 2026-10-09). Builds the real mode (InfiniteWorld.Build, the
    // same call the menu and --infinite make), then does what a player does with it: stands there, travels 3 km in
    // one direction across several floating-origin rebases, and jumps 10^7 m away. The properties that matter:
    //   - the ground the player stands on is the ground the generator describes -- checked by raycasting the REAL
    //     colliders at exact grid vertices and requiring the generator's height to the millimetre. A transposed,
    //     mirrored, mis-offset or mis-scaled heightfield, or a rebase that moved the colliders and not the bookkeeping,
    //     all fail this and nothing else would notice (the renderer draws the mesh, not the collider);
    //   - absolute position survives the rebases exactly (travelled 3000 m -> the absolute X moved 3000 m);
    //   - the engine's own coordinates stay small however far you go -- the whole point;
    //   - nothing is ever rescued from under the ground (RegionStreamer counts it; the count must stay 0);
    //   - a body moving through a rebase keeps its velocity and its place relative to the player.
    public sealed class InfiniteWorldTests : GameTest
    {
        public override string Name => "infinite.stream_rebase_far";
        public override double TimeoutSimSeconds => 300;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        RegionStreamer S => RegionStreamer.Active;
        // the player renders LERPED between physics ticks (PlayerController's manual interp), so GlobalPosition read
        // between ticks lags the body by a fraction of a step -- reasoning from it walked the test 0.1 m a tick, not 1
        static Vector3 P(PlayerController p) => p.TruePhysicsPosition;
        bool Settled() => S.Queued == 0 && S.InFlight == 0;

        /// <summary>Raycast the colliders straight down at exact LOD0 grid vertices near the focus and compare with the
        /// generator. Returns (vertices checked, worst error in metres). Hits on a tree trunk are skipped, not counted.</summary>
        (int n, float worst) GroundMatchesGenerator(int samples, ulong salt)
        {
            var space = World.GetWorld3D().DirectSpaceState;
            var c = S.FocusRegion();
            int n = 0; float worst = 0f;
            ulong h = 0x9E3779B97F4A7C15UL ^ salt;
            for (int k = 0; k < samples * 3 && n < samples; k++)
            {
                h ^= h << 13; h ^= h >> 7; h ^= h << 17;
                var rc = new RegionCoord(c.X + (int)(h % 3) - 1, c.Z + (int)((h >> 8) % 3) - 1);   // the 3x3 that has colliders
                int i = (int)((h >> 16) % 64), j = (int)((h >> 32) % 64);
                double ax = rc.MinX + i * 4.0, az = rc.MinZ + j * 4.0;
                float want = S.Gen.HeightAt(ax, az);
                var top = S.ToLocal(ax, want + 60.0, az);
                var q = PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * 120f, 1u << 0);
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) { worst = float.MaxValue; n++; continue; }   // NO ground at all: the worst possible answer
                if (hit["collider"].As<Node>()?.Name != "GroundBody") continue;  // a tree trunk; not what this measures
                worst = Mathf.Max(worst, Mathf.Abs(((Vector3)hit["position"]).Y - want));
                n++;
            }
            return (n, worst);
        }

        /// <summary>Put the player ON the collider at an absolute XZ (or on the sea surface), the way walking leaves you.
        /// Hovering them 1.2 m up every tick, as this first did, ran the movement sim's fall velocity up unopposed and
        /// fall damage killed them every ~6 s -- the respawn then teleported them home, which read as a streaming bug.</summary>
        int _offCollider;
        void PlaceOnGround(PlayerController p, double ax, double az)
        {
            float gy = S.Gen.HeightAt(ax, az);
            var top = S.ToLocal(ax, Mathf.Max(gy, InfiniteTerrain.SeaLevel) + 40.0, az);
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * 120f, 1u << 0));
            float y;
            if (hit.Count > 0 && hit["collider"].As<Node>()?.Name == "GroundBody") y = ((Vector3)hit["position"]).Y;
            else { y = gy; if (hit.Count == 0) _offCollider++; }
            p.TeleportTo(S.ToLocal(ax, Mathf.Max(y, InfiniteTerrain.SeaLevel) + 0.05, az));
        }

        public override IEnumerable<Step> Run()
        {
            bool water0 = Terrain.HasWater; float sea0 = Terrain.SeaLevelY; var active0 = Terrain.Active; string dir0 = Terrain.MapDir;
            try { foreach (var s in Body()) yield return s; }
            finally { Terrain.HasWater = water0; Terrain.SeaLevelY = sea0; Terrain.Active = active0; Terrain.MapDir = dir0; }
        }

        IEnumerable<Step> Body()
        {
            ItemCatalog.RegisterAll();
            var res = InfiniteWorld.Build(World, 1337);
            var p = res.Player;
            T.Check("the infinite world built, with a player and a streamer", res.Ready && p != null && S != null);
            if (S == null || p == null) yield break;

            // ---- 1. STANDING STILL: the full ring set arrives, and the ground under you is the generator's
            yield return Wait(() => Settled() && S.LoadedByLod[3] > 0, 60);
            yield return Ticks(10);
            var l = S.LoadedByLod;
            T.Check($"settled: L0 {l[0]} L1 {l[1]} L2 {l[2]} L3 {l[3]} (want 25/56/144/304 = the 23x23 rings)",
                l[0] == 25 && l[1] == 56 && l[2] == 144 && l[3] == 304);
            T.Check($"ground colliders on exactly the 3x3 around the player ({S.Colliders})", S.Colliders == 9);
            var g0 = GroundMatchesGenerator(40, 1);
            T.Check($"the collider IS the generator's ground: {g0.n} grid vertices, worst error {g0.worst * 1000f:0.###} mm", g0.n >= 30 && g0.worst < 0.01f);
            float ground0 = S.Gen.HeightAt(S.AbsX(P(p).X), S.AbsZ(P(p).Z));
            T.Check($"the player is standing on it (y {P(p).Y:0.00}, ground {ground0:0.00})",
                P(p).Y > ground0 - 0.3f && P(p).Y < ground0 + 2.5f);
            T.Check($"trees grew ({S.TreeCount})", S.TreeCount > 0);
            T.Check($"ground cover grew round the player: {S.FoliageCount:N0} grass/flowers/pebbles/bushes", S.FoliageCount > 50000);
            float spawnRoad = S.Gen.RoadDistance(S.AbsX(P(p).X), S.AbsZ(P(p).Z));
            T.Check($"spawned beside a road ({spawnRoad:0.0} m from its centreline)", spawnRoad > InfiniteTerrain.RoadHalfWidth && spawnRoad < 40f);
            int roadMeshes = 0;
            foreach (var n in S.FindChildren("Road", "MeshInstance3D", true, false)) roadMeshes++;
            T.Check($"...and the road is drawn: {roadMeshes} regions carry a road surface", roadMeshes > 0);
            int spans = 0, fields = 0;
            foreach (var n in S.FindChildren("Wires", "", true, false)) if (n is PowerLineField f) { fields++; spans += f.SpanCount; }
            T.Check($"power lines strung beside it: {spans} spans in {fields} regions", spans > 10);

            // ---- 2. TRAVEL 3 km EAST at 50 m/s, across rebases, keeping to the ground
            double startAbsX = S.AbsX(P(p).X), startAbsZ = S.AbsZ(P(p).Z);
            int rebases0 = S.Rebases, worstRegions = 0, groundChecks = 0; float maxLocal = 0f, worstGround = 0f;
            for (int step = 0; step < 3000; step++)
            {
                double ax = startAbsX + step + 1.0, az = startAbsZ;   // the TARGET track, not a read-back of where we ended up
                PlaceOnGround(p, ax, az);
                yield return Ticks(1);
                maxLocal = Mathf.Max(maxLocal, Mathf.Max(Mathf.Abs(P(p).X), Mathf.Abs(P(p).Z)));
                int total = l[0] + l[1] + l[2] + l[3];
                worstRegions = System.Math.Max(worstRegions, total);
                if (step % 250 == 249)
                {
                    var g = GroundMatchesGenerator(12, (ulong)step);
                    groundChecks += g.n;
                    worstGround = Mathf.Max(worstGround, g.worst);
                }
            }
            double travelled = S.AbsX(P(p).X) - startAbsX;
            T.Check($"crossed {S.Rebases - rebases0} rebases on the way (origin now {S.OriginX:0}, {S.OriginZ:0})", S.Rebases - rebases0 >= 2);
            T.Check($"absolute position survived them: travelled {travelled:0.000} m, want 3000", System.Math.Abs(travelled - 3000.0) < 0.01);
            T.Check($"the engine never saw a coordinate past {maxLocal:0} m (rebase at {RegionStreamer.RebaseDistance:0})",
                maxLocal < RegionStreamer.RebaseDistance + InfiniteTerrain.RegionSize);
            T.Check($"memory bounded: at most {worstRegions} regions resident at once (cap {(2 * RegionStreamer.MaxRing + 3) * (2 * RegionStreamer.MaxRing + 3)})",
                worstRegions <= (2 * RegionStreamer.MaxRing + 3) * (2 * RegionStreamer.MaxRing + 3));
            T.Check($"ground under the player matched the generator all the way ({groundChecks} vertices, worst {worstGround * 1000f:0.###} mm)",
                groundChecks >= 100 && worstGround < 0.01f);
            T.Check($"there was always a collider under the player ({_offCollider} steps with none)", _offCollider == 0);
            T.Check($"the player made the whole trip alive ({p.Health:0} hp, dead {p.IsDead})", !p.IsDead);

            // ---- 3. A BODY MOVING THROUGH A REBASE keeps its velocity and its place relative to the player
            yield return Wait(Settled, 30);
            var ball = new RigidBody3D { GravityScale = 0f, LinearDamp = 0f, LinearDampMode = RigidBody3D.DampMode.Replace, CollisionLayer = 0, CollisionMask = 0 };
            ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.3f } });
            World.AddChild(ball);
            // park the player just short of the rebase line, the ball 20 m behind it, both heading +X
            double bx = S.OriginX + RegionStreamer.RebaseDistance - 30.0;
            PlaceOnGround(p, bx, startAbsZ);
            ball.GlobalPosition = P(p) + new Vector3(-20f, 30f, 0f);
            ball.LinearVelocity = new Vector3(25f, 0f, 0f);
            yield return Ticks(2);
            int rb = S.Rebases;
            Vector3 rel0 = ball.GlobalPosition - P(p), v0 = ball.LinearVelocity;
            double ballAbs0 = S.AbsX(ball.GlobalPosition.X);
            RegionStreamer.DebugShiftLog = true;
            for (int k = 0; k < 120 && S.Rebases == rb; k++)
            {
                double ax = bx + 0.5 * (k + 1);   // the player walks on at 25 m/s too
                PlaceOnGround(p, ax, startAbsZ);
                yield return Ticks(1);
            }
            yield return Ticks(2);
            Vector3 rel1 = ball.GlobalPosition - P(p), v1 = ball.LinearVelocity;
            double ballAbs1 = S.AbsX(ball.GlobalPosition.X);
            T.Check($"a rebase happened mid-flight ({S.Rebases - rb})", S.Rebases > rb);
            T.Check($"the moving body kept its velocity ({v0.X:0.00} -> {v1.X:0.00} m/s)", Mathf.Abs(v1.X - v0.X) < 0.05f);
            T.Check($"...and kept flying in ABSOLUTE space: {ballAbs1 - ballAbs0:0.00} m covered (rel to player {rel0.X:0.0} -> {rel1.X:0.0})",
                ballAbs1 - ballAbs0 > 5.0 && ballAbs1 - ballAbs0 < 120.0 && Mathf.Abs(rel1.X - rel0.X) < 5f);
            ball.QueueFree();

            // ---- 4. 10^7 m AWAY: the far-origin case, through the console command a player would use
            var console = new DevConsole { Player = p };
            World.AddChild(console);
            yield return Ticks(1);
            console.RunForTest("world tp 10000000 -3000000");
            yield return Ticks(2);
            T.Check($"teleported: abs {S.AbsX(P(p).X):0}, {S.AbsZ(P(p).Z):0}",
                System.Math.Abs(S.AbsX(P(p).X) - 1e7) < 2 && System.Math.Abs(S.AbsZ(P(p).Z) + 3e6) < 2);
            T.Check($"...with the engine still near zero ({P(p).X:0}, {P(p).Z:0})",
                Mathf.Abs(P(p).X) < InfiniteTerrain.RegionSize && Mathf.Abs(P(p).Z) < InfiniteTerrain.RegionSize);
            yield return Ticks(60);   // let them land
            var gf = GroundMatchesGenerator(40, 99);
            T.Check($"10^7 m out the collider is still the generator's ground ({gf.n} vertices, worst {gf.worst * 1000f:0.###} mm)", gf.n >= 30 && gf.worst < 0.01f);
            yield return Wait(Settled, 60);
            l = S.LoadedByLod;
            T.Check($"...and the rings refill there: L0 {l[0]} L1 {l[1]} L2 {l[2]} L3 {l[3]}", l[0] == 25 && l[1] == 56 && l[2] == 144 && l[3] == 304);
            T.Check($"nobody was ever rescued from under the ground ({S.Rescues})", S.Rescues == 0);
        }
    }
}
