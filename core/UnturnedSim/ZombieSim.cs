using System;
using System.Collections.Generic;
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

    /// <summary>What a zombie is doing. Phase 1 has only the two movement states; sensing, attacking and
    /// dying arrive in phase 2, where they become transitions rather than new classes.</summary>
    public enum ZombieState : byte
    {
        Idle = 0,
        Pursue = 1,
        Investigate = 2,   // heading for a noise, or for where the player was last seen
        Attack = 3,        // in reach, swinging
        Dead = 4,
    }

    /// <summary>A swing that connected this step. The sim never applies damage to a player itself --
    /// players are the game layer's business -- so it reports and lets the caller resolve.</summary>
    public struct ZombieAttackEvent
    {
        public ZombieId Id;
        public int PlayerIndex;
        public float Damage;
    }

    /// <summary>A zombie that died this step, for the presentation layer to turn into a corpse.</summary>
    public struct ZombieDeathEvent
    {
        public ZombieId Id;
        public Vector3 Position;
        public Vector3 Facing;
        public ZombieLimb KillingLimb;
    }

    public struct ZombieStepStats
    {
        public int Close, Near, Far, Ambient;
        public int Due;          // rows scheduled to think this tick
        public int Orphan;       // rows outside every region -- should be 0; nonzero means bad spawn data
        public int Moving;       // rows that actually integrated a position this tick
        public int PathQueries;  // navmesh queries issued this tick (never exceeds PathQueriesPerTick)
        public int PathQueued;   // rows still waiting for one -- a persistent backlog means the budget is too small
        public int Dead;         // corpses still holding a row
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

        // --- movement + pathing (phase 1) ---------------------------------------------------------
        /// <summary>Where the walkable surface is. Null means nothing walks -- the sim still tiers and
        /// schedules, it just has nowhere to go.</summary>
        public IZombieNavQuery Nav;
        /// <summary>Path queries allowed per tick, across the whole level. This is the cap that stops a
        /// horde all hearing one gunshot and issuing sixty navmesh queries in a single tick.</summary>
        public int PathQueriesPerTick = 8;
        public float RepathInterval = 1.2f;  // seconds before a corridor is considered stale
        public float DestMovedTolerance = 3f;// how far the target may drift before the corridor is refetched
        public float WaypointReached = 0.7f;

        public const int MaxWaypoints = 8;

        // --- perception + combat (phase 2) ---------------------------------------------------------
        /// <summary>What blocks sight. Null means nothing does.</summary>
        public IZombieLineOfSight Sight = new OpenField();
        public float EyeHeight = 1.0f;           // where a zombie looks from, and gets looked at
        public float LoseSightGrace = 1.5f;      // seconds of no sight before a chase becomes an investigation
        public float InvestigateGiveUp = 3f;     // seconds standing at a noise before losing interest
        public float InvestigateTimeout = 12f;   // hard cap: an unreachable noise never holds one forever
        public float AttackInterval = 1f;        // seconds between swings
        public float AttackWindup = 0.4f;        // the hit lands mid-swing
        public float VerticalAttackReach = 2.1f;
        public float CorpseSeconds = 24f;        // how long a body lingers before its row is recycled

        // --- rows (dense, swap-removed; index here is a ROW, not an id) ----------------------------
        Vector3[] _pos = new Vector3[64];
        float[] _health = new float[64];
        ushort[] _kind = new ushort[64];
        byte[] _tier = new byte[64];
        int[] _region = new int[64];
        int[] _rowSlot = new int[64];
        int _count;

        // movement rows
        byte[] _state = new byte[64];          // ZombieState
        Vector3[] _dest = new Vector3[64];     // where this zombie is trying to get to
        Vector3[] _face = new Vector3[64];     // last non-zero heading, for the view layer
        Vector3[] _corridor = new Vector3[64 * MaxWaypoints];
        byte[] _corridorLen = new byte[64];
        byte[] _corridorAt = new byte[64];
        float[] _repath = new float[64];       // seconds since this corridor was fetched
        bool[] _queued = new bool[64];         // already waiting for a path query
        int[] _pathQueue = new int[64];
        int _queueHead, _queueTail, _queueCount;
        readonly Vector3[] _scratchCorridor = new Vector3[MaxWaypoints];

        // perception + combat rows
        Vector3[] _heardPos = new Vector3[64];
        float[] _heardSalience = new float[64];   // best sound heard since the last step
        float[] _huntSalience = new float[64];    // salience of the noise currently being investigated
        Vector3[] _lastSeen = new Vector3[64];
        bool[] _hasLastSeen = new bool[64];
        float[] _lostSight = new float[64];
        float[] _stateClock = new float[64];      // when the current investigation started
        float[] _lastAttack = new float[64];
        bool[] _swingPending = new bool[64];
        int[] _targetPlayer = new int[64];
        float[] _diedAt = new float[64];
        byte[] _killedBy = new byte[64];

        double _simTime;
        readonly List<ZombieAttackEvent> _attacks = new List<ZombieAttackEvent>();
        readonly List<ZombieDeathEvent> _deaths = new List<ZombieDeathEvent>();
        readonly List<ZombieId> _toRecycle = new List<ZombieId>();
        int[] _queryScratch = new int[256];

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

        /// <summary>Navmesh queries issued since the level loaded. Stats.PathQueries is the per-TICK
        /// figure, which is 0 on most ticks by design -- this is the one to watch when tuning the budget,
        /// and the one to assert on when the question is "did it path at all".</summary>
        public long TotalPathQueries { get; private set; }

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
            _state[row] = (byte)ZombieState.Idle;
            _dest[row] = position;
            _face[row] = new Vector3(0f, 0f, 1f);
            _corridorLen[row] = 0; _corridorAt[row] = 0;
            _repath[row] = 0f; _queued[row] = false;
            _heardSalience[row] = 0f; _huntSalience[row] = 0f;
            _hasLastSeen[row] = false; _lostSight[row] = 0f;
            _stateClock[row] = 0f; _lastAttack[row] = -1000f;
            _swingPending[row] = false; _targetPlayer[row] = -1;
            _diedAt[row] = 0f; _killedBy[row] = (byte)ZombieLimb.Spine;

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
                _state[row] = _state[last];
                _dest[row] = _dest[last];
                _face[row] = _face[last];
                _corridorLen[row] = _corridorLen[last];
                _corridorAt[row] = _corridorAt[last];
                _repath[row] = _repath[last];
                _queued[row] = _queued[last];
                _heardPos[row] = _heardPos[last];
                _heardSalience[row] = _heardSalience[last];
                _huntSalience[row] = _huntSalience[last];
                _lastSeen[row] = _lastSeen[last];
                _hasLastSeen[row] = _hasLastSeen[last];
                _lostSight[row] = _lostSight[last];
                _stateClock[row] = _stateClock[last];
                _lastAttack[row] = _lastAttack[last];
                _swingPending[row] = _swingPending[last];
                _targetPlayer[row] = _targetPlayer[last];
                _diedAt[row] = _diedAt[last];
                _killedBy[row] = _killedBy[last];
                System.Array.Copy(_corridor, last * MaxWaypoints, _corridor, row * MaxWaypoints, MaxWaypoints);
                _slotRow[_rowSlot[last]] = row;
            }
            _count = last;
            // Any queued path request naming a row is now naming a different zombie. Rather than patch
            // indices inside the queue, drop it: the row re-queues on its next due tick.
            DropQueue();

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

        /// <summary>Teleport a row. The corridor is dropped, because a route computed from where the
        /// zombie used to be is not a route from where it is now.</summary>
        public void SetPosition(int row, Vector3 p)
        {
            _pos[row] = p;
            _region[row] = _regions.RegionOf(p, _region[row]);
            _corridorLen[row] = 0; _corridorAt[row] = 0; _repath[row] = RepathInterval;
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
            // A region must never be colder than the tiers measured inside it. With a margin tighter than
            // NearRange, a zombie 50 m away in the next pocket stays AMBIENT and ignores you -- the region
            // test silently overrules the distance test. Found on PEI: the spawn sits in wilderness, more
            // than the old 32 m margin from every pocket, so the entire level slept.
            if (_regions.HotMargin < NearRange) _regions.HotMargin = NearRange;
            _simTime += dt;
            _attacks.Clear();
            _deaths.Clear();
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

                if ((ZombieState)_state[row] == ZombieState.Dead) stats.Dead++;
                if (IsDue(tier, _rowSlot[row], tick)) _due[_dueCount++] = row;
            }

            stats.Due = _dueCount;
            StepMovement(dt, ref stats);
            RecycleCorpses();
            stats.PathQueued = _queueCount;
            Stats = stats;
        }

        /// <summary>A corpse holds its row for CorpseSeconds so the presentation layer can ragdoll it,
        /// then the row is recycled. The old system spawned 11 PhysicalBone3D per corpse and never
        /// despawned any of them; a level's worth of that is its own performance problem.</summary>
        void RecycleCorpses()
        {
            _toRecycle.Clear();
            for (int row = 0; row < _count; row++)
            {
                if ((ZombieState)_state[row] != ZombieState.Dead) continue;
                if (_simTime - _diedAt[row] < CorpseSeconds) continue;
                _toRecycle.Add(IdOf(row));
            }
            foreach (var id in _toRecycle) Despawn(id);
        }

        /// <summary>
        /// Move the rows that are due. No physics body is involved: position is integrated along a
        /// navmesh corridor and Y comes from the surface. The corridor is what keeps them out of walls --
        /// steering is toward the next WAYPOINT, never straight at the target, so the route does the
        /// collision avoidance that a swept move used to do at 1-2.4 ms per 63 bodies.
        /// </summary>
        void StepMovement(double dt, ref ZombieStepStats stats)
        {
            for (int i = 0; i < _dueCount; i++)
            {
                int row = _due[i];
                var tier = (ZombieTier)_tier[row];
                if ((ZombieState)_state[row] == ZombieState.Dead) continue;
                if (tier == ZombieTier.Ambient) continue;   // objective-level advance lands with phase 3's goals
                if (Nav == null) { UpdateIntent(row, (float)dt * StrideOf(tier)); continue; }

                // A due row has skipped (stride - 1) ticks, so it must integrate all of them or a Far
                // zombie would crawl at a fifth speed and visibly change pace when it crossed a tier.
                float step = (float)dt * StrideOf(tier);

                UpdateIntent(row, step);
                var state = (ZombieState)_state[row];

                if (state == ZombieState.Attack) { TickAttack(row, step); continue; }
                if (state != ZombieState.Pursue && state != ZombieState.Investigate) continue;
                _swingPending[row] = false;   // left reach mid-swing; the blow does not follow you

                _repath[row] += step;
                if (NeedsPath(row)) Enqueue(row);
                if (_corridorLen[row] == 0) continue;   // waiting on the budget; stand still rather than beeline

                if (Advance(row, step)) stats.Moving++;
            }

            DrainPathQueue(ref stats);
        }

        int StrideOf(ZombieTier tier) =>
            tier == ZombieTier.Far ? Math.Max(1, FarStride) : (tier == ZombieTier.Ambient ? Math.Max(1, AmbientStride) : 1);

        /// <summary>
        /// Decide what this zombie is doing. Sight outranks sound; a zombie already committed to a noise
        /// stays on task unless a strictly more salient one arrives, so a horde does not re-target on
        /// every footstep. Losing sight mid-chase does not delete the target -- it becomes a place to go
        /// and look, which is what stops zombies tracking you through walls.
        /// </summary>
        void UpdateIntent(int row, float step)
        {
            var state = (ZombieState)_state[row];
            if (state == ZombieState.Dead) return;

            // A sound heard since the last step can start or redirect an investigation, but never
            // interrupts an active chase.
            if (_heardSalience[row] > 0f)
            {
                if (state != ZombieState.Pursue && state != ZombieState.Attack &&
                    _heardSalience[row] > _huntSalience[row])
                {
                    _state[row] = (byte)ZombieState.Investigate;
                    _dest[row] = _heardPos[row];
                    _huntSalience[row] = _heardSalience[row];
                    _stateClock[row] = (float)_simTime;
                    state = ZombieState.Investigate;
                }
                _heardSalience[row] = 0f;
            }

            int seen = SeePlayer(row);
            if (seen >= 0)
            {
                _targetPlayer[row] = seen;
                _lastSeen[row] = _players[seen];
                _hasLastSeen[row] = true;
                _lostSight[row] = 0f;
                _dest[row] = _players[seen];
                _state[row] = (byte)(InAttackReach(row, seen) ? ZombieState.Attack : ZombieState.Pursue);
                return;
            }

            if (state == ZombieState.Pursue || state == ZombieState.Attack)
            {
                _lostSight[row] += step;
                if (_lostSight[row] <= LoseSightGrace)
                {
                    // Brief occlusion -- a doorway, a passing tree. Keep closing on where they were.
                    _state[row] = (byte)ZombieState.Pursue;
                    return;
                }
                // Sight is properly gone: go look where they were, rather than tracking through walls.
                _state[row] = (byte)(_hasLastSeen[row] ? ZombieState.Investigate : ZombieState.Idle);
                if (_hasLastSeen[row]) { _dest[row] = _lastSeen[row]; _hasLastSeen[row] = false; }
                _huntSalience[row] = 0f;
                _stateClock[row] = (float)_simTime;
                return;
            }

            if (state == ZombieState.Investigate)
            {
                float elapsed = (float)_simTime - _stateClock[row];
                float d2 = HorizontalSqr(_dest[row], _pos[row]);
                bool arrived = d2 < 3f;
                if (elapsed > InvestigateTimeout || (arrived && elapsed > InvestigateGiveUp))
                {
                    _state[row] = (byte)ZombieState.Idle;
                    _huntSalience[row] = 0f;          // idle again: the next sound of any loudness can take us
                    _corridorLen[row] = 0;
                }
                return;
            }

            _state[row] = (byte)ZombieState.Idle;
            _huntSalience[row] = 0f;
        }

        /// <summary>Index of a player this zombie can currently SEE: inside the kind's sight range, within
        /// its cone, and with a clear line. -1 for none.</summary>
        int SeePlayer(int row)
        {
            if (_playerCount <= 0) return -1;
            var kind = _kinds[_kind[row]];
            Vector3 p = _pos[row];
            Vector3 eye = new Vector3(p.x, p.y + EyeHeight, p.z);
            Vector3 facing = _face[row];
            float cos = MathF.Cos(kind.SightHalfAngleDeg * MathF.PI / 180f);

            int best = -1; float bestD2 = float.MaxValue;
            for (int i = 0; i < _playerCount; i++)
            {
                Vector3 to = _players[i] - p;
                float d2 = to.sqrMagnitude;
                if (d2 > kind.SightRange * kind.SightRange || d2 >= bestD2) continue;

                Vector3 flat = new Vector3(to.x, 0f, to.z);
                if (flat.sqrMagnitude > 1e-6f && facing.sqrMagnitude > 1e-6f)
                    if (Vector3.Dot(flat.normalized, facing.normalized) < cos) continue;   // outside the cone

                var target = new Vector3(_players[i].x, _players[i].y + EyeHeight, _players[i].z);
                if (Sight != null && !Sight.CanSee(eye, target)) continue;

                best = i; bestD2 = d2;
            }
            return best;
        }

        bool InAttackReach(int row, int player)
        {
            var kind = _kinds[_kind[row]];
            float d2 = HorizontalSqr(_players[player], _pos[row]);
            float dy = MathF.Abs(_players[player].y - _pos[row].y);
            return d2 < kind.AttackRange * kind.AttackRange && dy < VerticalAttackReach;
        }

        static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>Swing on a cadence, with the hit landing mid-swing rather than on the frame the
        /// animation starts -- so a zombie you kill during the wind-up does not still hit you.</summary>
        void TickAttack(int row, float step)
        {
            int target = _targetPlayer[row];
            if (target < 0 || target >= _playerCount) { _state[row] = (byte)ZombieState.Idle; return; }
            float now = (float)_simTime;

            if (_swingPending[row])
            {
                if (now - _lastAttack[row] < AttackWindup) return;
                _swingPending[row] = false;
                if (!InAttackReach(row, target)) return;    // they moved out of reach mid-swing
                _attacks.Add(new ZombieAttackEvent
                {
                    Id = IdOf(row),
                    PlayerIndex = target,
                    Damage = _kinds[_kind[row]].AttackDamage,
                });
                return;
            }
            if (now - _lastAttack[row] > AttackInterval)
            {
                _lastAttack[row] = now;
                _swingPending[row] = true;
            }
        }

        bool NeedsPath(int row)
        {
            if (_queued[row]) return false;
            if (_corridorLen[row] == 0) return true;
            if (_repath[row] >= RepathInterval) return true;
            // The target walked off: the corridor still leads somewhere, just not to them any more.
            int last = row * MaxWaypoints + _corridorLen[row] - 1;
            return (_corridor[last] - _dest[row]).sqrMagnitude > DestMovedTolerance * DestMovedTolerance;
        }

        void Enqueue(int row)
        {
            if (_queued[row] || _queueCount >= _pathQueue.Length) return;
            _pathQueue[_queueTail] = row;
            _queueTail = (_queueTail + 1) % _pathQueue.Length;
            _queueCount++;
            _queued[row] = true;
        }

        void DrainPathQueue(ref ZombieStepStats stats)
        {
            int budget = Math.Max(0, PathQueriesPerTick);
            while (budget-- > 0 && _queueCount > 0)
            {
                int row = _pathQueue[_queueHead];
                _queueHead = (_queueHead + 1) % _pathQueue.Length;
                _queueCount--;
                if (row >= _count) continue;             // the row went away while it waited
                _queued[row] = false;

                int n = Nav.QueryPath(_pos[row], _dest[row], _scratchCorridor);
                stats.PathQueries++;
                TotalPathQueries++;
                if (n > MaxWaypoints) n = MaxWaypoints;
                for (int w = 0; w < n; w++) _corridor[row * MaxWaypoints + w] = _scratchCorridor[w];
                _corridorLen[row] = (byte)n;
                _corridorAt[row] = 0;
                _repath[row] = 0f;
            }
        }

        /// <summary>Follow the corridor for one step. Returns true if the zombie actually moved.</summary>
        bool Advance(int row, float step)
        {
            float speed = _kinds[_kind[row]].MoveSpeed;
            float remaining = speed * step;
            Vector3 p = _pos[row];
            bool moved = false;

            while (remaining > 1e-4f && _corridorAt[row] < _corridorLen[row])
            {
                Vector3 wp = _corridor[row * MaxWaypoints + _corridorAt[row]];
                Vector3 to = new Vector3(wp.x - p.x, 0f, wp.z - p.z);
                float d = to.magnitude;
                if (d <= WaypointReached) { _corridorAt[row]++; continue; }

                Vector3 dir = to / d;
                float travel = Math.Min(remaining, d);
                p += dir * travel;
                remaining -= travel;
                _face[row] = dir;
                moved = true;
            }

            if (_corridorAt[row] >= _corridorLen[row]) _corridorLen[row] = 0;   // corridor spent; refetch next time
            if (moved) _pos[row] = Nav.SnapToSurface(p);
            return moved;
        }

        // --- combat surface --------------------------------------------------------------------------

        /// <summary>Swings that connected this step, for the caller to resolve against its players.</summary>
        public ReadOnlySpan<ZombieAttackEvent> Attacks => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_attacks);
        /// <summary>Zombies that died this step, for the caller to turn into corpses.</summary>
        public ReadOnlySpan<ZombieDeathEvent> Deaths => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_deaths);
        public double SimTime => _simTime;


        /// <summary>
        /// Shoot. Candidates come from the spatial grid and are tested analytically against the kind's
        /// capsule -- no collider, no physics query, no requirement that the zombie be near enough to
        /// have a body or a rig. This is the whole reason bodies could become scarce.
        /// </summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out ZombieHit hit)
        {
            hit = default;
            if (_count == 0 || maxDistance <= 0f) return false;
            Vector3 dir = direction.normalized;
            if (dir.sqrMagnitude < 1e-6f) return false;

            // Rows store FEET, but a zombie is a capsule standing on them, so a chest-height shot passes
            // ~1.4 m ABOVE the stored position. Padding by radius alone nominated nobody and every shot
            // missed. Reach a whole body's height plus its width -- conservative, and every candidate is
            // exact-tested against the capsule immediately afterwards anyway.
            float pad = MathF.Max(0.01f, _kinds.MaxRadius + _kinds.MaxHeight);
            if (_queryScratch.Length < _count) _queryScratch = new int[Math.Max(_count, 256)];
            int n = _spatial.QuerySegment(origin, origin + dir * maxDistance, pad, _queryScratch);

            float bestT = float.MaxValue; int bestRow = -1;
            for (int i = 0; i < n; i++)
            {
                int row = _queryScratch[i];
                if ((ZombieState)_state[row] == ZombieState.Dead) continue;   // corpses are the ragdoll's problem
                var kind = _kinds[_kind[row]];
                float t = ZombieCombat.RayCapsule(origin, dir, _pos[row], kind.Radius, kind.Height);
                if (t < 0f || t > maxDistance || t >= bestT) continue;
                bestT = t; bestRow = row;
            }
            if (bestRow < 0) return false;

            Vector3 point = origin + dir * bestT;
            hit = new ZombieHit
            {
                Id = IdOf(bestRow),
                Row = bestRow,
                Point = point,
                Distance = bestT,
                Limb = ZombieCombat.LimbAt(point, _pos[bestRow], _kinds[_kind[bestRow]].Height),
                Hit = true,
            };
            return true;
        }

        /// <summary>Hurt a zombie. Returns true if this killed it. The caller owns limb multipliers --
        /// the gun already knows them, and inventing a second set here would be a guess.</summary>
        public bool Damage(ZombieId id, float amount, ZombieLimb limb = ZombieLimb.Spine)
        {
            if (!TryGetRow(id, out int row)) return false;
            if ((ZombieState)_state[row] == ZombieState.Dead) return false;
            _health[row] -= amount;
            if (_health[row] > 0f) return false;

            _health[row] = 0f;
            _state[row] = (byte)ZombieState.Dead;
            _diedAt[row] = (float)_simTime;
            _killedBy[row] = (byte)limb;
            _corridorLen[row] = 0;
            _swingPending[row] = false;
            _deaths.Add(new ZombieDeathEvent
            {
                Id = id,
                Position = _pos[row],
                Facing = _face[row],
                KillingLimb = limb,
            });
            return true;
        }

        /// <summary>A noise at <paramref name="pos"/> carrying <paramref name="loudness"/> metres. Only
        /// zombies whose ears reach it and for whom it carries that far hear it; each keeps the most
        /// salient one (loud and close beats loud and far). One grid query, not a pass over the level.</summary>
        public int Hear(Vector3 pos, float loudness)
        {
            if (_count == 0 || loudness <= 0f) return 0;
            if (_queryScratch.Length < _count) _queryScratch = new int[Math.Max(_count, 256)];
            int n = _spatial.QuerySphere(pos, loudness, _queryScratch);

            int heard = 0;
            for (int i = 0; i < n; i++)
            {
                int row = _queryScratch[i];
                if ((ZombieState)_state[row] == ZombieState.Dead) continue;
                float dist = (pos - _pos[row]).magnitude;
                if (dist > _kinds[_kind[row]].HearingRange) continue;   // outside its ears
                float salience = loudness - dist;                       // loud + close wins
                if (salience <= _heardSalience[row]) continue;
                _heardSalience[row] = salience;
                _heardPos[row] = pos;
                heard++;
            }
            return heard;
        }

        public ZombieState StateOf(int row) => (ZombieState)_state[row];
        public bool IsDead(int row) => (ZombieState)_state[row] == ZombieState.Dead;
        public Vector3 FacingOf(int row) => _face[row];
        public Vector3 DestinationOf(int row) => _dest[row];
        public int WaypointsRemaining(int row) => Math.Max(0, _corridorLen[row] - _corridorAt[row]);

        ZombieTier ClassifyTier(int row, int region)
        {
            // A zombie with something going on -- a noise it just heard, a chase or an investigation in
            // progress -- keeps thinking even where no player is standing. Without this, a gunshot next
            // to an AMBIENT zombie was silently discarded: the tier gate skipped it before it ever got
            // to react, so hearing only worked in regions that were already awake.
            var state = (ZombieState)_state[row];
            bool busy = _heardSalience[row] > 0f || state == ZombieState.Investigate ||
                        state == ZombieState.Pursue || state == ZombieState.Attack;

            if (!_regions.IsHot(region)) return busy ? ZombieTier.Far : ZombieTier.Ambient;
            if (_playerCount <= 0) return busy ? ZombieTier.Far : ZombieTier.Ambient;

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
            Array.Resize(ref _state, capacity);
            Array.Resize(ref _dest, capacity);
            Array.Resize(ref _face, capacity);
            Array.Resize(ref _corridor, capacity * MaxWaypoints);
            Array.Resize(ref _corridorLen, capacity);
            Array.Resize(ref _corridorAt, capacity);
            Array.Resize(ref _repath, capacity);
            Array.Resize(ref _queued, capacity);
            Array.Resize(ref _pathQueue, capacity);
            Array.Resize(ref _heardPos, capacity);
            Array.Resize(ref _heardSalience, capacity);
            Array.Resize(ref _huntSalience, capacity);
            Array.Resize(ref _lastSeen, capacity);
            Array.Resize(ref _hasLastSeen, capacity);
            Array.Resize(ref _lostSight, capacity);
            Array.Resize(ref _stateClock, capacity);
            Array.Resize(ref _lastAttack, capacity);
            Array.Resize(ref _swingPending, capacity);
            Array.Resize(ref _targetPlayer, capacity);
            Array.Resize(ref _diedAt, capacity);
            Array.Resize(ref _killedBy, capacity);
            DropQueue();   // the queue array just moved under the ring cursors
        }

        void DropQueue() { _queueHead = _queueTail = _queueCount = 0; for (int i = 0; i < _count; i++) _queued[i] = false; }
    }
}
