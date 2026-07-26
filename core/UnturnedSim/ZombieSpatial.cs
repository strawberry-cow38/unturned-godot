using System;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>
    /// Uniform spatial hash over zombie positions, rebuilt once per sim step.
    ///
    /// Every proximity question the game asks -- who is near the player, who heard that shot, what did
    /// this bullet pass through -- is answered here rather than by the physics engine. That is the point:
    /// in the old system "what did this bullet hit" was a physics raycast, so every shootable zombie
    /// needed a live collider, so every zombie needed a body, so every zombie paid for a swept move.
    /// Answering it from a grid is what lets bodies become scarce (plan section 5).
    ///
    /// Contract: queries are EXACT on centres -- they return every stored item whose position satisfies
    /// the predicate and nothing else. The grid is an accelerator, not an approximation, so hash
    /// collisions and cell granularity cost work but never change the answer. Capsule extent is the
    /// caller's business; it passes a widened radius.
    ///
    /// Allocation-free after warmup: buckets are a counting-sorted index array, not lists.
    /// </summary>
    public sealed class ZombieSpatial
    {
        public const float DefaultCellSize = 8f;
        const int MaxTraversalSteps = 8192;   // guard against NaN/denormal segments walking forever

        readonly float _cell, _invCell;
        readonly int _mask;
        readonly int[] _bucketStart;   // buckets + 1, prefix-summed
        readonly int[] _cursor;        // fill cursors during Build
        readonly int[] _bucketStamp;   // "already scanned during this query"
        int[] _items = Array.Empty<int>();
        Vector3[] _pos;                // borrowed at Build; valid until the next Build
        int _count;
        int _stamp;

        public ZombieSpatial(float cellSize = DefaultCellSize, int buckets = 4096)
        {
            if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (buckets <= 0 || (buckets & (buckets - 1)) != 0)
                throw new ArgumentException("buckets must be a power of two", nameof(buckets));
            _cell = cellSize; _invCell = 1f / cellSize; _mask = buckets - 1;
            _bucketStart = new int[buckets + 1];
            _cursor = new int[buckets];
            _bucketStamp = new int[buckets];
        }

        public float CellSize => _cell;
        public int Count => _count;
        public int BucketCount => _mask + 1;

        /// <summary>Rebuild from a dense position array. Keeps the reference; queries read through it,
        /// so the caller must not reallocate or reorder <paramref name="pos"/> before the next Build.</summary>
        public void Build(Vector3[] pos, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > 0 && pos == null) throw new ArgumentNullException(nameof(pos));
            _pos = pos; _count = count;

            Array.Clear(_bucketStart, 0, _bucketStart.Length);
            for (int i = 0; i < count; i++) _bucketStart[BucketOf(pos[i]) + 1]++;
            for (int b = 1; b < _bucketStart.Length; b++) _bucketStart[b] += _bucketStart[b - 1];
            Array.Copy(_bucketStart, _cursor, _cursor.Length);

            if (_items.Length < count) _items = new int[Math.Max(count, 64)];
            for (int i = 0; i < count; i++) _items[_cursor[BucketOf(pos[i])]++] = i;
        }

        /// <summary>Every item whose centre is within <paramref name="radius"/> of <paramref name="centre"/>.
        /// Returns the number written; stops at <c>results.Length</c>, so size it for the worst case.</summary>
        public int QuerySphere(Vector3 centre, float radius, int[] results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (_count == 0 || radius < 0f) return 0;
            float r2 = radius * radius;
            int n = 0;
            int minX = CellOf(centre.x - radius), maxX = CellOf(centre.x + radius);
            int minY = CellOf(centre.y - radius), maxY = CellOf(centre.y + radius);
            int minZ = CellOf(centre.z - radius), maxZ = CellOf(centre.z + radius);

            // A grid is only an accelerator while the cells it must visit are fewer than the items it
            // holds. Past that -- a huge radius, a nearly empty level -- walking cells is slower than
            // walking every zombie, and the cost is cubic in the radius rather than bounded by N.
            if (CellVolume(minX, maxX, minY, maxY, minZ, maxZ) > _count)
            {
                for (int i = 0; i < _count; i++)
                {
                    if ((_pos[i] - centre).sqrMagnitude > r2) continue;
                    if (n >= results.Length) return n;
                    results[n++] = i;
                }
                return n;
            }

            _stamp++;
            for (int cz = minZ; cz <= maxZ; cz++)
                for (int cy = minY; cy <= maxY; cy++)
                    for (int cx = minX; cx <= maxX; cx++)
                    {
                        int b = Hash(cx, cy, cz) & _mask;
                        if (_bucketStamp[b] == _stamp) continue;
                        _bucketStamp[b] = _stamp;
                        for (int k = _bucketStart[b]; k < _bucketStart[b + 1]; k++)
                        {
                            int it = _items[k];
                            if ((_pos[it] - centre).sqrMagnitude > r2) continue;   // exact; also filters hash collisions
                            if (n >= results.Length) return n;
                            results[n++] = it;
                        }
                    }
            return n;
        }

        /// <summary>Every item whose centre is within <paramref name="radius"/> of the segment a..b --
        /// the bullet query. Walks only the cells the segment actually passes through, so cost scales
        /// with the segment's length in cells, not with the volume of its bounding box.</summary>
        public int QuerySegment(Vector3 a, Vector3 b, float radius, int[] results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (_count == 0 || radius < 0f) return 0;
            float r2 = radius * radius;
            int margin = (int)MathF.Ceiling(radius * _invCell);
            int n = 0;

            Vector3 d = b - a;
            int cx = CellOf(a.x), cy = CellOf(a.y), cz = CellOf(a.z);
            int ex = CellOf(b.x), ey = CellOf(b.y), ez = CellOf(b.z);

            // Same escape hatch as QuerySphere: a long segment with a fat radius can nominate more cells
            // than there are zombies, at which point the linear scan is simply cheaper.
            long span = 2L * margin + 1;
            long estimate = (Math.Abs(ex - cx) + Math.Abs(ey - cy) + Math.Abs(ez - cz) + 1L) * span * span * span;
            if (estimate > _count)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (SqrDistanceToSegment(_pos[i], a, d) > r2) continue;
                    if (n >= results.Length) return n;
                    results[n++] = i;
                }
                return n;
            }

            _stamp++;

            int stepX = d.x > 0f ? 1 : (d.x < 0f ? -1 : 0);
            int stepY = d.y > 0f ? 1 : (d.y < 0f ? -1 : 0);
            int stepZ = d.z > 0f ? 1 : (d.z < 0f ? -1 : 0);
            float tMaxX = BoundaryT(a.x, d.x, cx, stepX), tDeltaX = DeltaT(d.x);
            float tMaxY = BoundaryT(a.y, d.y, cy, stepY), tDeltaY = DeltaT(d.y);
            float tMaxZ = BoundaryT(a.z, d.z, cz, stepZ), tDeltaZ = DeltaT(d.z);

            for (int step = 0; step < MaxTraversalSteps; step++)
            {
                for (int oz = -margin; oz <= margin; oz++)
                    for (int oy = -margin; oy <= margin; oy++)
                        for (int ox = -margin; ox <= margin; ox++)
                        {
                            int bk = Hash(cx + ox, cy + oy, cz + oz) & _mask;
                            if (_bucketStamp[bk] == _stamp) continue;
                            _bucketStamp[bk] = _stamp;
                            for (int k = _bucketStart[bk]; k < _bucketStart[bk + 1]; k++)
                            {
                                int it = _items[k];
                                if (SqrDistanceToSegment(_pos[it], a, d) > r2) continue;   // exact
                                if (n >= results.Length) return n;
                                results[n++] = it;
                            }
                        }

                if (cx == ex && cy == ey && cz == ez) break;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    if (tMaxX > 1f || stepX == 0) break;
                    cx += stepX; tMaxX += tDeltaX;
                }
                else if (tMaxY <= tMaxZ)
                {
                    if (tMaxY > 1f || stepY == 0) break;
                    cy += stepY; tMaxY += tDeltaY;
                }
                else
                {
                    if (tMaxZ > 1f || stepZ == 0) break;
                    cz += stepZ; tMaxZ += tDeltaZ;
                }
            }
            return n;
        }

        /// <summary>Squared distance from p to the segment starting at a with direction+length d.</summary>
        public static float SqrDistanceToSegment(Vector3 p, Vector3 a, Vector3 d)
        {
            float len2 = d.sqrMagnitude;
            if (len2 < 1e-12f) return (p - a).sqrMagnitude;
            float t = Vector3.Dot(p - a, d) / len2;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return (p - (a + d * t)).sqrMagnitude;
        }

        // long, and saturating: a 10 km radius spans ~2500 cells per axis, which overflows an int cubed.
        static long CellVolume(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            long sx = (long)maxX - minX + 1, sy = (long)maxY - minY + 1, sz = (long)maxZ - minZ + 1;
            if (sx > 0x100000 || sy > 0x100000 || sz > 0x100000) return long.MaxValue;
            return sx * sy * sz;
        }

        int CellOf(float v) => (int)MathF.Floor(v * _invCell);
        int BucketOf(Vector3 p) => Hash(CellOf(p.x), CellOf(p.y), CellOf(p.z)) & _mask;

        static int Hash(int cx, int cy, int cz) =>
            unchecked((cx * 73856093) ^ (cy * 19349663) ^ (cz * 83492791));

        float DeltaT(float d) => d != 0f ? MathF.Abs(_cell / d) : float.PositiveInfinity;

        // Parametric t at which the ray leaves its current cell along this axis.
        float BoundaryT(float origin, float d, int cell, int step)
        {
            if (step == 0) return float.PositiveInfinity;
            float boundary = (step > 0 ? cell + 1 : cell) * _cell;
            return (boundary - origin) / d;
        }
    }
}
