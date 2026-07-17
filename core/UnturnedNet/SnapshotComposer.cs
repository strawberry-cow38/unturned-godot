using System;
using System.Collections.Generic;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public sealed class SnapshotComposerDiagnostics
    {
        public long FullSnapshotsComposed;
        public long DeltaSnapshotsComposed;
        /// <summary>A system block that would have pushed the datagram past its byte budget was emitted as
        /// an EMPTY (byteLen 0) block instead -- the framing stays valid and every other system still
        /// applies. Phase 8 semantics: a skipped block LOSES NOTHING -- the composer pins that system's
        /// per-client baseline at the last tick the client provably received it, so the next included delta
        /// carries everything the skips withheld, and the priority accumulator guarantees the skipped
        /// system is tried first on the next compose. Non-zero under sustained overload only.</summary>
        public long OversizedBlocksSkipped;
        public long BytesComposed;              // total snapshot payload bytes composed (net-diag rollup)
        public long SyncCheckBlocksWritten;     // desync-detection hash blocks appended (0 unless enabled)
        public long FutureBaselineAcksRejected; // review L1: acks claiming a tick the server hasn't reached
    }

    /// <summary>
    /// Server side of the snapshot plane (MP_PLAN §2.3/§2.4). Composes one per-client datagram payload:
    ///   serverTick:32 + baselineTick:32 (0 = full) + repeated system blocks
    ///     (systemId:8 + byteLen:16 + AlignToByte() + payload)
    /// Byte-aligned, length-prefixed blocks are what let a client skip a system it doesn't know (forward
    /// compat, see SnapshotApplier) and let one corrupt block fail in isolation instead of the whole
    /// datagram.
    ///
    /// Per-client baseline: the server remembers each client's last-ACKED applied tick (fed by
    /// SetClientBaseline, normally wired to the reserved AckCommandId piggyback via RegisterAck). A client
    /// with no baseline yet (0, e.g. just joined) or one older than DirtyRingDepthTicks gets a full
    /// snapshot; otherwise a delta against that baseline. There is no per-client world copy -- each system
    /// just re-walks its own dirty state for whichever baseline this call needs.
    ///
    /// Phase 8 byte budget (MP_PLAN §2.6 "priority accumulators under a per-client byte budget"): blocks
    /// are composed in starvation-priority order (ties = registration order, so a never-skipped world is
    /// byte-identical to the pre-budget composer); a block that would overflow the budget is deferred as an
    /// empty block and its accumulator grows. Because a delta block may now be withheld from a snapshot the
    /// client acks, each system's delta composes against a PER-SYSTEM baseline: the newest acked tick whose
    /// snapshot actually carried that system's block. All of it is composer-side policy -- the wire format
    /// is unchanged, and the client needs no knowledge of any of this.
    /// </summary>
    public sealed class SnapshotComposer
    {
        /// <summary>Reserved CommandRegistry id for the client's snapshot-ack piggyback (MP_PLAN §2.4: "the
        /// client acks the applied snapshot tick back -- piggyback a tiny command/control"). Gameplay
        /// command ids must start at 1.</summary>
        public const byte AckCommandId = 0;

        /// <summary>Alias of NetQuantization.DirtyRingDepthTicks kept on the composer for discoverability.</summary>
        public const long DirtyRingDepthTicks = NetQuantization.DirtyRingDepthTicks;

        /// <summary>Default per-snapshot byte budget for Compose calls that don't pass an explicit
        /// maxBytes (the 25 Hz unreliable stream). The join path always passes its own reliable-channel
        /// budget explicitly, so lowering this never squeezes join snapshots.</summary>
        public int BudgetBytes = NetProtocol.MaxUnreliablePayload;

        /// <summary>Newest server tick, supplied by the host (NetWorldServer wires it to the session tick).
        /// When set, SetClientBaseline rejects acks claiming a FUTURE tick -- review L1: a client acking
        /// 0xFFFFFFFF would otherwise pin a bogus baseline and starve its own deltas forever. Null (unit
        /// tests that drive Compose directly) = no clamp.</summary>
        public Func<long> CurrentTick;

        sealed class ClientState
        {
            public long AckedTick;
            /// <summary>Per system: -1 = never skipped for this client, mirror AckedTick (the pre-Phase-8
            /// behavior, bit for bit). >= 0 = pinned at the newest ACKED tick whose snapshot actually
            /// carried this system's block; advanced only by acks whose recorded mask includes the system.</summary>
            public long[] SystemBaseline;
            /// <summary>Starvation accumulator: +1 per budget skip, reset to 0 on include; composed-first
            /// when highest.</summary>
            public float[] Priority;
            /// <summary>composed tick -> bitmask of systems whose block was actually written (not skipped).</summary>
            public readonly Dictionary<long, ulong> IncludedMask = new Dictionary<long, ulong>();

            public ClientState(int systemCount)
            {
                SystemBaseline = new long[systemCount];
                Priority = new float[systemCount];
                for (int i = 0; i < systemCount; i++) SystemBaseline[i] = -1;
            }
        }

        readonly List<IReplicatedSystem> _systems = new List<IReplicatedSystem>();
        readonly Dictionary<ushort, ClientState> _clients = new Dictionary<ushort, ClientState>();
        readonly int[] _orderScratch;
        readonly bool[] _oversizeWarnedOnce;   // one "this block can NEVER fit this budget" warn per system
        // Buffers sized far past the unreliable budget: blocks WRITE cleanly at any entity count, then the
        // per-block budget check below decides whether they fit the datagram (see OversizedBlocksSkipped).
        const int BufferBytes = 256 * 1024;
        readonly NetPakWriter _writer = new NetPakWriter { buffer = new byte[BufferBytes] };
        readonly NetPakWriter _scratch = new NetPakWriter { buffer = new byte[BufferBytes] };

        public SnapshotComposerDiagnostics Diag { get; } = new SnapshotComposerDiagnostics();

        public SnapshotComposer(IEnumerable<IReplicatedSystem> systems)
        {
            var seen = new HashSet<byte>();
            foreach (var s in systems)
            {
                if (!seen.Add(s.SystemId))
                    throw new InvalidOperationException($"SnapshotComposer: duplicate SystemId {s.SystemId}");
                _systems.Add(s);
            }
            if (_systems.Count > 64)
                throw new InvalidOperationException("SnapshotComposer: included-mask tracking supports up to 64 systems");
            _orderScratch = new int[_systems.Count];
            _oversizeWarnedOnce = new bool[_systems.Count];
        }

        // ---- desync detection (hardening pass, Part C) ----
        int _syncCheckIntervalTicks;   // 0 = off (default): the composed bytes are identical to pre-hardening
        int[] _syncCheckIdx;           // indices into _systems of the checked (globally-mirrored) systems
        long _syncHashCacheTick = -1;  // hashes are per-tick, not per-client -- compute once, reuse per peer
        ulong[] _syncHashCache;

        /// <summary>Enable the rolling state-hash block (desync detection): every intervalTicks the
        /// snapshot gains one extra block (ReplicationIds.SystemSyncCheck) carrying each listed system's
        /// StateHash() at this tick; SnapshotApplier compares them against the replicas after applying.
        /// Only list systems every client mirrors COMPLETELY -- owner-only (skills/inventory) and
        /// relevancy-filtered (zombies/world items) systems would false-alarm by design. The block is
        /// additive: a client that doesn't know the id skips it (forward compat), and it is withheld
        /// whenever any checked system's block was budget-skipped in the same compose (the replica is
        /// intentionally stale then, not desynced).</summary>
        public void EnableSyncCheck(int intervalTicks, params byte[] systemIds)
        {
            if (intervalTicks <= 0) throw new ArgumentOutOfRangeException(nameof(intervalTicks));
            var idx = new List<int>();
            foreach (byte id in systemIds)
            {
                int found = _systems.FindIndex(s => s.SystemId == id);
                if (found < 0) throw new InvalidOperationException($"EnableSyncCheck: SystemId {id} is not registered on this composer");
                idx.Add(found);
            }
            _syncCheckIntervalTicks = intervalTicks;
            _syncCheckIdx = idx.ToArray();
            _syncHashCache = new ulong[idx.Count];
            _syncHashCacheTick = -1;
        }

        void WriteSyncCheckBlock(long serverTick, ulong includedMask, int maxBytes)
        {
            if (_syncCheckIntervalTicks <= 0 || serverTick % _syncCheckIntervalTicks != 0) return;
            foreach (int idx in _syncCheckIdx)
                if ((includedMask & (1UL << idx)) == 0) return;   // a checked block was withheld: replica is legitimately stale
            if (_syncHashCacheTick != serverTick)
            {
                for (int i = 0; i < _syncCheckIdx.Length; i++) _syncHashCache[i] = _systems[_syncCheckIdx[i]].StateHash();
                _syncHashCacheTick = serverTick;
            }
            int blockLen = 1 + _syncCheckIdx.Length * 9;   // count:8 + per entry systemId:8 + hash:64
            if (_writer.writeByteIndex + 4 + blockLen > maxBytes) return;   // over budget this snapshot: just skip the check
            _writer.WriteUInt8(ReplicationIds.SystemSyncCheck);
            _writer.WriteUInt16((ushort)blockLen);
            _writer.AlignToByte();
            _writer.WriteUInt8((byte)_syncCheckIdx.Length);
            for (int i = 0; i < _syncCheckIdx.Length; i++)
            {
                _writer.WriteUInt8(_systems[_syncCheckIdx[i]].SystemId);
                _writer.WriteUInt64(_syncHashCache[i]);
            }
            Diag.SyncCheckBlocksWritten++;
        }

        /// <summary>Wires this composer's ack piggyback into a CommandRegistry under AckCommandId. Kept as
        /// an explicit opt-in step (rather than baked into the constructor) so unit tests that only need
        /// Compose() directly can skip building a registry at all.</summary>
        public void RegisterAck(CommandRegistry commands)
        {
            commands.Register(AckCommandId, (reader, senderPlayerId) =>
            {
                if (reader.ReadUInt32(out uint tick)) SetClientBaseline(senderPlayerId, tick);
            });
        }

        public long GetClientBaseline(ushort clientPlayerId)
            => _clients.TryGetValue(clientPlayerId, out var cs) ? cs.AckedTick : 0;

        /// <summary>Record a client's newly-acked applied tick. Newest-wins: an out-of-order/duplicate ack
        /// for an older tick must never regress the baseline backwards. Pinned per-system baselines advance
        /// here too -- but only when the acked snapshot's recorded mask proves that system's block was in it.</summary>
        public void SetClientBaseline(ushort clientPlayerId, long baselineTick)
        {
            // review L1: never accept an ack for a tick we haven't composed yet -- the delta math would
            // treat the huge baseline as "nothing changed since" and the client would starve itself.
            if (CurrentTick != null && baselineTick > CurrentTick())
            {
                Diag.FutureBaselineAcksRejected++;
                return;
            }
            var cs = StateFor(clientPlayerId);
            if (baselineTick <= cs.AckedTick) return;
            cs.AckedTick = baselineTick;
            if (cs.IncludedMask.TryGetValue(baselineTick, out ulong mask))
            {
                for (int i = 0; i < _systems.Count; i++)
                    if (cs.SystemBaseline[i] >= 0 && (mask & (1UL << i)) != 0 && baselineTick > cs.SystemBaseline[i])
                        cs.SystemBaseline[i] = baselineTick;
            }
        }

        /// <summary>Forget a client entirely (disconnect) so a reused playerId doesn't inherit a stale baseline.</summary>
        public void ForgetClient(ushort clientPlayerId) => _clients.Remove(clientPlayerId);

        /// <summary>True if the next Compose() for this client will be a full snapshot rather than a delta.
        /// A pinned system starved past the dirty-ring depth also forces a full -- delta correctness
        /// (tombstone pruning) only holds within the ring.</summary>
        public bool WillSendFull(ushort clientPlayerId, long serverTick)
        {
            var cs = StateFor(clientPlayerId);
            if (cs.AckedTick == 0 || (serverTick - cs.AckedTick) > DirtyRingDepthTicks) return true;
            for (int i = 0; i < _systems.Count; i++)
                if (cs.SystemBaseline[i] >= 0 && (serverTick - cs.SystemBaseline[i]) > DirtyRingDepthTicks) return true;
            return false;
        }

        /// <summary>Compose one snapshot payload for one client, right-sized and ready for
        /// NetSession.SendUnreliableSequenced. maxBytes is the datagram budget (default = BudgetBytes): a
        /// system block that would exceed it is emitted EMPTY (byteLen 0 -- readers no-op on it) so the
        /// framing never corrupts; the join path passes a reliable-channel-sized budget so the full world
        /// always fits there (§2.2: fragmentation is safe on ReliableOrdered).</summary>
        public byte[] Compose(long serverTick, ushort clientPlayerId, Vector3 viewPos, int maxBytes = 0)
        {
            if (maxBytes <= 0) maxBytes = BudgetBytes;
            var cs = StateFor(clientPlayerId);
            long baseline = cs.AckedTick;
            bool full = WillSendFull(clientPlayerId, serverTick);
            var ctx = new ReplicationContext(serverTick, clientPlayerId, viewPos);

            _writer.Reset();
            _writer.WriteUInt32((uint)serverTick);
            _writer.WriteUInt32(full ? 0u : (uint)baseline);

            // starvation-priority order; all-zero priorities = registration order = the pre-budget bytes
            for (int i = 0; i < _systems.Count; i++) _orderScratch[i] = i;
            Array.Sort(_orderScratch, (a, b) =>
            {
                int byPriority = cs.Priority[b].CompareTo(cs.Priority[a]);
                return byPriority != 0 ? byPriority : a.CompareTo(b);
            });

            ulong includedMask = 0;
            foreach (int idx in _orderScratch)
            {
                var system = _systems[idx];
                _scratch.Reset();
                if (full) system.WriteFull(_scratch, in ctx);
                else system.WriteDelta(_scratch, in ctx, cs.SystemBaseline[idx] >= 0 ? cs.SystemBaseline[idx] : baseline);
                _scratch.Flush();

                // block header = id:8 + len:16 + align ≈ 4 bytes; skip-oversized keeps the datagram valid
                int blockLen = _scratch.writeByteIndex;
                if (_writer.writeByteIndex + 4 + blockLen > maxBytes)
                {
                    // Hardening (DRIVE_DRIVER_VIEW_ROOTCAUSE.md §5): a block too big for even an EMPTY
                    // datagram of this budget can never be delivered at this size -- only the reliable
                    // recovery/join full can carry it (NetWorldServer routes those). This condition used
                    // to be silent, which is how the driver-freeze wedge went unseen; warn once per system.
                    if (NetLog.Enabled && !_oversizeWarnedOnce[idx] && 8 + 4 + blockLen > maxBytes)
                    {
                        _oversizeWarnedOnce[idx] = true;
                        NetLog.Warn($"snapshot system {system.SystemId} block ({blockLen} B) exceeds the {maxBytes} B budget outright -- deliverable only via a reliable full");
                    }
                    blockLen = 0;
                    Diag.OversizedBlocksSkipped++;
                    cs.Priority[idx] += 1f;
                    // first-ever skip pins the baseline at the last fully-trusted tick; from here on only
                    // acks of snapshots that actually carried this block advance it
                    if (cs.SystemBaseline[idx] < 0) cs.SystemBaseline[idx] = cs.AckedTick;
                }
                else
                {
                    cs.Priority[idx] = 0f;
                    includedMask |= 1UL << idx;
                }

                _writer.WriteUInt8(system.SystemId);
                _writer.WriteUInt16((ushort)blockLen);
                _writer.AlignToByte();
                if (blockLen > 0) _writer.WriteBytes(_scratch.buffer, 0, blockLen);
            }
            WriteSyncCheckBlock(serverTick, includedMask, maxBytes);   // desync detection; no-op unless enabled
            _writer.Flush();

            cs.IncludedMask[serverTick] = includedMask;
            PruneMaskRecords(cs, serverTick);

            if (full) Diag.FullSnapshotsComposed++; else Diag.DeltaSnapshotsComposed++;
            Diag.BytesComposed += _writer.writeByteIndex;

            var result = new byte[_writer.writeByteIndex];
            Buffer.BlockCopy(_writer.buffer, 0, result, 0, _writer.writeByteIndex);
            return result;
        }

        ClientState StateFor(ushort clientPlayerId)
        {
            if (!_clients.TryGetValue(clientPlayerId, out var cs))
                _clients[clientPlayerId] = cs = new ClientState(_systems.Count);
            return cs;
        }

        static void PruneMaskRecords(ClientState cs, long serverTick)
        {
            List<long> stale = null;
            foreach (long tick in cs.IncludedMask.Keys)
                if (serverTick - tick > DirtyRingDepthTicks * 2)
                    (stale ??= new List<long>()).Add(tick);
            if (stale != null) foreach (long tick in stale) cs.IncludedMask.Remove(tick);
        }
    }
}
