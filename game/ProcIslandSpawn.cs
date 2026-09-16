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

        /// <summary>Smooth a road's VERTICAL profile without letting it sink back into the ground.
        ///
        /// strawberry: "fix sharp vertical splines going into prop caps. should smoothly blend along its
        /// length." Seating every joint on the highest ground it spans stops the ribbon clipping, but it makes
        /// the height profile follow each local bump exactly -- so where a route leaves the town's flat pad and
        /// meets carved ground, the road steps rather than grades, and the step lands right at the cap.
        ///
        /// ⚠ SMOOTHING ALONE WOULD PUT THE CLIPPING BACK. A plain average pulls joints DOWN into the hillside
        /// they were lifted over, undoing the fix that took splines to zero exposed clipping. So each pass
        /// smooths and then CLAMPS UP to the ground it must clear: the result converges on the smoothest
        /// profile that still passes above everything, rather than trading one defect for the other.
        ///
        /// ⚠ ENDS ARE PINNED. The first and last joints meet a monument's cap piece, and moving one leaves the
        /// road ending beside the gate instead of in it -- the same hazard CarveRoutes' own note describes about
        /// snapping connectors after routing.</summary>
        static void SmoothProfile(System.Collections.Generic.List<Vector3> pts)
        {
            if (pts.Count < 5) return;
            var floor = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++) floor[i] = pts[i].Y;   // what each joint must clear
            for (int pass = 0; pass < 4; pass++)
            {
                var y = new float[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i == 0 || i == pts.Count - 1) { y[i] = pts[i].Y; continue; }
                    float sum = 0f; int n = 0;
                    for (int k = -2; k <= 2; k++)
                    {
                        int j = i + k;
                        if (j < 0 || j >= pts.Count) continue;
                        sum += pts[j].Y; n++;
                    }
                    y[i] = Mathf.Max(sum / n, floor[i]);   // smooth, then stay above the ground
                }
                for (int i = 0; i < pts.Count; i++) pts[i] = new Vector3(pts[i].X, y[i], pts[i].Z);
            }
        }

        /// <summary>Where a spline JOINT sits: on the highest ground it spans, lifted clear.
        ///
        /// ⭐ Same principle that took the tiles to zero, applied to the other kind of road: ONLY TERRAIN ABOVE
        /// A SURFACE CLIPS THROUGH IT. A joint seated on the ground exactly at its own point leaves the ribbon
        /// free to pass below any bump between it and the next joint -- and the ribbon is a smooth curve while
        /// the ground is not, so there is always something between them. Seating each joint on the local
        /// maximum lifts the whole chord above what it spans.
        /// ⚠ The radius is a little over half the joint spacing, so consecutive joints' samples OVERLAP.
        /// Sampling only at the joint leaves the midpoint of every segment unconsidered, which is exactly where
        /// the clipping was measured.</summary>
        public static Vector3 JointPosFor(Terrain terr, float px, float pz)
        {
            const float R = 5f;   // joints are ~8 m apart; this reaches past the midpoint from both sides
            var c = PosFor(terr, px, pz);
            float top = c.Y;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    float h = PosFor(terr, px + dx * R, pz + dz * R).Y;
                    if (h > top) top = h;
                }
            return new Vector3(c.X, top + RoadPropLift, c.Z);
        }

        /// <summary>How far a road prop sits ABOVE the ground it is laid on.
        ///
        /// ⚠ NOT ZERO, and zero is what was wrong. Seating the quad exactly on the terrain makes two coplanar
        /// surfaces at the same depth, and the depth buffer then picks between them per-pixel -- the terrain
        /// shows THROUGH the road in a shifting speckle. strawberry: "the 'road surface' that its measuring is
        /// z fighting with the terrain under it". A prop is a physical object lying on the ground, so it
        /// belongs a few centimetres above it, not embedded in it.</summary>
        public const float RoadPropLift = 0.06f;

        /// <summary>How far a BUILDING sits above the ground (strawberry: "all buildings need to be lifted off
        /// the ground an amount"). A touch more than a road prop's lift: a building's base is a bigger, flatter
        /// face pressed against the terrain, so it has more area to z-fight over, and unlike a road it reads
        /// fine sitting a little proud.</summary>
        public const float BuildingLift = 0.12f;

        /// <summary>Where a BUILDING sits: on the town's flat pad, lifted clear of it.</summary>
        public static Vector3 BuildingPosFor(Terrain terr, float px, float pz)
        {
            var c = PosFor(terr, px, pz);
            return new Vector3(c.X, c.Y + BuildingLift, c.Z);
        }

        /// <summary>Where a road TILE sits: on the town's flat pad, lifted clear of it.
        ///
        /// ⭐ This used to hunt the highest point under the tile's own footprint, because the ground under a
        /// town was not level and a flat quad had to clear whatever rose through it. FlattenTownsExactly makes
        /// the footprint exactly level, so there is nothing to hunt -- and because every tile samples the same
        /// flat pad, they all come back with the SAME height, which is what stops neighbouring 24 m pieces
        /// disagreeing at their shared edges. The fix for the overlap is the flat ground, not a rule about
        /// props.</summary>
        public static Vector3 TilePosFor(Terrain terr, float px, float pz)
        {
            var c = PosFor(terr, px, pz);
            return new Vector3(c.X, c.Y + RoadPropLift, c.Z);
        }

        /// <summary>The world position of a ProcIsland (x, z) pair, dropped onto the terrain.</summary>
        public static Vector3 PosFor(Terrain terr, float px, float pz)
        {
            float wx = px, wz = -pz;
            return new Vector3(wx, terr != null ? terr.SampleHeight(wx, wz) : 0f, wz);
        }

        /// <summary>Points of a carved route per spline joint. ⚠ SHARED with the clipping probe below, which
        /// measures the gap BETWEEN joints -- if the probe used its own copy it would measure chords the road
        /// does not have, and report a number about a road nobody builds. It did exactly that once: the road
        /// moved to 8 m joints while the probe still sampled 20 m ones, and the "worse" reading was the
        /// instrument, not the road.</summary>
        public const int RouteJointStride = 2;   // ~8 m between joints

        /// <summary>What the road kit actually laid, and how much of it opens onto nothing.
        ///
        /// strawberry: "roads cannot have exposed non-cap ends exposed to 'air' (not connected to a road)" and
        /// "prevent road quads being spammed in towns (there are entire towns of road quads and nothing else)".
        /// Both are claims about the MIX of pieces, so the mix is what gets counted -- rewriting selection logic
        /// on a hunch about which rule misfires is how the wrong rule gets tightened.
        ///
        /// An EXPOSED END is an arm of a non-cap piece pointing at a lattice cell that has no tile in it. Caps
        /// are exempt: terminating a run is their whole job.</summary>
        static void ReportPieces(Terrain terr)
        {
            if (terr?.IslandTiles == null) return;
            var counts = new System.Collections.Generic.Dictionary<ProcIsland.RoadPiece, int>();
            foreach (var t in terr.IslandTiles) { counts.TryGetValue(t.Piece, out int c); counts[t.Piece] = c + 1; }
            // ⚠ NEIGHBOURS BY DISTANCE, NOT BY A RECONSTRUCTED LATTICE INDEX. The obvious probe rounds
            // t.X / TileSize into a grid -- but a monument's tiles are laid at poi.X + k*TileSize and poi.X is
            // NOT a multiple of TileSize, so every town sits on its own sub-lattice and a shared integer grid
            // puts neighbouring tiles in non-adjacent cells. That probe reports phantom exposed arms, which is
            // an instrument failure wearing the shape of the bug it is looking for.
            // ⚠ PER-ARM, FROM THE REAL CONNECTOR DATA -- not a count of neighbours. Counting arms against
            // neighbours cannot see an arm pointing the WRONG WAY: a cell with two neighbours and two arms
            // scores clean even when neither arm faces one. strawberry described the actual failure per-arm
            // ("2 sides go into other road props, one goes into a spline (cap end) and the other is exposed to
            // air"), so the probe reads content/objects/road_connectors.txt -- the same file the kit is built
            // from -- rotates each connector through the REAL placement basis (ArmDir, not an open-coded
            // matrix) and asks whether a tile sits one step along it.
            // A CAP's ramp is its mesh +Y and is the one opening it is allowed.
            var arms = LoadRoadConnectors();
            var byPiece = new System.Collections.Generic.Dictionary<ProcIsland.RoadPiece, int>();
            int exposed = 0, checkedArms = 0;
            foreach (var t in terr.IslandTiles)
            {
                string prop = ProcIsland.PropFor(t.Piece);
                if (prop == null || !arms.TryGetValue(prop, out var list)) continue;
                bool isCap = t.Piece is ProcIsland.RoadPiece.LineCap or ProcIsland.RoadPiece.TeeCap or ProcIsland.RoadPiece.QuadCap;
                foreach (var (mx, my) in list)
                {
                    if (isCap && my > 0.5f && Mathf.Abs(mx) < 0.5f) continue;   // the ramp: a cap's one legitimate opening
                    var (ax, az) = ArmDir(t.YawDeg, mx, my);
                    float nx = t.X + ax * 24f, nz = t.Z + az * 24f;
                    bool found = false;
                    foreach (var o in terr.IslandTiles)
                        if (Mathf.Abs(o.X - nx) < 3f && Mathf.Abs(o.Z - nz) < 3f) { found = true; break; }
                    checkedArms++;
                    if (!found) { exposed++; byPiece.TryGetValue(t.Piece, out int e); byPiece[t.Piece] = e + 1; }
                }
            }
            var blame = new System.Collections.Generic.List<string>();
            foreach (var kv in byPiece) blame.Add($"{kv.Key} {kv.Value}");

            var parts = new System.Collections.Generic.List<string>();
            int total = terr.IslandTiles.Count;
            foreach (var kv in counts) parts.Add($"{kv.Key} {kv.Value} ({(total > 0 ? kv.Value * 100 / total : 0)}%)");
            Log.Print($"[island-pieces] {string.Join(", ", parts)}");
            if (ProcIsland.PadCount > 0)
                Log.Print($"[island-pads] {ProcIsland.PadCount} town pad(s): mean half-size "
                          + $"{ProcIsland.PadWas / ProcIsland.PadCount:0.#} -> {ProcIsland.PadNow / ProcIsland.PadCount:0.#} m, "
                          + $"smallest now {ProcIsland.PadSmallest:0.#} m");
            Log.Print($"[island-pieces] exit-grow: added {ProcIsland.GrowAdded}, blocked by lattice edge {ProcIsland.GrowBlocked}, trimmed {ProcIsland.GrowTrimmed}, still short {ProcIsland.GrowShort}");
            Log.Print($"[island-pieces] {exposed} arm(s) of {checkedArms} open onto air"
                      + (blame.Count > 0 ? $" -- from {string.Join(", ", blame)}" : ""));
        }

        /// <summary>Per-prop connector arms (mesh-local outward direction) from the file the kit is built
        /// from, so the probe cannot disagree with the art about where a road opens.</summary>
        static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(float mx, float my)>> LoadRoadConnectors()
        {
            var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(float, float)>>();
            string path = ProjectSettings.GlobalizePath("res://content/objects/road_connectors.txt");
            if (!System.IO.File.Exists(path)) { Log.Print("[island-pieces] no road_connectors.txt -- arm check skipped"); return map; }
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                if (line.StartsWith("#")) continue;
                var f = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 7) continue;
                if (!float.TryParse(f[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dx)) continue;
                if (!float.TryParse(f[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dy)) continue;
                if (!map.TryGetValue(f[0], out var l)) { l = new System.Collections.Generic.List<(float, float)>(); map[f[0]] = l; }
                l.Add((dx, dy));
            }
            return map;
        }

        /// <summary>UG_CLIPDBG=1: measure what is actually clipping, instead of guessing at it again.
        ///
        /// Two different failures wear the same symptom. A road TILE is a flat 24 m quad dropped at its centre
        /// height, so any height variation across its footprint buries one corner and floats another. A road
        /// SPLINE is a ribbon through joints 20 m apart, so terrain can rise through it BETWEEN joints while
        /// every joint itself sits perfectly on the ground. Reporting the worst and the mean of each says which
        /// one is worth fixing, and a fix aimed at the wrong one would look reasonable and change nothing.</summary>
        public static void ReportClipping(Terrain terr)
        {
            if (terr == null || System.Environment.GetEnvironmentVariable("UG_CLIPDBG") != "1") return;

            // ---- tiles: spread across the quad the prop actually covers -------------------------------------
            const float TileHalf = 12f;   // ProcIsland.TileSize * 0.5
            float worstTile = 0f, sumTile = 0f; int nTile = 0;
            if (terr.IslandTiles != null)
                foreach (var t in terr.IslandTiles)
                {
                    // ⚠ RISE ABOVE THE QUAD, not spread across it. The first version of this measured hi-lo,
                    // which counts a HOLLOW under the tile as badly as a hump through it -- and a hollow is
                    // invisible, the quad simply floats over a gap. Reporting spread made a lower-only fix look
                    // like a regression (6.31 m "worse") while the thing that actually clips had improved. The
                    // tile is placed at its CENTRE height, so what clips is how far the ground rises above that.
                    var c = TilePosFor(terr, t.X, t.Z);   // measure against where the tile IS, lift included
                    float rise = 0f;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            float h = PosFor(terr, t.X + dx * TileHalf, t.Z + dz * TileHalf).Y;
                            if (h - c.Y > rise) rise = h - c.Y;
                        }
                    if (rise > worstTile) worstTile = rise;
                    sumTile += rise; nTile++;
                }

            // ---- splines: how far terrain rises above the straight line BETWEEN joints ---------------------
            // The joints are on the ground by construction; the question is only what happens in the gap, which
            // is exactly what decimating to 20 m traded away.
            const int Stride = RouteJointStride;
            float worstGap = 0f, sumGap = 0f; int nGap = 0, over = 0;
            if (terr.IslandRoutes != null)
                foreach (var route in terr.IslandRoutes)
                {
                    if (route.Points == null || route.Points.Count < Stride + 1) continue;
                    for (int i = 0; i + Stride < route.Points.Count; i += Stride)
                    {
                        var a = JointPosFor(terr, route.Points[i].X, route.Points[i].Y);   // where the ribbon IS, lift included
                        var b = JointPosFor(terr, route.Points[i + Stride].X, route.Points[i + Stride].Y);
                        for (int k = 1; k < Stride; k++)
                        {
                            float f = k / (float)Stride;
                            var mid = route.Points[i + k];
                            float ground = PosFor(terr, mid.X, mid.Y).Y;
                            float ribbon = Mathf.Lerp(a.Y, b.Y, f);
                            float rise = ground - ribbon;      // + = terrain ABOVE the road surface
                            if (rise > worstGap) worstGap = rise;
                            if (rise > 0.05f) over++;
                            sumGap += rise; nGap++;
                        }
                    }
                }

            Log.Print($"[clipdbg] TILES worst RISE above the quad {worstTile:0.00} m, mean {(nTile > 0 ? sumTile / nTile : 0):0.00} m over {nTile} tile(s)");
            Log.Print($"[clipdbg] SPLINES worst rise above the chord {worstGap:0.00} m, mean {(nGap > 0 ? sumGap / nGap : 0):0.00} m, "
                      + $"{over}/{nGap} sample(s) above the surface");
        }

        /// <summary>The terrain LAYER a generated island treats as "nothing has been built here". CreateFlat
        /// paints the whole map layer 2 (Grass), so that is what untouched ground is.</summary>
        public const int GrassLayer = 2;
        public const int DirtLayer = 0;   // Terrain.DefaultLayerNames[0]

        /// <summary>Steepness at or above which ground stops being grass. ⚠ SHARED with the splat paint, so a
        /// boulder lands on exactly the ground that was painted -- two thresholds would put rocks on grass and
        /// leave bare dirt with nothing on it, and both would look deliberate.</summary>
        public const float SteepRise = 0.58f;   // ~30 degrees

        /// <summary>The boulder props that actually exist in content/objects. Listed rather than generated:
        /// the numbering has gaps.</summary>
        static readonly string[] BoulderProps =
        {
            "Boulder_00", "Boulder_01", "Boulder_02", "Boulder_03", "Boulder_04", "Boulder_06",
            "Boulder_08", "Boulder_09", "Boulder_10", "Boulder_11", "Boulder_12", "Boulder_13", "Boulder_22",
        };

        /// <summary>Scatter BOULDERS down the steep faces (strawberry: "place boulder props along the steep
        /// face"). Object props, not harvestable resources -- Boulder_NN live in content/objects, so they go
        /// through the editor's placer exactly like the road tiles and buildings.
        ///
        /// ⚠ The inverse rule to every other scatter here: everything else REFUSES steep ground, this one
        /// requires it. Placed on the same SteepRise the splat uses, so rocks sit on the dirt that the steepness
        /// created rather than near it.</summary>
        static void ScatterBoulders(Terrain terr, EditorObjects objs, ref int missing)
        {
            if (terr == null || objs == null) return;
            var b = terr.WorldBoundsXZ();
            var rng = new System.Random(20260916);
            const float Step = 17f, Apart = 13f;
            var placed = new System.Collections.Generic.List<Vector3>();
            int n = 0, miss = 0;
            for (float x = b.MinX; x < b.MaxX; x += Step)
                for (float z = b.MinZ; z < b.MaxZ; z += Step)
                {
                    float px = x + (float)(rng.NextDouble() * 2 - 1) * Step * 0.45f;
                    float pz = z + (float)(rng.NextDouble() * 2 - 1) * Step * 0.45f;
                    if (terr.SlopeAt(px, pz) < SteepRise) continue;
                    float y = terr.SampleHeight(px, pz);
                    if (Terrain.HasWater && y < Terrain.SeaLevelY) continue;   // boulders on the face, not the seabed
                    bool clash = false;
                    foreach (var q in placed)
                        if (Near(px, q.X, pz, q.Z, Apart)) { clash = true; break; }
                    if (clash) continue;
                    // A spread of the kit rather than one rock repeated down every hillside.
                    // ⚠ NAMED, NOT NUMBERED. Boulder_00..22 is not contiguous in the rip -- 05, 07 and 14-21
                    // are absent -- so generating an index range asked for props that do not exist and 141 of
                    // 829 placements silently failed. The miss counter is what caught it; a scatter that just
                    // skipped the nulls would have looked like a thinner hillside.
                    string prop = BoulderProps[rng.Next(BoulderProps.Length)];
                    var pos = new Vector3(px, y, pz);
                    if (objs.Place(prop, pos, RotFor((float)(rng.NextDouble() * 360.0))) != null) { placed.Add(pos); n++; }
                    else miss++;
                }
            missing += miss;
            Log.Print($"[island-rocks] {n} boulder(s) on ground steeper than {Mathf.RadToDeg(Mathf.Atan(SteepRise)):0.#} deg"
                      + (miss > 0 ? $" ({miss} prop name(s) not in the catalogue)" : ""));
        }

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
            // ⚠ THESE ARE THE GENERATOR'S OWN DIMENSIONS, NOT GUESSES. The first cut used 3.6 m under a road
            // tile and 8 m under a building and strawberry called it immediately: "the patches are very small
            // compared to what they are meant to be under". They were -- by about 5x. The road SURFACE is 16 m
            // wide (ProcIsland.HalfCarriageway = 8), the monument lattice steps every 24 m, and buildings run
            // up to 39 m across (Medic_1). A radius picked by eye against a mesh you have not measured is just
            // a number that looked reasonable in a comment.
            const float RoadHalf = 8f;       // = ProcIsland.HalfCarriageway; the carriageway is 16 m wide
            const float TileSpan = 24f;      // = ProcIsland.TileSize -- the prop mesh measures exactly 24.00 x 24.00
            // ⚠ THE BORDER IS PER-KIND, not one shared number. It started shared "so they match" and they
            // should not: strawberry, looking at the render, "road splines need to be a bit wider, buildings
            // need a lil less". A roadside shoulder and a building's cleared plot are different things and
            // reading them off one constant was tidiness standing in for a decision.
            const float TileBorder = 4.5f;   // town street verge
            const float RouteBorder = 6f;    // between-towns shoulder: wider, it runs through open country
            const float BuildBorder = 2f;    // a building's plot hugs the walls
            const float RouteHalf = 9f;      // the ribbon itself, widened with the shoulder
            int tiles = 0, builds = 0, sized = 0, routePts = 0;

            // ⚠ SQUARES, NOT CIRCLES, and this was a real bug: a road tile is a 24 m SQUARE, so its corner is
            // 12*sqrt(2) = 16.97 m from centre while the circle I was painting reached 12.5 m. That left 4.47 m
            // of diagonal bare at EVERY corner -- a diamond of grass at every joint between tiles, which is
            // exactly where strawberry saw it ("weird patches of grass ... seems to be along the joints").
            // Worse than cosmetic: the scatter reads the splat, so those diamonds were the one place in a town
            // that grew grass and trees, under the road.
            if (terr.IslandTiles != null)
                foreach (var t in terr.IslandTiles)
                {
                    var w = PosFor(terr, t.X, t.Z);
                    PaintFootprint(terr, w, t.YawDeg, TileSpan, TileSpan, TileBorder); tiles++;
                }

            if (terr.IslandBuildings != null)
                foreach (var b in terr.IslandBuildings)
                {
                    var w = PosFor(terr, b.X, b.Z);
                    var info = ProcIsland.PropInfo(b.Prop);
                    if (info.HasValue)
                    {
                        // "fits the size and shape": a real footprint, Width across by Front+Back deep, turned
                        // the way the building is turned -- not a circle around its origin.
                        PaintFootprint(terr, w, b.YawDeg, info.Value.Width, info.Value.Front + info.Value.Back, BuildBorder);
                        sized++;
                    }
                    else terr.PaintSplat(w.X, w.Z, 10f + BuildBorder, DirtLayer);   // prop not in the catalogue -> a skirt, and say so below
                    builds++;
                }

            // Routes are painted by STEPPING along the polyline rather than with PaintRiverBed: that one applies
            // a river's own overspray blend, tuned for a bank and not for a roadside.
            if (terr.IslandRoutes != null)
                foreach (var route in terr.IslandRoutes)
                {
                    if (route.Points == null) continue;
                    foreach (var p in route.Points)
                    {
                        var w = PosFor(terr, p.X, p.Y);
                        terr.PaintSplat(w.X, w.Z, RouteHalf + RouteBorder, DirtLayer); routePts++;
                    }
                }
            Log.Print($"[island-paint] dirt under {tiles} road tile(s) @{RoadHalf + TileBorder:0.#}m, routes @{RouteHalf + RouteBorder:0.#}m, "
                      + $"{builds} building(s) ({sized} to their real footprint), {routePts} route point(s)");
        }

        /// <summary>Stamp a rotated RECTANGLE of dirt under a building.
        ///
        /// ⚠ THE RECTANGLE'S AXES COME FROM RotFor -- the same basis that PLACED the building -- rather than
        /// from an open-coded sin/cos. This file has already been caught once getting that convention
        /// backwards (see ArmDir's note: check and code were wrong together and stayed green, and strawberry
        /// found it by looking at a render). Reusing the placement basis means the patch cannot disagree with
        /// the building about which way it faces, whatever the convention turns out to be.
        ///
        /// PaintSplat only draws circles, so the rectangle is stamped as an overlapping grid of them at a
        /// spacing below the radius -- gaps between stamps would show as grass islands inside the plot.</summary>
        static void PaintFootprint(Terrain terr, Vector3 centre, float yawDeg, float width, float depth, float border)
        {
            var basis = RotFor(yawDeg);
            var ax = Flatten(basis * new Vector3(1f, 0f, 0f));   // the prop's local X, in world
            var az = Flatten(basis * new Vector3(0f, 1f, 0f));   // ...and its local Y: these meshes stand up via ex=270, so local Y is the ground plane's other axis
            float halfW = width * 0.5f + border, halfD = depth * 0.5f + border;
            const float Brush = 5f;
            float step = Brush * 0.7f;   // overlap, or the corners of the stamp grid leave grass behind
            for (float u = -halfW; u <= halfW; u += step)
                for (float v = -halfD; v <= halfD; v += step)
                {
                    var pt = centre + ax * u + az * v;
                    terr.PaintSplat(pt.X, pt.Z, Brush, DirtLayer);
                }
        }

        /// <summary>Drop Y and renormalise. A basis column that has been stood up by the ex=270 correction can
        /// point out of the ground plane; painting is a 2D operation and wants the horizontal part.</summary>
        static Vector3 Flatten(Vector3 v)
        {
            var f = new Vector3(v.X, 0f, v.Z);
            return f.LengthSquared() < 1e-6f ? new Vector3(1f, 0f, 0f) : f.Normalized();
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
            // ⚠ 20 m of stride cost 0.84 m of clearance. Measured between joints on seed 12345: terrain rose
            // above the chord on 481 of 1792 samples, worst 0.84 m -- the ribbon is a smooth curve and the
            // ground is not, so decimating for smoothness quietly traded away the gap underneath it. 8 m still
            // beats a joint every 4 m (which is all control and no curve) without under-sampling the ground.
            const int Stride = RouteJointStride;
            const float MinLen = 24f;      // a route shorter than this is a stub inside a town, not a road between them
            int built = 0, skipped = 0;

            foreach (var route in terr.IslandRoutes)
            {
                if (route.Points == null || route.Points.Count < 2) { skipped++; continue; }
                var pts = new System.Collections.Generic.List<Vector3>();
                for (int i = 0; i < route.Points.Count; i += Stride)
                {
                    var p = route.Points[i];
                    pts.Add(JointPosFor(terr, p.X, p.Y));
                }
                var last = route.Points[^1];
                var lastW = JointPosFor(terr, last.X, last.Y);
                if (pts.Count == 0 || pts[^1].DistanceTo(lastW) > 0.01f) pts.Add(lastW);
                if (pts.Count < 2) { skipped++; continue; }

                float len = 0f;
                for (int i = 1; i < pts.Count; i++) len += pts[i].DistanceTo(pts[i - 1]);
                if (len < MinLen) { skipped++; continue; }
                SmoothProfile(pts);

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
                if (objs.Place(prop, TilePosFor(terr, t.X, t.Z), RotFor(t.YawDeg)) != null) roads++; else missing++;
            }
            foreach (var b in terr.IslandBuildings)
            {
                if (objs.Place(b.Prop, BuildingPosFor(terr, b.X, b.Z), RotFor(b.YawDeg)) != null) buildings++; else missing++;
            }
            ScatterBoulders(terr, objs, ref missing);
            ReportPieces(terr);
            Log.Print($"[island] spawned {roads} road props + {buildings} buildings" + (missing > 0 ? $" ({missing} MISSING from the object catalogue)" : ""));
            return (roads, buildings, missing);
        }
    }
}
