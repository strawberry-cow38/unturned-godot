using System;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>How much thinking a zombie gets this tick. Not whether it exists -- every zombie is
    /// always simulated at some rate; none are frozen (plan section 6).</summary>
    public enum ZombieTier : byte
    {
        Close = 0,    // interaction range of a player: every tick, may borrow a body for contact
        Near = 1,     // player's region, visible range: every tick, transform only
        Far = 2,      // player's region, beyond visible: ~10 Hz, coarse
        Ambient = 3,  // no player in region: ~1 Hz, objective-level advance only
    }

    /// <summary>Stable handle to a zombie. Survives the swap-remove that keeps the sim rows dense, and
    /// a handle to a despawned zombie can never resolve to the zombie that reused its slot.</summary>
    public readonly struct ZombieId : IEquatable<ZombieId>
    {
        public readonly int Slot;
        public readonly int Generation;   // 0 is never issued, so default(ZombieId) is always invalid
        public ZombieId(int slot, int generation) { Slot = slot; Generation = generation; }
        public static ZombieId None => default;
        public bool IsNone => Generation == 0;
        public bool Equals(ZombieId o) => Slot == o.Slot && Generation == o.Generation;
        public override bool Equals(object o) => o is ZombieId z && Equals(z);
        public override int GetHashCode() => (Slot * 397) ^ Generation;
        public override string ToString() => IsNone ? "zombie:none" : $"zombie:{Slot}.{Generation}";
    }

    public struct ZombieStepStats
    {
        public int Close, Near, Far, Ambient;
        public int Due;        // rows scheduled to think this tick
        public int Orphan;     // rows outside every region -- should be 0; nonzero means bad spawn data
        public int Alive => Close + Near + Far + Ambient;
    }

    /// <summary>
    /// The zombie simulation. Zombies are ROWS IN ARRAYS owned by this object -- not nodes, not objects
    /// with virtual methods. Nothing here touches the engine, so the whole of it is L0-testable, which
    /// is the reason the state lives in arrays in the first place (plan section 9).
    ///
    /// Phase 0 scope: the rows, the spatial hash, the region partition, tier assignment and the update
    /// schedule. Nothing moves, senses, or renders yet -- movement is phase 1, perception and combat are
    /// phase 2. What phase 0 has to prove is the cost shape: with the player elsewhere, per-tick work is
    /// flat in the zombie count.
    /// </summary>
    public sealed class ZombieSim : ISimStepped
    {
        // --- tuning -------------------------------------------------------------------------------
        public float CloseRange = 6f;        // contact range: may borrow a body
        public float NearRange = 96f;        // visible range: animated, drawn
        /// <summary>Tier boundaries widen by this factor to DEMOTE, so a zombie loitering on a boundary
        /// does not flip tier every tick and thrash the body/view pools it grants.</summary>
        public float TierHysteresis = 1.15f;
        public int FarStride = 5;            // 10 Hz at 50 Hz
        public int AmbientStride = 50;       // 1 Hz at 50 Hz

        // --- rows (dense, swap-removed; index here is a ROW, not an id) ----------------------------
        Vector3[] _pos = new Vector3[64];
        float[] _health = new float[64];
        ushort[] _kind = new ushort[64];
        byte[] _tier = new byte[64];
        int[] _region = new int[64];
        int[] _rowSlot = new int[64];
        int _count;

        // --- slot table (stable handles -> rows) ---------------------------------------------------
        int[] _slotRow = new int[64];
        int[] _slotGen = new int[64];
        int[] _freeSlots = new int[64];
        int _slotCount, _freeCount;

        // --- schedule ------------------------------------------------------------------------------
        int[] _due = new int[64];
        int _dueCount;

        Vector3[] _players = Array.Empty<Vector3>();
        int _playerCount;

        readonly ZombieSpatial _spatial;
        readonly ZombieRegions _regions;
        readonly ZombieKindTable _kinds;

        public ZombieSim(ZombieRegions regions, ZombieKindTable kinds = null, ZombieSpatial spatial = null)
        {
            _regions = regions ?? throw new ArgumentNullException(nameof(regions));
            _kinds = kinds ?? ZombieKindTable.Default();
            _spatial = spatial ?? new ZombieSpatial();
        }

        public int Count => _count;
        public ZombieSpatial Spatial => _spatial;
        public ZombieRegions Regions => _regions;
        public ZombieKindTable Kinds => _kinds;
        public ZombieStepStats Stats { get; private set; }

        /// <summary>Rows scheduled to think this tick, valid until the next step. Phases 1-2 iterate
        /// this, never all rows.</summary>
        public ReadOnlySpan<int> DueRows => new ReadOnlySpan<int>(_due, 0, _dueCount);

        // --- lifecycle ------------------------------------------------------------------------------

        public ZombieId Spawn(ushort kind, Vector3 position)
        {
            if (!_kinds.IsValid(kind)) throw new ArgumentOutOfRangeException(nameof(kind), $"no zombie kind {kind}");

            int slot;
            if (_freeCount > 0) slot = _freeSlots[--_freeCount];
            else
            {
                slot = _slotCount++;
                if (slot >= _slotRow.Length)
                {
                    Array.Resize(ref _slotRow, slot * 2);
                    Array.Resize(ref _slotGen, slot * 2);
                    Array.Resize(ref _freeSlots, slot * 2);
                }
            }

            int row = _count++;
            if (row >= _pos.Length) GrowRows(row * 2);

            _pos[row] = position;
            _health[row] = _kinds[kind].Health;
            _kind[row] = kind;
            _tier[row] = (byte)ZombieTier.Ambient;   // corrected by the first step; never assume Close
            _region[row] = _regions.RegionOf(position);
            _rowSlot[row] = slot;

            _slotRow[slot] = row;
            if (_slotGen[slot] == 0) _slotGen[slot] = 1;   // generation 0 is reserved for "no zombie"
            return new ZombieId(slot, _slotGen[slot]);
        }

        public bool Despawn(ZombieId id)
        {
            if (!TryGetRow(id, out int row)) return false;
            int last = _count - 1;
            if (row != last)
            {
                _pos[row] = _pos[last];
                _health[row] = _health[last];
                _kind[row] = _kind[last];
                _tier[row] = _tier[last];
                _region[row] = _region[last];
                _rowSlot[row] = _rowSlot[last];
                _slotRow[_rowSlot[last]] = row;
            }
            _count = last;

            _slotRow[id.Slot] = -1;
            _slotGen[id.Slot]++;                          // every outstanding handle to this slot dies here
            if (_slotGen[id.Slot] == 0) _slotGen[id.Slot] = 1;
            if (_freeCount >= _freeSlots.Length) Array.Resize(ref _freeSlots, _freeSlots.Length * 2);
            _freeSlots[_freeCount++] = id.Slot;
            return true;
        }

        public bool IsAlive(ZombieId id) => TryGetRow(id, out _);

        public bool TryGetRow(ZombieId id, out int row)
        {
            row = -1;
            if (id.IsNone || (uint)id.Slot >= (uint)_slotCount) return false;
            if (_slotGen[id.Slot] != id.Generation) return false;
            int r = _slotRow[id.Slot];
            if (r < 0 || r >= _count) return false;
            row = r;
            return true;
        }

        // --- row accessors (by row; callers holding an id go through TryGetRow once) -----------------

        public Vector3 PositionOf(int row) => _pos[row];
        public ushort KindOf(int row) => _kind[row];
        public float HealthOf(int row) => _health[row];
        public ZombieTier TierOf(int row) => (ZombieTier)_tier[row];
        public int RegionOf(int row) => _region[row];
        public ZombieId IdOf(int row) => new ZombieId(_rowSlot[row], _slotGen[_rowSlot[row]]);

        public void SetPosition(int row, Vector3 p)
        {
            _pos[row] = p;
            _region[row] = _regions.RegionOf(p, _region[row]);
        }

        /// <summary>Player positions the tiering is measured against. The sim keeps the reference, so
        /// the caller may update the contents in place between steps.</summary>
        public void SetPlayers(Vector3[] players, int count)
        {
            _players = players ?? Array.Empty<Vector3>();
            _playerCount = Math.Max(0, count);
        }

        // --- the step -------------------------------------------------------------------------------

        public void SimStep(long tick, double dt)
        {
            _regions.MarkHot(_players, _playerCount);
            _spatial.Build(_pos, _count);

            if (_due.Length < _count) Array.Resize(ref _due, Math.Max(_count, 64));
            _dueCount = 0;
            var stats = new ZombieStepStats();

            for (int row = 0; row < _count; row++)
            {
                int region = _regions.RegionOf(_pos[row], _region[row]);
                _region[row] = region;
                if (region < 0) stats.Orphan++;

                ZombieTier tier = ClassifyTier(row, region);
                _tier[row] = (byte)tier;

                switch (tier)
                {
                    case ZombieTier.Close: stats.Close++; break;
                    case ZombieTier.Near: stats.Near++; break;
                    case ZombieTier.Far: stats.Far++; break;
                    default: stats.Ambient++; break;
                }

                if (IsDue(tier, _rowSlot[row], tick)) _due[_dueCount++] = row;
            }

            stats.Due = _dueCount;
            Stats = stats;
        }

        ZombieTier ClassifyTier(int row, int region)
        {
            if (!_regions.IsHot(region)) return ZombieTier.Ambient;
            if (_playerCount <= 0) return ZombieTier.Ambient;

            Vector3 p = _pos[row];
            float best = float.MaxValue;
            for (int i = 0; i < _playerCount; i++)
            {
                float d2 = (_players[i] - p).sqrMagnitude;
                if (d2 < best) best = d2;
            }

            // Widen the band a zombie is ALREADY in, so crossing back out takes real movement.
            var prev = (ZombieTier)_tier[row];
            float close = CloseRange, near = NearRange;
            if (prev == ZombieTier.Close) { close *= TierHysteresis; near *= TierHysteresis; }
            else if (prev == ZombieTier.Near) near *= TierHysteresis;

            if (best <= close * close) return ZombieTier.Close;
            if (best <= near * near) return ZombieTier.Near;
            return ZombieTier.Far;
        }

        /// <summary>Tier picks the rate; the SLOT picks the phase. Phasing by slot spreads a tier's work
        /// evenly across its stride instead of every Far zombie thinking on the same tick -- that spike
        /// is what "10 Hz" quietly means if you skip this.</summary>
        bool IsDue(ZombieTier tier, int slot, long tick)
        {
            int stride = tier == ZombieTier.Far ? FarStride : (tier == ZombieTier.Ambient ? AmbientStride : 1);
            if (stride <= 1) return true;
            return (int)(((slot + tick) % stride + stride) % stride) == 0;
        }

        void GrowRows(int capacity)
        {
            Array.Resize(ref _pos, capacity);
            Array.Resize(ref _health, capacity);
            Array.Resize(ref _kind, capacity);
            Array.Resize(ref _tier, capacity);
            Array.Resize(ref _region, capacity);
            Array.Resize(ref _rowSlot, capacity);
        }
    }
}
