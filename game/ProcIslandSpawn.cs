using Godot;

namespace UnturnedGodot
{
    /// <summary>Turn a generated island's road/building lists into REAL props in the world.
    ///
    /// ProcIsland is deliberately pure: it produces heights and lists of (prop, x, z, yaw) and instantiates
    /// nothing, so every check in the suite can run headless. This is the one place that crosses over, and it
    /// exists because "the generator works" and "the generator produces a world you can walk around in" are
    /// different claims -- the suite could only ever have proved the first.
    ///
    /// THE FRAME. ProcIsland works in grid-index * 4 m, both axes positive; Terrain renders grid (gx,gy) at
    /// world (gx*4, h, -gy*4) -- "Z is negated relative to the grid" (Terrain.WorldBoundsXZ). That is the same
    /// relationship a retail map's coordinates have to the world, which is why WorldBuilder's own transform is
    /// the right one to reuse rather than invent a second: position Z negates, and yaw becomes 180 - yaw
    /// (EditorObjects.FromEuler / WorldBuilder's `Basis(Y, 180 - ey)`). Getting this wrong is quiet -- a
    /// symmetric Line or Quad looks identical either way, and only an asymmetric building shows the 180.</summary>
    public static class ProcIslandSpawn
    {
        /// <summary>The rotation a prop authored in ProcIsland's frame needs in the world.
        ///
        /// This is the retail placement euler with the yaw filled in, and it MUST go through the same
        /// FromEuler the map loader uses. ex=270 is the standing correction for the Z-up prop meshes (see
        /// EditorObjects.FromEuler); the first cut here passed a bare `Basis(Up, 180 - yaw)` and every road tile
        /// came out as a 24 m WALL on its edge -- which from a 3/4 view read as "the roads are a bit thick" and
        /// from directly above as a hairline. A boxy house lying on its side still reads as a house, so the
        /// buildings gave nothing away at all.</summary>
        public static Basis RotFor(float procYawDeg) => EditorObjects.FromEuler(270f, procYawDeg, 0f);

        /// <summary>Where a prop-LOCAL direction (mesh X/Y, the frame road_connectors.txt is written in) ends
        /// up in ProcIsland's frame once the prop is placed at this yaw.
        ///
        /// Runs the real placement basis rather than an open-coded matrix, because an open-coded one is where
        /// this went wrong: the suite's copy read
        ///     wx = lx*cos - ly*sin ;  wz = -lx*sin - ly*cos
        /// whose determinant is -1 -- a REFLECTION, not a rotation. It agreed on the +/-Y column, so Line,
        /// LineCap, Tee, TeeCap, Quad and QuadCap all passed: their X arms come in symmetric +/- pairs, and
        /// negating the X image maps that set onto itself. The Turn is the only piece with a lone +X arm, and
        /// its placement carried the matching flip, so check and code were wrong together and stayed green.
        /// strawberry found it by looking at a render.</summary>
        public static (float x, float z) ArmDir(float procYawDeg, float meshX, float meshY)
        {
            var v = RotFor(procYawDeg) * new Vector3(meshX, meshY, 0f);
            return (v.X, -v.Z);   // world -> ProcIsland's frame, which negates Z
        }

        /// <summary>The world position of a ProcIsland (x, z) pair, dropped onto the terrain.</summary>
        public static Vector3 PosFor(Terrain terr, float px, float pz)
        {
            float wx = px, wz = -pz;
            return new Vector3(wx, terr != null ? terr.SampleHeight(wx, wz) : 0f, wz);
        }

        /// <summary>The terrain LAYER a generated island treats as "nothing has been built here". CreateFlat
        /// paints the whole map layer 2 (Grass), so that is what untouched ground is.</summary>
        public const int GrassLayer = 2;
        public const int DirtLayer = 0;   // Terrain.DefaultLayerNames[0]

