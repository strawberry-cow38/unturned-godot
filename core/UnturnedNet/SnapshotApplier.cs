using System;
using System.Collections.Generic;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    public sealed class SnapshotApplierDiagnostics
    {
        public long SnapshotsApplied;
        public long FullSnapshotsApplied;
        public long DeltaSnapshotsApplied;
        public long UnknownSystemBlocksSkipped; // forward-compat proof: a systemId this client build doesn't know
        public long TruncatedSnapshotsDropped;  // the header or a block's declared length ran past the buffer
    }

    /// <summary>
    /// Client side of the snapshot plane (MP_PLAN §2.3/§2.4). Parses one datagram payload produced by
    /// SnapshotComposer and routes each system block to the matching registered IReplicatedSystem. Each
    /// block's exact byteLen is extracted into its own buffer before being handed to that system's
    /// ReadSnapshot -- so a system that reads too few or too many bits only corrupts its own block, never
    /// the rest of the datagram, and an unrecognized systemId is skipped the same way (the forward-compat
    /// guarantee: a client can ignore a system it doesn't build with).
    /// </summary>
    public sealed class SnapshotApplier
    {
        readonly Dictionary<byte, IReplicatedSystem> _bySystemId = new Dictionary<byte, IReplicatedSystem>();
        readonly NetPakReader _reader = new NetPakReader();

        public SnapshotApplierDiagnostics Diag { get; } = new SnapshotApplierDiagnostics();

        /// <summary>serverTick of the most recently successfully-applied snapshot -- this is what the client
        /// echoes back as its ack (see SnapshotComposer.AckCommandId).</summary>
        public long LastAppliedServerTick { get; private set; }
        public bool LastAppliedWasFull { get; private set; }

        public SnapshotApplier(IEnumerable<IReplicatedSystem> systems)
        {
            foreach (var s in systems)
            {
                if (!_bySystemId.TryAdd(s.SystemId, s))
                    throw new InvalidOperationException($"SnapshotApplier: duplicate SystemId {s.SystemId}");
            }
        }

        /// <summary>Parse + apply one snapshot datagram. Returns false only for a malformed/truncated
        /// datagram (a bad framing header, or a block whose declared byteLen runs past the buffer) -- an
        /// unrecognized-but-well-formed systemId block is not an error, it's skipped and counted.</summary>
        public bool Apply(byte[] data, int length)
        {
            _reader.Reset();
            _reader.SetBufferSegment(data, length);
            if (!_reader.ReadUInt32(out uint tick) || !_reader.ReadUInt32(out uint baselineTick))
            {
                Diag.TruncatedSnapshotsDropped++;
                return false;
            }
            bool full = baselineTick == 0;

            while (_reader.RemainingSegmentLength > 0)
            {
                if (!_reader.ReadUInt8(out byte systemId) || !_reader.ReadUInt16(out ushort byteLen))
                {
                    Diag.TruncatedSnapshotsDropped++;
                    return false; // trailing partial block header -- the datagram itself is corrupt
                }
                _reader.AlignToByte();

                var block = new byte[byteLen];
                if (byteLen > 0 && !_reader.ReadBytes(block, byteLen))
                {
                    Diag.TruncatedSnapshotsDropped++;
                    return false; // declared length ran past the buffer -- corrupt, not merely "unknown"
                }

                if (_bySystemId.TryGetValue(systemId, out var system))
                {
                    var blockReader = new NetPakReader();
                    blockReader.SetBufferSegment(block, byteLen);
                    system.ReadSnapshot(blockReader, full);
                }
                else
                {
                    Diag.UnknownSystemBlocksSkipped++; // forward-compat: sized block, safely ignored
                }
            }

            LastAppliedServerTick = tick;
            LastAppliedWasFull = full;
            Diag.SnapshotsApplied++;
            if (full) Diag.FullSnapshotsApplied++; else Diag.DeltaSnapshotsApplied++;
            return true;
        }
    }
}
