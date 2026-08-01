using System.Collections.Generic;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Bookkeeping every <see cref="IReplicatedSystem"/> needs and each one used to carry its own copy of.
    /// Ten byte-identical `PruneTombstones` and nine `SortedIds` bodies lived across the replication files
    /// before this; see docs/DUPLICATE_AUDIT.md 1.3 / 1.4.
    ///
    /// Both are load-bearing rather than incidental:
    ///   - the tombstone depth MUST match <see cref="NetQuantization.DirtyRingDepthTicks"/>, because a
    ///     removal dropped earlier than a client's baseline can reach is a removal that client never sees;
    ///   - <see cref="SortedIds{T}"/> exists because <c>NetEntityRegistry.Ids</c> is dictionary order, and
    ///     <c>StateHash</c> is order-dependent (see NetHash) -- an unsorted walk desyncs server and client
    ///     on nothing but insertion history.
    /// Keeping one copy of each means those two invariants have one place to be wrong.
    /// </summary>
    internal static class ReplicationUtil
    {
        /// <summary>Drop removal tombstones older than the dirty-ring depth. Generic in the key so the
        /// uint-keyed (NetId) and ushort-keyed (playerId) systems share it.</summary>
        public static void PruneTombstones<TKey>(Dictionary<TKey, long> tombstones, long serverTick)
        {
            List<TKey> stale = null;
            foreach (var kv in tombstones)
                if (serverTick - kv.Value > NetQuantization.DirtyRingDepthTicks)
                    (stale ??= new List<TKey>()).Add(kv.Key);
            if (stale != null) foreach (TKey key in stale) tombstones.Remove(key);
        }

        /// <summary>Registry ids in ascending order — the deterministic walk every WriteFull/WriteDelta and
        /// every StateHash depends on.</summary>
        public static List<uint> SortedIds<T>(NetEntityRegistry<T> registry)
        {
            var ids = new List<uint>();
            foreach (var id in registry.Ids) ids.Add(id.Value);
            ids.Sort();
            return ids;
        }

        /// <summary>Ascending keys of a dictionary keyed by owner playerId — the ushort twin of
        /// <see cref="SortedIds{T}"/>, for the owner-keyed systems.</summary>
        public static List<ushort> SortedKeys<TValue>(Dictionary<ushort, TValue> map)
        {
            var keys = new List<ushort>(map.Count);
            foreach (var kv in map) keys.Add(kv.Key);
            keys.Sort();
            return keys;
        }
    }
}
