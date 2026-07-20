using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // Destructible props / rubble (retail ObjectAsset.Rubble + InteractableObjectRubble). PEI's placed
    // objects whose asset .dat carries `Rubble` != None are breakable: a health pool that server-side
    // combat whittles down, and at 0 the object vanishes (DESTROY mode -- every placed PEI destructible is
    // this mode) and respawns after Rubble_Reset seconds. This mirrors the ResourceReplication (SystemId 12)
    // tree alive-bitmap EXACTLY: authored map data loaded identically on every peer, so the deterministic
    // LOAD-ORDER index (WorldBuilder assigns it in placements.txt scan order, holiday-stable) is the implicit
    // wire id -- no NetIds, no transforms on the wire. One alive-bit per destructible; the server owns the
    // health + respawn clock (ServerDestructibles), the bitmap only carries the alive result.

    public struct ObjectDestroyedEvent
    {
        public ushort Index;
        public void Write(NetPakWriter w) => w.WriteUInt16(Index);
        public static bool TryRead(NetPakReader r, out ObjectDestroyedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt16(out ushort index)) return false;
            evt = new ObjectDestroyedEvent { Index = index };
            return true;
        }
    }

    public struct ObjectRestoredEvent
    {
        public ushort Index;
        public void Write(NetPakWriter w) => w.WriteUInt16(Index);
        public static bool TryRead(NetPakReader r, out ObjectRestoredEvent evt)
        {
            evt = default;
            if (!r.ReadUInt16(out ushort index)) return false;
            evt = new ObjectRestoredEvent { Index = index };
            return true;
        }
    }

    /// <summary>
    /// Destructible-object alive-bitmap as an IReplicatedSystem (SystemId 16). One bit per placed
    /// destructible in the deterministic index space; WriteFull carries the whole bitmap (the join path),
    /// WriteDelta carries (index, alive) changes, and Destroyed/Restored events give clients immediacy for
    /// the break fx. Byte-for-byte the ResourceReplication wire shape (both are "authored index -> alive").
    /// </summary>
    public sealed class DestructibleReplication : IReplicatedSystem
    {
        bool[] _alive = System.Array.Empty<bool>();
        long[] _changedTick = System.Array.Empty<long>();

        public byte SystemId => ReplicationIds.SystemDestructibles;

        public int Count => _alive.Length;

        /// <summary>Bumped on every applied change -- node views poll this instead of diffing the bitmap.</summary>
        public long Version { get; private set; }

        public bool IsAlive(int index) => index >= 0 && index < _alive.Length && _alive[index];

        public int AliveCount
        {
            get { int n = 0; for (int i = 0; i < _alive.Length; i++) if (_alive[i]) n++; return n; }
        }

        // ---- server side ----

        static long Stamp(long tick) => tick + 1;

        /// <summary>Size the bitmap to the world's destructible count, all alive (server boot).</summary>
        public void ServerInit(int count, long tick)
        {
            _alive = new bool[count];
            _changedTick = new long[count];
            for (int i = 0; i < count; i++) { _alive[i] = true; _changedTick[i] = Stamp(tick); }
            Version++;
        }

        public bool ServerSetAlive(int index, bool alive, long tick)
        {
            if (index < 0 || index >= _alive.Length || _alive[index] == alive) return false;
            _alive[index] = alive;
            _changedTick[index] = Stamp(tick);
            Version++;
            return true;
        }

        // ---- client-side event application (idempotent) ----

        public void ApplyDestroyed(in ObjectDestroyedEvent evt, long tick) => ServerSetAlive(evt.Index, false, tick);
        public void ApplyRestored(in ObjectRestoredEvent evt, long tick) => ServerSetAlive(evt.Index, true, tick);

        // ---- IReplicatedSystem ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            w.WriteUInt16((ushort)_alive.Length);
            for (int i = 0; i < _alive.Length; i++) w.WriteBit(_alive[i]);
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            var changed = new List<int>();
            for (int i = 0; i < _alive.Length; i++)
                if (_changedTick[i] > baselineTick) changed.Add(i);
            w.WriteUInt16((ushort)changed.Count);
            foreach (int i in changed)
            {
                w.WriteUInt16((ushort)i);
                w.WriteBit(_alive[i]);
            }
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (full)
            {
                if (!r.ReadUInt16(out ushort count)) return;
                if (_alive.Length != count)
                {
                    _alive = new bool[count];
                    _changedTick = new long[count];
                }
                for (int i = 0; i < count; i++)
                {
                    if (!r.ReadBit(out bool alive)) return;
                    _alive[i] = alive;
                }
                Version++;
                return;
            }
            if (!r.ReadUInt16(out ushort changedCount)) return;
            for (int i = 0; i < changedCount; i++)
            {
                if (!r.ReadUInt16(out ushort index)) return;
                if (!r.ReadBit(out bool alive)) return;
                if (index < _alive.Length) _alive[index] = alive;
            }
            if (changedCount > 0) Version++;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            h = NetHash.MixUInt32(h, (uint)_alive.Length);
            for (int i = 0; i < _alive.Length; i++)
                if (!_alive[i]) h = NetHash.MixUInt32(h, (uint)i);   // dead set defines the state; all-alive folds fast
            return h;
        }
    }
}