        /// <summary>Paint DIRT under everything the generator built -- road tiles, buildings and the routes
        /// between towns -- so the ground says where the map has been worked (strawberry 2026-09-16: "around
        /// props, road splines etc theres a patch of the dirt material that fits the size and shape of the
        /// prop/spline + some border").
        ///
        /// ⚠ THIS RUNS BEFORE THE FOLIAGE BAKE, ON PURPOSE, and that ordering is the whole feature. The paint
        /// is the single source of truth for "is this ground built on": the scatter then refuses anything that
        /// is not Grass, so "no grass on dirt" falls out of reading the same map the player is looking at
        /// rather than from a second exclusion rule that can drift away from it. Anything a human paints dirt
        /// later is excluded by the same test, for free.
        ///
        /// ⚠ It does NOT need the RoadField. Painting the ground and laying the road ribbon are separate jobs
        /// with different prerequisites -- the splines want a field that is already in the scene tree, which
        /// only exists much later in the build, while the paint only needs the routes. Keeping them apart is
        /// what lets the dirt exist before the foliage is scattered over it.
        ///
        /// Circles rather than true footprints: a road tile is a 4 m square and a circle a little larger than
        /// it covers the square plus master's border, and building footprints are not known here (PlaceBuildings
        /// records a prop name and a position, not an extent) so a radius is the honest approximation.</summary>
        public static void PaintGroundwork(Terrain terr)
        {
            if (terr == null) return;
            const float TileR = 3.6f;    // a 4 m tile's corner is 2.83 m out; this covers it plus a border
            const float BuildR = 8.0f;   // footprint unknown -> a skirt wide enough to read as a cleared plot
            const float RouteR = 5.2f;   // the carved corridor is wider than the ribbon that will sit on it
            int tiles = 0, builds = 0, routePts = 0;

            if (terr.IslandTiles != null)
                foreach (var t in terr.IslandTiles)
                {
                    var w = PosFor(terr, t.X, t.Z);
                    terr.PaintSplat(w.X, w.Z, TileR, DirtLayer); tiles++;
                }
            if (terr.IslandBuildings != null)
                foreach (var b in terr.IslandBuildings)
                {
                    var w = PosFor(terr, b.X, b.Z);
                    terr.PaintSplat(w.X, w.Z, BuildR, DirtLayer); builds++;
                }
            // Routes are painted by STEPPING along the polyline rather than with PaintRiverBed: that one applies
            // a river's own overspray blend, which is tuned for a bank and not for a roadside.
            if (terr.IslandRoutes != null)
                foreach (var route in terr.IslandRoutes)
                {
                    if (route.Points == null) continue;
                    for (int i = 0; i < route.Points.Count; i++)
                    {
                        var p = route.Points[i];
                        var w = PosFor(terr, p.X, p.Y);
                        terr.PaintSplat(w.X, w.Z, RouteR, DirtLayer); routePts++;
                    }
                }
            Log.Print($"[island-paint] dirt under {tiles} road tile(s), {builds} building(s), {routePts} route point(s)");
        }

        /// <summary>Lay REAL SPLINE ROADS along the routes between towns.
        ///
        /// ⚠ THE ROUTES ALREADY EXISTED -- they were just invisible. GenerateIsland calls CarveRoutes, which
        /// flattens a path through the heightmap between every pair of linked POIs, and then nothing ever put a
        /// road surface on them. So a generated island had graded corridors running between its towns with bare
        /// grass down the middle. strawberry: "there are no road splines between towns".
        ///
        /// ⚠ AND THIS IS WHY THEY DO NOT SINK. The in-town grid is ROAD TILES: flat quads placed at the sampled
        /// centre height with yaw only (RotFor is `FromEuler(270, yaw, 0)` -- no terrain normal), so on any slope
        /// the uphill edge buries and the downhill edge floats. A spline road is not a tile; RoadField builds a
        /// ribbon that follows the joints, so it rides the ground it was carved into. Same reason one change
        /// answers two of the reported items.
        ///
        /// DECIMATED, because a carved route has a point per 4 m grid step and a joint every 4 m makes a spline
        /// that is all control and no curve -- Catmull-Rom through dense collinear points is just the polyline
        /// back again, with a mesh segment per step. Endpoints are always kept: they are where the route meets
        /// the town, and moving one leaves the road pointing at where the gate used to be.</summary>
        public static int SpawnRoutes(Terrain terr, RoadField rf, int material = 0)
        {
            if (terr == null || rf == null || terr.IslandRoutes == null) return 0;
            const int Stride = 5;          // ~20 m between joints
            const float MinLen = 24f;      // a route shorter than this is a stub inside a town, not a road between them
            int built = 0, skipped = 0;

            foreach (var route in terr.IslandRoutes)
            {
                if (route.Points == null || route.Points.Count < 2) { skipped++; continue; }
                var pts = new System.Collections.Generic.List<Vector3>();
                for (int i = 0; i < route.Points.Count; i += Stride)
                {
                    var p = route.Points[i];
                    pts.Add(PosFor(terr, p.X, p.Y));   // route points are in ProcIsland's 2D frame; PosFor negates Z and drops it on the ground
                }
                var last = route.Points[^1];
                var lastW = PosFor(terr, last.X, last.Y);
                if (pts.Count == 0 || pts[^1].DistanceTo(lastW) > 0.01f) pts.Add(lastW);
                if (pts.Count < 2) { skipped++; continue; }

                float len = 0f;
                for (int i = 1; i < pts.Count; i++) len += pts[i].DistanceTo(pts[i - 1]);
                if (len < MinLen) { skipped++; continue; }

                if (rf.AddRoadFromPolyline(pts, material) >= 0) built++; else skipped++;
            }
            Log.Print($"[island-roads] {built} spline road(s) between towns" + (skipped > 0 ? $" ({skipped} route(s) skipped as too short or degenerate)" : ""));
            return built;
        }

