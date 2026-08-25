using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Zombie AI rewrite -- PHASE 2: the flow field (docs/ZOMBIE_REDESIGN.md).
    //
    // ONE field per active region replaces N per-zombie pathfinds. It floods an INTEGRATION field (cheapest step
    // count to the target) out from the target over a fine grid, treating walls as impassable, then bakes a FLOW
    // field: each cell stores the unit direction to its lowest-cost neighbour -- "downhill toward the target,
    // around the buildings". Every zombie in the region just samples its cell -> O(1) each, and one BFS covers
    // all of them. This is the navmesh replacement: no NavigationAgent3D, no baked pockets.
    //
    // Cost model: 8-connected brushfire (every step = 1). Diagonals cost the same as cardinals, which very slightly
    // distorts true distance but leaves the downhill DIRECTIONS clean -- plenty for zombie steering, and O(cells).
    public class ZombieFlowField
    {
        public const float Cell = 4f;      // metres per field cell -- routes around buildings; fine enough for drift

        int _ox, _oz;                       // world cell coords (floor(world/Cell)) of the grid's min corner
        int _w, _h;                         // grid size in cells
        ushort[] _cost;                     // integration cost; Blocked = wall/unreachable
        Vector2[] _flow;                    // unit flow direction per cell (toward the target, 0 where unreachable)
        Vector3 _target;
        const ushort Blocked = ushort.MaxValue;

        public Vector3 Target => _target;
        public bool Ready => _flow != null;
        public int BlockedCells { get; private set; }   // diagnostics: how many cells the walkability probe marked as wall
        public int CellCount => _w * _h;
        public int CostAt(Vector3 pos)                   // integration cost, or -3 unreached / -2 outside / -1 no field
        {
            if (_cost == null) return -1;
            int cx = CellOf(pos.X) - _ox, cz = CellOf(pos.Z) - _oz;
            if (cx < 0 || cx >= _w || cz < 0 || cz >= _h) return -2;
            int c = _cost[cz * _w + cx];
            return c >= Blocked - 1 ? -3 : c;
        }

        // world X/Z -> cell index
        static int CellOf(float w) => Mathf.FloorToInt(w / Cell);

        // Flood the field over region [min,max] (world XZ) from `target`. `walkable(cx,cz)` reports whether the world
        // cell (cx,cz) is passable (open ground) vs blocked (a building wall). Cheap: one BFS over the region cells.
        public void Build(Vector3 min, Vector3 max, Vector3 target, System.Func<int, int, bool> walkable)
        {
            _target = target;
            _ox = CellOf(min.X); _oz = CellOf(min.Z);
            _w = Mathf.Max(1, CellOf(max.X) - _ox + 1);
            _h = Mathf.Max(1, CellOf(max.Z) - _oz + 1);
            int n = _w * _h;
            if (_cost == null || _cost.Length != n) { _cost = new ushort[n]; _flow = new Vector2[n]; }

            BlockedCells = 0;
            for (int i = 0; i < n; i++)
            {
                int cx = _ox + (i % _w), cz = _oz + (i / _w);
                bool ok = walkable(cx, cz);
                _cost[i] = ok ? (ushort)(Blocked - 1) : Blocked;   // Blocked-1 = "open but unvisited"
                if (!ok) BlockedCells++;
                _flow[i] = Vector2.Zero;
            }

            int tx = CellOf(target.X) - _ox, tz = CellOf(target.Z) - _oz;
            if (tx < 0 || tx >= _w || tz < 0 || tz >= _h) return;   // target outside the region -> Sample() falls back to a straight bearing
            int ti = tz * _w + tx;
            if (_cost[ti] == Blocked) _cost[ti] = Blocked - 1;       // never let a wall-classified target cell strand the flood
            _cost[ti] = 0;

            // 8-connected BFS wavefront from the target.
            _q.Clear();
            _q.Enqueue(ti);
            while (_q.Count > 0)
            {
                int i = _q.Dequeue();
                int cx = i % _w, cz = i / _w;
                ushort nextCost = (ushort)(_cost[i] + 1);
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dx[d], nz = cz + Dz[d];
                    if (nx < 0 || nx >= _w || nz < 0 || nz >= _h) continue;
                    int ni = nz * _w + nx;
                    if (_cost[ni] == Blocked) continue;              // wall -> impassable (must skip: Blocked > nextCost, so the <= test alone would flood THROUGH it)
                    if (_cost[ni] <= nextCost) continue;             // already reached at least as cheaply
                    _cost[ni] = nextCost;
                    _q.Enqueue(ni);
                }
            }

            // Bake flow: each reachable cell points at its lowest-cost neighbour.
            for (int i = 0; i < n; i++)
            {
                if (_cost[i] >= Blocked - 1) continue;                // wall or unreached -> no flow (Sample falls back)
                int cx = i % _w, cz = i / _w;
                ushort bestCost = _cost[i]; int bdx = 0, bdz = 0;
                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + Dx[d], nz = cz + Dz[d];
                    if (nx < 0 || nx >= _w || nz < 0 || nz >= _h) continue;
                    ushort c = _cost[nz * _w + nx];
                    if (c < bestCost) { bestCost = c; bdx = Dx[d]; bdz = Dz[d]; }
                }
                if (bdx != 0 || bdz != 0) _flow[i] = new Vector2(bdx, bdz).Normalized();
            }
        }

        // Flow direction (unit XZ) at a world position. Outside the region or on an unreached cell, fall back to a
        // straight bearing at the target so a stray zombie still heads the right way.
        public Vector2 Sample(Vector3 pos)
        {
            Vector2 toTarget = new Vector2(_target.X - pos.X, _target.Z - pos.Z);
            Vector2 direct = toTarget.LengthSquared() > 1e-6f ? toTarget.Normalized() : Vector2.Zero;
            // OPEN-GROUND SMOOTHING: if nothing blocks the straight line to the target, BEELINE -- avoids the 8-direction
            // grid zigzag that made open paths look wide/dumb (master). Only fall back to the (grid) flow AROUND walls.
            if (_flow == null || ClearLineTo(pos)) return direct;
            int cx = CellOf(pos.X) - _ox, cz = CellOf(pos.Z) - _oz;
            if (cx < 0 || cx >= _w || cz < 0 || cz >= _h) return direct;
            var f = _flow[cz * _w + cx];
            return f == Vector2.Zero ? direct : f;
        }

        // Bresenham walkability check over the field cells from pos to the target -- any Blocked cell on the line -> not clear.
        bool ClearLineTo(Vector3 pos)
        {
            if (_cost == null) return true;
            int x0 = CellOf(pos.X), z0 = CellOf(pos.Z), x1 = CellOf(_target.X), z1 = CellOf(_target.Z);
            int dx = Mathf.Abs(x1 - x0), dz = Mathf.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1, err = dx - dz;
            for (int guard = 0; guard < 4096; guard++)
            {
                int lx = x0 - _ox, lz = z0 - _oz;
                if (lx >= 0 && lx < _w && lz >= 0 && lz < _h && _cost[lz * _w + lx] == Blocked) return false;
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
                yield return (pos, _flow[i], _cost[i] == Blocked);
            }
        }

        static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        readonly Queue<int> _q = new();
    }
}
