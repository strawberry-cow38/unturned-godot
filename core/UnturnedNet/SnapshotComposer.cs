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
        /// applies; that system's state simply waits (Phase 8's relevancy/byte-budget policies are the real
        /// fix for sustained oversize). Zero in healthy worlds.</summary>
        public long OversizedBlocksSkipped;
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
    /// </summary>
    public sealed class SnapshotComposer
    {
        /// <summary>Reserved CommandRegistry id for the client's snapshot-ack piggyback (MP_PLAN §2.4: "the
        /// client acks the applied snapshot tick back -- piggyback a tiny command/control"). Gameplay
        /// command ids must start at 1.</summary>
        public const byte AckCommandId = 0;

        /// <summary>Alias of NetQuantization.DirtyRingDepthTicks kept on the composer for discoverability.</summary>
        public const long DirtyRingDepthTicks = NetQuantization.DirtyRingDepthTicks;

        readonly List<IReplicatedSystem> _systems = new List<IReplicatedSystem>();
        readonly Dictionary<ushort, long> _clientBaselineTick = new Dictionary<ushort, long>();
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
            => _clientBaselineTick.TryGetValue(clientPlayerId, out long t) ? t : 0;

        /// <summary>Record a client's newly-acked applied tick. Newest-wins: an out-of-order/duplicate ack
        /// for an older tick must never regress the baseline backwards.</summary>
        public void SetClientBaseline(ushort clientPlayerId, long baselineTick)
        {
            if (baselineTick > GetClientBaseline(clientPlayerId))
                _clientBaselineTick[clientPlayerId] = baselineTick;
        }

        /// <summary>Forget a client entirely (disconnect) so a reused playerId doesn't inherit a stale baseline.</summary>
        public void ForgetClient(ushort clientPlayerId) => _clientBaselineTick.Remove(clientPlayerId);

        /// <summary>True if the next Compose() for this client will be a full snapshot rather than a delta.</summary>
        public bool WillSendFull(ushort clientPlayerId, long serverTick)
        {
            long baseline = GetClientBaseline(clientPlayerId);
            return baseline == 0 || (serverTick - baseline) > DirtyRingDepthTicks;
        }

        /// <summary>Compose one snapshot payload for one client, right-sized and ready for
        /// NetSession.SendUnreliableSequenced. maxBytes is the datagram budget: a system block that would
        /// exceed it is emitted EMPTY (byteLen 0 -- readers no-op on it) so the framing never corrupts; the
        /// join path passes a reliable-channel-sized budget so the full world always fits there (§2.2:
        /// fragmentation is safe on ReliableOrdered).</summary>
        public byte[] Compose(long serverTick, ushort clientPlayerId, Vector3 viewPos,
                              int maxBytes = NetProtocol.MaxUnreliablePayload)
        {
            long baseline = GetClientBaseline(clientPlayerId);
            bool full = WillSendFull(clientPlayerId, serverTick);
            var ctx = new ReplicationContext(serverTick, clientPlayerId, viewPos);

            _writer.Reset();
            _writer.WriteUInt32((uint)serverTick);
            _writer.WriteUInt32(full ? 0u : (uint)baseline);

            foreach (var system in _systems)
            {
                _scratch.Reset();
                if (full) system.WriteFull(_scratch, in ctx);
                else system.WriteDelta(_scratch, in ctx, baseline);
                _scratch.Flush();

                // block header = id:8 + len:16 + align ≈ 4 bytes; skip-oversized keeps the datagram valid
                int blockLen = _scratch.writeByteIndex;
                if (_writer.writeByteIndex + 4 + blockLen > maxBytes) { blockLen = 0; Diag.OversizedBlocksSkipped++; }

                _writer.WriteUInt8(system.SystemId);
                _writer.WriteUInt16((ushort)blockLen);
                _writer.AlignToByte();
                if (blockLen > 0) _writer.WriteBytes(_scratch.buffer, 0, blockLen);
            }
            _writer.Flush();

            if (full) Diag.FullSnapshotsComposed++; else Diag.DeltaSnapshotsComposed++;

            var result = new byte[_writer.writeByteIndex];
            Buffer.BlockCopy(_writer.buffer, 0, result, 0, _writer.writeByteIndex);
            return result;
        }
    }
}
