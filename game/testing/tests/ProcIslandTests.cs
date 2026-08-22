using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // PROCEDURAL ISLAND HEIGHTMAP (strawberry 2026-08-21: "island. yes deterministic.")
    //
    // Two properties matter and they fail in different ways, so they are checked separately: that the SAME SEED
    // gives the same map (the seed is the shareable artifact -- if it drifts, nothing else here is worth having),
    // and that the result is actually an ISLAND rather than a plane, a bowl, or a map that happens to be all
    // water. The second needs real measurement: "it generated something" is satisfied by every one of those.
    public sealed class ProcIslandTests : GameTest
    {
        public override string Name => "world.proc_island";
        public override double TimeoutSimSeconds => 60;

        static float[,] Gen(int tiles, int seed) => Gen(tiles, seed, out _);

        static float[,] Gen(int tiles, int seed, out System.Collections.Generic.List<ProcIsland.Poi> pois)
        {
            int gw = tiles * 256 + 1, gh = tiles * 256 + 1;
            var g = new float[gw, gh];
            var pars = ProcIsland.Params.Default(seed);
            ProcIsland.Fill(g, gw, gh, pars);
            pois = ProcIsland.PlacePois(g, gw, gh, pars);   // flattens pads + smooths, so the grid IS the final one
            return g;
        }

        // Land fraction, and how much of the BORDER is dry -- the two numbers that separate an island from the
        // shapes that would also pass a naive "heights vary" check.
        static (float land, float borderLand, float minY, float maxY) Survey(float[,] g)
        {
            int gw = g.GetLength(0), gh = g.GetLength(1);
            float sea = 25.6f;
            int land = 0, border = 0, borderLand = 0;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int x = 0; x < gw; x++)
                for (int y = 0; y < gh; y++)
                {
                    float w = ProcIsland.ToWorld(g[x, y]);
                    lo = Mathf.Min(lo, w); hi = Mathf.Max(hi, w);
                    if (w > sea) land++;
                    if (x == 0 || y == 0 || x == gw - 1 || y == gh - 1)
                    {
                        border++;
                        if (w > sea) borderLand++;
                    }
                }
            return (land / (float)(gw * gh), borderLand / (float)border, lo, hi);
        }

        public override IEnumerable<Step> Run()
        {
            // ---- DETERMINISM. Bit-identical, not approximately equal: the seed is what gets shared, so "close
            // enough" is a different map. Compared exactly, on the raw stored floats.
            var a = Gen(1, 1234);
            var b = Gen(1, 1234);
            int diffs = 0;
            for (int x = 0; x < a.GetLength(0); x++)
                for (int y = 0; y < a.GetLength(1); y++)
                    if (a[x, y] != b[x, y]) diffs++;
            T.Check($"the same seed is bit-identical ({diffs} differing cells of {a.Length})", diffs == 0);

            // ...and the CONTROL, without which the check above is also passed by a generator that ignores its
            // seed entirely and returns the same map every time.
            var c = Gen(1, 9876);
            int seedDiffs = 0;
            for (int x = 0; x < a.GetLength(0); x++)
                for (int y = 0; y < a.GetLength(1); y++)
                    if (a[x, y] != c[x, y]) seedDiffs++;
            T.Check($"control: a DIFFERENT seed is a different map ({seedDiffs} cells differ)",
                seedDiffs > a.Length / 10);

            // ---- IS IT AN ISLAND. Land in the middle, water at every edge.
            var (land, borderLand, lo, hi) = Survey(a);
            T.Check($"it is land, not an ocean ({land * 100f:0.#}% above sea)", land > 0.12f);
            T.Check($"...and ocean, not a continent ({land * 100f:0.#}% above sea)", land < 0.75f);
            T.Check($"...and the coast CLOSES -- the border is all water ({borderLand * 100f:0.##}% of the rim is dry)",
                borderLand < 0.005f);
            T.Check($"...with a seabed below the water and hills above it ({lo:0.#} m to {hi:0.#} m, sea 25.6)",
                lo < 20f && hi > 45f);

            // ---- SIZE IS A PARAMETER, which is the half of the brief that is about later ("configurable to a
            // larger map later on"). A generator that only works at the size it was tuned at has not got it.
            var big = Gen(2, 1234);
            T.Check($"a larger map generates at its own size ({big.GetLength(0)}x{big.GetLength(1)} vs {a.GetLength(0)}x{a.GetLength(1)})",
                big.GetLength(0) == 513 && a.GetLength(0) == 257);
            var (bland, bborder, _, bhi) = Survey(big);
            T.Check($"...and is still an island at that size ({bland * 100f:0.#}% land, {bborder * 100f:0.##}% rim dry, peak {bhi:0.#} m)",
                bland > 0.12f && bland < 0.75f && bborder < 0.005f && bhi > 45f);

            // ---- POIs (strawberry: "placing a few town/military base/construction site markers ... these pois
            // each have a size, they will flatten a terrain area around them, then a slight terrain smoothing").
            Gen(1, 1234, out var pois);
            // EVERY REQUESTED KIND, not just a count. `pois.Count >= 3` passed on a run that placed both
            // construction sites and the base and ZERO of the two towns -- the count was right and the mix was
            // wrong, which is invisible to any check that only counts. Towns are the demanding case (largest
            // footprint, so the hardest to fit inland), which is exactly why counting hides their absence.
            int nTown = 0, nBase = 0, nSite = 0;
            foreach (var q in pois)
            {
                if (q.Kind == ProcIsland.PoiKind.Town) nTown++;
                else if (q.Kind == ProcIsland.PoiKind.MilitaryBase) nBase++;
                else nSite++;
            }
            T.Check($"POIs got placed ({pois.Count}: {string.Join(", ", pois)})", pois.Count >= 3);
            T.Check($"...every requested KIND is present ({nTown} town, {nBase} base, {nSite} site) [{ProcIsland.LastRejectReport}]",
                nTown >= 1 && nBase >= 1 && nSite >= 1);

            // Every one has to be somewhere you could actually build: dry, and clear of the coast by more than
            // the pad it flattens, or the flatten eats the shoreline and leaves a rectangular beach.
            bool allDry = true, allApart = true;
            foreach (var poi in pois)
            {
                if (poi.GroundY <= 25.6f + 3f) allDry = false;
                foreach (var o in pois)
                {
                    if (o.X == poi.X && o.Z == poi.Z) continue;
                    // Chebyshev, matching the square footprints: two axis-aligned squares overlap only when
                    // they overlap on BOTH axes, and a Euclidean test passes diagonal pairs whose corners are
                    // inside each other.
                    float gap = Mathf.Max(Mathf.Abs(o.X - poi.X), Mathf.Abs(o.Z - poi.Z));
                    if (gap < o.HalfSize + poi.HalfSize) allApart = false;
                }
            }
            T.Check("...all on dry land, above the waterline", allDry);
            T.Check("...and none overlapping another", allApart);

            // THE FLATTEN ACTUALLY FLATTENED. Measured as the height spread inside each footprint -- a POI whose
            // pad still has 30 m of relief in it has a marker and nothing else, and every check above would pass.
            float worstSpread = 0f;
            foreach (var poi in pois)
            {
                float plo = float.MaxValue, phi = float.MinValue;
                int cx = Mathf.RoundToInt(poi.X / 4f), cy = Mathf.RoundToInt(poi.Z / 4f);
                int rad = Mathf.FloorToInt(poi.HalfSize / 4f);
                for (int x = Mathf.Max(0, cx - rad); x <= Mathf.Min(a.GetLength(0) - 1, cx + rad); x++)
                    for (int y = Mathf.Max(0, cy - rad); y <= Mathf.Min(a.GetLength(1) - 1, cy + rad); y++)
                    {
                        if (Mathf.Max(Mathf.Abs(x * 4f - poi.X), Mathf.Abs(y * 4f - poi.Z)) > poi.HalfSize) continue;
                        float w = ProcIsland.ToWorld(a[x, y]);
                        plo = Mathf.Min(plo, w); phi = Mathf.Max(phi, w);
                    }
                worstSpread = Mathf.Max(worstSpread, phi - plo);
            }
            T.Check($"...and the ground under them is actually level (worst spread {worstSpread:0.##} m across a footprint)",
                worstSpread < 3.5f);

            // ---- MONUMENT NETWORK (strawberry: "monuments can have road/trail(dirt road)/rail connections
            // between them. make em make sense").
            var links = ProcIsland.BuildLinks(pois);
            var cons = ProcIsland.BuildConnectors(pois, links);

            // EVERY monument reachable. A link list that looks reasonable can still leave one stranded, and a
            // stranded monument is a place with no way in -- which no count of links would show.
            var seen = new System.Collections.Generic.HashSet<int> { 0 };
            for (int pass = 0; pass < pois.Count; pass++)
                foreach (var l in links)
                {
                    if (seen.Contains(l.A)) seen.Add(l.B);
                    if (seen.Contains(l.B)) seen.Add(l.A);
                }
            var netDesc = new System.Text.StringBuilder();
            foreach (var l in links) netDesc.Append($"{l.Kind} {pois[l.A].Kind}<->{pois[l.B].Kind} {l.Length:0}m; ");
            // PRINTED, not just embedded in a check message. A T.Check's text is only shown when it FAILS, so
            // putting the network description there made it readable exactly when the network was already known
            // to be broken -- useless for the thing it was added for, which is reading a HEALTHY network to see
            // whether the road/trail/rail choices make sense.
            GD.Print($"[island] {pois.Count} monuments, {links.Count} links: {netDesc}");
            foreach (var q in pois) GD.Print($"[island]   {q}");
            T.Check($"the network reaches every monument ({seen.Count}/{pois.Count}) -- {netDesc}",
                seen.Count == pois.Count);

            T.Check($"...and each link has a gate at BOTH ends ({cons.Count} gates for {links.Count} links)",
                cons.Count == links.Count * 2);

            // GATES SIT ON THE EDGE. Chebyshev distance from the centre must equal HalfSize exactly -- a gate
            // computed radially lands OUTSIDE the pad on the diagonals (a corner is 1.41x further out than an
            // edge midpoint), leaving a gap between a monument and its own road that nothing else would catch.
            float worstOff = 0f; bool allOutward = true;
            foreach (var gate in cons)
            {
                var owner = pois[gate.Poi];
                float cheb = Mathf.Max(Mathf.Abs(gate.X - owner.X), Mathf.Abs(gate.Z - owner.Z));
                worstOff = Mathf.Max(worstOff, Mathf.Abs(cheb - owner.HalfSize));
                // ...and facing OUT, or the path stage starts by driving into the monument it just left.
                if ((gate.X - owner.X) * gate.DirX + (gate.Z - owner.Z) * gate.DirZ <= 0f) allOutward = false;
            }
            T.Check($"...every gate lies ON its monument's edge (worst off-edge {worstOff:0.###} m)", worstOff < 0.01f);
            T.Check("...and every gate faces outward, away from its own monument", allOutward);

            // THE RULES MAKE SENSE. Asserted as rules rather than as a tally: "3 trails" is true of a run that
            // paved a route to a construction site and dirt-tracked one between two towns.
            bool siteAlwaysTrail = true, railOnlyLongAndPermanent = true, railExists = false;
            foreach (var l in links)
            {
                bool touchesSite = pois[l.A].Kind == ProcIsland.PoiKind.ConstructionSite
                                || pois[l.B].Kind == ProcIsland.PoiKind.ConstructionSite;
                if (touchesSite && l.Kind != ProcIsland.LinkKind.Trail) siteAlwaysTrail = false;
                if (l.Kind == ProcIsland.LinkKind.Rail)
                {
                    railExists = true;
                    if (touchesSite || l.Length <= 900f) railOnlyLongAndPermanent = false;
                }
            }
            T.Check("a construction site is always reached by a dirt trail, never a paved road", siteAlwaysTrail);
            T.Check($"rail only runs long hauls between permanent places (any rail: {railExists})", railOnlyLongAndPermanent);

            // ---- MONUMENTS BUILT FROM THE ROAD KIT.
            cons = ProcIsland.SnapConnectorsToLattice(pois, cons);
            var tiles = new System.Collections.Generic.List<ProcIsland.MonumentTile>();
            for (int i = 0; i < pois.Count; i++) tiles.AddRange(ProcIsland.BuildMonument(i, pois[i], cons));
            T.Check($"every monument got streets ({tiles.Count} props across {pois.Count} monuments)",
                tiles.Count >= pois.Count);

            // ONLY CAPS CARRY CONNECTIONS, AND EVERY CONNECTION IS ON A CAP. Two directions, because each is
            // satisfiable while the other fails: an interior crossroads wearing a Cap is as wrong as a gate
            // opening onto a plain Line.
            bool capsOnlyAtGates = true, everyGateOnACap = true;
            foreach (var gate in cons)
            {
                var owner = pois[gate.Poi];
                bool found = false;
                foreach (var t in tiles)
                {
                    if (t.Poi != gate.Poi) continue;
                    bool isCap = t.Piece is ProcIsland.RoadPiece.LineCap or ProcIsland.RoadPiece.TeeCap or ProcIsland.RoadPiece.QuadCap;
                    // The cap's own connector: tile centre, out along its ramp, half a tile.
                    float yaw = Mathf.DegToRad(t.YawDeg);
                    float rx = -Mathf.Sin(yaw), rz = -Mathf.Cos(yaw);   // mesh +Y after yaw -- the ramp
                    float px = t.X + rx * ProcIsland.TileSize * 0.5f, pz = t.Z + rz * ProcIsland.TileSize * 0.5f;
                    if (isCap && Mathf.Abs(px - gate.X) < 0.6f && Mathf.Abs(pz - gate.Z) < 0.6f) { found = true; break; }
                }
                if (!found) everyGateOnACap = false;
            }
            // ...and no Cap anywhere that is not serving a gate.
            foreach (var t in tiles)
            {
                bool isCap = t.Piece is ProcIsland.RoadPiece.LineCap or ProcIsland.RoadPiece.TeeCap or ProcIsland.RoadPiece.QuadCap;
                if (!isCap) continue;
                float yaw = Mathf.DegToRad(t.YawDeg);
                float px = t.X - Mathf.Sin(yaw) * ProcIsland.TileSize * 0.5f, pz = t.Z - Mathf.Cos(yaw) * ProcIsland.TileSize * 0.5f;
                bool serves = false;
                foreach (var gate in cons)
                    if (gate.Poi == t.Poi && Mathf.Abs(px - gate.X) < 0.6f && Mathf.Abs(pz - gate.Z) < 0.6f) { serves = true; break; }
                if (!serves) capsOnlyAtGates = false;
            }
            T.Check("every gate opens onto a Cap prop, at its ramp", everyGateOnACap);
            T.Check("...and no Cap exists that is not serving a gate", capsOnlyAtGates);

            // THE LATTICE ACTUALLY LINES UP. A gate off the lattice means the road meets the monument up to 12 m
            // past the end of the very road piece it is supposed to join -- and every check above still passes,
            // because they all measure the cap against the gate rather than either against the grid.
            float worstOffLattice = 0f;
            foreach (var gate in cons)
            {
                var owner = pois[gate.Poi];
                float along = Mathf.Abs(gate.DirX) > 0.5f ? gate.Z - owner.Z : gate.X - owner.X;
                int n2 = ProcIsland.TilesFor(owner.Kind);
                float k = along / ProcIsland.TileSize + (n2 - 1) * 0.5f;
                worstOffLattice = Mathf.Max(worstOffLattice, Mathf.Abs(k - Mathf.Round(k)) * ProcIsland.TileSize);
            }
            T.Check($"every gate sits on a lattice line (worst {worstOffLattice:0.###} m off)", worstOffLattice < 0.01f);

            // DOES IT LINE UP. This is the property the other checks do NOT cover: each prop's connector arms
            // must point at exactly the neighbouring tiles it is supposed to join, plus its ramp if it is a cap.
            // "Every gate is on a cap" and "everything is on the lattice" are both satisfied by a grid of
            // correctly-placed pieces rotated wrongly -- the arms then open onto empty ground and the neighbour
            // they should meet presents solid kerb. That reads as a road that does not connect.
            int mismatched = 0; string firstBad = "";
            foreach (var t in tiles)
            {
                var owner2 = pois[t.Poi];
                float yaw2 = Mathf.DegToRad(t.YawDeg);
                (int, int)[] localDirs = t.Piece switch
                {
                    ProcIsland.RoadPiece.Line or ProcIsland.RoadPiece.LineCap => new[] { (0, 1), (0, -1) },
                    ProcIsland.RoadPiece.Turn                                  => new[] { (1, 0), (0, -1) },
                    ProcIsland.RoadPiece.Tee or ProcIsland.RoadPiece.TeeCap    => new[] { (1, 0), (-1, 0), (0, 1) },
                    _                                                          => new[] { (1, 0), (-1, 0), (0, 1), (0, -1) },
                };
                var arms = new System.Collections.Generic.HashSet<(int, int)>();
                foreach (var (lx, ly) in localDirs)
                {
                    float wxd = lx * Mathf.Cos(yaw2) + ly * -Mathf.Sin(yaw2);
                    float wzd = lx * -Mathf.Sin(yaw2) + ly * -Mathf.Cos(yaw2);
                    arms.Add((Mathf.RoundToInt(wxd), Mathf.RoundToInt(wzd)));
                }
                // what this tile MUST serve: a neighbouring tile of the same monument, or its own gate
                var must = new System.Collections.Generic.HashSet<(int, int)>();
                foreach (var u in tiles)
                {
                    if (u.Poi != t.Poi) continue;
                    float ddx = u.X - t.X, ddz = u.Z - t.Z;
                    if (Mathf.Abs(Mathf.Abs(ddx) - ProcIsland.TileSize) < 0.5f && Mathf.Abs(ddz) < 0.5f) must.Add((System.Math.Sign(ddx), 0));
                    if (Mathf.Abs(Mathf.Abs(ddz) - ProcIsland.TileSize) < 0.5f && Mathf.Abs(ddx) < 0.5f) must.Add((0, System.Math.Sign(ddz)));
                }
                foreach (var gate in cons)
                {
                    if (gate.Poi != t.Poi) continue;
                    if (Mathf.Abs(gate.X - (t.X + gate.DirX * 12f)) < 0.6f && Mathf.Abs(gate.Z - (t.Z + gate.DirZ * 12f)) < 0.6f)
                        must.Add((Mathf.RoundToInt(gate.DirX), Mathf.RoundToInt(gate.DirZ)));
                }
                foreach (var m in must)
                    if (!arms.Contains(m))
                    {
                        mismatched++;
                        if (firstBad == "") firstBad = $"{t} needs an arm toward ({m.Item1},{m.Item2}) and has none";
                        break;
                    }
            }
            T.Check($"every prop's arms reach its neighbours ({mismatched} pieces mis-rotated{(firstBad == "" ? "" : " -- " + firstBad)})",
                mismatched == 0);

            // ...AND NO ARM REACHES NOTHING. The converse, and the one that was missing: a piece may serve every
            // neighbour it has and still have a SPARE connector, which is laid as carriageway into empty ground.
            // A Quad used as a fallback on a 3-way cell does exactly that, and it reads as a road stub crossing
            // out of the street for no reason. Both directions are needed -- "reaches its neighbours" and
            // "reaches nothing else" are independent, and the kit only has an exact piece for some shapes.
            int spareArms = 0; string firstSpare = "";
            foreach (var t in tiles)
            {
                float yaw3 = Mathf.DegToRad(t.YawDeg);
                (int, int)[] localDirs2 = t.Piece switch
                {
                    ProcIsland.RoadPiece.Line or ProcIsland.RoadPiece.LineCap => new[] { (0, 1), (0, -1) },
                    ProcIsland.RoadPiece.Turn                                  => new[] { (1, 0), (0, -1) },
                    ProcIsland.RoadPiece.Tee or ProcIsland.RoadPiece.TeeCap    => new[] { (1, 0), (-1, 0), (0, 1) },
                    _                                                          => new[] { (1, 0), (-1, 0), (0, 1), (0, -1) },
                };
                // ONLY JUNCTIONS. A Line/LineCap's two ends are its own body -- 12 m of carriageway that simply
                // stops -- and a road ending at the edge of a construction site is a road ending, not a defect.
                // A junction is different: a Quad or Tee with an unused arm draws a spur crossing out of the
                // street into open ground, which is what strawberry saw as "a random cross". So the rule is
                // about junction pieces, not about every connector.
                if (t.Piece is ProcIsland.RoadPiece.Line or ProcIsland.RoadPiece.LineCap) continue;
                foreach (var (lx, ly) in localDirs2)
                {
                    float wxd = lx * Mathf.Cos(yaw3) + ly * -Mathf.Sin(yaw3);
                    float wzd = lx * -Mathf.Sin(yaw3) + ly * -Mathf.Cos(yaw3);
                    int adx = Mathf.RoundToInt(wxd), adz = Mathf.RoundToInt(wzd);
                    bool lands = false;
                    foreach (var u in tiles)
                    {
                        if (u.Poi != t.Poi) continue;
                        if (Mathf.Abs(u.X - (t.X + adx * ProcIsland.TileSize)) < 0.5f && Mathf.Abs(u.Z - (t.Z + adz * ProcIsland.TileSize)) < 0.5f) { lands = true; break; }
                    }
                    if (!lands)
                        foreach (var gate in cons)
                            if (gate.Poi == t.Poi && Mathf.RoundToInt(gate.DirX) == adx && Mathf.RoundToInt(gate.DirZ) == adz
                                && Mathf.Abs(gate.X - (t.X + adx * 12f)) < 0.6f && Mathf.Abs(gate.Z - (t.Z + adz * 12f)) < 0.6f) { lands = true; break; }
                    if (!lands)
                    {
                        spareArms++;
                        if (firstSpare == "") firstSpare = $"{t} lays an arm toward ({adx},{adz}) with nothing there";
                    }
                }
            }
            T.Check($"no JUNCTION piece lays an arm into empty ground ({spareArms} stubs{(firstSpare == "" ? "" : " -- " + firstSpare)})",
                spareArms == 0);
            GD.Print($"[island] {tiles.Count} road props placed; worst gate off-lattice {worstOffLattice:0.###} m");
            foreach (var gate in cons) GD.Print($"[island]   GATE poi#{gate.Poi} at ({gate.X:0},{gate.Z:0}) normal ({gate.DirX:0},{gate.DirZ:0}) {gate.Kind}");
            // Two gates that snap to the SAME point both "find a Cap" -- the same one -- so the coverage check
            // passes while one of them has quietly ceased to exist as a distinct connection.
            int dupGates = 0;
            for (int i = 0; i < cons.Count; i++)
                for (int j = i + 1; j < cons.Count; j++)
                    if (cons[i].Poi == cons[j].Poi && Mathf.Abs(cons[i].X - cons[j].X) < 0.5f && Mathf.Abs(cons[i].Z - cons[j].Z) < 0.5f) dupGates++;
            T.Check($"no two gates snapped onto the same point ({dupGates} collisions)", dupGates == 0);
            foreach (var t in tiles) GD.Print($"[island]   {t}");

            // ---- BUILDINGS, set back from the streets they front.
            var builds = new System.Collections.Generic.List<ProcIsland.MonumentBuilding>();
            for (int i = 0; i < pois.Count; i++) builds.AddRange(ProcIsland.PlaceBuildings(i, pois[i], tiles, ProcIsland.Params.Default(1234)));
            T.Check($"the town got buildings ({builds.Count})", builds.Count >= 4);

            // EVERY BUILDING FRONTS A STREET AT THE SAME DISTANCE. "It got placed" is satisfied by a building
            // dropped in the middle of a block, or one facing away from the road, and both look wrong in
            // exactly the way a setback rule exists to prevent.
            float minSet = float.MaxValue, maxSet = float.MinValue; bool allFace = true;
            foreach (var bld in builds)
            {
                float nearest = float.MaxValue; float fx = 0f, fz = 0f;
                foreach (var t in tiles)
                {
                    if (t.Poi != bld.Poi) continue;
                    float dd = Mathf.Sqrt((t.X - bld.X) * (t.X - bld.X) + (t.Z - bld.Z) * (t.Z - bld.Z));
                    if (dd < nearest) { nearest = dd; fx = bld.X - t.X; fz = bld.Z - t.Z; }
                }
                minSet = Mathf.Min(minSet, nearest); maxSet = Mathf.Max(maxSet, nearest);
                // its +Y should point AWAY from that street, so the front (-Y) faces it
                float yaw = Mathf.DegToRad(bld.YawDeg);
                float ax = -Mathf.Sin(yaw), az = -Mathf.Cos(yaw);
                float len = Mathf.Sqrt(fx * fx + fz * fz);
                if (len > 0.01f && (ax * fx + az * fz) / len < 0.9f) allFace = false;
            }
            T.Check($"...every one at the same setback from its street ({minSet:0.#}..{maxSet:0.#} m)",
                Mathf.Abs(maxSet - minSet) < 0.5f);
            T.Check("...and every one turned to face the street it fronts", allFace);

            // No building may sit ON a street tile -- they go in the blocks.
            bool clearOfRoad = true;
            foreach (var bld in builds)
                foreach (var t in tiles)
                    if (t.Poi == bld.Poi && Mathf.Abs(t.X - bld.X) < 8f && Mathf.Abs(t.Z - bld.Z) < 8f) clearOfRoad = false;
            T.Check("...and none standing in the carriageway", clearOfRoad);
            foreach (var bld in builds) GD.Print($"[island]   {b}");

            // ---- ROADS. Routed over the terrain, then carved into it.
            var routes = ProcIsland.CarveRoutes(a, a.GetLength(0), a.GetLength(1), pois, links, cons, ProcIsland.Params.Default(1234));
            T.Check($"every link got a route ({routes.Count}/{links.Count})", routes.Count == links.Count);

            // THE ROUTE FOLLOWS THE GROUND. This is the check the obvious ones cannot make: a dead-straight path
            // between two gates connects them perfectly and drives through a hillside, and "it reaches the other
            // end" is equally true of both. Measured as the worst per-step gradient along each route.
            float worstGrade = 0f; string worstOn = "";
            bool routesDry = true;
            foreach (var rt in routes)
            {
                for (int i = 1; i < rt.Points.Count; i++)
                {
                    var pa = rt.Points[i - 1]; var pb = rt.Points[i];
                    int ax = Mathf.RoundToInt(pa.X / 4f), ay = Mathf.RoundToInt(pa.Y / 4f);
                    int bx = Mathf.RoundToInt(pb.X / 4f), by = Mathf.RoundToInt(pb.Y / 4f);
                    float ha = ProcIsland.ToWorld(a[ax, ay]), hb = ProcIsland.ToWorld(a[bx, by]);
                    float run = pa.DistanceTo(pb);
                    if (run > 0.01f) { float g = Mathf.Abs(hb - ha) / run; if (g > worstGrade) { worstGrade = g; worstOn = rt.Kind.ToString(); } }
                    if (hb <= 25.6f) routesDry = false;
                }
            }
            T.Check($"...and no route runs through water ({routes.Count} routes)", routesDry);
            T.Check($"...at a gradient something could actually drive (worst {worstGrade * 100f:0.#}% on a {worstOn})",
                worstGrade < 0.35f);

            // Rail is the strict one: real track tops out around 2-3%, so a railway that shrugs at a hillside is
            // the most obviously-wrong thing this could produce. Asserted separately from the fleet-wide bound.
            float worstRail = 0f; int railRoutes = 0;
            foreach (var rt in routes)
            {
                if (rt.Kind != ProcIsland.LinkKind.Rail) continue;
                railRoutes++;
                for (int i = 1; i < rt.Points.Count; i++)
                {
                    int ax = Mathf.RoundToInt(rt.Points[i - 1].X / 4f), ay = Mathf.RoundToInt(rt.Points[i - 1].Y / 4f);
                    int bx = Mathf.RoundToInt(rt.Points[i].X / 4f), by = Mathf.RoundToInt(rt.Points[i].Y / 4f);
                    float g = Mathf.Abs(ProcIsland.ToWorld(a[bx, by]) - ProcIsland.ToWorld(a[ax, ay])) / rt.Points[i - 1].DistanceTo(rt.Points[i]);
                    worstRail = Mathf.Max(worstRail, g);
                }
            }
            T.Check($"rail is graded harder than road ({railRoutes} rail routes, worst {worstRail * 100f:0.#}%)",
                railRoutes == 0 || worstRail < 0.12f);
            // DOES IT ACTUALLY BEND? A route that ignores the terrain is exactly as long as the straight line
            // between its gates, and every check above still passes on it -- "reaches the other end", "no water",
            // even the gradient one if the ground happens to be gentle. The detour ratio is the only number here
            // that separates "routed over the terrain" from "drew a line and carved it".
            float worstDetour = 1f; string detourOn = "";
            foreach (var rt in routes)
            {
                float walked = 0f;
                for (int i = 1; i < rt.Points.Count; i++) walked += rt.Points[i - 1].DistanceTo(rt.Points[i]);
                float direct = rt.Points[0].DistanceTo(rt.Points[rt.Points.Count - 1]);
                if (direct > 1f)
                {
                    float ratio = walked / direct;
                    GD.Print($"[island]   {rt.Kind}: {rt.Points.Count} pts, {walked:0} m walked vs {direct:0} m direct = {ratio:0.00}x");
                    if (ratio > worstDetour) { worstDetour = ratio; detourOn = rt.Kind.ToString(); }
                }
            }
            // PERPENDICULAR DEPARTURE, measured. "It leaves the gate" is true of a road that exits diagonally;
            // the check is the ANGLE between the first segment and the gate's edge normal, which is 0 only if
            // the road actually goes straight out of the wall.
            float worstExit = 0f;
            foreach (var rt in routes)
            {
                if (rt.Points.Count < 2) continue;
                foreach (var end in new[] { (a: rt.Points[0], b: rt.Points[1]), (a: rt.Points[rt.Points.Count - 1], b: rt.Points[rt.Points.Count - 2]) })
                {
                    var seg = (end.b - end.a).Normalized();
                    // find the gate at this end
                    foreach (var gate in cons)
                    {
                        if (Mathf.Abs(gate.X - end.a.X) > 0.5f || Mathf.Abs(gate.Z - end.a.Y) > 0.5f) continue;
                        float dot = seg.X * gate.DirX + seg.Y * gate.DirZ;
                        worstExit = Mathf.Max(worstExit, Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(dot, -1f, 1f))));
                    }
                }
            }
            // NO HARD BENDS. Measured as the turn angle between consecutive segments -- "the route is smooth"
            // is not checkable, the sharpest corner on it is. An 8-connected A* turns in 45-degree steps, so
            // anything at or near 90 means the relaxation is not reaching that part of the path.
            float worstTurn = 0f; string turnOn = "";
            foreach (var rt in routes)
            {
                for (int i = 2; i < rt.Points.Count; i++)
                {
                    var v1 = rt.Points[i - 1] - rt.Points[i - 2];
                    var v2 = rt.Points[i] - rt.Points[i - 1];
                    if (v1.Length() < 0.01f || v2.Length() < 0.01f) continue;
                    float ang = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(v1.Normalized().Dot(v2.Normalized()), -1f, 1f)));
                    if (ang > worstTurn)
                    {
                        worstTurn = ang; turnOn = rt.Kind.ToString();
                        // WHERE, not just how much. A hairpin at index 3 of 70 is the stub handing over; one in
                        // the middle is the relaxation failing to reach it. Different bugs, same number.
                        GD.Print($"[island]   turn {ang:0.#}deg at pt {i}/{rt.Points.Count} on {rt.Kind}: {rt.Points[i-2]} -> {rt.Points[i-1]} -> {rt.Points[i]}");
                    }
                }
            }
            T.Check($"no hard bends -- sharpest turn on any route is {worstTurn:0.#}deg (on a {turnOn})", worstTurn < 50f);
            GD.Print($"[island] sharpest turn {worstTurn:0.#}deg on a {turnOn}");

            // SPREAD ACROSS THE ISLAND. Clustering passes every other check here -- four monuments crammed into
            // one quadrant are still placed, still dry, still unlinked-to-each-other-correctly. The measure is
            // how much of the island's own extent the monument set actually spans.
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var q in pois) { minX = Mathf.Min(minX, q.X); maxX = Mathf.Max(maxX, q.X); minZ = Mathf.Min(minZ, q.Z); maxZ = Mathf.Max(maxZ, q.Z); }
            float lminX = float.MaxValue, lmaxX = float.MinValue, lminZ = float.MaxValue, lmaxZ = float.MinValue;
            for (int x = 0; x < a.GetLength(0); x++)
                for (int y = 0; y < a.GetLength(1); y++)
                    if (ProcIsland.ToWorld(a[x, y]) > 25.6f)
                    { lminX = Mathf.Min(lminX, x * 4f); lmaxX = Mathf.Max(lmaxX, x * 4f); lminZ = Mathf.Min(lminZ, y * 4f); lmaxZ = Mathf.Max(lmaxZ, y * 4f); }
            float spanX = (maxX - minX) / Mathf.Max(1f, lmaxX - lminX), spanZ = (maxZ - minZ) / Mathf.Max(1f, lmaxZ - lminZ);
            T.Check($"monuments span the island rather than clustering ({spanX * 100f:0}% of its width, {spanZ * 100f:0}% of its depth)",
                spanX > 0.45f && spanZ > 0.45f);
            GD.Print($"[island] monument spread: {spanX * 100f:0}% x {spanZ * 100f:0}% of the landmass extent");

            T.Check($"every route leaves its gate perpendicular to the wall (worst departure {worstExit:0.#}deg off the edge normal)",
                worstExit < 1f);
            GD.Print($"[island] worst gate departure {worstExit:0.##}deg off normal");

            T.Check($"at least one route BENDS around the terrain rather than running straight (worst detour {worstDetour:0.00}x on a {detourOn})",
                worstDetour > 1.02f);
            GD.Print($"[island] {routes.Count} routes carved, worst grade {worstGrade * 100f:0.#}% ({worstOn}), worst detour {worstDetour:0.00}x");

            // UG_ISLAND_PNG=<path>: dump a preview so the shape can be LOOKED at. Every check above is a
            // statistic, and a plausible land fraction with a closed coast still describes shapes nobody wants
            // -- a ring, a starfish, four blobs. Gated, so a normal run pays nothing.
            var png = System.Environment.GetEnvironmentVariable("UG_ISLAND_PNG");
            if (!string.IsNullOrEmpty(png))
            {
                int gw = a.GetLength(0), gh = a.GetLength(1);
                var img = Image.CreateEmpty(gw, gh, false, Image.Format.Rgb8);
                for (int x = 0; x < gw; x++)
                    for (int y = 0; y < gh; y++)
                    {
                        float w = ProcIsland.ToWorld(a[x, y]);
                        // Water in blue by DEPTH, land in green-to-white by height, with the shoreline a hard
                        // colour break -- a smooth grey ramp hides exactly the thing being inspected.
                        Color col = w <= 25.6f
                            ? new Color(0.05f, 0.18f + 0.30f * Mathf.Clamp((w + 18f) / 43.6f, 0f, 1f), 0.45f)
                            : Color.FromHsv(0.28f - 0.28f * Mathf.Clamp((w - 25.6f) / 70f, 0f, 1f),
                                            0.55f, 0.35f + 0.60f * Mathf.Clamp((w - 25.6f) / 70f, 0f, 1f));
                        img.SetPixel(x, y, col);
                    }
                // POI markers, drawn last so they sit over the terrain: white ring at the footprint, and a
                // fainter ring at the skirt where the flatten blends out -- the two radii are the thing worth
                // eyeballing, because a pad that reads as "stamped on" is a skirt problem, not a size problem.
                foreach (var poi in pois)
                {
                    float rIn = poi.HalfSize / 4f, rOut = poi.HalfSize * 1.6f / 4f;
                    var tint = poi.Kind == ProcIsland.PoiKind.Town ? new Color(1f, 1f, 1f)
                             : poi.Kind == ProcIsland.PoiKind.MilitaryBase ? new Color(1f, 0.35f, 0.35f)
                             : new Color(1f, 0.85f, 0.2f);
                    int pcx = Mathf.RoundToInt(poi.X / 4f), pcy = Mathf.RoundToInt(poi.Z / 4f);
                    void Box(float halfCells, Color c)
                    {
                        int h = Mathf.RoundToInt(halfCells);
                        for (int d = -h; d <= h; d++)
                        {
                            foreach (var (px, py) in new[] { (pcx + d, pcy - h), (pcx + d, pcy + h), (pcx - h, pcy + d), (pcx + h, pcy + d) })
                                if (px >= 0 && py >= 0 && px < gw && py < gh) img.SetPixel(px, py, c);
                        }
                    }
                    Box(rIn, tint);
                    Box(rOut, tint * 0.45f);
                }
                // The road props themselves: each tile as a filled block, caps lit brighter with a spur drawn
                // along their ramp. Colour by family so a wrong ROTATION is visible as a spur pointing the wrong
                // way -- the numbers say "every gate is on a cap", they cannot say the cap faces the road.
                foreach (var t in tiles)
                {
                    bool cap = t.Piece is ProcIsland.RoadPiece.LineCap or ProcIsland.RoadPiece.TeeCap or ProcIsland.RoadPiece.QuadCap;
                    var tc = cap ? new Color(1f, 0.95f, 0.55f) : new Color(0.62f, 0.62f, 0.66f);
                    int tcx = Mathf.RoundToInt(t.X / 4f), tcy = Mathf.RoundToInt(t.Z / 4f);
                    int half = Mathf.RoundToInt(ProcIsland.TileSize * 0.5f / 4f) - 1;   // 24m tile -> 6 cells, inset
                    for (int ox = -half; ox <= half; ox++)
                        for (int oy = -half; oy <= half; oy++)
                        {
                            int px = tcx + ox, py = tcy + oy;
                            if (px >= 0 && py >= 0 && px < gw && py < gh) img.SetPixel(px, py, tc);
                        }
                    if (cap)
                    {
                        float yaw = Mathf.DegToRad(t.YawDeg);
                        float rx = -Mathf.Sin(yaw), rz = -Mathf.Cos(yaw);
                        for (int k = 0; k <= half + 2; k++)
                        {
                            int px = tcx + Mathf.RoundToInt(rx * k), py = tcy + Mathf.RoundToInt(rz * k);
                            if (px >= 0 && py >= 0 && px < gw && py < gh) img.SetPixel(px, py, new Color(1f, 0.35f, 0f));
                        }
                    }
                }
                foreach (var gate in cons)
                {
                    int gx = Mathf.RoundToInt(gate.X / 4f), gy = Mathf.RoundToInt(gate.Z / 4f);
                    for (int ox = -1; ox <= 1; ox++)
                        for (int oy = -1; oy <= 1; oy++)
                        {
                            int px = gx + ox, py = gy + oy;
                            if (px >= 0 && py >= 0 && px < gw && py < gh) img.SetPixel(px, py, new Color(1f, 0.1f, 0.9f));
                        }
                }
                // Links drawn UNDER the monument boxes: road white-ish, trail brown, rail grey with sleepers,
                // so the type is readable at a glance rather than needing the log alongside the picture.
                foreach (var rt in routes)
                {
                    var lc2 = rt.Kind == ProcIsland.LinkKind.Road ? new Color(0.95f, 0.95f, 0.9f)
                            : rt.Kind == ProcIsland.LinkKind.Trail ? new Color(1f, 0.55f, 0.05f)
                            : new Color(0.15f, 0.15f, 0.18f);
                    foreach (var pt in rt.Points)
                    {
                        int px = Mathf.RoundToInt(pt.X / 4f), py = Mathf.RoundToInt(pt.Y / 4f);
                        if (px >= 0 && py >= 0 && px < gw && py < gh) img.SetPixel(px, py, lc2);
                    }
                }
                foreach (var l in links)
                {
                    if (true) continue;   // straight-line debug draw, superseded by the routed path above
                    var pa = pois[l.A]; var pb = pois[l.B];
                    var lc = l.Kind == ProcIsland.LinkKind.Road ? new Color(0.92f, 0.92f, 0.88f)
                           : l.Kind == ProcIsland.LinkKind.Trail ? new Color(1f, 0.55f, 0.05f)   // brown-on-olive
                                 // was unreadable against the terrain -- a preview I cannot read is not an
                                 // instrument, and I could not tell whether the trails had been drawn at all.
                           : new Color(0.20f, 0.20f, 0.24f);
                    int steps = Mathf.CeilToInt(l.Length / 4f) * 2;
                    for (int t = 0; t <= steps; t++)
                    {
                        float f = t / (float)steps;
                        if (l.Kind == ProcIsland.LinkKind.Rail && (t / 3) % 2 == 0) continue;   // sleeper dashes
                        int px = Mathf.RoundToInt(Mathf.Lerp(pa.X, pb.X, f) / 4f);
                        int py = Mathf.RoundToInt(Mathf.Lerp(pa.Z, pb.Z, f) / 4f);
                        if (px >= 0 && py >= 0 && px < gw && py < gh) img.SetPixel(px, py, lc);
                    }
                }
                img.SavePng(png);

                // A ZOOMED monument, because the island view is 4 m per pixel and the thing being checked here
                // is a 24 m prop's ROTATION. At that scale a cap facing the wrong way is two pixels.
                {
                    var hub = pois[0];
                    int span = ProcIsland.TilesFor(hub.Kind) * 24 + 48;    // footprint plus a margin
                    const int Px = 6;                                       // pixels per metre-ish
                    int side = span * Px / 4;
                    var zi = Image.CreateEmpty(side, side, false, Image.Format.Rgb8);
                    float ox0 = hub.X - span * 0.5f, oz0 = hub.Z - span * 0.5f;
                    for (int px = 0; px < side; px++)
                        for (int py = 0; py < side; py++)
                        {
                            float wx = ox0 + px * 4f / Px, wz = oz0 + py * 4f / Px;
                            int gx2 = Mathf.Clamp(Mathf.RoundToInt(wx / 4f), 0, gw - 1), gy2 = Mathf.Clamp(Mathf.RoundToInt(wz / 4f), 0, gh - 1);
                            float h = ProcIsland.ToWorld(a[gx2, gy2]);
                            zi.SetPixel(px, py, h <= 25.6f ? new Color(0.05f, 0.25f, 0.45f)
                                                           : new Color(0.30f + 0.004f * (h - 25.6f), 0.38f + 0.003f * (h - 25.6f), 0.24f));
                        }
                    void ZLine(float x1, float z1, float x2, float z2, Color c)
                    {
                        int n2 = 260;
                        for (int k = 0; k <= n2; k++)
                        {
                            float f = k / (float)n2;
                            int px = Mathf.RoundToInt((Mathf.Lerp(x1, x2, f) - ox0) * Px / 4f);
                            int py = Mathf.RoundToInt((Mathf.Lerp(z1, z2, f) - oz0) * Px / 4f);
                            if (px >= 0 && py >= 0 && px < side && py < side) zi.SetPixel(px, py, c);
                        }
                    }
                    // footprint
                    ZLine(hub.X - hub.HalfSize, hub.Z - hub.HalfSize, hub.X + hub.HalfSize, hub.Z - hub.HalfSize, new Color(1f, 1f, 1f));
                    ZLine(hub.X + hub.HalfSize, hub.Z - hub.HalfSize, hub.X + hub.HalfSize, hub.Z + hub.HalfSize, new Color(1f, 1f, 1f));
                    ZLine(hub.X + hub.HalfSize, hub.Z + hub.HalfSize, hub.X - hub.HalfSize, hub.Z + hub.HalfSize, new Color(1f, 1f, 1f));
                    ZLine(hub.X - hub.HalfSize, hub.Z + hub.HalfSize, hub.X - hub.HalfSize, hub.Z - hub.HalfSize, new Color(1f, 1f, 1f));
                    // DRAW THE CARRIAGEWAY, not the debug axes. The first version of this drew each prop's
                    // local +X/+Y as little lines, which is unreadable as a road -- strawberry, immediately:
                    // "those lines go in random directions? theres no path". Correct: they were axis markers,
                    // not surface. Each piece lays an ARM from its centre out to every connector it has, so
                    // drawing those arms at carriageway width is drawing the road itself, and a connected path
                    // then either appears or does not.
                    void ZFill(float cx0, float cz0, float cx1, float cz1, float halfW, Color c)
                    {
                        float lo_x = Mathf.Min(cx0, cx1) - halfW, hi_x = Mathf.Max(cx0, cx1) + halfW;
                        float lo_z = Mathf.Min(cz0, cz1) - halfW, hi_z = Mathf.Max(cz0, cz1) + halfW;
                        for (float wx = lo_x; wx <= hi_x; wx += 4f / Px)
                            for (float wz = lo_z; wz <= hi_z; wz += 4f / Px)
                            {
                                int qx = Mathf.RoundToInt((wx - ox0) * Px / 4f), qy = Mathf.RoundToInt((wz - oz0) * Px / 4f);
                                if (qx >= 0 && qy >= 0 && qx < side && qy < side) zi.SetPixel(qx, qy, c);
                            }
                    }
                    const float RoadHalfW = 8f;    // 16 m carriageway inside the 24 m tile
                    foreach (var t in tiles)
                    {
                        if (t.Poi != 0) continue;
                        bool cap = t.Piece is ProcIsland.RoadPiece.LineCap or ProcIsland.RoadPiece.TeeCap or ProcIsland.RoadPiece.QuadCap;
                        var surf = new Color(0.30f, 0.30f, 0.33f);
                        float yaw = Mathf.DegToRad(t.YawDeg);
                        // the piece's own connector directions, in LOCAL axes, then rotated
                        var local = t.Piece switch
                        {
                            ProcIsland.RoadPiece.Line or ProcIsland.RoadPiece.LineCap => new[] { (0, 1), (0, -1) },
                            ProcIsland.RoadPiece.Turn                                  => new[] { (1, 0), (0, -1) },
                            ProcIsland.RoadPiece.Tee or ProcIsland.RoadPiece.TeeCap    => new[] { (1, 0), (-1, 0), (0, 1) },
                            _                                                          => new[] { (1, 0), (-1, 0), (0, 1), (0, -1) },
                        };
                        foreach (var (lx, ly) in local)
                        {
                            // mesh +X -> (cos, -sin); mesh +Y -> (-sin, -cos)
                            float wxd = lx * Mathf.Cos(yaw) + ly * -Mathf.Sin(yaw);
                            float wzd = lx * -Mathf.Sin(yaw) + ly * -Mathf.Cos(yaw);
                            ZFill(t.X, t.Z, t.X + wxd * ProcIsland.TileSize * 0.5f, t.Z + wzd * ProcIsland.TileSize * 0.5f, RoadHalfW, surf);
                        }
                        // the ramp end of a cap, brighter, so its direction is unmistakable
                        if (cap)
                        {
                            float rx = -Mathf.Sin(yaw), rz = -Mathf.Cos(yaw);
                            ZFill(t.X + rx * 8f, t.Z + rz * 8f, t.X + rx * ProcIsland.TileSize * 0.5f, t.Z + rz * ProcIsland.TileSize * 0.5f, RoadHalfW, new Color(0.95f, 0.72f, 0.15f));
                        }
                    }
                    // Buildings: footprint box turned to face the street, with a short bar on the FRONT edge so
                    // a building facing the wrong way is visible rather than merely wrong in a number.
                    foreach (var bb in builds)
                    {
                        if (bb.Poi != 0) continue;
                        float byaw = Mathf.DegToRad(bb.YawDeg);
                        float ux = Mathf.Cos(byaw), uz = -Mathf.Sin(byaw);      // mesh +X
                        float vx = -Mathf.Sin(byaw), vz = -Mathf.Cos(byaw);     // mesh +Y (back)
                        float hw = 9f, hd = 10f;
                        Vector2 C(float a2, float b2) => new(bb.X + ux * a2 + vx * b2, bb.Z + uz * a2 + vz * b2);
                        var c1 = C(-hw, -hd); var c2 = C(hw, -hd); var c3 = C(hw, hd); var c4 = C(-hw, hd);
                        var bc = new Color(0.78f, 0.70f, 0.55f);
                        ZLine(c1.X, c1.Y, c2.X, c2.Y, new Color(0.95f, 0.45f, 0.25f));   // FRONT edge (-Y)
                        ZLine(c2.X, c2.Y, c3.X, c3.Y, bc);
                        ZLine(c3.X, c3.Y, c4.X, c4.Y, bc);
                        ZLine(c4.X, c4.Y, c1.X, c1.Y, bc);
                    }
                    foreach (var gate in cons)
                    {
                        if (gate.Poi != 0) continue;
                        int px = Mathf.RoundToInt((gate.X - ox0) * Px / 4f), py = Mathf.RoundToInt((gate.Z - oz0) * Px / 4f);
                        for (int oxx = -2; oxx <= 2; oxx++)
                            for (int oyy = -2; oyy <= 2; oyy++)
                            {
                                int qx = px + oxx, qy = py + oyy;
                                if (qx >= 0 && qy >= 0 && qx < side && qy < side) zi.SetPixel(qx, qy, new Color(1f, 0.1f, 0.9f));
                            }
                    }
                    zi.SavePng(png.Replace(".png", "_zoom.png"));
                    GD.Print($"[island] zoom -> {png.Replace(".png", "_zoom.png")} ({side}x{side})");
                }
                GD.Print($"[island] preview -> {png}  ({gw}x{gh})");
            }

            yield break;
        }
    }
}
