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

        public int Count => _byId.Count;

        public void Add(NetId id, T entity) => _byId[id.Value] = entity;

        public bool Remove(NetId id) => _byId.Remove(id.Value);

        public bool TryGet(NetId id, out T entity) => _byId.TryGetValue(id.Value, out entity);

        public bool Contains(NetId id) => _byId.ContainsKey(id.Value);

        public IEnumerable<NetId> Ids
        {
            get
            {
                foreach (uint id in _byId.Keys) yield return new NetId(id);
            }
        }

        public void Clear() => _byId.Clear();
    }
}
