using System;
using System.Collections.Generic;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Counters + gauges for tests and net diagnostics. Counters are cumulative; gauges live on NetSession.
    /// </summary>
    public sealed class NetSessionDiagnostics
    {
        public long DatagramsSent;
        public long DatagramsReceived;
        public long MalformedDropped;
        public long VersionMismatchDropped;
        public long ReliableMessagesSent;       // accepted by SendReliable (admitted or queued)
        public long ReliableMessagesDelivered;  // handed to the app, in order, exactly once
        public long ReliableFragmentsSent;      // first sends + retransmits
        public long ReliableRetransmits;
        public long DuplicateFragmentsDropped;
        public long OutOfWindowDropped;
        public long UnreliableSent;
        public long UnreliableDelivered;
        public long StaleUnreliableDropped;
        public long WriterErrors;               // datagram compose overflowed the MTU budget (a bug if ever nonzero)
        public long BytesSent;                  // datagram bytes out (accepted datagrams only)
        public long BytesReceived;              // datagram bytes in (valid header + matching version only)
        public long ReassemblyOverflowDropped;  // fragments refused past MaxReassemblyBufferBytes (review M1)
        public long ReassemblyEvicted;          // incomplete messages evicted past ReassemblyTtlTicks (review M1)
    }

    /// <summary>
    /// The per-peer reliability engine of MP_PLAN §2.2 -- one instance per remote peer, layered ON TOP of
    /// the dumb transports (which stay bare datagrams, per SendType.cs's own layering note). Owns the
    /// packet header, Gaffer-style seq/ack-bitfield accounting, and the three channels:
    ///   Control            -- handshake + keepalive; non-keepalive types are surfaced via ControlReceived.
    ///   ReliableOrdered    -- msgId window, RTO retransmit (max(100 ms, 1.5xRTT)), fragmentation over the
    ///                         1200-byte MTU budget, in-order exactly-once delivery.
    ///   UnreliableSequenced -- newest-seq-wins, stale datagrams dropped.
    /// Entirely tick-driven: the owner calls HandleDatagram() for each received datagram and Tick() once
    /// per 50 Hz tick. No wall clock, no threads -- deterministic under the MemTransport test harness.
    /// </summary>
    public sealed class NetSession
    {
        public delegate void RawSend(byte[] buffer, int length);

        readonly RawSend _send;
        readonly byte _version;
        readonly NetPakWriter _writer = new NetPakWriter { buffer = new byte[NetProtocol.MaxDatagramBytes] };
        readonly NetPakReader _reader = new NetPakReader();

        long _now;
        long _lastSendTick;
        long _lastReceiveTick;
        bool _ackReliablePending; // reliable data arrived and no datagram has carried acks back yet

        public long LastSendTick => _lastSendTick;
        public long LastReceiveTick => _lastReceiveTick;

        /// <summary>1 Hz keepalives + prompt acks; enabled once the connection is established.</summary>
        public bool KeepAliveEnabled { get; set; }

        /// <summary>Non-keepalive control messages (Connect/Accept/Reject/Disconnect); the reader is
        /// positioned at the control payload. Owned by the client/server session wrappers.</summary>
        public event Action<NetControlType, NetPakReader> ControlReceived;

        public NetSessionDiagnostics Diag { get; } = new NetSessionDiagnostics();

        // ---- datagram seq / ack accounting (connection-wide, all channels share one seq space) ----
        ushort _localSeq;      // last seq sent; incremented before use, skips 0 (0 = reserved "none")
        ushort _remoteSeq;     // newest remote seq received (0 = nothing yet)
        uint _remoteAckBits;   // bit n => (_remoteSeq - 1 - n) received

        const int SentRingSize = 1024; // power of two; > max datagrams plausibly in flight
        struct SentRecord
        {
            public ushort Seq;
            public long SendTick;
            public bool Used;
            public bool Acked;
            public bool Reliable;
            public ushort MsgId;
            public byte FragIdx;
        }
        readonly SentRecord[] _sentRing = new SentRecord[SentRingSize];

        double _srttTicks = -1.0; // smoothed RTT in ticks; <0 = no sample yet
        public double SrttTicks => _srttTicks;
        public int RtoTicks => _srttTicks < 0
            ? NetProtocol.MinRtoTicks
            : Math.Max(NetProtocol.MinRtoTicks, (int)Math.Ceiling(_srttTicks * NetProtocol.RtoRttMultiplier));

        // ---- reliable-ordered: send side ----
        sealed class OutMessage
        {
            public ushort MsgId;
            public byte[] Data;
            public int FragCount;
            public bool[] FragAcked;
            public long[] FragLastSendTick;
            public int AckedCount;
        }
        ushort _nextMsgId;
        readonly Dictionary<ushort, OutMessage> _inFlight = new Dictionary<ushort, OutMessage>();
        readonly Queue<byte[]> _pendingSend = new Queue<byte[]>(); // waiting for window space

        public int InFlightMessageCount => _inFlight.Count;
        public int PendingSendCount => _pendingSend.Count;

        // ---- reliable-ordered: receive side ----
        sealed class InMessage
        {
            public int FragCount;
            public int ReceivedCount;
            public byte[][] Frags;
            public long FirstFragTick;   // TTL anchor (review M1); deliberately NOT refreshed per fragment
            public int BufferedBytes;    // stored payload bytes, subtracted from the session total on removal
        }
        ushort _nextDeliverMsgId;
        readonly Dictionary<ushort, InMessage> _reassembly = new Dictionary<ushort, InMessage>();
        readonly Queue<byte[]> _reliableRecvQueue = new Queue<byte[]>();
        int _reassemblyBytes;

        public int ReassemblyCount => _reassembly.Count;
        public int ReassemblyBufferedBytes => _reassemblyBytes;

        /// <summary>Latched when this peer's reliable buffering broke the M1 budget (fragment refused past
        /// MaxReassemblyBufferBytes, or an incomplete message evicted past ReassemblyTtlTicks). The channel
        /// may have lost acked data at that point, so the session is unrecoverable by design -- the owner
        /// (NetServerSession.Tick) kicks flagged peers. Never set by legit traffic.</summary>
        public bool ReassemblyBudgetExceeded { get; private set; }

        // ---- unreliable-sequenced: receive side ----
        ushort _lastUnreliableSeq; // 0 = none seen (datagram seq 0 is never sent)
        readonly Queue<byte[]> _unreliableRecvQueue = new Queue<byte[]>();

        public NetSession(RawSend send, byte protocolVersion = NetProtocol.Version)
        {
            _send = send;
            _version = protocolVersion;
        }

        // ================================ receive path ================================

        public void HandleDatagram(long now, byte[] buffer, int length)
        {
            if (now > _now) _now = now;
            _reader.Reset(); // SetBufferSegment alone keeps the previous datagram's read position
            _reader.SetBufferSegment(buffer, length);
            if (!NetProtocol.TryReadHeader(_reader, out var h) || h.MagicByte != NetProtocol.Magic)
            {
                Diag.MalformedDropped++;
                return;
            }
            if (h.Version != _version)
            {
                // Cross-version we only honor control Reject/Disconnect -- that's how a client with the
                // wrong version learns it was refused (the handshake escape hatch, MP_PLAN §2.2).
                if (h.Channel == NetChannel.Control
                    && _reader.ReadUInt8(out byte t)
                    && ((NetControlType)t == NetControlType.Reject || (NetControlType)t == NetControlType.Disconnect))
                {
                    ControlReceived?.Invoke((NetControlType)t, _reader);
                }
                else
                {
                    Diag.VersionMismatchDropped++;
                }
                return;
            }

            Diag.DatagramsReceived++;
            Diag.BytesReceived += length;
            _lastReceiveTick = _now;
            RegisterRemoteSeq(h.Seq);
            ProcessAcks(h.Ack, h.AckBits);

            switch (h.Channel)
            {
                case NetChannel.Control:
                    HandleControl();
                    break;
                case NetChannel.ReliableOrdered:
                    // any reliable datagram (fresh or duplicate) deserves a prompt ack -- a duplicate means
                    // the sender never saw our earlier ack, so answering again is what stops the retransmits
                    _ackReliablePending = true;
                    HandleReliable();
                    break;
                case NetChannel.UnreliableSequenced:
                    HandleUnreliable(h.Seq);
                    break;
                default:
                    Diag.MalformedDropped++;
                    break;
            }
        }

        void HandleControl()
        {
            if (!_reader.ReadUInt8(out byte type)) { Diag.MalformedDropped++; return; }
            if ((NetControlType)type == NetControlType.KeepAlive) return; // pure ack carrier; receipt already counted
            ControlReceived?.Invoke((NetControlType)type, _reader);
        }

        void HandleReliable()
        {
            if (!_reader.ReadUInt16(out ushort msgId)
                || !_reader.ReadUInt8(out byte fragIdx)
                || !_reader.ReadUInt8(out byte fragCount)
                || !_reader.ReadUInt16(out ushort len)
                || fragCount < 1 || fragIdx >= fragCount || len > NetProtocol.MaxFragmentPayload)
            {
                Diag.MalformedDropped++;
                return;
            }
            var payload = new byte[len];
            if (len > 0 && !_reader.ReadBytes(payload, len)) { Diag.MalformedDropped++; return; }

            if (!NetSeq.IsNewerOrEqual(msgId, _nextDeliverMsgId))
            {
                Diag.DuplicateFragmentsDropped++; // already delivered this message
                return;
            }
            if (NetSeq.Diff(msgId, _nextDeliverMsgId) >= NetProtocol.RecvWindowMessages)
            {
                Diag.OutOfWindowDropped++; // sender window discipline should make this unreachable
                return;
            }

            if (!_reassembly.TryGetValue(msgId, out var msg))
            {
                msg = new InMessage { FragCount = fragCount, Frags = new byte[fragCount][], FirstFragTick = _now };
                _reassembly[msgId] = msg;
            }
            if (msg.FragCount != fragCount) { Diag.MalformedDropped++; return; }
            if (msg.Frags[fragIdx] != null) { Diag.DuplicateFragmentsDropped++; return; }
            // review M1: hard cap on total buffered reassembly bytes. The datagram was already acked at
            // the header layer, so a refused fragment is lost for good -- which is why this latches the
            // session as broken (the server kicks it) instead of pretending the channel still works.
            if (_reassemblyBytes + len > NetProtocol.MaxReassemblyBufferBytes)
            {
                Diag.ReassemblyOverflowDropped++;
                ReassemblyBudgetExceeded = true;
                return;
            }
            msg.Frags[fragIdx] = payload;
            msg.ReceivedCount++;
            msg.BufferedBytes += len;
            _reassemblyBytes += len;

            // in-order delivery: drain every consecutive complete message starting at the window head
            while (_reassembly.TryGetValue(_nextDeliverMsgId, out var head) && head.ReceivedCount == head.FragCount)
            {
                _reliableRecvQueue.Enqueue(Assemble(head));
                _reassembly.Remove(_nextDeliverMsgId);
                _reassemblyBytes -= head.BufferedBytes;
                _nextDeliverMsgId++;
                Diag.ReliableMessagesDelivered++;
            }
        }

        // review M1: an incomplete message the peer hasn't finished within the TTL is evicted (its
        // fragments were acked, so it can never legitimately complete after this -- latch the session).
        // The dictionary is empty/tiny for healthy peers, so the per-tick sweep is effectively free.
        List<ushort> _evictScratch;
        void EvictStaleReassembly()
        {
            if (_reassembly.Count == 0) return;
            foreach (var kv in _reassembly)
            {
                // complete messages are only parked here waiting for the window head -- they deliver, not rot
                if (kv.Value.ReceivedCount == kv.Value.FragCount) continue;
                if (_now - kv.Value.FirstFragTick < NetProtocol.ReassemblyTtlTicks) continue;
                (_evictScratch ??= new List<ushort>()).Add(kv.Key);
            }
            if (_evictScratch == null || _evictScratch.Count == 0) return;
            foreach (ushort msgId in _evictScratch)
            {
                _reassemblyBytes -= _reassembly[msgId].BufferedBytes;
                _reassembly.Remove(msgId);
                Diag.ReassemblyEvicted++;
                ReassemblyBudgetExceeded = true;
            }
            _evictScratch.Clear();
        }

        static byte[] Assemble(InMessage msg)
        {
            if (msg.FragCount == 1) return msg.Frags[0];
            int total = 0;
            for (int i = 0; i < msg.FragCount; i++) total += msg.Frags[i].Length;
            var data = new byte[total];
            int offset = 0;
            for (int i = 0; i < msg.FragCount; i++)
            {
                Buffer.BlockCopy(msg.Frags[i], 0, data, offset, msg.Frags[i].Length);
                offset += msg.Frags[i].Length;
            }
            return data;
        }

        void HandleUnreliable(ushort datagramSeq)
        {
            if (!_reader.ReadUInt16(out ushort len) || len > NetProtocol.MaxUnreliablePayload)
            {
                Diag.MalformedDropped++;
                return;
            }
            var payload = new byte[len];
            if (len > 0 && !_reader.ReadBytes(payload, len)) { Diag.MalformedDropped++; return; }

            if (_lastUnreliableSeq != 0 && !NetSeq.IsNewer(datagramSeq, _lastUnreliableSeq))
            {
                Diag.StaleUnreliableDropped++; // older than (or duplicate of) what we already have
                return;
            }
            _lastUnreliableSeq = datagramSeq;
            _unreliableRecvQueue.Enqueue(payload);
            Diag.UnreliableDelivered++;
        }

        void RegisterRemoteSeq(ushort seq)
        {
            if (seq == 0) return; // never legally sent
            if (_remoteSeq == 0)
            {
                _remoteSeq = seq;
                _remoteAckBits = 0;
                return;
            }
            if (NetSeq.IsNewer(seq, _remoteSeq))
            {
                int shift = NetSeq.Diff(seq, _remoteSeq);
                // old head lands at bit (shift-1); C# masks shift counts, so guard >= 32 explicitly
                _remoteAckBits = shift < 32 ? (_remoteAckBits << shift) : 0;
                if (shift <= 32) _remoteAckBits |= 1u << (shift - 1);
                _remoteSeq = seq;
            }
            else if (seq != _remoteSeq)
            {
                int n = NetSeq.Diff(_remoteSeq, seq) - 1;
                if (n >= 0 && n < 32) _remoteAckBits |= 1u << n;
                // older than the 33-datagram window: unrepresentable; the channels dedup anyway
            }
        }

        void ProcessAcks(ushort ack, uint ackBits)
        {
            if (ack == 0) return; // peer hasn't received anything yet
            MarkDatagramAcked(ack);
            for (int n = 0; n < 32; n++)
            {
                if ((ackBits & (1u << n)) == 0) continue;
                ushort seq = (ushort)(ack - 1 - n);
                if (seq != 0) MarkDatagramAcked(seq);
            }
        }

        void MarkDatagramAcked(ushort seq)
        {
            ref SentRecord rec = ref _sentRing[seq & (SentRingSize - 1)];
            if (!rec.Used || rec.Seq != seq || rec.Acked) return;
            rec.Acked = true;
            SampleRtt(_now - rec.SendTick);
            if (rec.Reliable) MarkFragmentAcked(rec.MsgId, rec.FragIdx);
        }

        void SampleRtt(long sampleTicks)
        {
            if (sampleTicks < 0) return;
            _srttTicks = _srttTicks < 0 ? sampleTicks : 0.875 * _srttTicks + 0.125 * sampleTicks;
        }

        void MarkFragmentAcked(ushort msgId, byte fragIdx)
        {
            if (!_inFlight.TryGetValue(msgId, out var msg) || msg.FragAcked[fragIdx]) return;
            msg.FragAcked[fragIdx] = true;
            msg.AckedCount++;
            if (msg.AckedCount == msg.FragCount)
            {
                _inFlight.Remove(msgId);
                AdmitPending();
            }
        }

        // ================================ send path ================================

        /// <summary>Queue a message for reliable in-order delivery. Fragments over the MTU budget.
        /// Returns false only if the message exceeds MaxReliableMessageBytes (~301 kB).</summary>
        public bool SendReliable(byte[] data, int offset, int count)
        {
            if (count < 0 || count > NetProtocol.MaxReliableMessageBytes) return false;
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            Diag.ReliableMessagesSent++;
            if (WindowHasSpace()) Admit(copy);
            else _pendingSend.Enqueue(copy);
            return true;
        }

        public bool SendReliable(byte[] data) => SendReliable(data, 0, data.Length);

        /// <summary>Send a newest-wins message (inputs, snapshots). Must fit one datagram; larger payloads
        /// are refused -- unreliable data is kept under budget by design, never fragmented.</summary>
        public bool SendUnreliableSequenced(byte[] data, int offset, int count)
        {
            if (count < 0 || count > NetProtocol.MaxUnreliablePayload) return false;
            _writer.Reset();
            WriteHeaderFor(NetChannel.UnreliableSequenced, out ushort seq);
            _writer.WriteUInt16((ushort)count);
            _writer.WriteBytes(data, offset, count);
            RecordSent(seq, reliable: false, 0, 0);
            Transmit();
            Diag.UnreliableSent++;
            return true;
        }

        public bool SendUnreliableSequenced(byte[] data) => SendUnreliableSequenced(data, 0, data.Length);

        public void SendControl(NetControlType type) => SendControl(type, null);

        public void SendControl(NetControlType type, Action<NetPakWriter> writePayload)
        {
            _writer.Reset();
            WriteHeaderFor(NetChannel.Control, out ushort seq);
            _writer.WriteUInt8((byte)type);
            writePayload?.Invoke(_writer);
            RecordSent(seq, reliable: false, 0, 0);
            Transmit();
        }

        public bool TryReceiveReliable(out byte[] message)
        {
            if (_reliableRecvQueue.Count > 0) { message = _reliableRecvQueue.Dequeue(); return true; }
            message = null;
            return false;
        }

        public bool TryReceiveUnreliable(out byte[] message)
        {
            if (_unreliableRecvQueue.Count > 0) { message = _unreliableRecvQueue.Dequeue(); return true; }
            message = null;
            return false;
        }

        /// <summary>Once per 50 Hz tick: RTO retransmits, prompt acks for reliable data, idle keepalive.</summary>
        public void Tick(long now)
        {
            if (now > _now) _now = now;
            EvictStaleReassembly();
            RetransmitDueFragments();
            if (KeepAliveEnabled)
            {
                // prompt ack: reliable data arrived and nothing has carried acks back since
                if (_ackReliablePending) SendControl(NetControlType.KeepAlive);
                // idle keepalive at 1 Hz keeps the peer's timeout at bay
                if (_now - _lastSendTick >= NetProtocol.KeepAliveIntervalTicks) SendControl(NetControlType.KeepAlive);
            }
        }

        bool WindowHasSpace()
        {
            if (_inFlight.Count == 0) return true;
            return NetSeq.Diff(_nextMsgId, OldestInFlightMsgId()) < NetProtocol.SendWindowMessages;
        }

        ushort OldestInFlightMsgId()
        {
            bool first = true;
            ushort oldest = 0;
            foreach (ushort id in _inFlight.Keys)
            {
                if (first || NetSeq.IsNewer(oldest, id)) oldest = id;
                first = false;
            }
            return oldest;
        }

        void AdmitPending()
        {
            while (_pendingSend.Count > 0 && WindowHasSpace()) Admit(_pendingSend.Dequeue());
        }

        void Admit(byte[] data)
        {
            int fragCount = data.Length == 0
                ? 1
                : (data.Length + NetProtocol.MaxFragmentPayload - 1) / NetProtocol.MaxFragmentPayload;
            var msg = new OutMessage
            {
                MsgId = _nextMsgId++,
                Data = data,
                FragCount = fragCount,
                FragAcked = new bool[fragCount],
                FragLastSendTick = new long[fragCount],
            };
            _inFlight[msg.MsgId] = msg;
            for (int i = 0; i < fragCount; i++) SendFragment(msg, i);
        }

        void SendFragment(OutMessage msg, int fragIdx)
        {
            int offset = fragIdx * NetProtocol.MaxFragmentPayload;
            int len = Math.Min(NetProtocol.MaxFragmentPayload, msg.Data.Length - offset);
            _writer.Reset();
            WriteHeaderFor(NetChannel.ReliableOrdered, out ushort seq);
            _writer.WriteUInt16(msg.MsgId);
            _writer.WriteUInt8((byte)fragIdx);
            _writer.WriteUInt8((byte)msg.FragCount);
            _writer.WriteUInt16((ushort)len);
            if (len > 0) _writer.WriteBytes(msg.Data, offset, len);
            RecordSent(seq, reliable: true, msg.MsgId, (byte)fragIdx);
            Transmit();
            msg.FragLastSendTick[fragIdx] = _now;
            Diag.ReliableFragmentsSent++;
        }

        void RetransmitDueFragments()
        {
            int rto = RtoTicks;
            foreach (var msg in _inFlight.Values)
            {
                for (int i = 0; i < msg.FragCount; i++)
                {
                    if (msg.FragAcked[i] || _now - msg.FragLastSendTick[i] < rto) continue;
                    SendFragment(msg, i);
                    Diag.ReliableRetransmits++;
                }
            }
        }

        void WriteHeaderFor(NetChannel channel, out ushort seq)
        {
            seq = NextLocalSeq();
            NetProtocol.WriteHeader(_writer, new NetProtocol.Header
            {
                MagicByte = NetProtocol.Magic,
                Version = _version,
                Channel = channel,
                Seq = seq,
                Ack = _remoteSeq,
                AckBits = _remoteAckBits,
            });
        }

        ushort NextLocalSeq()
        {
            _localSeq++;
            if (_localSeq == 0) _localSeq = 1; // 0 is reserved for "nothing sent/received"
            return _localSeq;
        }

        void RecordSent(ushort seq, bool reliable, ushort msgId, byte fragIdx)
        {
            _sentRing[seq & (SentRingSize - 1)] = new SentRecord
            {
                Seq = seq,
                SendTick = _now,
                Used = true,
                Reliable = reliable,
                MsgId = msgId,
                FragIdx = fragIdx,
            };
        }

        void Transmit()
        {
            _writer.Flush();
            if (_writer.errors != NetPakWriter.EErrorFlags.None)
            {
                Diag.WriterErrors++; // compose overflowed the 1200-byte budget: a protocol bug, don't send garbage
                return;
            }
            _send(_writer.buffer, _writer.writeByteIndex);
            Diag.DatagramsSent++;
            Diag.BytesSent += _writer.writeByteIndex;
            _lastSendTick = _now;
            _ackReliablePending = false; // every datagram carries current ack/ackBits
        }
    }
}
