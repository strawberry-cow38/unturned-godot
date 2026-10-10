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
            if (hit.Count > 0 && hit["collider"].As<Node>()?.Name.ToString() is "GroundBody" or "Paved" or "Trail") y = ((Vector3)hit["position"]).Y;   // a road is ground too
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
            // the core lays the bridge kit with numbers copied from the bridge tool (core cannot see the game): they must agree
            T.Check($"the infinite world's bridge kit is the bridge tool's (pitch {InfiniteRoads.BridgePitch} / {EditorBridgeSpline.Pitch}, half-width {InfiniteRoads.BridgeHalfWidth} / {EditorBridgeSpline.HalfWidth})",
                InfiniteRoads.BridgePitch == EditorBridgeSpline.Pitch && InfiniteRoads.BridgeHalfWidth == EditorBridgeSpline.HalfWidth
                && InfiniteRoads.DeckSoffit == EditorBridgeSpline.DeckSoffit && InfiniteRoads.DeckParapetTop == EditorBridgeSpline.DeckParapetTop
                && InfiniteRoads.PierTop == EditorBridgeSpline.PierTop && InfiniteRoads.PierBottom == EditorBridgeSpline.PierBottom
                && InfiniteRoads.PierEveryUnits == EditorBridgeSpline.PierEveryUnits && InfiniteRoads.MinPierDrop == EditorBridgeSpline.MinPierDrop);
            {
                // ...and the tunnel section's numbers are the prop's own, read off it the way the tunnel tool reads them
                var prof = TunnelMesh.ProfileFrom(ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/") + EditorTunnelSpline.BoreUnit + ".obj"));
                float shellHalf = 0f, top = 0f, crown = 0f, feet = float.MaxValue;
                foreach (var ch in prof) foreach (var q in ch) { shellHalf = Mathf.Max(shellHalf, Mathf.Abs(q.X)); top = Mathf.Max(top, q.Y); feet = Mathf.Min(feet, q.Y); }
                float boreHalf = TunnelMesh.BoreHalfWidth(prof);
                foreach (var ch in prof) { float w = 0f, hi = 0f; foreach (var q in ch) { w = Mathf.Max(w, Mathf.Abs(q.X)); hi = Mathf.Max(hi, q.Y); } if (Mathf.Abs(w - boreHalf) < 0.01f) crown = hi; }
                T.Check($"the infinite world's tunnel section is the tunnel prop's (bore half {InfiniteRoads.TunnelBoreHalf} / {boreHalf:0.00}, shell half {InfiniteRoads.TunnelShellHalf} / {shellHalf:0.00}, " +
                        $"crown {InfiniteRoads.TunnelBoreTop} / {crown:0.00}, shell top {InfiniteRoads.ShellTop(0f)} / {top:0.00}, feet -{InfiniteRoads.TunnelFloorDrop} / {feet:0.00}; section {InfiniteRoads.TunnelSectionLength} / {EditorTunnelSpline.PortalLength})",
                    Mathf.Abs(boreHalf - InfiniteRoads.TunnelBoreHalf) < 0.01f && Mathf.Abs(shellHalf - InfiniteRoads.TunnelShellHalf) < 0.01f
                    && Mathf.Abs(crown - InfiniteRoads.TunnelBoreTop) < 0.01f && Mathf.Abs(top - InfiniteRoads.ShellTop(0f)) < 0.01f
                    && Mathf.Abs(feet + InfiniteRoads.TunnelFloorDrop) < 0.01f && InfiniteRoads.TunnelSectionLength == EditorTunnelSpline.PortalLength
                    && InfiniteRoads.TunnelBorder == EditorTunnelSpline.BoreBorder && InfiniteRoads.TunnelStep == EditorTunnelSpline.Step);
            }

            T.Check($"ground cover grew round the player: {S.FoliageCount:N0} grass/flowers/pebbles/bushes", S.FoliageCount > 50000);
            float spawnRoad = S.Gen.RoadClearance(S.AbsX(P(p).X), S.AbsZ(P(p).Z));
            T.Check($"spawned beside a road ({spawnRoad:0.0} m from its asphalt)", spawnRoad > 0f && spawnRoad < 40f);
            int roadMeshes = 0;
            foreach (var n in S.FindChildren("Road_*", "MeshInstance3D", true, false)) roadMeshes++;
            T.Check($"...and the road is drawn: {roadMeshes} regions carry a road surface", roadMeshes > 0);
            {
                // the road is SOLID, with its thickness: a ray onto a road's middle stops on the slab's top at the core's
                // SurfaceY (strawberry 2026-10-09: "give the road splines actual collision and the proper thickness"),
                // on a body tagged for its surface. Before, the drawn ribbon had no collider and a car drove on the
                // ground 0.12 m under the asphalt it was shown on.
                var space = World.GetWorld3D().DirectSpaceState;
                var fc = S.FocusRegion();
                int probes = 0, wrongBody = 0; float worstTop = 0f; var kinds = new int[4];
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        foreach (var rp in S.Gen.Generate(new RegionCoord(fc.X + dx, fc.Z + dz), 0).Roads)
                        {
                            var kind = (RoadKind)rp.Kind;
                            if (kind == RoadKind.Highway || kinds[rp.Kind] >= 6) continue;   // highways: the bridge-end probe below
                            double mx = (rp.X0 + rp.X1) * 0.5, mz = (rp.Z0 + rp.Z1) * 0.5;
                            // only where THIS road is the one under the point (not a junction another road shapes)
                            var under = S.Gen.Roads.Influence(mx, mz);
                            if (!under.Any || under.Kind != kind || under.Dist > 0.5f) continue;
                            float want = InfiniteRoads.SurfaceY(kind, (rp.H0 + rp.H1) * 0.5f);
                            var top = S.ToLocal(mx, want + 5.0, mz);
                            var rh = space.IntersectRay(PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * 15f, 1u << 0));
                            probes++; kinds[rp.Kind]++;
                            if (rh.Count == 0) { worstTop = float.MaxValue; continue; }
                            if (rh["collider"].As<Node>()?.Name != (kind == RoadKind.Trail ? "Trail" : "Paved")) wrongBody++;
                            worstTop = Mathf.Max(worstTop, Mathf.Abs(((Vector3)rh["position"]).Y - want));
                        }
                T.Check($"a ray onto a road stops ON it: {probes} probes (main {kinds[1]}, small {kinds[2]}, trail {kinds[3]}), top within {worstTop * 1000f:0.0} mm of the driven surface, {wrongBody} on the wrong body",
                    probes >= 4 && worstTop < 0.01f && wrongBody == 0);
            }
            int spans = 0, fields = 0;
            foreach (var n in S.FindChildren("Wires", "", true, false)) if (n is PowerLineField f) { fields++; spans += f.SpanCount; }
            T.Check($"power lines strung beside it: {spans} spans in {fields} regions", spans > 10);
            // WEATHER (strawberry 2026-10-10: "work on getting the weather engine in the inf world mode"): the same
            // WeatherManager as PEI, on this world's clock. Set to heavy rain now; the trip below carries it through rebases.
            var wm = WeatherManager.Current;
            T.Check($"the weather runs here: {(wm == null ? "no WeatherManager" : $"WeatherManager on the world's {wm.Cycle?.DayLength:0} s day")}",
                wm != null && wm.Cycle != null && wm.Cycle == res.DayNight);
            wm?.Sim.SetPerpetual(1);

            // ---- 2. TRAVEL 3 km EAST at 50 m/s, across rebases, keeping to the ground
            double startAbsX = S.AbsX(P(p).X), startAbsZ = S.AbsZ(P(p).Z);
            int rebases0 = S.Rebases, worstRegions = 0, groundChecks = 0; float maxLocal = 0f, worstGround = 0f;
            double patMin = double.MaxValue, patMax = double.MinValue;   // where the SHADERS see the start spot (WorldOrigin)
            for (int step = 0; step < 3000; step++)
            {
                double ax = startAbsX + step + 1.0, az = startAbsZ;   // the TARGET track, not a read-back of where we ended up
                PlaceOnGround(p, ax, az);
                yield return Ticks(1);
                maxLocal = Mathf.Max(maxLocal, Mathf.Max(Mathf.Abs(P(p).X), Mathf.Abs(P(p).Z)));
                double pat = (float)(startAbsX - S.OriginX) + WorldOrigin.Offset.X;   // the engine's local x for that spot + the shaders' offset
                patMin = System.Math.Min(patMin, pat); patMax = System.Math.Max(patMax, pat);
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
            // the rebase twitch (strawberry): water, wind and ripple patterns are drawn from local + WorldOrigin.Offset,
            // so a FIXED spot must read the same pattern position through every rebase. Without the offset it moved a
            // kilometre at each one.
            T.Check($"world-space shader patterns stayed put through them: a fixed spot drifted {patMax - patMin:0.000} m (WorldOrigin.Offset {WorldOrigin.Offset.X:0}, {WorldOrigin.Offset.Y:0})",
                patMax - patMin < 0.01);
            T.Check($"the engine never saw a coordinate past {maxLocal:0} m (rebase at {RegionStreamer.RebaseDistance:0})",
                maxLocal < RegionStreamer.RebaseDistance + InfiniteTerrain.RegionSize);
            T.Check($"memory bounded: at most {worstRegions} regions resident at once (cap {(2 * RegionStreamer.MaxRing + 3) * (2 * RegionStreamer.MaxRing + 3)})",
                worstRegions <= (2 * RegionStreamer.MaxRing + 3) * (2 * RegionStreamer.MaxRing + 3));
            T.Check($"ground under the player matched the generator all the way ({groundChecks} vertices, worst {worstGround * 1000f:0.###} mm)",
                groundChecks >= 100 && worstGround < 0.01f);
            T.Check($"there was always a collider under the player ({_offCollider} steps with none)", _offCollider == 0);
            T.Check($"the player made the whole trip alive ({p.Health:0} hp, dead {p.IsDead})", !p.IsDead);
            if (wm != null)
            {
                T.Check($"and it rained the whole way: rain intensity {wm.Rain3DIntensity:0.00} ({wm.Sim.Active?.Name}, blend {wm.RainIntensity:0.00})", wm.Rain3DIntensity > 0.2f);
                // THE RAIN'S ROOF MAP is filed by engine-space cell, and a rebase moves the world under it. Let it fill round
                // the player, then FORCE a rebase and read it back the same frame -- before anything can re-cast: re-filed,
                // the cells round the player are still known and still the ground here; un-re-filed, they are unknown (or
                // somewhere else's).
                var roof = RainRoofMap.Current;
                var space2 = World.GetWorld3D().DirectSpaceState;
                (int n, float worst, string where) RoofVsGround()
                {
                    int n = 0; float worst = 0f; string where = "";
                    for (int dz = -12; dz <= 12 && roof != null; dz += 3)
                        for (int dx = -12; dx <= 12; dx += 3)
                        {
                            var at = P(p) + new Vector3(dx, 0f, dz);
                            // a fresh ray where the cache's own cast went: the 0.5 m cell's centre, onto the COLLIDER (the
                            // 4 m grid it is drawn from sits up to ~15 cm off the smooth height function between vertices)
                            var cc = new Vector3((Mathf.Floor(at.X / RainRoofMap.Cell) + 0.5f) * RainRoofMap.Cell, at.Y + RainRoofMap.Above, (Mathf.Floor(at.Z / RainRoofMap.Cell) + 0.5f) * RainRoofMap.Cell);
                            var hit = space2.IntersectRay(PhysicsRayQueryParameters3D.Create(cc, cc + Vector3.Down * RainRoofMap.RayLen, RainRoofMap.SolidMask));
                            if (hit.Count == 0 || hit["collider"].As<Node>()?.Name != "GroundBody") continue;   // open ground only
                            n++;
                            float cached = roof.RoofYAt(at), fresh = ((Vector3)hit["position"]).Y;
                            float err = cached == float.MinValue ? float.PositiveInfinity : Mathf.Abs(cached - fresh);
                            if (err > worst) { worst = err; where = $" worst at ({dx},{dz}): cached {(cached == float.MinValue ? "unknown" : cached.ToString("0.00"))} vs ground {fresh:0.00}"; }
                        }
                    return (n, worst, where);
                }
                yield return Ticks(240);   // the window fills at RaysPerFrame
                var before = RoofVsGround();
                T.Check($"the rain's roof map knows the ground round the player after the trip: {before.n} open cells within {before.worst * 100f:0.0} cm{before.where}",
                    roof != null && before.n >= 20 && before.worst < 0.05f);
                // the same WORLD spots, read back through the new origin the same frame (before anything can re-cast, and
                // before the physics space has caught up with the moved bodies, so no fresh ray is trusted here)
                var spots = new System.Collections.Generic.List<(double ax, double az, float y)>();
                for (int dz = -12; dz <= 12 && roof != null; dz += 3)
                    for (int dx = -12; dx <= 12; dx += 3)
                    {
                        var at = P(p) + new Vector3(dx, 0f, dz);
                        float y = roof.RoofYAt(at);
                        if (y != float.MinValue) spots.Add((S.AbsX(at.X), S.AbsZ(at.Z), y));
                    }
                S.DebugShift(1, 0);
                int same = 0; string firstOff = "";
                foreach (var (sax, saz, y) in spots)
                {
                    var now = S.ToLocal(sax, 0.0, saz);
                    float y2 = roof.RoofYAt(now);
                    if (y2 == y) same++;
                    else if (firstOff == "") firstOff = $"; first miss at ({sax:0}, {saz:0}): {y:0.00} before, {(y2 == float.MinValue ? "unknown" : y2.ToString("0.00"))} after";
                }
                T.Check($"...and a rebase re-files it: {same} of {spots.Count} cells read the same through the new origin{firstOff}",
                    spots.Count >= 40 && same == spots.Count);
                // ...and it is not keeping the whole trip: a 64 m window swept 3 km, evicting past EvictRadius
                int tileBound = (int)(2f * RainRoofMap.EvictRadius / (RainRoofMap.Cell * 32f) + 2) * (int)(2f * RainRoofMap.Half / (RainRoofMap.Cell * 32f) + 2);
                T.Check($"its cache is bounded: {RainRoofMap.TileCount} tiles after the trip (a straight sweep keeps at most {tileBound})",
                    RainRoofMap.TileCount <= tileBound);
            }

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
            // ---- 5. A BRIDGE: stand beside the first raised highway stretch the generator knows of, and drive-test its deck
            BridgePiece? deck = null;
            for (int axis = 0; axis < 2 && deck == null; axis++)
                for (long band = -1; band <= 0 && deck == null; band++)
                    for (long k = -2; k <= 1 && deck == null; k++)
                        foreach (var bp in S.Gen.Roads.BridgesOf(axis, band, k))
                            if (bp.Kind == 0) { deck = bp; break; }
            T.Check("the generator has a bridge to visit", deck != null);
            if (deck is BridgePiece dk)
            {
                S.TeleportAbsolute(dk.X + 40.0, dk.Z + 40.0);
                yield return Wait(Settled, 60);
                yield return Ticks(5);
                T.Check($"bridges stream in round it: {S.BridgeCount} deck units in range", S.BridgeCount > 0);
                // the deck is a SURFACE: a ray dropped onto a deck unit's centre stops on its roadway, not on the valley floor
                var top = S.ToLocal(dk.X, dk.Y + 30.0, dk.Z);
                var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * 200f, 1u << 0));
                float roadY = hit.Count > 0 ? ((Vector3)hit["position"]).Y : float.NaN;
                float ground = S.Gen.NaturalHeight(dk.X, dk.Z);
                T.Check($"a ray onto the deck stops on its roadway: y {roadY:0.00} vs deck {dk.Y:0.00} (natural ground {ground:0.0} below)",
                    hit.Count > 0 && Mathf.Abs(roadY - (float)dk.Y) < 0.01f && dk.Y - ground > 2f);
            }
            // ---- 6. A BRIDGE END: the road slab carries straight on to the deck -- same height on both sides of the
            // joint, both solid (strawberry 2026-10-09: "make sure they are aligned properly and theres no big gap")
            BridgePiece? cap = null;
            for (int axis = 0; axis < 2 && cap == null; axis++)
                for (long band = -1; band <= 0 && cap == null; band++)
                    for (long k = -2; k <= 1 && cap == null; k++)
                        foreach (var bp in S.Gen.Roads.BridgesOf(axis, band, k))
                            if (bp.Kind == 2) { cap = bp; break; }
            T.Check("the generator has a bridge end to visit", cap != null);
            if (cap is BridgePiece cp)
            {
                S.TeleportAbsolute(cp.X + 30.0, cp.Z + 30.0);
                yield return Wait(Settled, 60);
                yield return Ticks(5);
                var space = World.GetWorld3D().DirectSpaceState;
                double ch = Mathf.Sqrt(cp.DX * cp.DX + cp.DZ * cp.DZ);
                // the cap faces OUT of the bridge: +D is the approach road, -D the deck
                (float y, string body) Drop(double along)
                {
                    double x = cp.X + cp.DX / ch * along, z = cp.Z + cp.DZ / ch * along;
                    var top = S.ToLocal(x, cp.Y + 5.0, z);
                    var h = space.IntersectRay(PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * 30f, 1u << 0));
                    return h.Count > 0 ? (((Vector3)h["position"]).Y, h["collider"].As<Node>()?.Name ?? "?") : (float.NaN, "nothing");
                }
                float grade = cp.DY / (float)ch;
                var (roadIn, bodyIn) = Drop(-0.25); var (roadOut, bodyOut) = Drop(0.25);
                var (roadFar, bodyFar) = Drop(3.0);
                float stepAcross = roadOut - roadIn - grade * 0.5f;
                T.Check($"across the deck end the surface is continuous: deck {roadIn:0.000} ({bodyIn}) -> road {roadOut:0.000} ({bodyOut}), step {stepAcross * 1000f:0.0} mm after the {grade * 100f:0.0}% grade",
                    bodyOut == "Paved" && bodyIn != "nothing" && bodyIn != "GroundBody" && Mathf.Abs(stepAcross) < 0.01f);
                // the deck's baked two-way paint is gone from what is DRAWN (the highway mesh lays Highway_1 over it)
                T.Check($"the deck unit's own roadway is stripped from its render mesh ({RegionStreamer.DeckRoadwayTrisStripped} triangles)",
                    RegionStreamer.DeckRoadwayTrisStripped > 0);
                T.Check($"...and 3 m along the approach it is still the road slab at its surface ({roadFar:0.000} on {bodyFar}, cap at {cp.Y:0.000})",
                    bodyFar == "Paved" && Mathf.Abs(roadFar - ((float)cp.Y + grade * 3f)) < 0.03f);
            }
            // ---- 7. A TUNNEL (strawberry 2026-10-09: "wiring up tunnels to use the tool nyatools made"): drive into the
            // mouth -- the ground's heightfield has HOLES there or the hill's surface runs straight across it -- stand on
            // the road under the hill without being "rescued" onto the hilltop, and hit the bore's walls and ceiling from
            // INSIDE (the sweep's faces point out; one-sided, you would walk through them)
            InfiniteRoads.TunnelSpan tun = null;
            for (int axis = 0; axis < 2 && tun == null; axis++)
                for (long band = -2; band <= 1 && tun == null; band++)
                    for (long k = -4; k <= 3 && tun == null; k++)
                        foreach (var t in S.Gen.Roads.TunnelsOf(axis, band, k)) { tun = t; break; }
            T.Check("the generator has a tunnel to visit", tun != null);
            if (tun != null)
            {
                int tm = tun.X.Length, mid = tm / 2;
                double ux = tun.X[1] - tun.X[0], uz = tun.Z[1] - tun.Z[0], ul = System.Math.Sqrt(ux * ux + uz * uz); ux /= ul; uz /= ul;   // into the tunnel
                // over its MIDDLE, so the whole tunnel (up to ~300 m) and both mouths are inside the collider ring
                S.TeleportAbsolute(tun.X[mid], tun.Z[mid]);
                yield return Wait(Settled, 60);
                yield return Ticks(5);
                var space = World.GetWorld3D().DirectSpaceState;
                T.Check($"tunnels stream in round it: {S.TunnelCount} in range", S.TunnelCount > 0);
                // into a tube's mouth along its carriageway, 3 m up: nothing between 15 m out and 15 m in
                var from = S.ToLocal(tun.SX[1][0] - ux * 15.0, tun.Y[0] + 3.0, tun.SZ[1][0] - uz * 15.0);
                var into = S.ToLocal(tun.SX[1][0] + ux * 15.0, tun.Y[0] + 3.0 + (tun.Y[System.Math.Min(7, tm - 1)] - tun.Y[0]), tun.SZ[1][0] + uz * 15.0);
                var mouth = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, into, 1u << 0));
                // CONTROL, so "nothing" means open and not "no ground loaded": just past the hole band, over the tubes, a ray
                // from the sky must land on the ground -- the hill cut back over the portal, in the same cells' neighbourhood
                var capAt = S.ToLocal(tun.X[0] + ux * (InfiniteRoads.TunnelHoleIn + 8.0), tun.Y[0] + 200.0, tun.Z[0] + uz * (InfiniteRoads.TunnelHoleIn + 8.0));
                var hillRay = space.IntersectRay(PhysicsRayQueryParameters3D.Create(capAt, capAt + Vector3.Down * 260f, 1u << 0));
                string hillName = hillRay.Count > 0 ? hillRay["collider"].As<Node>()?.Name : "nothing";
                T.Check($"the mouth is open: a ray along the road from 15 m out to 15 m in hits {(mouth.Count == 0 ? "nothing" : mouth["collider"].As<Node>()?.Name + " at " + ((Vector3)mouth["position"]).DistanceTo(from).ToString("0.0") + " m")}" +
                        $" (control: a drop onto the hill just behind the mouth hits {hillName})",
                    mouth.Count == 0 && hillName == "GroundBody");
                // the DRAWN road through the mouth and the forecourts lies at its driven surface: nothing lifts it onto the
                // ground mesh, which there is the hill (or a cell across the facade interpolating it). The first render
                // stood the approach's last pieces up as 6 m walls across the mouth
                {
                    double alongMax = 0; var stH = new double[tm];
                    for (int i = 1; i < tm; i++) stH[i] = stH[i - 1] + System.Math.Sqrt((tun.X[i] - tun.X[i - 1]) * (tun.X[i] - tun.X[i - 1]) + (tun.Z[i] - tun.Z[i - 1]) * (tun.Z[i] - tun.Z[i - 1]));
                    alongMax = stH[tm - 1];
                    float worstLift = 0f; int verts = 0;
                    foreach (var n in S.FindChildren("Road_Highway", "MeshInstance3D", true, false))
                    {
                        var mi = (MeshInstance3D)n;
                        if (mi.Mesh == null) continue;
                        var vs = mi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var xf = mi.GlobalTransform;
                        foreach (var lv in vs)
                        {
                            var gv = xf * lv;
                            double ax = S.AbsX(gv.X), az = S.AbsZ(gv.Z);
                            // nearest station segment: along and across
                            double bestD = double.MaxValue, along = 0; float ry = 0;
                            for (int i = 0; i + 1 < tm; i++)
                            {
                                double sx = tun.X[i + 1] - tun.X[i], sz = tun.Z[i + 1] - tun.Z[i], qx = ax - tun.X[i], qz = az - tun.Z[i];
                                double ss = sx * sx + sz * sz, tt = (qx * sx + qz * sz) / ss;
                                double tc = System.Math.Clamp(tt, 0, 1), dd = System.Math.Sqrt((qx - sx * tc) * (qx - sx * tc) + (qz - sz * tc) * (qz - sz * tc));
                                if (dd < bestD) { bestD = dd; along = stH[i] + tt * System.Math.Sqrt(ss); ry = tun.Y[i] + (tun.Y[i + 1] - tun.Y[i]) * (float)tc; }
                            }
                            if (bestD > InfiniteRoads.PavedHalf(RoadKind.Highway) + 1.0 || along < -InfiniteRoads.TunnelForecourt || along > alongMax + InfiniteRoads.TunnelForecourt) continue;
                            // beyond the facades the profile runs on at the end's grade: allow it, and the road's own surface
                            double past = along < 0 ? -along : along > alongMax ? along - alongMax : 0;
                            verts++;
                            worstLift = Mathf.Max(worstLift, gv.Y - ry - (float)(past * InfiniteRoads.MaxGrade(RoadKind.Highway)));
                        }
                    }
                    T.Check($"the drawn road through the tunnel and its forecourts stays on its surface: {verts} vertices, worst {worstLift:0.00} m above it",
                        verts > 100 && worstLift < 0.05f);
                }
                // the road under the hill: a drop from 5 m over a CARRIAGEWAY (the stations run down the median) lands on
                // the slab at its driven surface
                var side = new Vector3((float)-uz, 0f, (float)ux);
                var top = S.ToLocal(tun.X[mid], tun.Y[mid] + 5.0, tun.Z[mid]);
                var lane = top + side * InfiniteRoads.HighwayRibbonOffset;
                var onRoad = space.IntersectRay(PhysicsRayQueryParameters3D.Create(lane, lane + Vector3.Down * 10f, 1u << 0));
                float roadY = onRoad.Count > 0 ? ((Vector3)onRoad["position"]).Y : float.NaN;
                T.Check($"under the hill the road is solid: y {roadY:0.000} vs {tun.Y[mid]:0.000} on {(onRoad.Count > 0 ? onRoad["collider"].As<Node>()?.Name : "nothing")}",
                    onRoad.Count > 0 && Mathf.Abs(roadY - tun.Y[mid]) < 0.02f && onRoad["collider"].As<Node>()?.Name == "Paved");
                // ...and between the slab and the wall, the floor at the road's bed
                var verge = top + side * (InfiniteRoads.PavedHalf(RoadKind.Highway) + 0.8f);
                var onFloor = space.IntersectRay(PhysicsRayQueryParameters3D.Create(verge, verge + Vector3.Down * 10f, 1u << 0));
                float floorY = onFloor.Count > 0 ? ((Vector3)onFloor["position"]).Y : float.NaN, bedY = tun.Y[mid] - InfiniteRoads.Proud - InfiniteRoads.Lift(RoadKind.Highway);
                T.Check($"beside it, the floor: y {floorY:0.000} vs the bed {bedY:0.000}", onFloor.Count > 0 && Mathf.Abs(floorY - bedY) < 0.06f);
                // the ceiling and both walls of the +offset tube, from its own carriageway
                var up = space.IntersectRay(PhysicsRayQueryParameters3D.Create(lane, lane + Vector3.Up * 30f, 1u << 0));
                float ceil = up.Count > 0 ? ((Vector3)up["position"]).Y - tun.Y[mid] : float.NaN;
                var eye = lane + Vector3.Down * 3f;
                float Wall(Vector3 dir)
                {
                    var w = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, eye + dir * 40f, 1u << 0));
                    return w.Count > 0 ? ((Vector3)w["position"] - eye).Length() : float.NaN;
                }
                float wallOut = Wall(side), wallIn = Wall(-side);
                float boreW = InfiniteRoads.TunnelBoreHalf * InfiniteRoads.TunnelLateral;
                T.Check($"the bore holds you in: ceiling {ceil:0.00} m over the road (crown {InfiniteRoads.TunnelBoreTop}), walls {wallIn:0.00} / {wallOut:0.00} m either side (bore {boreW:0.00})",
                    Mathf.Abs(ceil - InfiniteRoads.TunnelBoreTop) < 0.2f && Mathf.Abs(wallOut - boreW) < 0.3f && Mathf.Abs(wallIn - boreW) < 0.3f);
                // two tubes, not one: from the median a wall stands within the gap either side
                var mid3 = top + Vector3.Down * 3f;
                var gapR = space.IntersectRay(PhysicsRayQueryParameters3D.Create(mid3, mid3 + side * 5f, 1u << 0));
                var gapL = space.IntersectRay(PhysicsRayQueryParameters3D.Create(mid3, mid3 - side * 5f, 1u << 0));
                float gr = gapR.Count > 0 ? ((Vector3)gapR["position"] - mid3).Length() : float.NaN, gl = gapL.Count > 0 ? ((Vector3)gapL["position"] - mid3).Length() : float.NaN;
                float halfGap = InfiniteRoads.HighwayRibbonOffset - boreW;
                T.Check($"one tube per carriageway: from the median, walls {gl:0.00} / {gr:0.00} m away (the tubes stand {2f * halfGap:0.00} m apart)",
                    Mathf.Abs(gr - halfGap) < 0.2f && Mathf.Abs(gl - halfGap) < 0.2f);
                // and the hill is over it all: from high above, the first thing hit is the ground, above the shell
                var sky = S.ToLocal(tun.X[mid], tun.Y[mid] + 300.0, tun.Z[mid]);
                var hill = space.IntersectRay(PhysicsRayQueryParameters3D.Create(sky, sky + Vector3.Down * 400f, 1u << 0));
                float hillY = hill.Count > 0 ? ((Vector3)hill["position"]).Y - tun.Y[mid] : float.NaN;
                T.Check($"the hill stands over it: {hillY:0.0} m above the road on {(hill.Count > 0 ? hill["collider"].As<Node>()?.Name : "nothing")} (shell top {InfiniteRoads.ShellTop(0f)})",
                    hill.Count > 0 && hill["collider"].As<Node>()?.Name == "GroundBody" && hillY > InfiniteRoads.ShellTop(0f));
                // stand in it: the guard must not lift you onto the hill
                int rescues0 = S.Rescues;
                p.TeleportTo(lane + Vector3.Down * 4.7f);
                yield return Ticks(90);
                float standY = P(p).Y;
                T.Check($"standing in the tunnel: y {standY:0.00} vs road {tun.Y[mid]:0.00}, rescues {S.Rescues - rescues0}",
                    S.Rescues == rescues0 && Mathf.Abs(standY - tun.Y[mid]) < 1.5f);
            }
            // ---- 8. GRADE SEPARATIONS (strawberry 2026-10-10: "routing roads around eachother/over/under eachother with
            // bridges"): at the nearest OVERPASS the main is carried on its own (widened) decks and the highway runs
            // open underneath; at the nearest UNDERPASS the highway's decks carry it over the main's open cutting. Each
            // "open" ray has a CONTROL 7 m up -- above the deck's soffit -- that must hit the deck, so "nothing" means
            // open and not "no colliders here"
            foreach (bool wantOver in new[] { true, false })
            {
                InfiniteRoads.Underpass up = null; (double x, double z, float h)[] mline = null; double best = double.MaxValue;
                for (long mcx = -4; mcx <= 3; mcx++)
                    for (long mcz = -4; mcz <= 3; mcz++)
                        for (int d = 0; d < 2; d++)
                            foreach (var u in S.Gen.Roads.MainUnderpasses(mcx, mcz, d))
                                if (!u.Existing && u.Over == wantOver && u.X * u.X + u.Z * u.Z < best) { best = u.X * u.X + u.Z * u.Z; up = u; mline = S.Gen.Roads.MainCentreline(mcx, mcz, d); }
                string kind = wantOver ? "overpass" : "underpass";
                T.Check($"the generator has an {kind} to visit", up != null);
                if (up == null) continue;
                // the main's heading there, and the highway's (square to it: the crossing is laid square-on)
                int bi = 0; double bd = double.MaxValue;
                for (int i = 0; i < mline.Length; i++) { double dd = (mline[i].x - up.X) * (mline[i].x - up.X) + (mline[i].z - up.Z) * (mline[i].z - up.Z); if (dd < bd) { bd = dd; bi = i; } }
                int i0 = System.Math.Max(0, bi - 1), i1 = System.Math.Min(mline.Length - 1, bi + 1);
                double mx = mline[i1].x - mline[i0].x, mz = mline[i1].z - mline[i0].z, ml = System.Math.Sqrt(mx * mx + mz * mz); mx /= ml; mz /= ml;
                double hx = -mz, hz = mx;
                // stand in the quadrant between the two roads, off both their lines
                S.TeleportAbsolute(up.X + (mx + hx) * 50.0, up.Z + (mz + hz) * 50.0);
                yield return Wait(Settled, 60);
                yield return Ticks(5);
                var space = World.GetWorld3D().DirectSpaceState;
                string Who(Godot.Collections.Dictionary h) => h.Count == 0 ? "nothing" : h["collider"].As<Node>() is Node n ? (n.GetParent()?.Name == "BridgeBodies" ? "deck" : n.Name.ToString()) : "?";
                Godot.Collections.Dictionary Ray(double ax, double ay, double az, double bx, double by, double bz)
                    => space.IntersectRay(PhysicsRayQueryParameters3D.Create(S.ToLocal(ax, ay, az), S.ToLocal(bx, by, bz), 1u << 0));
                // the LOWER road runs open under the upper one, 2.5 m up, from 25 m before the crossing to 25 m after (a 7%
                // highway climbs 1.75 m in that); the control 7 m up hits the upper road's deck
                double lx = wantOver ? hx : mx, lz = wantOver ? hz : mz;
                float lowY = wantOver ? up.HwSurface : up.MainSurface, highY = wantOver ? up.MainSurface : up.HwSurface;
                var open = Ray(up.X - lx * 25, lowY + 2.5, up.Z - lz * 25, up.X + lx * 25, lowY + 2.5, up.Z + lz * 25);
                var ctl = Ray(up.X - lx * 25, lowY + 7.0, up.Z - lz * 25, up.X + lx * 25, lowY + 7.0, up.Z + lz * 25);
                T.Check($"{kind}: the {(wantOver ? "highway" : "main")} runs open under the {(wantOver ? "main" : "highway")}: 2.5 m up along it through the crossing hits {Who(open)}" +
                        $"{(open.Count > 0 ? $" at y {((Vector3)open["position"]).Y:0.00}" : "")} (control, 7 m up: {Who(ctl)})",
                    open.Count == 0 && Who(ctl) == "deck");
                // the UPPER road is a deck at its surface: a drop onto it at the crossing (for the highway, onto a
                // carriageway: its median is open)
                double ux = wantOver ? 0 : mx * InfiniteRoads.HighwayRibbonOffset, uz = wantOver ? 0 : mz * InfiniteRoads.HighwayRibbonOffset;
                var top = Ray(up.X + ux, highY + 5, up.Z + uz, up.X + ux, highY - 30, up.Z + uz);
                float topY = top.Count > 0 ? ((Vector3)top["position"]).Y : float.NaN;
                // the roadway strip drawn over a deck is in the road collider too ("Paved", flush with the deck's roadway,
                // so a drop meets it first): look again THROUGH it -- the deck itself must be there, at the same height
                var under = top;
                if (Who(top) == "Paved")
                {
                    var q = PhysicsRayQueryParameters3D.Create(S.ToLocal(up.X + ux, highY + 5, up.Z + uz), S.ToLocal(up.X + ux, highY - 30, up.Z + uz), 1u << 0);
                    q.Exclude = new Godot.Collections.Array<Rid> { (Rid)top["rid"] };
                    under = space.IntersectRay(q);
                }
                float deckY = under.Count > 0 ? ((Vector3)under["position"]).Y : float.NaN;
                T.Check($"{kind}: the {(wantOver ? "main" : "highway")} crosses on a deck at its surface: y {topY:0.000} on {Who(top)}, the deck under it at {deckY:0.000} ({Who(under)}), road {highY:0.000}",
                    Who(under) == "deck" && Mathf.Abs(topY - highY) < 0.03f && Mathf.Abs(deckY - highY) < 0.03f);
                if (wantOver)
                {
                    // the main's deck is WIDENED to its asphalt (x DeckScale): its parapet stands 9.2-9.8 m out, where the
                    // unwidened deck's ends at 8.5 -- a drop there hits the parapet's top, not the highway 9.5 m below
                    double px = up.X + hx * 9.5, pz = up.Z + hz * 9.5;
                    var par = Ray(px, highY + 5, pz, px, highY - 30, pz);
                    float parY = par.Count > 0 ? ((Vector3)par["position"]).Y : float.NaN;
                    T.Check($"overpass: the main's deck is its full width, parapet 9.5 m out at y {parY - highY:+0.00;-0.00} m over the road on {Who(par)} (deck scale {InfiniteRoads.DeckScale(RoadKind.Main):0.000})",
                        Who(par) == "deck" && parY > highY + 0.5f);
                }
            }
            T.Check($"nobody was ever rescued from under the ground ({S.Rescues})", S.Rescues == 0);
        }
    }
}
