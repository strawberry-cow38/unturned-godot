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
        public long SyncChecksPassed;           // hash blocks where every checked replica matched the server
        public long SyncChecksFailed;           // hash blocks with at least one mismatching system
    }

    /// <summary>One confirmed replica-vs-server hash mismatch (desync detection, hardening Part C).</summary>
    public struct DesyncReport
    {
        public long ServerTick;   // the tick both hashes describe
        public byte SystemId;     // which system diverged
        public ulong ServerHash;
        public ulong ClientHash;

        public override string ToString()
            => $"desync at server tick {ServerTick}: system {SystemId} server hash {ServerHash:x16} != client hash {ClientHash:x16}";
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

        /// <summary>Fired when DesyncConfirmChecks CONSECUTIVE sync-check blocks mismatched -- once per
        /// mismatching system of the confirming check. The threshold (default 2) absorbs the benign
        /// one-check race where a reliable event overtakes an earlier-composed snapshot on the wire; a real
        /// desync persists and keeps re-confirming. Costs nothing unless the server enabled sync checks.</summary>
        public event Action<DesyncReport> DesyncDetected;
        public int DesyncConfirmChecks = 2;
        int _consecutiveMismatchedChecks;
        readonly List<DesyncReport> _mismatchScratch = new List<DesyncReport>();

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

                if (systemId == ReplicationIds.SystemSyncCheck)
                {
                    // composed LAST by the server, so every state block of this snapshot is applied by now
                    ProcessSyncCheck(block, byteLen, tick);
                }
                else if (_bySystemId.TryGetValue(systemId, out var system))
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

        // One sync-check block (desync detection): count:8 then (systemId:8 + serverHash:64) per checked
        // system. Compare each server hash against the local replica's StateHash for the same tick; a
        // system this build doesn't register is skipped like any unknown block content.
        void ProcessSyncCheck(byte[] block, int byteLen, uint serverTick)
        {
            var r = new NetPakReader();
            r.SetBufferSegment(block, byteLen);
            if (!r.ReadUInt8(out byte count)) return;
            _mismatchScratch.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!r.ReadUInt8(out byte systemId) || !r.ReadUInt64(out ulong serverHash)) return;
                if (!_bySystemId.TryGetValue(systemId, out var system)) continue;
                ulong clientHash = system.StateHash();
                if (clientHash != serverHash)
                    _mismatchScratch.Add(new DesyncReport
                    {
                        ServerTick = serverTick,
                        SystemId = systemId,
                        ServerHash = serverHash,
                        ClientHash = clientHash,
                    });
            }

            if (_mismatchScratch.Count == 0)
            {
                Diag.SyncChecksPassed++;
                _consecutiveMismatchedChecks = 0;
                return;
            }
            Diag.SyncChecksFailed++;
            if (++_consecutiveMismatchedChecks < DesyncConfirmChecks) return;
            _consecutiveMismatchedChecks = 0;   // keep re-confirming (and re-alerting) while diverged
            foreach (var report in _mismatchScratch)
            {
                if (NetLog.Enabled) NetLog.Warn(report.ToString());
                DesyncDetected?.Invoke(report);
            }
        }
    }
}
