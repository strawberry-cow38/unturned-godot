using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Zombie AI rewrite -- PHASE 2: the flow field (docs/ZOMBIE_REDESIGN.md).
    //
    // ONE field per active region replaces N per-zombie pathfinds. A DIJKSTRA integration field with OCTILE step costs
    // (diagonals cost sqrt2, cardinals 1) floods out from the target -> a smooth, Euclidean-ish geodesic distance field.
    // (The old version was a uniform BFS -- every step == 1 -- whose square cost contours funnelled agents along wall
    // FACES; that needed a clearance hack. This doesn't.) The flow at each cell is the cost-weighted blend of the
    // directions to its LOWER-cost OPEN neighbours: a smooth "downhill toward the target, around buildings" vector that
    // aims at corners and runs straight down corridors on its own, and never points into a wall. Every agent samples
    // its cell O(1); one flood covers the whole region. Navmesh replacement -- no NavigationAgent3D, no baked pockets.
    public class ZombieFlowField
    {
        public const float Cell = 4f;      // metres per field cell -- routes around buildings; fine enough for drift

        int _ox, _oz;                       // world cell coords (floor(world/Cell)) of the grid's min corner
        int _w, _h;                         // grid size in cells
        float[] _cost;                      // Dijkstra octile integration cost; float.MaxValue = unreached
        bool[] _blocked;                    // wall / impassable cells
        Vector2[] _flow;                    // unit downhill direction per cell (toward the target; 0 where unreachable)
        Vector3 _target;

        public Vector3 Target => _target;
        public bool Ready => _flow != null;
        public int BlockedCells { get; private set; }   // diagnostics: how many cells the walkability probe marked as wall
        public int CellCount => _w * _h;
        public int CostAt(Vector3 pos)                   // integration cost (rounded), or -3 unreached / -2 outside / -1 no field
        {
            if (_cost == null) return -1;
            int cx = CellOf(pos.X) - _ox, cz = CellOf(pos.Z) - _oz;
            if (cx < 0 || cx >= _w || cz < 0 || cz >= _h) return -2;
            int i = cz * _w + cx;
            return (_blocked[i] || _cost[i] == float.MaxValue) ? -3 : Mathf.RoundToInt(_cost[i]);
        }

        // world X/Z -> cell index
        static int CellOf(float w) => Mathf.FloorToInt(w / Cell);

        // Flood the field over region [min,max] (world XZ) from `target`. `walkable(cx,cz)` reports whether the world
        // cell (cx,cz) is passable (open ground) vs blocked (a building wall). Dijkstra octile flood, one per region.
        public void Build(Vector3 min, Vector3 max, Vector3 target, System.Func<int, int, bool> walkable)
        {
            _target = target;
            _ox = CellOf(min.X); _oz = CellOf(min.Z);
            _w = Mathf.Max(1, CellOf(max.X) - _ox + 1);
            _h = Mathf.Max(1, CellOf(max.Z) - _oz + 1);
            int n = _w * _h;
            if (_cost == null || _cost.Length != n) { _cost = new float[n]; _flow = new Vector2[n]; _blocked = new bool[n]; }

            BlockedCells = 0;
            for (int i = 0; i < n; i++)
            {
                int cx = _ox + (i % _w), cz = _oz + (i / _w);
                bool ok = walkable(cx, cz);
                _blocked[i] = !ok;
                _cost[i] = float.MaxValue;
                _flow[i] = Vector2.Zero;
                if (!ok) BlockedCells++;
            }

            int tx = CellOf(target.X) - _ox, tz = CellOf(target.Z) - _oz;
            if (tx < 0 || tx >= _w || tz < 0 || tz >= _h) return;   // target outside the region -> Sample() falls back to a straight bearing
            int ti = tz * _w + tx;
            _blocked[ti] = false;                                    // never let a wall-classified target cell strand the flood
            _cost[ti] = 0f;

            // Dijkstra with octile step costs (cardinal 1, diagonal sqrt2) -> smooth geodesic distances. A lazy-deletion
            // binary heap: stale entries are skipped by the `ci > _cost[i]` guard instead of a decrease-key.
            _pq.Clear();
            _pq.Enqueue(ti, 0f);
            while (_pq.Count > 0)
            {
                _pq.TryDequeue(out int i, out float ci);
                if (ci > _cost[i]) continue;                         // stale heap entry (already relaxed cheaper)
                int cx = i % _w, cz = i / _w;
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dx[d], nz = cz + Dz[d];
                    if (nx < 0 || nx >= _w || nz < 0 || nz >= _h) continue;
                    int ni = nz * _w + nx;
                    if (_blocked[ni]) continue;                      // wall -> impassable
                    float nc = ci + Step[d];
                    if (nc < _cost[ni]) { _cost[ni] = nc; _pq.Enqueue(ni, nc); }
                }
            }

            // Flow = cost-weighted blend of the directions to LOWER-cost OPEN neighbours. Smooth (blends all 8, not the
            // single lowest) and it only sums OPEN cells, so it never points into a wall -> on the smooth octile field
            // agents aim at corners and run down corridors naturally, WITHOUT any clearance/repulsion hack.
            for (int i = 0; i < n; i++)
            {
                if (_blocked[i] || _cost[i] == float.MaxValue) continue;
                int cx = i % _w, cz = i / _w;
                float c = _cost[i];
                Vector2 f = Vector2.Zero;
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dx[d], nz = cz + Dz[d];
                    if (nx < 0 || nx >= _w || nz < 0 || nz >= _h) continue;
                    int ni = nz * _w + nx;
                    if (_blocked[ni] || _cost[ni] == float.MaxValue) continue;
                    float drop = c - _cost[ni];                      // how much downhill this neighbour is
                    if (drop > 0f) f += Dir[d] * drop;               // steeper downhill -> more pull
                }
                if (f.LengthSquared() > 1e-9f) _flow[i] = f.Normalized();
            }
        }

        // Flow direction (unit XZ) at a world position. Outside the region or on an unreached cell, fall back to a
        // straight bearing at the target so a stray zombie still heads the right way.
        public Vector2 Sample(Vector3 pos)
        {
            Vector2 toTarget = new Vector2(_target.X - pos.X, _target.Z - pos.Z);
            Vector2 direct = toTarget.LengthSquared() > 1e-6f ? toTarget.Normalized() : Vector2.Zero;
            if (_flow == null) return direct;
            int cx = CellOf(pos.X) - _ox, cz = CellOf(pos.Z) - _oz;
            if (cx < 0 || cx >= _w || cz < 0 || cz >= _h) return direct;
            var f = _flow[cz * _w + cx];
            if (f == Vector2.Zero)
            {
                // Blocked/unreached cell. A zombie is usually here standing on the OPEN sliver of a wall-BORDER cell --
                // the 2m wall's footprint rounds up to the whole 4m cell, so the cell reads "wall" though it's mostly
                // walkable. Steer toward the single LOWEST-COST open neighbour = get onto the geodesic and slide ALONG the
                // wall toward the corner. (Averaging ALL neighbours' flow cancelled the tangential part -- some point up to
                // the corner, some down past it -- leaving a push straight INTO the wall, so bodies pinned on the face and
                // never progressed. Returning `direct` was even worse: straight at the target THROUGH the wall.)
                float best = float.MaxValue; Vector2 bestDir = Vector2.Zero;
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dx[d], nz = cz + Dz[d];
                    if (nx < 0 || nx >= _w || nz < 0 || nz >= _h) continue;
                    int ni = nz * _w + nx;
                    if (_blocked[ni] || _cost[ni] == float.MaxValue) continue;
                    if (_cost[ni] < best) { best = _cost[ni]; bestDir = Dir[d]; }
                }
                return bestDir != Vector2.Zero ? bestDir : direct;
            }
            // OPEN cell: BEELINE if the target is directly visible (cancels the grid's octile bias), else the smooth flow.
            if (ClearLineTo(pos)) return direct;
            return f;
        }

        // Bresenham walkability check over the field cells from pos to the target -- any Blocked cell on the line -> not clear.
        bool ClearLineTo(Vector3 pos)
        {
            if (_blocked == null) return true;
            int x0 = CellOf(pos.X), z0 = CellOf(pos.Z), x1 = CellOf(_target.X), z1 = CellOf(_target.Z);
            int dx = Mathf.Abs(x1 - x0), dz = Mathf.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1, err = dx - dz;
            for (int guard = 0; guard < 4096; guard++)
            {
                int lx = x0 - _ox, lz = z0 - _oz;
                if (lx >= 0 && lx < _w && lz >= 0 && lz < _h && _blocked[lz * _w + lx]) return false;
                if (x0 == x1 && z0 == z1) return true;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; x0 += sx; }
                if (e2 < dx) { err += dx; z0 += sz; }
            }
            return true;
        }

        // ---- debug (for the --zflow verify render): iterate baked flow arrows ----
        public IEnumerable<(Vector3 pos, Vector2 dir, bool blocked)> DebugCells()
        {
            if (_flow == null) yield break;
            for (int i = 0; i < _w * _h; i++)
            {
                int cx = _ox + (i % _w), cz = _oz + (i / _w);
                var pos = new Vector3((cx + 0.5f) * Cell, 0f, (cz + 0.5f) * Cell);
                yield return (pos, _flow[i], _blocked[i]);
            }
        }

        static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        static readonly float[] Step = { 1f, 1f, 1f, 1f, 1.4142135f, 1.4142135f, 1.4142135f, 1.4142135f };
        static readonly Vector2[] Dir =
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.70710677f, 0.70710677f), new Vector2(0.70710677f, -0.70710677f),
            new Vector2(-0.70710677f, 0.70710677f), new Vector2(-0.70710677f, -0.70710677f),
        };
        readonly PriorityQueue<int, float> _pq = new();
    }
}
