using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>
    /// BASEMENT CUTS: the ground BENT to a building's basement and dug out inside it (strawberry, 2026-10-15: "maybe just
    /// add support for 'bending' terrain, moving verts of surrounding terrain to fit").
    ///
    /// A cut is the outline of what a basement takes out of the ground -- its footprint to the walls' outer faces, and its
    /// hatch's well (BuildingGenResult.Dig, its rectangles turned and moved with the building) -- as convex quads in world
    /// x/z. Every cut on the map is unioned into one outline, so a well beside its footprint, or two buildings close
    /// together, make ONE boundary and are bent once. The terrain grid is 4 m quads, and the walls do not stand on its
    /// lines, so grid vertices are MOVED onto the outline: one onto each corner (the nearest), then, wherever the outline
    /// crosses a grid edge, that edge's nearer end slides along it onto the crossing -- so the moved vertices run along the
    /// outline in order, each pair of neighbours two corners of one quad. Where two neighbours are a quad's opposite corners
    /// the outline runs along its diagonal, and that quad is split along it (a turned building's walls cross the grid at a
    /// slant); then each of a quad's two triangles is on one side of the outline, and those inside it are dug -- a hole flag
    /// per triangle, beside the authored per-quad holes. The ground round it just stretches. A moved vertex takes the height
    /// the ground had at its new spot (bilinear off the grid), so the ground looks as it did, but for its edge now lying on
    /// the wall.
    ///
    /// Conflicts, decided the same way every time: a vertex wanted by a corner and an edge goes to the corner (the corner
    /// is on that edge); by one edge twice (it crosses both grid edges at that vertex), to the foot of the vertex on that
    /// edge; by two corners or two edges, to the nearer (then the lower target, x then y). The loser is left where it is,
    /// and the outline is not followed there. No quad may fold: one the moves turned inside out is split the other way
    /// if that mends it, else gives a moved corner back (an edge's before a corner's, the farther move first).
    ///
    /// The 4 m grid cannot follow every outline -- two buildings 1.5 m apart, a well in another's notch -- so the bend
    /// is only the first pass, and it need not be perfect: every triangle the outline still runs through is CLIPPED to
    /// it, exactly, the part outside kept as convex pieces with vertices of their own (each on the triangle's plane, so
    /// the ground does not move) and the part inside dropped. So the ground stops at the outline everywhere -- no hole
    /// beside a wall, no ground over a well, none left inside -- and the bend just keeps those pieces few.
    ///
    /// The offsets are a per-vertex table the chunk mesh and its trimesh collider both read (RebuildChunk,
    /// ApplyChunkTrimesh), as are the per-quad split, dug and clipped flags and the pieces; a chunk with a bent vertex or
    /// a cut takes the trimesh collider, as one with a hole always did. Nothing here touches the rivers. Re-derived from
    /// the cuts whenever one is added or removed, so removing a building's cut puts the ground back exactly.
    /// </summary>
    public partial class Terrain
    {
        readonly Dictionary<int, List<Vector2[]>> _cuts = new();
        /// <summary>Per cut, how many of its quads are covered (the building stands over them); those after are OPEN to the
        /// sky -- a hatch's well.</summary>
        readonly Dictionary<int, int> _cutCovered = new();
        /// <summary>Each grid vertex's move, in GRID units (x along gx, y along gy); null while nothing is bent.</summary>
        Vector2[,] _bend;
        /// <summary>Per quad, what the cuts made of it: CutTriA (the triangle at its (gx, gy+1) corner) and CutTriB (the one at
        /// (gx+1, gy)) dug, CutSplit11 split 00-11 rather than 01-10, CutClipA / CutClipB that triangle clipped to the
        /// outline (its pieces in _cutPieces). 0 for untouched; null while nothing is cut.</summary>
        byte[,] _cutTri;
        public const byte CutTriA = 1, CutTriB = 2, CutSplit11 = 4, CutClipA = 8, CutClipB = 16;
        /// <summary>Per quad with a clipped triangle, what is left of it: convex pieces in grid units, wound as the mesh is.</summary>
        Dictionary<(int, int), (Vector2[][] A, Vector2[][] B)> _cutPieces;

        /// <summary>What the last derivation did, in words: the corners and edge vertices moved, the conflicts, the triangles dug.</summary>
        public string CutReport { get; private set; } = "";
        public IEnumerable<int> BasementCutIds => _cuts.Keys;

        /// <summary>Cut a basement (id: the building's; a second cut under the same id replaces the first) out of the ground:
        /// `worldQuads`, each four world x/z corners of a convex quad -- the first `covered` of them under the building, the
        /// rest open to the sky (a hatch's well: BuildingGenResult.HatchWell, last in Dig). Bends, digs and rebuilds the chunks it
        /// changes. Returns how many triangles all the cuts now dig.</summary>
        public int AddBasementCut(int id, IReadOnlyList<Vector2[]> worldQuads, int covered = int.MaxValue)
        {
            var copy = new List<Vector2[]>();
            int kept = 0, cov = 0;
            foreach (var q in worldQuads)
            {
                if (q != null && q.Length >= 3) { copy.Add((Vector2[])q.Clone()); if (kept < covered) cov++; }
                kept++;
            }
            _cuts[id] = copy; _cutCovered[id] = cov;
            return RederiveCuts();
        }

        /// <summary>Take a basement's cut away, and with it every bend and dug triangle it made. False for no such cut.</summary>
        public bool RemoveBasementCut(int id)
        {
            if (!_cuts.Remove(id)) return false;
            _cutCovered.Remove(id);
            RederiveCuts();
            return true;
        }

        /// <summary>The cuts as they are, by id (a save writes these: the cut is stored as its outline, never as offsets).</summary>
        public IReadOnlyDictionary<int, List<Vector2[]>> BasementCuts => _cuts;

        byte CutTri(int gx, int gy) => _cutTri == null || gx < 0 || gy < 0 || gx >= _gw - 1 || gy >= _gh - 1 ? (byte)0 : _cutTri[gx, gy];
        /// <summary>Both of a quad's triangles dug by a cut: the quad is a hole, as an authored one is.</summary>
        bool IsCutHole(int gx, int gy) => (CutTri(gx, gy) & (CutTriA | CutTriB)) == (CutTriA | CutTriB);

        /// <summary>A vertex's place in grid units, bent or not.</summary>
        Vector2 GridVertex(int gx, int gy) => _bend == null ? new Vector2(gx, gy) : new Vector2(gx, gy) + _bend[gx, gy];

        /// <summary>The ground's height in world Y at a point in grid units: bilinear across the four grid samples round it.</summary>
        float GridHeightAt(float fx, float fy)
        {
            int xi = Mathf.FloorToInt(fx), yi = Mathf.FloorToInt(fy);
            float tx = fx - xi, ty = fy - yi;
            int x0 = Mathf.Clamp(xi, 0, _gw - 1), x1 = Mathf.Clamp(xi + 1, 0, _gw - 1);
            int y0 = Mathf.Clamp(yi, 0, _gh - 1), y1 = Mathf.Clamp(yi + 1, 0, _gh - 1);
            float h = Mathf.Lerp(Mathf.Lerp(_grid[x0, y0], _grid[x1, y0], tx), Mathf.Lerp(_grid[x0, y1], _grid[x1, y1], tx), ty);
            return h * TILE_HEIGHT - TILE_HEIGHT / 2f;
        }

        /// <summary>A grid vertex where the mesh and the collider put it: moved by the cuts, its height the ground's there.</summary>
        Vector3 CutVertex(int gx, int gy)
        {
            var g = GridVertex(gx, gy);
            float y = (_bend == null || _bend[gx, gy] == Vector2.Zero ? _grid[gx, gy] * TILE_HEIGHT - TILE_HEIGHT / 2f : GridHeightAt(g.X, g.Y)) - RiverCutAt(gx, gy);
            return new Vector3(_bx + g.X * UNIT, y, -(_bz + g.Y * UNIT));
        }

        /// <summary>A cut quad's ground, triangle by triangle, as three of its corners (0 = 00, 1 = 10, 2 = 01, 3 = 11) in the
        /// winding the plain quad uses (00-01-10, 10-01-11) -- split the other way where the cut says, a dug triangle left out
        /// -- with null for the whole triangle, or once per piece of a clipped one (grid units, the same winding).</summary>
        void CutQuadParts(int gx, int gy, System.Action<int, int, int, Vector2[]> emit)
        {
            byte f = CutTri(gx, gy);
            bool s11 = (f & CutSplit11) != 0;
            (int, int, int) ta = s11 ? (0, 2, 3) : (0, 2, 1), tb = s11 ? (0, 3, 1) : (1, 2, 3);   // the triangles at the 01 and 10 corners
            (Vector2[][] A, Vector2[][] B) pc = default;
            if ((f & (CutClipA | CutClipB)) != 0) _cutPieces?.TryGetValue((gx, gy), out pc);
            void One((int, int, int) t, byte dug, byte clip, Vector2[][] pieces)
            {
                if ((f & dug) != 0) return;
                if ((f & clip) == 0) { emit(t.Item1, t.Item2, t.Item3, null); return; }
                if (pieces != null) foreach (var p in pieces) emit(t.Item1, t.Item2, t.Item3, p);
            }
            One(ta, CutTriA, CutClipA, pc.A);
            One(tb, CutTriB, CutClipB, pc.B);
        }

        /// <summary>A point's weights on a triangle's three corners (grid units): what a piece's vertex takes of each.</summary>
        static Vector3 Bary(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d = (b - a).Cross(c - a);
            float wb = (p - a).Cross(c - a) / d, wc = (b - a).Cross(p - a) / d;
            return new Vector3(1f - wb - wc, wb, wc);
        }

        /// <summary>The render mesh's triangles for a cut quad (RebuildChunk): a whole triangle by its corners' indices, a
        /// clipped one's pieces as vertices of their own, everything (position, normal, splat UV, colour) weighted off the
        /// triangle's corners -- so a piece lies in its triangle's plane and shades as it would have.</summary>
        void EmitCutQuad(int gx, int gy, int i00, int i10, int i01, int i11, List<Vector3> v, List<Vector3> n, List<Vector2> u, List<Color> c, List<int> idx)
        {
            int[] ci = { i00, i10, i01, i11 };
            Vector2[] g = { GridVertex(gx, gy), GridVertex(gx + 1, gy), GridVertex(gx, gy + 1), GridVertex(gx + 1, gy + 1) };
            CutQuadParts(gx, gy, (a, b, cc, piece) =>
            {
                int ia = ci[a], ib = ci[b], ic = ci[cc];
                if (piece == null) { idx.Add(ia); idx.Add(ib); idx.Add(ic); return; }
                int b0 = v.Count;
                foreach (var p in piece)
                {
                    var w = Bary(p, g[a], g[b], g[cc]);
                    v.Add(v[ia] * w.X + v[ib] * w.Y + v[ic] * w.Z);
                    n.Add((n[ia] * w.X + n[ib] * w.Y + n[ic] * w.Z).Normalized());
                    u.Add(u[ia] * w.X + u[ib] * w.Y + u[ic] * w.Z);
                    c.Add(c[ia] * w.X + c[ib] * w.Y + c[ic] * w.Z);
                }
                for (int k = 1; k + 1 < piece.Length; k++) { idx.Add(b0); idx.Add(b0 + k); idx.Add(b0 + k + 1); }
            });
        }

        /// <summary>The trimesh collider's triangles for a cut quad (ApplyChunkTrimesh), the same as EmitCutQuad's: the
        /// ground you stand on is the ground you see.</summary>
        void EmitCutQuadCollider(int gx, int gy, Vector3 v00, Vector3 v10, Vector3 v01, Vector3 v11, List<Vector3> tris)
        {
            Vector3[] cv = { v00, v10, v01, v11 };
            Vector2[] g = { GridVertex(gx, gy), GridVertex(gx + 1, gy), GridVertex(gx, gy + 1), GridVertex(gx + 1, gy + 1) };
            CutQuadParts(gx, gy, (a, b, cc, piece) =>
            {
                if (piece == null) { tris.Add(cv[a]); tris.Add(cv[b]); tris.Add(cv[cc]); return; }
                var pts = new Vector3[piece.Length];
                for (int k = 0; k < piece.Length; k++) { var w = Bary(piece[k], g[a], g[b], g[cc]); pts[k] = cv[a] * w.X + cv[b] * w.Y + cv[cc] * w.Z; }
                for (int k = 1; k + 1 < pts.Length; k++) { tris.Add(pts[0]); tris.Add(pts[k]); tris.Add(pts[k + 1]); }
            });
        }

        /// <summary>Does this chunk carry a bent vertex or a cut quad? Its collider is then the trimesh: a heightfield is one
        /// height per column on a fixed lattice.</summary>
        bool ChunkHasCut(int cxi, int cyi)
        {
            if (_bend == null && _cutTri == null) return false;
            int x0 = cxi * CHUNK, y0 = cyi * CHUNK;
            int x1 = System.Math.Min(x0 + CHUNK, _gw - 1), y1 = System.Math.Min(y0 + CHUNK, _gh - 1);
            for (int gx = x0; gx <= x1; gx++)
                for (int gy = y0; gy <= y1; gy++)
                {
                    if (_bend != null && _bend[gx, gy] != Vector2.Zero) return true;
                    if (gx < x1 && gy < y1 && CutTri(gx, gy) != 0) return true;
                }
            return false;
        }

        // ---- the derivation -----------------------------------------------------------------------------------------

        const float CutEps = 1e-4f;
        /// <summary>A triangle smaller than this (grid units squared: 1.6 cm2) is flat: a quad with one under both splits folds.</summary>
        const float MinTri = 1e-5f;
        /// <summary>How much of a triangle (grid units squared: 16 cm2) may be on the wrong side of the outline before it is
        /// clipped rather than dug or kept whole.</summary>
        const float ClipEps = 1e-4f;
        /// <summary>A clipped piece smaller than this (grid units squared: 1.6 cm2) is a crumb on an edge, and dropped.</summary>
        const float PieceEps = 1e-5f;
        /// <summary>Within this (grid units: 0.4 mm, against a float's grain of 0.24 mm 4 km from the map's corner) two
        /// points are one, and a point is on a line.</summary>
        const float ClipTol = 1e-4f;
        /// <summary>How far a corner may move a vertex that is not its nearest (grid units): its nearest is a corner's already.</summary>
        const float SecondMove = 0.75f;

        /// <summary>Bend, split, dig and clip for every cut, from scratch, and rebuild (mesh and collider) every chunk whose
        /// vertices or quads changed. Returns the triangles dug.</summary>
        int RederiveCuts()
        {
            if (_grid == null) return 0;
            var oldBend = _bend; var oldTri = _cutTri; var oldPieces = _cutPieces;
            // every cut's quads in grid units (world z is negated, as everywhere in this class)
            var polys = new List<Vector2[]>(); var open = new List<Vector2[]>();
            foreach (var kv in _cuts)
            {
                int cov = _cutCovered.TryGetValue(kv.Key, out var c0) ? c0 : int.MaxValue;
                for (int qi = 0; qi < kv.Value.Count; qi++)
                {
                    var q = kv.Value[qi];
                    var p = new Vector2[q.Length];
                    for (int i = 0; i < q.Length; i++) p[i] = new Vector2((q[i].X - _bx) / UNIT, (-q[i].Y - _bz) / UNIT);
                    polys.Add(p);
                    if (qi >= cov) open.Add(p);
                }
            }
            Vector2[,] bend = null; byte[,] tri = null; Dictionary<(int, int), (Vector2[][] A, Vector2[][] B)> pieces = null;
            int dug = 0, corners = 0, edges = 0, lost = 0, clipped = 0, splits = 0;
            if (polys.Count > 0)
            {
                bool Inside(Vector2 p) { foreach (var q in polys) if (InConvex(q, p)) return true; return false; }
                // (a corner of an open quad -- a well's -- before one under the building: ground left over a well is in the
                // way and in sight, ground left under the building is neither)
                bool OpenCorner(Vector2 c)
                {
                    foreach (var q in open)
                        for (int i = 0; i < q.Length; i++)
                        {
                            Vector2 a = q[i], b = q[(i + 1) % q.Length], d = b - a;
                            float t = Mathf.Clamp((c - a).Dot(d) / System.Math.Max(d.LengthSquared(), 1e-9f), 0f, 1f);
                            if ((a + d * t - c).LengthSquared() < 1e-6f) return true;
                        }
                    return false;
                }
                var segs = Boundary(polys, Inside);
                var outline = Corners(segs);
                // the claims: each vertex its target, the outline pieces that target lies on, whether a corner wants it
                var claims = new Dictionary<(int, int), (Vector2 To, bool Corner, float D, HashSet<int> On)>();
                var lostWhat = new List<string>();
                void Claim(int gx, int gy, Vector2 to, bool corner, HashSet<int> on)
                {
                    if (gx < 0 || gy < 0 || gx >= _gw || gy >= _gh) return;
                    var k = (gx, gy); var v = new Vector2(gx, gy);
                    float d = (to - v).Length();
                    if (!claims.TryGetValue(k, out var had)) { claims[k] = (to, corner, d, on); return; }
                    if ((had.To - to).LengthSquared() < CutEps * CutEps) { had.On.UnionWith(on); claims[k] = (had.To, had.Corner || corner, had.D, had.On); return; }
                    // a corner over an edge's claim: no loss, the corner is on that edge
                    if (corner != had.Corner) { if (corner) { on.UnionWith(had.On); claims[k] = (to, true, d, on); } else had.On.UnionWith(on); return; }
                    // one piece of the outline twice (it crosses both grid edges at this vertex): the vertex's foot on it
                    if (!corner && on.Count == 1 && had.On.SetEquals(on))
                    {
                        foreach (int s in on) { var (a, b) = segs[s]; var foot = a + (b - a) * Mathf.Clamp((v - a).Dot(b - a) / (b - a).LengthSquared(), 0f, 1f); claims[k] = (foot, false, (foot - v).Length(), had.On); }
                        return;
                    }
                    bool openTo = corner && OpenCorner(to), openHad = corner && OpenCorner(had.To);
                    bool better = openTo != openHad ? openTo
                                : d < had.D - CutEps || (System.Math.Abs(d - had.D) <= CutEps && (to.X < had.To.X - CutEps || (System.Math.Abs(to.X - had.To.X) <= CutEps && to.Y < had.To.Y)));
                    lost++;
                    lostWhat.Add($"({gx},{gy}) {(better ? had.To : to)} lost to {(better ? to : had.To)}{(corner ? " (corners)" : " (edges)")}");
                    if (better) claims[k] = (to, corner, d, on);
                }
                // corners first: the well's, then the nearest to a vertex, each to the nearest vertex of its grid cell no
                // corner has yet -- its nearest, else the next, so long as the move is under SecondMove (a hatch well's end is
                // 3.2 m, and both its corners often round to one vertex). A corner with none is lost.
                var cornerOrder = new List<(Vector2 C, bool Open, float D)>();
                foreach (var c in outline) cornerOrder.Add((c, OpenCorner(c), (c - new Vector2(Mathf.Round(c.X), Mathf.Round(c.Y))).Length()));
                cornerOrder.Sort((p, q) => p.Open != q.Open ? (p.Open ? -1 : 1) : p.D != q.D ? p.D.CompareTo(q.D) : p.C.X != q.C.X ? p.C.X.CompareTo(q.C.X) : p.C.Y.CompareTo(q.C.Y));
                var second = new HashSet<(int, int)>();
                foreach (var (c, _, _) in cornerOrder)
                {
                    var on = new HashSet<int>();
                    for (int s = 0; s < segs.Count; s++) if ((segs[s].A - c).LengthSquared() < 1e-6f || (segs[s].B - c).LengthSquared() < 1e-6f) on.Add(s);
                    int fx = Mathf.FloorToInt(c.X), fy = Mathf.FloorToInt(c.Y);
                    var cell = new List<(int X, int Y, float D)>();
                    foreach (var (vx, vy) in new[] { (fx, fy), (fx + 1, fy), (fx, fy + 1), (fx + 1, fy + 1) }) cell.Add((vx, vy, (c - new Vector2(vx, vy)).Length()));
                    cell.Sort((p, q) => p.D != q.D ? p.D.CompareTo(q.D) : p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
                    bool placed = false;
                    for (int i = 0; i < cell.Count && !placed; i++)
                    {
                        var (vx, vy, d) = cell[i];
                        if (claims.TryGetValue((vx, vy), out var had) && had.Corner) continue;
                        if (i > 0 && d > SecondMove) break;
                        Claim(vx, vy, c, true, on);
                        if (i > 0) second.Add((vx, vy));
                        placed = true;
                    }
                    if (!placed) { lost++; lostWhat.Add($"corner {c} (its cell's vertices all taken by corners nearer them)"); }
                    corners++;
                }
                // then every grid edge the outline crosses: its nearer end, slid along it onto the crossing
                for (int s = 0; s < segs.Count; s++)
                {
                    var (a, b) = segs[s]; var d = b - a;
                    if (System.Math.Abs(d.X) > CutEps)
                        for (int k = Mathf.CeilToInt(System.Math.Min(a.X, b.X) - CutEps); k <= Mathf.FloorToInt(System.Math.Max(a.X, b.X) + CutEps); k++)
                        {
                            float y = a.Y + (k - a.X) * d.Y / d.X;
                            Claim(k, Mathf.RoundToInt(y), new Vector2(k, y), false, new HashSet<int> { s }); edges++;
                        }
                    if (System.Math.Abs(d.Y) > CutEps)
                        for (int k = Mathf.CeilToInt(System.Math.Min(a.Y, b.Y) - CutEps); k <= Mathf.FloorToInt(System.Math.Max(a.Y, b.Y) + CutEps); k++)
                        {
                            float x = a.X + (k - a.Y) * d.X / d.Y;
                            Claim(Mathf.RoundToInt(x), k, new Vector2(x, k), false, new HashSet<int> { s }); edges++;
                        }
                }
                bend = new Vector2[_gw, _gh];
                foreach (var kv in claims) bend[kv.Key.Item1, kv.Key.Item2] = kv.Value.To - new Vector2(kv.Key.Item1, kv.Key.Item2);
                // every quad within the outline's reach
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var q in polys) foreach (var p in q) { minX = System.Math.Min(minX, p.X); minY = System.Math.Min(minY, p.Y); maxX = System.Math.Max(maxX, p.X); maxY = System.Math.Max(maxY, p.Y); }
                int qx0 = System.Math.Max(0, Mathf.FloorToInt(minX) - 2), qx1 = System.Math.Min(_gw - 2, Mathf.CeilToInt(maxX) + 1);
                int qy0 = System.Math.Max(0, Mathf.FloorToInt(minY) - 2), qy1 = System.Math.Min(_gh - 2, Mathf.CeilToInt(maxY) + 1);
                Vector2 B(int gx, int gy) => new Vector2(gx, gy) + bend[gx, gy];
                // (a quad's two splits: each fine if both its triangles are wound as the mesh is, and not flat)
                bool PlainOk(int gx, int gy) => Tri(B(gx, gy), B(gx, gy + 1), B(gx + 1, gy)) < -MinTri && Tri(B(gx + 1, gy), B(gx, gy + 1), B(gx + 1, gy + 1)) < -MinTri;
                bool OtherOk(int gx, int gy) => Tri(B(gx, gy), B(gx, gy + 1), B(gx + 1, gy + 1)) < -MinTri && Tri(B(gx, gy), B(gx + 1, gy + 1), B(gx + 1, gy)) < -MinTri;
                // no quad may fold: one inside out (or flat) under both splits gives a moved corner back -- an edge's before a
                // corner's, a corner's second choice before its first, the farther move first -- and its neighbours are looked
                // at again, until none folds (it ends: a vertex given back is never moved again, and an unmoved quad is fine)
                int Rank((int, int) k) => !claims[k].Corner ? 0 : second.Contains(k) ? 1 : 2;
                var work = new Queue<(int, int)>();
                for (int gx = qx0; gx <= qx1; gx++) for (int gy = qy0; gy <= qy1; gy++) work.Enqueue((gx, gy));
                while (work.Count > 0)
                {
                    var (qx, qy) = work.Dequeue();
                    if (PlainOk(qx, qy) || OtherOk(qx, qy)) continue;
                    (int, int) give = (-1, -1);
                    foreach (var k in new[] { (qx, qy), (qx + 1, qy), (qx, qy + 1), (qx + 1, qy + 1) })
                    {
                        if (!claims.ContainsKey(k)) continue;
                        if (give.Item1 < 0 || Rank(k) < Rank(give) || (Rank(k) == Rank(give) && bend[k.Item1, k.Item2].Length() > bend[give.Item1, give.Item2].Length())) give = k;
                    }
                    if (give.Item1 < 0) continue;
                    lostWhat.Add($"({give.Item1},{give.Item2}) {claims[give].To} folded quad ({qx},{qy}): given back");
                    lost++; claims.Remove(give); bend[give.Item1, give.Item2] = Vector2.Zero;
                    for (int ax = give.Item1 - 1; ax <= give.Item1; ax++)
                        for (int ay = give.Item2 - 1; ay <= give.Item2; ay++)
                            if (ax >= 0 && ay >= 0 && ax < _gw - 1 && ay < _gh - 1) work.Enqueue((ax, ay));
                }
                if (lostWhat.Count > 0) Log.Print($"[terrain] basement cut conflicts: {string.Join("; ", lostWhat)}");
                HashSet<int> On(int gx, int gy) => claims.TryGetValue((gx, gy), out var c) ? c.On : null;
                bool Shared(HashSet<int> p, HashSet<int> q) => p != null && q != null && p.Overlaps(q);
                // the outline's quads wound counter-clockwise, for the clipping
                var ccw = new List<Vector2[]>();
                foreach (var q in polys) { var r = (Vector2[])q.Clone(); if (PolyArea(r) < 0f) System.Array.Reverse(r); ccw.Add(r); }
                // a triangle (the mesh's winding) dug if it is inside the outline, kept if it is outside, else clipped: what is
                // left of it outside every quad, as convex pieces in the mesh's winding
                byte Classify(Vector2 p, Vector2 q, Vector2 r, byte dugBit, byte clipBit, ref Vector2[][] keep)
                {
                    float whole = -Tri(p, q, r);
                    if (whole < MinTri) { dug++; return dugBit; }   // (flat: nothing to draw -- the fold pass leaves none)
                    var parts = new List<Vector2[]> { new[] { r, q, p } };
                    foreach (var Q in ccw) { parts = Subtract(parts, Q); if (parts.Count == 0) break; }
                    float outside = 0f;
                    foreach (var pc in parts) outside += PolyArea(pc);
                    if (outside <= ClipEps) { dug++; return dugBit; }
                    if (outside >= whole - ClipEps) return 0;
                    keep = new Vector2[parts.Count][];
                    for (int i = 0; i < parts.Count; i++) { var w = (Vector2[])parts[i].Clone(); System.Array.Reverse(w); keep[i] = w; }
                    clipped++;
                    return clipBit;
                }
                // each quad: split along the outline where it runs corner to corner (unless that split folds and the other
                // does not), then each triangle dug, kept or clipped
                for (int gx = qx0; gx <= qx1; gx++)
                    for (int gy = qy0; gy <= qy1; gy++)
                    {
                        Vector2 c00 = B(gx, gy), c10 = B(gx + 1, gy), c01 = B(gx, gy + 1), c11 = B(gx + 1, gy + 1);
                        byte f = 0;
                        bool want11 = Shared(On(gx, gy), On(gx + 1, gy + 1)) && !Shared(On(gx, gy + 1), On(gx + 1, gy));
                        bool plainOk = PlainOk(gx, gy), otherOk = OtherOk(gx, gy);
                        if (want11 ? otherOk || !plainOk : !plainOk && otherOk) { f |= CutSplit11; splits++; }
                        Vector2[][] pa = null, pb = null;
                        if ((f & CutSplit11) != 0) f |= (byte)(Classify(c00, c01, c11, CutTriA, CutClipA, ref pa) | Classify(c00, c11, c10, CutTriB, CutClipB, ref pb));
                        else f |= (byte)(Classify(c00, c01, c10, CutTriA, CutClipA, ref pa) | Classify(c10, c01, c11, CutTriB, CutClipB, ref pb));
                        if (pa != null || pb != null) { pieces ??= new(); pieces[(gx, gy)] = (pa, pb); }
                        if (f == 0) continue;
                        tri ??= new byte[_gw - 1, _gh - 1];
                        tri[gx, gy] = f;
                    }
            }
            _bend = bend; _cutTri = tri; _cutPieces = pieces;
            CutReport = polys.Count == 0 ? "no basement cuts"
                : $"{_cuts.Count} cut(s), {polys.Count} quad(s): {corners} corner(s) and {edges} edge crossing(s) bent, {lost} conflict(s) lost, {splits} quad(s) split corner to corner, {dug} triangle(s) dug, {clipped} clipped to the outline";
            // rebuild what changed, mesh AND collider (a cut is not a sculpt stroke: stale collision would stand in the hole)
            int cx0 = int.MaxValue, cy0 = int.MaxValue, cx1 = -1, cy1 = -1;
            for (int gx = 0; gx < _gw; gx++)
                for (int gy = 0; gy < _gh; gy++)
                {
                    var o = oldBend == null ? Vector2.Zero : oldBend[gx, gy];
                    var n = bend == null ? Vector2.Zero : bend[gx, gy];
                    byte t0 = oldTri != null && gx < _gw - 1 && gy < _gh - 1 ? oldTri[gx, gy] : (byte)0;
                    byte t1 = tri != null && gx < _gw - 1 && gy < _gh - 1 ? tri[gx, gy] : (byte)0;
                    if (o == n && t0 == t1) continue;
                    cx0 = System.Math.Min(cx0, gx); cx1 = System.Math.Max(cx1, gx); cy0 = System.Math.Min(cy0, gy); cy1 = System.Math.Max(cy1, gy);
                }
            // (and a quad whose pieces changed while its flags did not: another cut moved the outline through it)
            static bool Same(Vector2[][] x, Vector2[][] y)
            {
                if (x == null || y == null) return x == y;
                if (x.Length != y.Length) return false;
                for (int i = 0; i < x.Length; i++)
                {
                    if (x[i].Length != y[i].Length) return false;
                    for (int j = 0; j < x[i].Length; j++) if (x[i][j] != y[i][j]) return false;
                }
                return true;
            }
            foreach (var (from, to) in new[] { (oldPieces, pieces), (pieces, oldPieces) })
                if (from != null)
                    foreach (var kv in from)
                    {
                        (Vector2[][] A, Vector2[][] B) other = default;
                        if (to != null && to.TryGetValue(kv.Key, out other) && Same(kv.Value.A, other.A) && Same(kv.Value.B, other.B)) continue;
                        var (gx, gy) = kv.Key;
                        cx0 = System.Math.Min(cx0, gx); cx1 = System.Math.Max(cx1, gx + 1); cy0 = System.Math.Min(cy0, gy); cy1 = System.Math.Max(cy1, gy + 1);
                    }
            if (cx1 >= 0 && _chunkMi != null) RebuildChunksIn(System.Math.Max(0, cx0 - 1), cx1 + 1, System.Math.Max(0, cy0 - 1), cy1 + 1, withCollider: true);
            Log.Print($"[terrain] basement cuts: {CutReport}");
            // UG_TERRAINCUTDUMP=path writes the outline and the grid round it, bent, split, dug and clipped, for a diagram
            var dumpPath = System.Environment.GetEnvironmentVariable("UG_TERRAINCUTDUMP");
            if (!string.IsNullOrEmpty(dumpPath) && polys.Count > 0)
            {
                var lines = new List<string>();
                foreach (var q in polys) { var sb = new System.Text.StringBuilder("poly"); foreach (var p in q) sb.Append(System.FormattableString.Invariant($" {p.X:0.######} {p.Y:0.######}")); lines.Add(sb.ToString()); }
                float mnx = float.MaxValue, mny = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue;
                foreach (var q in polys) foreach (var p in q) { mnx = System.Math.Min(mnx, p.X); mny = System.Math.Min(mny, p.Y); mxx = System.Math.Max(mxx, p.X); mxy = System.Math.Max(mxy, p.Y); }
                for (int gx = System.Math.Max(0, (int)mnx - 3); gx <= System.Math.Min(_gw - 1, (int)mxx + 4); gx++)
                    for (int gy = System.Math.Max(0, (int)mny - 3); gy <= System.Math.Min(_gh - 1, (int)mxy + 4); gy++)
                    {
                        var v = GridVertex(gx, gy);
                        lines.Add(System.FormattableString.Invariant($"v {gx} {gy} {v.X:0.######} {v.Y:0.######} {CutTri(gx, gy)}"));
                    }
                if (_cutPieces != null)
                    foreach (var kv in _cutPieces)
                        foreach (var (which, ps) in new[] { ("A", kv.Value.A), ("B", kv.Value.B) })
                            if (ps != null)
                                foreach (var p in ps)
                                {
                                    var sb = new System.Text.StringBuilder($"piece {kv.Key.Item1} {kv.Key.Item2} {which}");
                                    foreach (var c in p) sb.Append(System.FormattableString.Invariant($" {c.X:0.######} {c.Y:0.######}"));
                                    lines.Add(sb.ToString());
                                }
                System.IO.File.WriteAllLines(dumpPath, lines);
            }
            return dug;
        }

        /// <summary>The outline of a union of convex quads: every quad's edges, split wherever another's crosses or touches
        /// them, and kept where the union is on exactly one side -- so an edge two quads share, or one inside another,
        /// goes. Grid units.</summary>
        static List<(Vector2 A, Vector2 B)> Boundary(List<Vector2[]> polys, System.Func<Vector2, bool> inside)
        {
            var edges = new List<(Vector2 A, Vector2 B)>();
            foreach (var q in polys) for (int i = 0; i < q.Length; i++) edges.Add((q[i], q[(i + 1) % q.Length]));
            var outp = new List<(Vector2 A, Vector2 B)>();
            var seen = new HashSet<(int, int, int, int)>();
            foreach (var (a, b) in edges)
            {
                var d = b - a; float len2 = d.LengthSquared();
                if (len2 < CutEps) continue;
                var ts = new List<float> { 0f, 1f };
                foreach (var (c, e) in edges)
                {
                    // where the other edge's ends lie on this one, and where the two cross
                    foreach (var p in new[] { c, e })
                    {
                        float t = (p - a).Dot(d) / len2;
                        if (t > 0f && t < 1f && (a + d * t - p).LengthSquared() < CutEps * CutEps) ts.Add(t);
                    }
                    var f = e - c; float den = d.Cross(f);
                    if (System.Math.Abs(den) < 1e-9f) continue;
                    float tt = (c - a).Cross(f) / den, uu = (c - a).Cross(d) / den;
                    if (tt > 0f && tt < 1f && uu >= -CutEps && uu <= 1f + CutEps) ts.Add(tt);
                }
                ts.Sort();
                var n = new Vector2(-d.Y, d.X).Normalized() * 1e-3f;
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    if (ts[i + 1] - ts[i] < 1e-6f) continue;
                    Vector2 p0 = a + d * ts[i], p1 = a + d * ts[i + 1], m = (p0 + p1) * 0.5f;
                    if (inside(m + n) == inside(m - n)) continue;
                    // (two quads' collinear edges give the same piece twice: once is enough)
                    (int, int, int, int) Key(Vector2 u, Vector2 v) => (Mathf.RoundToInt(u.X * 1000), Mathf.RoundToInt(u.Y * 1000), Mathf.RoundToInt(v.X * 1000), Mathf.RoundToInt(v.Y * 1000));
                    if (!seen.Add(Key(p0, p1)) || !seen.Add(Key(p1, p0))) continue;
                    outp.Add((p0, p1));
                }
            }
            return outp;
        }

        /// <summary>The outline's corners: the ends where two of its pieces meet at an angle (a straight run split in two is
        /// no corner).</summary>
        static List<Vector2> Corners(List<(Vector2 A, Vector2 B)> segs)
        {
            var at = new Dictionary<(int, int), (Vector2 P, List<Vector2> Dirs)>();
            void Add(Vector2 p, Vector2 dir)
            {
                var k = (Mathf.RoundToInt(p.X * 1000), Mathf.RoundToInt(p.Y * 1000));
                if (!at.TryGetValue(k, out var v)) { v = (p, new List<Vector2>()); at[k] = v; }
                v.Dirs.Add(dir.Normalized());
            }
            foreach (var (a, b) in segs) { Add(a, b - a); Add(b, a - b); }
            var outp = new List<Vector2>();
            foreach (var v in at.Values)
            {
                bool corner = v.Dirs.Count == 1;
                for (int i = 0; i < v.Dirs.Count && !corner; i++)
                    for (int j = i + 1; j < v.Dirs.Count && !corner; j++)
                        if (System.Math.Abs(v.Dirs[i].Cross(v.Dirs[j])) > 1e-3f) corner = true;
                if (corner) outp.Add(v.P);
            }
            return outp;
        }

        static float Tri(Vector2 a, Vector2 b, Vector2 c) => (b - a).Cross(c - a) * 0.5f;
        /// <summary>A polygon's signed area: positive counter-clockwise. (Taken about its first point: about the origin, 2 km
        /// out, it is the difference of products a float holds only to ~0.03 grid units squared -- 0.5 m2 -- and that once
        /// dropped a real 0.2 m2 piece and left a hole.)</summary>
        static float PolyArea(Vector2[] p)
        {
            float a = 0f;
            for (int i = 1; i + 1 < p.Length; i++) a += (p[i] - p[0]).Cross(p[i + 1] - p[0]);
            return a * 0.5f;
        }

        /// <summary>Convex pieces (counter-clockwise) minus a convex quad (counter-clockwise): each piece cut by the quad's
        /// edges in turn, the part beyond an edge kept as a piece of its own and the rest carried on to the next -- so the
        /// parts are convex and do not overlap, and a quad wholly inside a piece leaves a ring of them round it.</summary>
        static List<Vector2[]> Subtract(List<Vector2[]> pieces, Vector2[] q)
        {
            var outp = new List<Vector2[]>();
            float qx0 = float.MaxValue, qy0 = float.MaxValue, qx1 = float.MinValue, qy1 = float.MinValue;
            foreach (var v in q) { qx0 = System.Math.Min(qx0, v.X); qy0 = System.Math.Min(qy0, v.Y); qx1 = System.Math.Max(qx1, v.X); qy1 = System.Math.Max(qy1, v.Y); }
            foreach (var p in pieces)
            {
                float px0 = float.MaxValue, py0 = float.MaxValue, px1 = float.MinValue, py1 = float.MinValue;
                foreach (var v in p) { px0 = System.Math.Min(px0, v.X); py0 = System.Math.Min(py0, v.Y); px1 = System.Math.Max(px1, v.X); py1 = System.Math.Max(py1, v.Y); }
                if (px1 <= qx0 + CutEps || qx1 <= px0 + CutEps || py1 <= qy0 + CutEps || qy1 <= py0 + CutEps) { outp.Add(p); continue; }
                var rest = p;
                for (int i = 0; i < q.Length && rest.Length >= 3; i++)
                {
                    Vector2 a = q[i], b = q[(i + 1) % q.Length];
                    var beyond = HalfPlane(rest, b, a);   // (right of a->b: outside the quad)
                    if (beyond.Length >= 3 && PolyArea(beyond) > PieceEps) outp.Add(beyond);
                    rest = HalfPlane(rest, a, b);
                }
            }
            return outp;
        }

        /// <summary>The part of a convex polygon on the left of the line a->b, the line itself included (a point within
        /// ClipTol of it counts as on it, so a piece's edge along the outline is not split into crumbs).</summary>
        static Vector2[] HalfPlane(Vector2[] p, Vector2 a, Vector2 b)
        {
            var outp = new List<Vector2>(p.Length + 1);
            var d = b - a; float tol = ClipTol * d.Length();
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 s = p[i], e = p[(i + 1) % p.Length];
                float ss = d.Cross(s - a), se = d.Cross(e - a);
                if (System.Math.Abs(ss) <= tol) ss = 0f;
                if (System.Math.Abs(se) <= tol) se = 0f;
                if (ss >= 0f) outp.Add(s);
                if ((ss > 0f && se < 0f) || (ss < 0f && se > 0f)) outp.Add(s + (e - s) * (ss / (ss - se)));
            }
            return Clean(outp);
        }

        /// <summary>A convex polygon without the points within ClipTol of the one before, or of the line through their two
        /// neighbours: what is left is its corners, so the fan a piece is drawn with has no flat triangles in it.</summary>
        static Vector2[] Clean(List<Vector2> p)
        {
            for (bool again = true; again && p.Count >= 3;)
            {
                again = false;
                for (int i = 0; i < p.Count && p.Count >= 3; i++)
                {
                    Vector2 a = p[(i + p.Count - 1) % p.Count], v = p[i], b = p[(i + 1) % p.Count], ab = b - a;
                    float len = ab.Length();
                    bool keep = (v - a).Length() > ClipTol && (len <= ClipTol || System.Math.Abs(ab.Cross(v - a)) / len > ClipTol);
                    if (keep) continue;
                    p.RemoveAt(i); again = true; i--;
                }
            }
            return p.Count >= 3 ? p.ToArray() : System.Array.Empty<Vector2>();
        }

        /// <summary>Strictly inside a convex polygon, either winding.</summary>
        static bool InConvex(Vector2[] q, Vector2 p)
        {
            int sign = 0;
            for (int i = 0; i < q.Length; i++)
            {
                float c = (q[(i + 1) % q.Length] - q[i]).Cross(p - q[i]);
                if (System.Math.Abs(c) <= CutEps) return false;
                int s = c > 0f ? 1 : -1;
                if (sign == 0) sign = s; else if (s != sign) return false;
            }
            return true;
        }
    }
}