        /// <summary>Give a generated island its own player spawn points.
        ///
        /// A generated island had NONE -- `spawn` does not appear in ProcIsland at all -- and the editor's save
        /// line said so every time: `238 props, 0 spawns`.
        ///
        /// ⚠ WHAT THIS DOES **NOT** FIX, because the two look like one problem and are not. Playtest drops you
        /// under the fly camera on purpose (EditorPlayMode, master: "spawns u to walk around somewhere near the
        /// editor camera"), so a rooftop landing there is that feature working, not this one missing. And at
        /// REAL play time nothing reads these yet: LevelSpawns.PlayerSpawns reads `&lt;mapRoot&gt;/Spawns/Players.dat`
        /// in the retail binary format, and a generated map never changes _mapRoot off PEI -- so it would
        /// inherit PEI's 22 spawn points, which describe PEI's geometry and mean nothing on this island. That is
        /// an architectural gap (custom maps have no map root of their own) and is deliberately NOT papered over
        /// here; these points exist, are visible, editable and save with the map, which is what the editor is for.
        ///
        /// CHOOSING A POINT. Candidates are the verge beside a road tile, because a spawn wants to be somewhere
        /// you can walk out of and somewhere that reads as a place. The four cardinal offsets are tried rather
        /// than the road's own perpendicular: the tile yaw convention is the exact thing that has been got
        /// backwards here before (see ArmDir's note), and "is this spot flat, dry and clear" answers itself
        /// without needing to know which way the road runs.
        ///
        /// Deterministic from the island seed -- the same seed must give the same island, spawns included, or
        /// two machines generating "the same" world disagree about where people start.</summary>
        public static int PlacePlayerSpawns(Terrain terr, EditorSpawns spawns, int seed, int want = 24)
        {
            if (spawns == null) return 0;
            var picks = ChoosePlayerSpawns(terr, seed, want);
            foreach (var (pos, yaw) in picks) spawns.AddSpawn(pos, yaw);
            Log.Print($"[island] placed {picks.Count} player spawn(s) of {want} wanted"
                      + (picks.Count < want ? " -- ran out of flat, dry, unblocked roadside" : ""));
            return picks.Count;
        }

        /// <summary>The CHOICE, with no nodes in it -- same split ProcIsland itself keeps ("deliberately pure:
        /// it produces heights and lists and instantiates nothing, so every check in the suite can run
        /// headless"). PlacePlayerSpawns is the crossover; this is the part a test can interrogate, and
        /// "24 points were placed" is exactly the kind of number that looks like success while being 24 bad
        /// points.</summary>
        public static System.Collections.Generic.List<(Vector3 Pos, float Yaw)> ChoosePlayerSpawns(
            Terrain terr, int seed, int want = 24)
        {
            var picks = new System.Collections.Generic.List<(Vector3, float)>();
            if (terr == null || terr.IslandTiles == null || terr.IslandTiles.Count == 0) return picks;

            const float Verge = 7f;        // m from the tile centre: off the carriageway, still roadside
            const float FlatProbe = 2.5f;  // m; the square sampled to call a spot flat
            const float FlatTol = 1.1f;    // m of height spread tolerated across that square
            const float DryMargin = 3f;    // m above sea level -- a spawn at the waterline is a spawn in the surf
            const float ClearBuild = 9f;   // m from a building origin (footprints are unknown; a radius is the honest approximation)
            const float ClearRoad = 4f;    // m from any OTHER road tile, so a spawn never lands in a carriageway
            const float Apart = 30f;       // m between spawns, so 24 points are a map's worth and not a car park

            var chosen = new System.Collections.Generic.List<Vector3>();
            // Walk the tiles in a seeded order rather than in list order: list order is the generator's build
            // order, so taking the first N clusters every spawn in whichever monument happened to be built first.
            var order = new System.Collections.Generic.List<int>();
            for (int i = 0; i < terr.IslandTiles.Count; i++) order.Add(i);
            var rng = new System.Random(seed);
            for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }

