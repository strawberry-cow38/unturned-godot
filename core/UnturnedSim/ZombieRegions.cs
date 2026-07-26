using System;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    // XZ bounds of one region. Y is not partitioned: levels are wide and shallow, and a region is a
    // column, same as the navmesh volumes it comes from.
    public struct ZombieRegionBounds
    {
        public float MinX, MinZ, MaxX, MaxZ;

        public ZombieRegionBounds(float minX, float minZ, float maxX, float maxZ)
        { MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ; }

        public bool Contains(Vector3 p) => p.x >= MinX && p.x < MaxX && p.z >= MinZ && p.z < MaxZ;

        public bool ContainsExpanded(Vector3 p, float margin) =>
            p.x >= MinX - margin && p.x < MaxX + margin && p.z >= MinZ - margin && p.z < MaxZ + margin;

        public Vector3 Center => new Vector3((MinX + MaxX) * 0.5f, 0f, (MinZ + MaxZ) * 0.5f);
    }

    /// <summary>
    /// The region partition, and which regions are HOT (a player is in or near them).
    ///
    /// This is the mechanism behind requirement 8: a level full of zombies costs nothing while the
    /// player is elsewhere, because only hot regions run the fast tiers. It is how retail does it --
    /// <c>ZombieManager</c> sizes its region array from <c>LevelNavigation.bounds</c> (one region per
    /// navmesh volume) and its per-frame loop walks <c>regionsWithPlayers</c>, not all zombies.
    ///
    /// So the primary constructor takes the navmesh volume bounds. <see cref="UniformGrid"/> exists for
    /// tests and for levels with no navigation data.
    ///
    /// Deviation from retail, on purpose: a region counts as hot when a player is within
    /// <see cref="HotMargin"/> of its bounds, not strictly inside. Retail's strict test lets a zombie
    /// one metre outside the volume boundary go dumb while the player stands next to it.
    /// </summary>
    public sealed class ZombieRegions
    {
        readonly ZombieRegionBounds[] _bounds;
        readonly int[] _hotStamp;
        int _stamp;

        public float HotMargin = 32f;

        public ZombieRegions(ZombieRegionBounds[] bounds)
        {
            _bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
            _hotStamp = new int[bounds.Length];
        }

        public int Count => _bounds.Length;
        public int HotCount { get; private set; }
        public ZombieRegionBounds BoundsOf(int region) => _bounds[region];

        /// <summary>Retail's level grid: 64x64 regions of 128 units spanning -4096..+4096
        /// (<c>Regions.WORLD_SIZE</c> = 64, <c>REGION_SIZE</c> = 8192/64). Used when a level has no
        /// navmesh volumes, and by tests that want a predictable partition.</summary>
        public static ZombieRegions UniformGrid(int cellsPerAxis = 64, float regionSize = 128f, float originOffset = 4096f)
        {
            if (cellsPerAxis <= 0) throw new ArgumentOutOfRangeException(nameof(cellsPerAxis));
            var b = new ZombieRegionBounds[cellsPerAxis * cellsPerAxis];
            for (int z = 0; z < cellsPerAxis; z++)
                for (int x = 0; x < cellsPerAxis; x++)
                    b[z * cellsPerAxis + x] = new ZombieRegionBounds(
                        x * regionSize - originOffset, z * regionSize - originOffset,
                        (x + 1) * regionSize - originOffset, (z + 1) * regionSize - originOffset);
            return new ZombieRegions(b);
        }

        /// <summary>Region containing <paramref name="p"/>, or -1 if it is outside every region.
        /// <paramref name="hint"/> (the caller's last known region) is checked first, so a zombie that
        /// stayed put costs one bounds test instead of a scan.</summary>
        public int RegionOf(Vector3 p, int hint = -1)
        {
            if ((uint)hint < (uint)_bounds.Length && _bounds[hint].Contains(p)) return hint;
            for (int i = 0; i < _bounds.Length; i++)
                if (_bounds[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>Recompute the hot set from player positions. O(regions * players); players are few
        /// and this runs once per step, not once per zombie.</summary>
        public void MarkHot(Vector3[] players, int playerCount)
        {
            _stamp++;
            HotCount = 0;
            if (players == null || playerCount <= 0) return;
            for (int r = 0; r < _bounds.Length; r++)
            {
                for (int p = 0; p < playerCount; p++)
                {
                    if (!_bounds[r].ContainsExpanded(players[p], HotMargin)) continue;
                    _hotStamp[r] = _stamp;
                    HotCount++;
                    break;
                }
            }
        }

        /// <summary>True if this region was hot as of the last <see cref="MarkHot"/>. Region -1
        /// (off-partition) is never hot.</summary>
        public bool IsHot(int region) => (uint)region < (uint)_bounds.Length && _hotStamp[region] == _stamp;
    }
}
