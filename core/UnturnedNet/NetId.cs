using System;
using System.Collections.Generic;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// MP_PLAN §2.6 / §5 item 2: a session-scoped entity id, minted by the server, monotonically increasing,
    /// never persisted (a future save system uses its own stable keys). 0 is reserved for "invalid/none" and
    /// is never minted. One flat id space is shared by every replicated entity across every system; the
    /// owning system gives the value meaning (e.g. sub-addressing a deployable's ports is (NetId, portIndex),
    /// not a second id space).
    /// </summary>
    public readonly struct NetId : IEquatable<NetId>
    {
        public static readonly NetId Invalid = new NetId(0);

        public readonly uint Value;

        public NetId(uint value) => Value = value;

        public bool IsValid => Value != 0;

        public bool Equals(NetId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is NetId other && Equals(other);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => "NetId(" + Value + ")";

        public static bool operator ==(NetId a, NetId b) => a.Value == b.Value;
        public static bool operator !=(NetId a, NetId b) => a.Value != b.Value;
    }

    /// <summary>
    /// The server-side half of §2.6: mints fresh NetIds, monotonically, starting at 1. One minter is shared
    /// by every system that needs a NetId (the id space is flat and global) -- systems that just need to
    /// track already-minted ids use NetEntityRegistry&lt;T&gt; below instead.
    /// </summary>
    public sealed class NetIdMinter
    {
        uint _next = 1;

        public NetId Mint() => new NetId(_next++);

        /// <summary>Number of ids minted so far (tests/diagnostics only).</summary>
        public uint MintedCount => _next - 1;
    }

    /// <summary>
    /// A NetId -> entity map. Server-side this is "track what I minted"; client-side this is "look up the
    /// replica for a NetId seen in a snapshot/event". Deliberately does not mint (minting is one shared
    /// NetIdMinter per §2.6) so multiple systems can't accidentally hand out colliding ids.
    /// </summary>
    public sealed class NetEntityRegistry<T>
    {
        readonly Dictionary<uint, T> _byId = new Dictionary<uint, T>();

        // ASCENDING-ID CACHE. The replication layer asks for a sorted id list several times per tick per
        // registry, and rebuilding it was ~50% of the entire game's allocations in an ETW capture -- a fresh
        // List<uint> grown one id at a time, then sorted, for a set that usually has not changed since last
        // tick. The cache lives HERE rather than in the four Replication classes on purpose: Add/Remove/Clear
        // are the only ways the key set can move, so invalidation cannot be forgotten at a call site. That
        // matters more than usual because this order IS THE WIRE -- WriteFull writes the count and then the
        // entities in this order, and WriteDelta walks the same order to build changed/removed -- so a stale
        // list is not a slow frame, it is a desync.
        //
        // COPY-ON-WRITE, never cleared in place: a rebuild allocates a NEW list and leaves the old one intact.
        // ContainerReplication.All is an iterator that yields to its consumer mid-walk, so a caller can still
        // be enumerating the previous list when an entity spawns; recycling one list would corrupt that walk.
        List<uint> _sorted;
        bool _sortedDirty = true;

        public int Count => _byId.Count;

        public void Add(NetId id, T entity)
        {
            if (!_byId.ContainsKey(id.Value)) _sortedDirty = true;   // an overwrite leaves the key set alone
            _byId[id.Value] = entity;
        }

        public bool Remove(NetId id)
        {
            bool removed = _byId.Remove(id.Value);
            if (removed) _sortedDirty = true;
            return removed;
        }

        /// <summary>Every id, ascending. Shared and MUST NOT be mutated by the caller -- take a copy to filter.
        /// Rebuilt only when the key set actually changed.</summary>
        public List<uint> SortedIdValues()
        {
            if (_sortedDirty || _sorted == null)
            {
                var fresh = new List<uint>(_byId.Count);
                foreach (uint id in _byId.Keys) fresh.Add(id);
                fresh.Sort();
                _sorted = fresh;
                _sortedDirty = false;
            }
            return _sorted;
        }

        public bool TryGet(NetId id, out T entity) => _byId.TryGetValue(id.Value, out entity);

        public bool Contains(NetId id) => _byId.ContainsKey(id.Value);

        public IEnumerable<NetId> Ids
        {
            get
            {
                foreach (uint id in _byId.Keys) yield return new NetId(id);
            }
        }

        public void Clear() { _byId.Clear(); _sortedDirty = true; }
    }
}
