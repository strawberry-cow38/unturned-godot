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
            // ⚠ THE ENDS' FLOOR IS THEIR OWN SEAT. They were pinned to the cap's height by the caller, and if
            // the ground at the gate is fractionally above that (it is the same flat pad, but the lift is only
            // 6 cm) the clamp below would not touch them anyway -- they are never smoothed. Recording the floor
            // from the pinned value keeps the ease's clamp honest at the seam.
            SmoothPass(4);
            EaseEnd(0, +1);
            EaseEnd(pts.Count - 1, -1);
            SmoothPass(2);

            void SmoothPass(int passes)
            {
                for (int pass = 0; pass < passes; pass++)
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

            // EASE INTO THE CAP (strawberry 2026-09-16: "the splines really need to be eased into where they
            // connect").
            //
            // ⚠ SMOOTHING CANNOT DO THIS, for the same reason Relax's horizontal Ease exists: the end joint is
            // PINNED, so a windowed average has nothing to move it toward and every bit of the gradient change
            // between the pinned cap height and the terrain-following interior lands on the first free joint.
            // Smoothing harder makes that worse, not better -- a straighter interior meets the pin at a sharper
            // angle. The kink is not too rough, it is in the wrong place.
            //
            // So force a CONSTANT GRADE over the first few joints: a straight line from the pinned end to where
            // the profile already is at the end of the run. The turn is then spread over the whole ease by
            // construction. Clamped up afterwards like everything else here, because a straight grade that
            // passes under a hummock is a road with a hill through it.
            void EaseEnd(int from, int step)
            {
                const int Ease = 5;   // ~40 m at the 8 m joint stride
                int to = from + step * Ease;
                if (to < 0 || to >= pts.Count) return;
                float y0 = pts[from].Y, y1 = pts[to].Y;
                for (int k = 1; k < Ease; k++)
                {
                    int i = from + step * k;
                    float want = Mathf.Lerp(y0, y1, k / (float)Ease);
                    pts[i] = new Vector3(pts[i].X, Mathf.Max(want, floor[i]), pts[i].Z);
                }
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
        /// <summary>Measure, per route end, how far the spline actually lands from the cap it is supposed to
        /// meet -- in PLAN and in HEIGHT separately (strawberry 2026-09-16: "some road splines arent connecting
        /// to the prop road caps").
        ///
        /// ⚠ THE TWO NUMBERS ARE DIFFERENT FAULTS and reporting one distance would hide whichever was fine.
        /// In plan the join was already exact: a route's first point IS its gate, which sits on the monument's
        /// perimeter, which is the outer edge of the cap tile. The gap was vertical, and it came from the
        /// endpoints being seated by JointPosFor -- the highest ground within 5 m -- while the cap itself is
        /// seated on the flat pad. A single 3D distance would have averaged a correct 0 m with a wrong 2 m and
        /// reported "about a metre", which is a description of neither.</summary>
        static void ReportCapJoins(Terrain terr)
        {
            if (terr?.IslandRoutes == null || terr.IslandTiles == null) return;
            float worstPlan = 0f, worstY = 0f; int ends = 0, far = 0;
            float sumY = 0f;
            foreach (var route in terr.IslandRoutes)
            {
                if (route.Points == null || route.Points.Count < 2) continue;
                foreach (var end in new[] { route.Points[0], route.Points[^1] })
                {
                    // The cap's MOUTH, not its centre: the ramp is the piece's local +Y and the tile is 24 m
                    // square, so the opening is half a tile out along that arm.
                    float bestPlan = float.MaxValue, atY = 0f;
                    var bestTile = default(ProcIsland.MonumentTile); bool haveTile = false;
                    foreach (var t in terr.IslandTiles)
                    {
                        if (t.Piece != ProcIsland.RoadPiece.LineCap && t.Piece != ProcIsland.RoadPiece.TeeCap
                            && t.Piece != ProcIsland.RoadPiece.QuadCap) continue;
                        var arm = ArmDir(t.YawDeg, 0f, 1f);
                        float mx = t.X + arm.x * (ProcIsland.TileSize * 0.5f);
                        float mz = t.Z + arm.z * (ProcIsland.TileSize * 0.5f);
                        float d = new Vector2(end.X - mx, end.Y - mz).Length();
                        if (d < bestPlan) { bestPlan = d; atY = TilePosFor(terr, t.X, t.Z).Y; bestTile = t; haveTile = true; }
                    }
                    if (bestPlan == float.MaxValue) continue;
                    ends++;
                    float dy = Mathf.Abs(TilePosFor(terr, end.X, end.Y).Y - atY);
                    sumY += dy;
                    if (bestPlan > worstPlan) worstPlan = bestPlan;
                    if (dy > worstY) worstY = dy;
                    if (bestPlan > 2f || dy > 0.5f)
                    {
                        far++;
                        // ⚠ NAME THE OFFENDER. "2 not joined" out of 56 is a number I would otherwise have to
                        // guess the cause of, and the last four guesses about this generator were all wrong.
                        if (far <= 4 && haveTile)
                        {
                            // ⚠ AND SAY WHICH HALF IS WRONG. A route end that is NOT at its own gate is a
                            // routing fault; one that IS at its gate with no cap there is a monument fault.
                            // Without the gate in the line the two are indistinguishable, and the first guess
                            // at this (two gates sharing an exit cell) was wrong -- the fix changed nothing.
                            float gd = float.MaxValue; var gp = Vector2.Zero; float gdx = 0f, gdz = 0f; int gpoi = -1;
                            foreach (var c in terr.IslandConnectors)
                            {
                                float d2 = new Vector2(end.X - c.X, end.Y - c.Z).Length();
                                if (d2 < gd) { gd = d2; gp = new Vector2(c.X, c.Z); gdx = c.DirX; gdz = c.DirZ; gpoi = c.Poi; }
                            }
                            var own = new System.Text.StringBuilder();
                            foreach (var t2 in terr.IslandTiles) if (t2.Poi == gpoi) own.Append($" {t2.Piece}({t2.X:0},{t2.Z:0})y{t2.YawDeg:0}");
                            var gates = new System.Text.StringBuilder();
                            foreach (var c2 in terr.IslandConnectors) if (c2.Poi == gpoi) gates.Append($" ({c2.X:0},{c2.Z:0})d({c2.DirX:0.#},{c2.DirZ:0.#})");
                            Log.Print($"[island-capjoin] end ({end.X:0},{end.Y:0}) -> nearest {bestTile.Piece} @ ({bestTile.X:0},{bestTile.Z:0}) yaw {bestTile.YawDeg:0}, plan {bestPlan:0.0} m dY {dy:0.00} m"
                                      + $" | nearest gate poi#{gpoi} ({gp.X:0},{gp.Y:0}) dir ({gdx:0.#},{gdz:0.#}) at {gd:0.0} m"
                                      + $"\n    poi#{gpoi} gates:{gates}\n    poi#{gpoi} tiles:{own}");
                        }
                    }
                }
            }
            if (ends == 0) return;
            Log.Print($"[island-capjoin] {ends} route end(s): worst plan gap {worstPlan:0.00} m, worst height gap {worstY:0.00} m, mean height gap {sumY / ends:0.00} m, {far} not joined");
        }

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
                        // ⚠ ACROSS THE RIBBON, NOT JUST ALONG IT. This used to sample the CENTRELINE only, and
                        // the ribbon is 18.4 m wide -- so on a hillside traverse the uphill EDGE can be metres
                        // into the hill while the middle is perfectly clear, and the probe reported 0/1137
                        // samples above the surface on an island strawberry could see bald patches in. A
                        // measurement that only looks where the fault is not will always agree with you.
                        var perp = (route.Points[i + Stride] - route.Points[i]);
                        perp = perp.Length() > 1e-4f ? new Vector2(-perp.Y, perp.X).Normalized() : Vector2.Right;
                        for (int k = 1; k < Stride; k++)
                        {
                            float f = k / (float)Stride;
                            var mid = route.Points[i + k];
                            float ribbon = Mathf.Lerp(a.Y, b.Y, f);
                            for (int e = -2; e <= 2; e++)
                            {
                                float off = e * (ProcIsland.RenderedRoadHalf * 0.5f);
                                float ground = PosFor(terr, mid.X + perp.X * off, mid.Y + perp.Y * off).Y;
                                float rise = ground - ribbon;      // + = terrain ABOVE the road surface
                                if (rise > worstGap) worstGap = rise;
                                if (rise > 0.05f) over++;
                                sumGap += rise; nGap++;
                            }
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
        /// <summary>Street furniture along the town's road props: lights, signals, hydrants and bins
        /// (strawberry 2026-09-16: "add hydrants, street lights, traffic lights, garbage can (smart container
        /// variants) around along road props in the towns").
        ///
        /// ⭐ EVERY NUMBER HERE IS OFF RETAIL placements.txt, read back the same way the roadside pass was:
        ///   Fire_Hydrant_0 x46, nearest-neighbour median 20.0 m;  Street_Light_0 x39, median 19.2 m with a
        ///   min of 1.0 (pairs facing each other across a street);  Traffic_Light_0 x21, median 20.3 m and
        ///   EVERY one has 2-4 neighbours within 40 m -- they come in clusters at a junction, not singly;
        ///   Garbage_0/1 x17/x16, median 3.0-3.6 m, i.e. bins stand in little groups. The lattice steps 24 m,
        ///   so "one per tile" lands almost exactly on retail's spacing without a rule about it.
        ///
        /// ⚠ THE ARMS POINT ALONG LOCAL +Y, measured off the meshes rather than assumed: Street_Light_0's
        /// vertices above z=4.5 run local Y -0.12..+2.35 and Traffic_Light_0's run -0.12..+8.91, while both
        /// bases are centred on Y=0. So the arm is the +Y half, and a light yawed with YawForDir pointing at
        /// the street reaches OVER the carriageway. Yawed the other way it would hang the signal over the
        /// pavement, which a symmetric-looking pole gives away only in a close render.
        ///
        /// ⚠ THE BINS ARE MESHES HERE, NOT LIVE CONTAINERS. Dumpster_3/4 ("Trash Can") and Garbage_0/1 are in
        /// WorldBuilder.ContainerShelf, so they ARE smart containers on the world-load path -- but the editor's
        /// own placement path attaches devices through SmartProps, which covers lights, hydrants and doors and
        /// has no container case. Adding one means editing shared container code that tinyclaw is live in, so
        /// it is flagged rather than done.</summary>
        static void ScatterTownFurniture(Terrain terr, EditorObjects objs, ref int missing)
        {
            if (terr?.IslandTiles == null || objs == null) return;

            // ⚠ MEASURED FROM THE PIECE. The carriageway is 16 m inside a 24 m tile, so the pavement is the
            // ring from 8 m to 12 m off the tile centreline. 10 m is the middle of it: clear of the tarmac,
            // inside the prop, and well short of the 14.5 m where a building's front wall now starts.
            const float Verge = 10f;
            var rng = new System.Random(20260917);
            int lights = 0, signals = 0, hydrants = 0, bins = 0, miss = 0;
            var taken = new System.Collections.Generic.List<(float X, float Z)>();

            bool Free(float x, float z, float r)
            {
                foreach (var q in taken) if (Near(x, q.X, z, q.Z, r)) return false;
                return true;
            }

            // The street directions a piece serves, in ProcIsland's frame. Each piece has its connectors on
            // FIXED local axes (Line +Y/-Y, Turn +X/-Y, Tee +X/-X/+Y, Quad all four, and a Cap's ramp is +Y),
            // so running them through ArmDir at the tile's own yaw gives the real bearings -- the same helper
            // the exposure report uses, rather than a second table that can disagree with it.
            static (float x, float y)[] ArmsOf(ProcIsland.RoadPiece p) => p switch
            {
                ProcIsland.RoadPiece.Line    => new[] { (0f, 1f), (0f, -1f) },
                ProcIsland.RoadPiece.Turn    => new[] { (1f, 0f), (0f, -1f) },
                ProcIsland.RoadPiece.Tee     => new[] { (1f, 0f), (-1f, 0f), (0f, 1f) },
                ProcIsland.RoadPiece.Quad    => new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) },
                ProcIsland.RoadPiece.LineCap => new[] { (0f, 1f), (0f, -1f) },
                ProcIsland.RoadPiece.TeeCap  => new[] { (0f, 1f), (1f, 0f), (-1f, 0f) },
                ProcIsland.RoadPiece.QuadCap => new[] { (0f, 1f), (1f, 0f), (-1f, 0f), (0f, -1f) },
                _ => new[] { (0f, 1f), (0f, -1f) },
            };

            int idx = -1;
            foreach (var t in terr.IslandTiles)
            {
                idx++;
                // ⚠ A REAL JUNCTION ONLY. The Cap variants were in this set and they are not crossroads --
                // a cap is where a street LEAVES the town, and signalling it put 111 traffic lights on an
                // island where PEI has 21 in total. Quad and Tee are the shapes a driver actually has to be
                // told what to do at.
                bool junction = t.Piece is ProcIsland.RoadPiece.Quad or ProcIsland.RoadPiece.Tee;
                var arms = ArmsOf(t.Piece);

                // ---- TRAFFIC LIGHTS: one per approach of a junction, mast arm out over the carriageway -----
                if (junction)
                {
                    int put = 0;
                    foreach (var a in arms)
                    {
                        if (put >= 2) break;   // two opposing approaches, not one per arm
                        var d = ArmDir(t.YawDeg, a.x, a.y);
                        // Stand on the corner BESIDE this approach and reach back across it: position along the
                        // arm, offset to one side, and yaw so +Y points at the tile centre.
                        var side = (-d.z, d.x);
                        float px = t.X + d.x * Verge + side.Item1 * Verge;
                        float pz = t.Z + d.z * Verge + side.Item2 * Verge;
                        if (!Free(px, pz, 6f) || !TownPropOk(terr, px, pz)) continue;
                        // The mast arm reaches back ALONG THE APPROACH it controls, not diagonally across the
                        // junction: the pole stands on the corner and the signal hangs over that road's own
                        // carriageway, which is where a driver on it can see it.
                        float yaw = ProcIsland.YawForDir(-d.x, -d.z);
                        if (objs.Place("Traffic_Light_0", PosFor(terr, px, pz), RotFor(yaw)) != null)
                        { signals++; put++; taken.Add((px, pz)); }
                        else miss++;
                    }
                }

                // ---- STREET LIGHTS: one per tile, on alternating sides so a street is lit from both ---------
                {
                    var a = arms[idx % arms.Length];
                    var d = ArmDir(t.YawDeg, a.x, a.y);
                    var side = ((idx & 1) == 0) ? (-d.z, d.x) : (d.z, -d.x);
                    float px = t.X + side.Item1 * Verge, pz = t.Z + side.Item2 * Verge;
                    if (Free(px, pz, 5f) && TownPropOk(terr, px, pz))
                    {
                        // +Y toward the street: the lamp arm has to reach over the carriageway, not the verge.
                        float yaw = ProcIsland.YawForDir(-side.Item1, -side.Item2);
                        if (objs.Place("Street_Light_0", PosFor(terr, px, pz), RotFor(yaw)) != null)
                        { lights++; taken.Add((px, pz)); }
                        else miss++;
                    }
                }

                // ---- HYDRANTS: every third tile, opposite the light -----------------------------------------
                if (idx % 3 == 1)
                {
                    var a = arms[0];
                    var d = ArmDir(t.YawDeg, a.x, a.y);
                    var side = ((idx & 1) == 0) ? (d.z, -d.x) : (-d.z, d.x);
                    float px = t.X + side.Item1 * Verge + d.x * 6f, pz = t.Z + side.Item2 * Verge + d.z * 6f;
                    if (Free(px, pz, 3f) && TownPropOk(terr, px, pz))
                    {
                        if (objs.Place("Fire_Hydrant_0", PosFor(terr, px, pz), RotFor(ProcIsland.YawForDir(-side.Item1, -side.Item2))) != null)
                        { hydrants++; taken.Add((px, pz)); }
                        else miss++;
                    }
                }

                // ---- BINS: in little groups, like retail's 3 m clusters -------------------------------------
                if (idx % 6 == 2)
                {
                    var a = arms[0];
                    var d = ArmDir(t.YawDeg, a.x, a.y);
                    var side = ((idx & 2) == 0) ? (-d.z, d.x) : (d.z, -d.x);
                    int group = 2 + rng.Next(2);
                    for (int k = 0; k < group; k++)
                    {
                        float along = (k - (group - 1) * 0.5f) * 1.6f;   // retail's bins sit ~1.1-3.6 m apart
                        float px = t.X + side.Item1 * Verge + d.x * along;
                        float pz = t.Z + side.Item2 * Verge + d.z * along;
                        if (!Free(px, pz, 1.2f) || !TownPropOk(terr, px, pz)) continue;
                        // Dumpster_3/4 is the wheelie bin the container table already labels "Trash Can";
                        // Garbage_0/1 are the tied-off bags that stand next to one.
                        string prop = k == 0
                            ? (rng.Next(2) == 0 ? "Dumpster_3" : "Dumpster_4")
                            : (rng.Next(2) == 0 ? "Garbage_0" : "Garbage_1");
                        if (objs.Place(prop, PosFor(terr, px, pz), RotFor((float)(rng.NextDouble() * 360.0))) != null)
                        { bins++; taken.Add((px, pz)); }
                        else miss++;
                    }
                }
            }

            missing += miss;
            Log.Print($"[island-street] {lights} street light(s), {signals} traffic light(s), {hydrants} hydrant(s), {bins} bin(s) on the town verges"
                      + (miss > 0 ? $" ({miss} prop name(s) not in the catalogue)" : ""));
        }

        /// <summary>Whether a piece of street furniture can stand here: on a town's own flat pad, clear of the
        /// sea. ⚠ The pad test has NO margin, unlike the roadside pass's -- these belong INSIDE the town, which
        /// is the exact opposite requirement, and reusing RoadsideOk would have refused every one of them.</summary>
        static bool TownPropOk(Terrain terr, float px, float pz)
        {
            if (!ProcIsland.InsideAnyTownPad(px, pz)) return false;
            var w = PosFor(terr, px, pz);
            return !Terrain.HasWater || w.Y >= Terrain.SeaLevelY + 0.5f;
        }

        /// <summary>Roadside furniture along the routes between towns: crash barriers on the bends and a power
        /// line down one side (strawberry 2026-09-16: "place fence road props along sharp-ish road spline
        /// corners. place power lines along one side of the road splines, off to the side on the dirt beside
        /// the actual spline").
        ///
        /// ⭐ SPACING, OFFSET AND FACING ARE MEASURED OFF RETAIL, not chosen. PEI places 131 Power_Line_0 and
        /// 22 Fence_Road_0 in content/objects/placements.txt, and read back they say exactly how this kit is
        /// meant to be used:
        ///   * fences butt END TO END -- 20 of the 22 have a nearest neighbour at 16.0 m, and the mesh is
        ///     16.25 m long, so a run is continuous rather than a line of separate panels;
        ///   * poles stand 22-40 m apart, median 30;
        ///   * BOTH props run along their own local +Y: comparing each placement's yaw against the bearing to
        ///     its nearest neighbour, 126 of 131 poles and 22 of 22 fences are parallel to within a few
        ///     degrees. That is what makes YawFor -- which is defined as "the yaw that points local +Y along
        ///     this direction" -- the right and only rotation needed here.
        /// A guess would have had a 50/50 chance of laying every fence across the road instead of along it, and
        /// a symmetric prop gives nothing away in a screenshot.</summary>
        static void ScatterRoadside(Terrain terr, EditorObjects objs, ref int missing)
        {
            if (terr == null || objs == null || terr.IslandRoutes == null) return;

            const float FenceSpan = 16f;     // retail's measured run spacing; the mesh is 16.25 m long
            const float PoleSpan = 30f;      // retail's median pole-to-pole distance
            // OFF THE TARMAC AND ON THE DIRT, which is a band with two edges. RoadField draws the ribbon
            // 9.2 m to each side (RenderedRoadHalf) and PaintGroundwork paints route dirt out to 15 m
            // (RouteHalf 9 + RouteBorder 6). Anything between those two numbers is beside the road, on worked
            // ground, and inside the strip the foliage scatter already refuses -- so a pole does not end up
            // standing in a bush it was placed on top of.
            const float PoleOffset = 12f;
            const float FenceOffset = 11f;   // tighter: a barrier belongs at the edge of the carriageway
            // What counts as a corner worth a barrier. Radius of curvature, in metres, measured over the
            // joint spacing: below this the bend is tight enough to want protecting.
            const float CornerRadius = 90f;
            const int CornerPad = 1;         // joints of barrier carried either side, so a run reads as a rail

            int fences = 0, poles = 0, miss = 0, corners = 0;
            float sharpest = float.MaxValue; var radii = new System.Collections.Generic.List<float>();
            int ri = -1;

            foreach (var route in terr.IslandRoutes)
            {
                ri++;
                // ⚠ WALK THE JOINTS THE ROAD IS BUILT FROM, NOT THE RAW A* PATH. SpawnRoutes decimates
                // route.Points by RouteJointStride and hands THOSE to RoadField, so the ribbon a player sees
                // is the curve through every second point. Measuring curvature on the raw 4 m path measures
                // the A* staircase instead: an 8-connected search turns in 45-degree steps, and a single
                // diagonal jog across an 8 m chord reads as a 10 m-radius corner. First run said so -- 540
                // "bends tighter than 90 m" on one island, sharpest 6 m, which is not a road, it is the
                // sampling. Same stride, same curve, same answer as the thing being decorated.
                var raw = route.Points;
                if (raw == null || raw.Count < 6) continue;
                var pts = new System.Collections.Generic.List<Vector2>();
                for (int i = 0; i < raw.Count; i += RouteJointStride) pts.Add(raw[i]);
                if (pts[^1] != raw[^1]) pts.Add(raw[^1]);
                if (pts.Count < 6) continue;

                // Tangent and left-normal in ProcIsland's frame at each point, plus arc length so both passes
                // can step in METRES rather than in joints -- the A* path's spacing alternates 4 m and 5.66 m
                // with every diagonal step, so counting joints would put poles 20% closer together on a
                // diagonal stretch than on a straight one.
                int m = pts.Count;
                var tan = new Vector2[m];
                var arc = new float[m];
                for (int i = 0; i < m; i++)
                {
                    var a = pts[Mathf.Max(0, i - 1)];
                    var b2 = pts[Mathf.Min(m - 1, i + 1)];
                    var d = b2 - a;
                    tan[i] = d.Length() > 1e-4f ? d.Normalized() : Vector2.Right;
                    if (i > 0) arc[i] = arc[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                }

                // ---- the power line: one side, the whole length ------------------------------------------
                // WHICH side is fixed per route, not per point: a line that swaps sides halfway along is not a
                // power line, it is a mistake. Alternating by route index keeps the island from having every
                // pole on the same compass side of every road.
                float side = (ri & 1) == 0 ? 1f : -1f;
                float nextPole = PoleSpan * 0.5f;
                for (int i = 1; i < m; i++)
                {
                    while (nextPole <= arc[i])
                    {
                        float t = (arc[i] - arc[i - 1]) > 1e-4f ? (nextPole - arc[i - 1]) / (arc[i] - arc[i - 1]) : 0f;
                        var at = pts[i - 1].Lerp(pts[i], t);
                        var tg = tan[i];
                        var nrm = new Vector2(-tg.Y, tg.X) * side;
                        nextPole += PoleSpan;
                        float px = at.X + nrm.X * PoleOffset, pz = at.Y + nrm.Y * PoleOffset;
                        if (!RoadsideOk(terr, px, pz)) continue;
                        // ⚠ NO LIFT. Power_Line_0's mesh runs from -1.00 to 8.00 on its up axis -- a metre of
                        // pole below the origin, which is how a pole is planted. Seating the origin ON the
                        // ground buries that metre; lifting it clear would leave the pole standing on its tip.
                        var pos = PosFor(terr, px, pz);
                        if (objs.Place("Power_Line_0", pos, RotFor(ProcIsland.YawForDir(tg.X, tg.Y))) != null) poles++;
                        else miss++;
                    }
                }

                // ---- barriers on the bends ----------------------------------------------------------------
                // Curvature from the turn between consecutive tangents over the chord between them: a heading
                // change of theta radians across L metres is a radius of L/theta. Cheaper and steadier than
                // fitting a circle to three points, which blows up on the straights where theta is ~0.
                // ⚠ AND MEASURE IT OVER A BEND'S LENGTH, not between two joints. Even on the decimated path a
                // one-joint window is dominated by whatever wobble the smoothing left behind; a real road
                // corner is tens of metres long. W joints either side is ~32 m of arc, which is about the
                // length of the bend a barrier is for.
                const int W = 2;
                var wantFence = new bool[m];
                for (int i = W; i < m - W; i++)
                {
                    float chord = pts[i + W].DistanceTo(pts[i - W]);
                    if (chord < 1e-3f) continue;
                    float dot = Mathf.Clamp(tan[i - W].Dot(tan[i + W]), -1f, 1f);
                    float turn = Mathf.Acos(dot);
                    if (turn < 1e-4f) continue;
                    float radius = chord / turn;
                    radii.Add(radius);
                    if (radius > CornerRadius) continue;
                    if (radius < sharpest) sharpest = radius;
                    corners++;
                    for (int k = -CornerPad; k <= CornerPad; k++)
                        if (i + k >= 0 && i + k < m) wantFence[i + k] = true;
                }

                // Lay the barrier in RUNS, stepping 16 m along the arc like retail does, on the OUTSIDE of the
                // bend -- which is the side a vehicle leaves the road on, and the side a real crash barrier is
                // on. The outside is away from the centre of curvature, i.e. opposite the direction the tangent
                // is turning toward.
                float nextFence = 0f;
                for (int i = 1; i < m; i++)
                {
                    while (nextFence <= arc[i])
                    {
                        float segT = (arc[i] - arc[i - 1]) > 1e-4f ? (nextFence - arc[i - 1]) / (arc[i] - arc[i - 1]) : 0f;
                        int home = segT < 0.5f ? i - 1 : i;
                        nextFence += FenceSpan;
                        if (!wantFence[home]) continue;
                        var at = pts[i - 1].Lerp(pts[i], segT);
                        var tg = tan[home];
                        // Which way the road is turning here: the sign of the 2D cross product of the tangents
                        // either side. Outside of the bend is the opposite normal.
                        int lo = Mathf.Max(0, home - 2), hi = Mathf.Min(m - 1, home + 2);
                        float cross = tan[lo].X * tan[hi].Y - tan[lo].Y * tan[hi].X;
                        float outward = cross >= 0f ? -1f : 1f;
                        var nrm = new Vector2(-tg.Y, tg.X) * outward;
                        float px = at.X + nrm.X * FenceOffset, pz = at.Y + nrm.Y * FenceOffset;
                        if (!RoadsideOk(terr, px, pz)) continue;
                        // Fence_Road_0's mesh also runs a metre below its origin, for the same reason.
                        var pos = PosFor(terr, px, pz);
                        if (objs.Place("Fence_Road_0", pos, RotFor(ProcIsland.YawForDir(tg.X, tg.Y))) != null) fences++;
                        else miss++;
                    }
                }
            }

            missing += miss;
            radii.Sort();
            string spread = radii.Count > 0 ? $", corner radii median {radii[radii.Count / 2]:0} m / sharpest {sharpest:0} m" : "";
            Log.Print($"[island-roadside] {poles} power pole(s) at {PoleSpan:0} m, {fences} barrier panel(s) over {corners} bend(s) tighter than {CornerRadius:0} m{spread}"
                      + (miss > 0 ? $" ({miss} prop name(s) not in the catalogue)" : ""));
        }

        /// <summary>Whether a roadside prop can stand at this spot: on dry land, off the town pads, and not on
        /// a face it would be sticking out of sideways. ⚠ The town test carries a margin -- a pole a couple of
        /// metres outside the pad boundary is still standing in the town's front garden.</summary>
        static bool RoadsideOk(Terrain terr, float px, float pz)
        {
            if (ProcIsland.InsideAnyTownPad(px, pz, 6f)) return false;
            var w = PosFor(terr, px, pz);
            if (Terrain.HasWater && w.Y < Terrain.SeaLevelY + 0.5f) return false;
            return terr.SlopeAt(px, pz) < SteepRise;
        }

        /// <summary>The rock kit WITH ITS MEASURED PLAN RADIUS, in mesh metres at scale 1.
        ///
        /// ⚠ THE SPREAD IS THE WHOLE PROBLEM, and it is why a bare name list was not enough. These props are
        /// not variations on a boulder -- measured off the OBJs in content/objects they run from Boulder_08 at
        /// 8.9 x 10.2 m to Boulder_04 at 31 x 48 m and 32 m TALL. A single uniform scale range applied across
        /// that kit produces rocks anywhere from 3 m to 30 m across at random, which is exactly what
        /// strawberry saw: "boulder placement seems very erratic and inconsistent". The scatter below therefore
        /// picks the SIZE IT WANTS first and solves for the scale, so what varies is a decision instead of an
        /// accident of which prop the roll landed on.
        ///
        /// ⚠ NAMED, NOT NUMBERED. Boulder_00..22 is not contiguous in the rip -- 05, 07 and 14-21 are absent --
        /// so generating an index range asked for props that do not exist and 141 of 829 placements silently
        /// failed. The miss counter is what caught it.</summary>
        static readonly (string Name, float Radius, float Bottom, float Height)[] BoulderProps =
        {
            // ⚠ THE PEI SET ONLY (strawberry 2026-09-16: "should only be using PEI boulders because those are
            // the correct color for the dirt"). The kit has 15 rock props; PEI's placements.txt uses exactly
            // FOUR of them -- Boulder_13 x118, Boulder_11 x85, Boulder_12 x48, Boulder_22_PEI x7 -- and the
            // rest are other maps' palettes. Every one of the other eleven is placed ZERO times. The island
            // shares PEI's terrain textures, so a rock from Russia's set is the right shape and the wrong
            // colour against this dirt, which is not something the geometry can tell you.
            //
            // Radius is half the larger plan axis; Bottom and Height are the mesh's own vertical extent about
            // its origin, both measured off the OBJs. These props are ORIGIN-CENTRED (Boulder_13 spans
            // -4.61..+4.38), so a flat "sink it a bit" on top of that buries most of the rock -- the seating
            // below solves for where the origin has to go instead.
            ("Boulder_11",  9.63f, -7.85f, 17.28f),
            ("Boulder_12", 16.90f, -2.48f,  5.83f),
            ("Boulder_13",  8.70f, -4.61f,  9.00f),
            ("Boulder_22_PEI", 15.57f, -4.53f, 9.17f),
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

            // ⚠ THE OLD SCAN COULD NOT COVER A FACE, and no amount of tuning its numbers would have.
            // It stepped a 17 m lattice and took ONE jittered sample per cell: a cell that is half cliff and
            // half meadow got a rock only if that single sample happened to land on the cliff half, so a face
            // came out speckled with gaps that have nothing to do with the terrain -- strawberry: "very erratic
            // and inconsistent. not covering the full cliff faces". Density also could not follow the ground,
            // because one sample per 289 m^2 is one sample whether the cell is a 30-degree bank or a vertical
            // wall.
            //
            // So: scan FINE, and let the rocks themselves decide the spacing. Every sample on steep ground is a
            // candidate; the only thing that turns one down is another rock already occupying the space. That
            // makes coverage a property of the face rather than of the lattice.
            const float Step = 5f;

            // WHAT SIZE OF ROCK, decided before which prop. The kit spans 5 m to 24 m of radius, so a shared
            // scale range means the roll of a prop name IS the size roll -- the inconsistency complaint.
            // Choosing a target radius and solving scale = target / propRadius makes every rock the size it was
            // meant to be, out of whichever mesh got picked.
            const float SmallR = 1.6f, BigR = 4.5f;
            // ...with a few landmarks. A face of nothing but 2 m rocks is as uniform as a face of nothing but
            // 20 m ones; the tail is what makes it read as a rockfall.
            const float LandmarkR = 9f, LandmarkChance = 0.06f;

            // Radius-aware occupancy, in a spatial hash. The old test was O(placed) against every rock on the
            // island for every candidate, which a 5 m scan would have turned into minutes, and it compared
            // against a FIXED 13 m whatever size the two rocks were -- so 5 m pebbles could not sit near each
            // other and 30 m slabs sat inside each other.
            const float Cell = 16f;
            var occ = new System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<(float X, float Z, float R)>>();
            bool Blocked(float px, float pz, float r)
            {
                int cx = Mathf.FloorToInt(px / Cell), cz = Mathf.FloorToInt(pz / Cell);
                int reach = Mathf.CeilToInt((r + BigR * 2f) / Cell);
                for (int i = cx - reach; i <= cx + reach; i++)
                    for (int j = cz - reach; j <= cz + reach; j++)
                    {
                        if (!occ.TryGetValue((i, j), out var list)) continue;
                        foreach (var q in list)
                        {
                            float dx = px - q.X, dz = pz - q.Z;
                            // 0.8, not 1.0: rocks in a real fall lean on each other. Full separation reads as a
                            // row of ornaments placed at arm's length.
                            float min = (r + q.R) * 0.8f;
                            if (dx * dx + dz * dz < min * min) return true;
                        }
                    }
                return false;
            }
            void Occupy(float px, float pz, float r)
            {
                var key = (Mathf.FloorToInt(px / Cell), Mathf.FloorToInt(pz / Cell));
                if (!occ.TryGetValue(key, out var list)) { list = new System.Collections.Generic.List<(float, float, float)>(); occ[key] = list; }
                list.Add((px, pz, r));
            }

            // ⚠ AND NOT ON THE ROAD. The old 17 m scan was sparse enough that this never came up; scanning at
            // 5 m made it reachable, and the first render had a rock sitting in the carriageway at a fork.
            // Steepness alone does not exclude a road: the corridor is graded flat down the middle but its
            // SHOULDERS are the steepest ground for hundreds of metres, which is exactly where the scan now
            // looks hardest. A road surface is laid over the terrain, so nothing about the terrain can tell the
            // scatter it is there -- the route list has to.
            var corridor = RouteCorridorCells(terr);
            int n = 0, miss = 0, steepSamples = 0, onRoad = 0;
            for (float x = b.MinX; x < b.MaxX; x += Step)
                for (float z = b.MinZ; z < b.MaxZ; z += Step)
                {
                    float px = x + (float)(rng.NextDouble() * 2 - 1) * Step * 0.45f;
                    float pz = z + (float)(rng.NextDouble() * 2 - 1) * Step * 0.45f;
                    float rise = terr.SlopeAt(px, pz);
                    if (rise < SteepRise) continue;
                    float y = terr.SampleHeight(px, pz);
                    if (Terrain.HasWater && y < Terrain.SeaLevelY) continue;   // boulders on the face, not the seabed
                    // ⚠ FRAME. This scan walks WORLD coordinates (WorldBoundsXZ, SampleHeight), and both the
                    // route list and the town pads are in ProcIsland's frame, which negates Z. Passing world
                    // coordinates straight in matched NOTHING -- the first run said "0 refused as road or
                    // town", which is what a filter looks like when it is asking about the mirror image of the
                    // island. PosFor is the one-way map (px, -pz); this is its inverse, and it is the same
                    // negation every crossover in this file has to do.
                    if (corridor.Contains(CorridorKey(px, -pz)) || ProcIsland.InsideAnyTownPad(px, -pz, 4f)) { onRoad++; continue; }
                    steepSamples++;

                    // THE STEEPER THE FACE, THE BIGGER AND DENSER THE ROCK. A 30-degree bank gets the small end
                    // of the range and a near-vertical wall the large end, which is both what a scree slope
                    // looks like and what makes the rocks read as having come OFF the face rather than been
                    // dropped on it. steep = 0 at the threshold, 1 at roughly twice it.
                    float steep = Mathf.Clamp((rise - SteepRise) / SteepRise, 0f, 1f);
                    float want = Mathf.Lerp(SmallR, BigR, steep * (float)rng.NextDouble() + (1f - steep) * (float)rng.NextDouble() * 0.5f);
                    if (rng.NextDouble() < LandmarkChance * (0.3f + steep)) want = LandmarkR * (0.7f + (float)rng.NextDouble() * 0.6f);
                    if (Blocked(px, pz, want)) continue;

                    // Pick a prop whose natural size is nearest what was asked for, out of a few candidates, so
                    // the scale factor stays near 1 and the mesh is not stretched into something it is not.
                    var pick = BoulderProps[rng.Next(BoulderProps.Length)];
                    for (int t = 0; t < 3; t++)
                    {
                        var alt = BoulderProps[rng.Next(BoulderProps.Length)];
                        if (Mathf.Abs(alt.Radius - want) < Mathf.Abs(pick.Radius - want)) pick = alt;
                    }
                    float scale = want / pick.Radius;

                    // ⚠ SIT ON THE SLOPE, NOT ON A FLAT WORLD (strawberry: "scale and rotate the props to
                    // actually fit the slopes"). RotFor gives the stand-up + yaw every prop here uses, which
                    // leaves the rock perfectly upright -- on a 30-degree face that reads as a boulder balanced
                    // on a hillside rather than resting in it, and the steeper the face the more obviously
                    // wrong it looks. Tilting world-up onto the terrain normal settles it into the ground.
                    // Composed tilt * stand, not the other way: the yaw must happen in the prop's own frame
                    // before the whole thing is laid over, or turning a rock also changes which way it leans.
                    var normal = terr.NormalAt(px, pz);
                    var axis = Vector3.Up.Cross(normal);
                    var stand = RotFor((float)(rng.NextDouble() * 360.0));
                    var basis = axis.LengthSquared() < 1e-8f
                        ? stand
                        : new Basis(axis.Normalized(), Vector3.Up.AngleTo(normal)) * stand;
                    // SEAT IT BY ITS OWN MESH, not by a fudge. Put the origin where the rock's BOTTOM lands a
                    // fifth of its height under the ground: bottom-to-origin is -Bottom*scale, so the origin
                    // sits that far above the surface, less the bury. A flat sink could not do this across a
                    // kit whose origins sit anywhere from -2.5 m to -7.9 m inside the mesh -- it left the tall
                    // rocks perched and swallowed the flat ones.
                    float sc = scale;
                    var pos = new Vector3(px, y - pick.Bottom * sc - pick.Height * sc * 0.20f, pz);
                    if (objs.Place(pick.Name, pos, basis.Scaled(Vector3.One * scale)) != null) { Occupy(px, pz, want); n++; }
                    else miss++;
                }
            missing += miss;
            Log.Print($"[island-rocks] {n} boulder(s) on ground steeper than {Mathf.RadToDeg(Mathf.Atan(SteepRise)):0.#} deg"
                      + $" ({steepSamples} steep sample(s) scanned at {Step:0.#} m, {onRoad} refused as road or town)"
                      + (miss > 0 ? $" ({miss} prop name(s) not in the catalogue)" : ""));
        }

        /// <summary>Coarse cells that any route passes through, inflated by the width of the road that is drawn
        /// on it. A set membership test instead of a distance search: a route is thousands of points and the
        /// boulder scan asks hundreds of thousands of times.
        /// ⚠ The cell is the same size as the reach, so a point in a cell can still be up to a cell-diagonal
        /// from the road -- deliberately generous. Refusing a rock that would have been fine costs nothing on a
        /// face with hundreds of candidates; letting one stand in the road costs a screenshot.</summary>
        const float CorridorCell = 12f;
        static (int, int) CorridorKey(float x, float z) => (Mathf.FloorToInt(x / CorridorCell), Mathf.FloorToInt(z / CorridorCell));

        static System.Collections.Generic.HashSet<(int, int)> RouteCorridorCells(Terrain terr)
        {
            var set = new System.Collections.Generic.HashSet<(int, int)>();
            if (terr?.IslandRoutes == null) return set;
            // Rendered half-width plus the paint's shoulder: the dirt band IS the road's footprint as far as
            // anything standing beside it is concerned.
            int reach = Mathf.CeilToInt((ProcIsland.RenderedRoadHalf + 8f) / CorridorCell);
            foreach (var route in terr.IslandRoutes)
            {
                if (route.Points == null) continue;
                foreach (var pt in route.Points)
                {
                    var k = CorridorKey(pt.X, pt.Y);
                    for (int i = -reach; i <= reach; i++)
                        for (int j = -reach; j <= reach; j++)
                            set.Add((k.Item1 + i, k.Item2 + j));
                }
            }
            return set;
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
                // ⚠ THE END JOINTS ARE THE CAP'S HEIGHT, NOT THE LOCAL MAXIMUM (strawberry 2026-09-16: "some
                // road splines arent connecting to the prop road caps").
                //
                // A route's first and last points ARE its gate -- the connector sits on the monument's
                // perimeter, which is the outer edge of the cap tile, so in PLAN the ribbon already starts
                // exactly where the ramp ends. The disconnect was vertical and it was self-inflicted:
                // JointPosFor seats a joint on the HIGHEST ground within 5 m, which is right everywhere along
                // the route and wrong at its ends, because 5 m from the pad edge reaches off the flat pad onto
                // country that is usually higher. The cap prop is seated by TilePosFor -- pad height plus the
                // lift, with no hunting -- so the ribbon started above the piece it was supposed to meet.
                // Seating the ends the same way TilePosFor seats the cap makes them agree by construction
                // rather than by the two happening to sample the same number.
                pts[0] = TilePosFor(terr, route.Points[0].X, route.Points[0].Y);
                pts[^1] = TilePosFor(terr, last.X, last.Y);

                float len = 0f;
                for (int i = 1; i < pts.Count; i++) len += pts[i].DistanceTo(pts[i - 1]);
                if (len < MinLen) { skipped++; continue; }
                SmoothProfile(pts);

                if (rf.AddRoadFromPolyline(pts, material) >= 0) built++; else skipped++;
            }
            Log.Print($"[island-roads] {built} spline road(s) between towns" + (skipped > 0 ? $" ({skipped} route(s) skipped as too short or degenerate)" : ""));
            ReportCapJoins(terr);
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
            ScatterRoadside(terr, objs, ref missing);
            ScatterTownFurniture(terr, objs, ref missing);
            ReportPieces(terr);
            Log.Print($"[island] spawned {roads} road props + {buildings} buildings" + (missing > 0 ? $" ({missing} MISSING from the object catalogue)" : ""));
            return (roads, buildings, missing);
        }
    }
}