            foreach (int idx in order)
            {
                if (chosen.Count >= want) break;
                var t = terr.IslandTiles[idx];
                for (int dir = 0; dir < 4; dir++)
                {
                    float px = t.X + (dir == 0 ? Verge : dir == 1 ? -Verge : 0f);
                    float pz = t.Z + (dir == 2 ? Verge : dir == 3 ? -Verge : 0f);
                    var w = PosFor(terr, px, pz);

                    if (Terrain.HasWater && w.Y < Terrain.SeaLevelY + DryMargin) continue;

                    // flat enough to stand on, measured rather than assumed from the tile being a road
                    float lo = w.Y, hi = w.Y;
                    for (int k = 0; k < 4; k++)
                    {
                        float sx = px + (k == 0 ? FlatProbe : k == 1 ? -FlatProbe : 0f);
                        float sz = pz + (k == 2 ? FlatProbe : k == 3 ? -FlatProbe : 0f);
                        float h = PosFor(terr, sx, sz).Y;
                        if (h < lo) lo = h; if (h > hi) hi = h;
                    }
                    if (hi - lo > FlatTol) continue;

                    bool blocked = false;
                    foreach (var b in terr.IslandBuildings)
                        if (Near(px, pz, b.X, b.Z, ClearBuild)) { blocked = true; break; }
                    if (blocked) continue;
                    foreach (var o in terr.IslandTiles)
                        if (!(o.X == t.X && o.Z == t.Z) && Near(px, pz, o.X, o.Z, ClearRoad)) { blocked = true; break; }
                    if (blocked) continue;
                    foreach (var c in chosen)
                        if (Near(w.X, w.Z, c.X, c.Z, Apart)) { blocked = true; break; }
                    if (blocked) continue;

                    // Face the road you are standing beside, so you spawn looking at somewhere to go.
                    var road = PosFor(terr, t.X, t.Z);
                    float yaw = Mathf.RadToDeg(Mathf.Atan2(road.X - w.X, road.Z - w.Z));
                    picks.Add((w, yaw));
                    chosen.Add(w);
                    break;   // one spawn per tile at most
                }
            }
            return picks;
        }

        static bool Near(float ax, float az, float bx, float bz, float r)
        {
            float dx = ax - bx, dz = az - bz;
            return dx * dx + dz * dz < r * r;
        }

        /// <summary>Place every road tile and building of the last GenerateIsland through the editor's own
        /// object placer, so the result is selectable, movable and saves with the map like anything a human
        /// dragged in. Returns what actually landed -- a name the catalogue does not know returns null from
        /// Place and would otherwise vanish silently.</summary>
        public static (int roads, int buildings, int missing) Spawn(Terrain terr, EditorObjects objs)
        {
            if (terr == null || objs == null) return (0, 0, 0);
            int roads = 0, buildings = 0, missing = 0;

            foreach (var t in terr.IslandTiles)
            {
                string prop = ProcIsland.PropFor(t.Piece);
                if (prop == null) { missing++; continue; }
                if (objs.Place(prop, PosFor(terr, t.X, t.Z), RotFor(t.YawDeg)) != null) roads++; else missing++;
            }
            foreach (var b in terr.IslandBuildings)
            {
                if (objs.Place(b.Prop, PosFor(terr, b.X, b.Z), RotFor(b.YawDeg)) != null) buildings++; else missing++;
            }
            Log.Print($"[island] spawned {roads} road props + {buildings} buildings" + (missing > 0 ? $" ({missing} MISSING from the object catalogue)" : ""));
            return (roads, buildings, missing);
        }
    }
}
