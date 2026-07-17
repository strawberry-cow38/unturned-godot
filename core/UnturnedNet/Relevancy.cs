using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Interest-management v1 (MP_PLAN §2.6, Phase 8): the POLICY that slots into the ViewPos hook that has
    /// been in every WriteFull/WriteDelta signature since Phase 2 -- no protocol or wire-format change. An
    /// entity is relevant to a client when it is inside the client's distance ring OR shares a relevancy
    /// CELL with the client (on PEI the cells are the 19 zombie nav pockets -- ZombieField already buckets
    /// zombies by pocket, so a whole town's horde stays visible while you are in that town even past the
    /// ring). A null policy (the core default) is AllRelevant: byte-identical to the pre-Phase-8 behavior.
    /// </summary>
    public sealed class InterestPolicy
    {
        /// <summary>Always-relevant radius around the client's view position, meters. &lt;= 0 disables the
        /// ring (then only cells grant relevancy).</summary>
        public float RingRadius = 128f;

        /// <summary>Optional relevancy-cell lookup: position -&gt; cell id, or -1 for "no cell" (open
        /// country). The game supplies the 19 PEI nav pockets; L0 tests supply a grid. Null = rings only.</summary>
        public Func<Vector3, int> CellOf;

        public bool IsRelevant(Vector3 viewPos, Vector3 entityPos)
        {
            if (RingRadius > 0f)
            {
                float dx = entityPos.x - viewPos.x, dy = entityPos.y - viewPos.y, dz = entityPos.z - viewPos.z;
                if (dx * dx + dy * dy + dz * dz <= RingRadius * RingRadius) return true;
            }
            if (CellOf != null)
            {
                int viewCell = CellOf(viewPos);
                if (viewCell >= 0 && viewCell == CellOf(entityPos)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Per-client relevancy bookkeeping for one replicated system, ACK-SAFE by construction: because the
    /// composer's baselines only ever advance on client acks, "keep sending until baselineTick passes the
    /// transition tick" survives arbitrary snapshot loss (the same argument the dirty-ring delta scheme
    /// rests on). An entity ENTERING relevancy is written into every delta until an ack proves the client
    /// saw it; an entity EXITING relevancy rides the delta's existing removals list (replicas already handle
    /// removal -- relevancy-exit is indistinguishable from despawn on the wire) until acked, then the entry
    /// is dropped. WriteFull resets the client's set to exactly what that full contained.
    /// </summary>
    public sealed class RelevancyTracker
    {
        sealed class EntityState
        {
            public long EnteredTick;   // when the entity became relevant to this client
            public long ExitedTick;    // 0 = currently relevant; > 0 = removal pending until acked past it
        }

        readonly Dictionary<ushort, Dictionary<uint, EntityState>> _clients =
            new Dictionary<ushort, Dictionary<uint, EntityState>>();

        Dictionary<uint, EntityState> For(ushort clientPlayerId)
        {
            if (!_clients.TryGetValue(clientPlayerId, out var map))
                _clients[clientPlayerId] = map = new Dictionary<uint, EntityState>();
            return map;
        }

        /// <summary>Delta-path decision for one live entity. Returns true if the entity must be WRITTEN
        /// into this delta (newly/unackedly entered, or dirty since the baseline while relevant).</summary>
        public bool ShouldWrite(ushort clientPlayerId, uint netId, bool relevant,
                                long entityChangedTick, long baselineTick, long serverTick)
        {
            var map = For(clientPlayerId);
            if (relevant)
            {
                if (!map.TryGetValue(netId, out var st)) map[netId] = st = new EntityState { EnteredTick = serverTick };
                else if (st.ExitedTick != 0) { st.ExitedTick = 0; st.EnteredTick = serverTick; }   // re-entered
                return st.EnteredTick > baselineTick || entityChangedTick > baselineTick;
            }
            if (map.TryGetValue(netId, out var gone) && gone.ExitedTick == 0)
                gone.ExitedTick = serverTick;   // exit starts pending; RemovalsFor emits it until acked
            return false;
        }

        /// <summary>Delta-path pending relevancy-exit removals (netIds the client still believes in).
        /// Entries whose exit the client has acked past are dropped here.</summary>
        public void CollectRemovals(ushort clientPlayerId, long baselineTick, List<uint> removals)
        {
            var map = For(clientPlayerId);
            List<uint> acked = null;
            foreach (var kv in map)
            {
                if (kv.Value.ExitedTick == 0) continue;
                if (kv.Value.ExitedTick > baselineTick) removals.Add(kv.Key);
                else (acked ??= new List<uint>()).Add(kv.Key);
            }
            if (acked != null) foreach (uint id in acked) map.Remove(id);
        }

        /// <summary>Full-snapshot path: the client's replica resets to exactly this set on apply.</summary>
        public void ResetFull(ushort clientPlayerId, List<uint> includedNetIds, long serverTick)
        {
            var map = For(clientPlayerId);
            map.Clear();
            foreach (uint id in includedNetIds) map[id] = new EntityState { EnteredTick = serverTick };
        }

        /// <summary>The entity left the WORLD (despawn): the system's own tombstone list carries the wire
        /// removal to every client, so per-client relevancy state just drops.</summary>
        public void ForgetEntity(uint netId)
        {
            foreach (var map in _clients.Values) map.Remove(netId);
        }

        /// <summary>Disconnect (or rejoin under a recycled playerId): no state may leak across sessions.</summary>
        public void ForgetClient(ushort clientPlayerId) => _clients.Remove(clientPlayerId);
    }
}
