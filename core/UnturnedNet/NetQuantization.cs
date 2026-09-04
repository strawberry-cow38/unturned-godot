using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Tunable wire-quantization constants for snapshot payloads (MP_PLAN §2.4 / §5 item 10). Locked by
    /// golden byte tests (SnapshotFramingGoldenTests) -- changing any bit width here is a wire-format
    /// change: bump NetProtocol.Version and re-golden in the same commit.
    ///
    /// Position bounds are baked in for the biggest plausible map now (§5 item 10 says choose once): PEI
    /// fits +-1024 m on the XZ plane, so 11 int bits (+-2048 m range) leaves comfortable headroom; Y is
    /// shallower terrain so 9 int bits (+-256 m) is ample. 8 fractional bits on every axis is ~1/256 m
    /// (~4 mm) precision, matching MP_PLAN §2.4's "~55 bits per player position" napkin math:
    /// (11+8)*2 [XZ] + (9+8) [Y] = 55.
    /// </summary>
    public static class NetQuantization
    {
        public const int PositionXZIntBits = 11;
        public const int PositionXZFracBits = 8;
        public const int PositionYIntBits = 9;
        public const int PositionYFracBits = 8;

        /// <summary>Yaw/pitch via WriteDegrees/ReadDegrees, wrapped into [0, 360) -- MP_PLAN §2.4: "yaw/pitch
        /// via WriteDegrees(11)".</summary>
        public const int YawBits = 11;
        public const int PitchBits = 11;

        /// <summary>How stale a client's acked baseline may get before the composer falls back to a full
        /// resend instead of a delta (MP_PLAN §2.3: "baseline older than the dirty-ring depth (64 ticks) ->
        /// send full"). Also doubles as the loss-recovery mechanism: a client whose acks keep getting lost
        /// eventually gets a full snapshot regardless.</summary>
        public const long DirtyRingDepthTicks = 64;

        /// <summary>Round a value through the exact wire quantization (encode then decode) so a value stored
        /// authoritatively is already bit-identical to what every client reconstructs after the wire
        /// round-trip -- StateHash comparisons then need no tolerance, they're exact equality.</summary>
        // PERF (ETW allocation ticks, 2026-09-02): these round-trips ran every physics step for every replicated value
        // (the SP loopback too) and each call allocated a NetPakWriter + an 8-byte buffer + a NetPakReader -- 74% of ALL
        // managed garbage in a PEI idle capture (~2.5 MB/s), driving a ~16 ms gen1 GC pause every ~4 s (the periodic
        // physics hitch). One writer/reader pair per thread, reused; Reset()/SetBufferSegment re-arm them per call.
        [System.ThreadStatic] static NetPakWriter _w;
        [System.ThreadStatic] static NetPakReader _r;
        static NetPakWriter Writer() => _w ??= new NetPakWriter { buffer = new byte[8] };
        static NetPakReader Reader()
        {
            var r = _r ??= new NetPakReader();
            r.Reset();   // SetBufferSegment only swaps the buffer; without this the read index/scratch carry over, the 3rd call reads past the 8-byte buffer, and from then on every read fails quietly (value 0). Shipped that way in 6e19565b for ~an hour -- never again.
            return r;
        }

        public static float QuantizeClampedFloat(float value, int intBits, int fracBits)
        {
            var w = Writer();
            w.Reset();
            w.WriteClampedFloat(value, intBits, fracBits);
            w.Flush();
            var r = Reader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadClampedFloat(intBits, fracBits, out float result);
            return result;
        }

        /// <summary>Same idea as QuantizeClampedFloat, for WriteSignedNormalizedFloat -- what MoveInput's
        /// move axes go through on the wire. The client-side predictor quantizes its OWN input through this
        /// before integrating, so it consumes exactly the bytes the server will read (MP_PLAN §2.5b).</summary>
        public static float QuantizeSignedNormalizedFloat(float value, int bitCount)
        {
            var w = Writer();
            w.Reset();
            w.WriteSignedNormalizedFloat(value, bitCount);
            w.Flush();
            var r = Reader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadSignedNormalizedFloat(bitCount, out float result);
            return result;
        }

        /// <summary>Same idea as QuantizeClampedFloat, for WriteUnsignedNormalizedFloat -- what the B5
        /// SystemVitals owner block runs food/water/stamina/infection through (8 bits each). The server hashes
        /// the ROUND-TRIPPED value so its StateHashFor matches the owner replica's StateHash exactly (the
        /// replica only ever holds this quantized reconstruction), never a tolerance -- the signed-float
        /// mirror above, unsigned.</summary>
        public static float QuantizeUnsignedNormalizedFloat(float value, int bitCount)
        {
            var w = Writer();
            w.Reset();
            w.WriteUnsignedNormalizedFloat(value, bitCount);
            w.Flush();
            var r = Reader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadUnsignedNormalizedFloat(bitCount, out float result);
            return result;
        }

        /// <summary>Same idea as QuantizeClampedFloat, for WriteDegrees/ReadDegrees.</summary>
        public static float QuantizeDegrees(float value, int bitCount)
        {
            var w = Writer();
            w.Reset();
            w.WriteDegrees(value, bitCount);
            w.Flush();
            var r = Reader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadDegrees(out float result, bitCount);
            return result;
        }
    }
}
