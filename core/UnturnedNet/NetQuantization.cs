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

        // Every Quantize* below is the same six lines -- allocate a writer + an 8-byte buffer + a reader,
        // encode, decode -- differing only in which Write/Read pair they call (DUPLICATE_AUDIT 1.11). That
        // is three allocations to round-trip ONE float, on a path that runs per replicated field per entity
        // per tick.
        //
        // The scratch is [ThreadStatic] rather than plain static: NetPak's own writer already has shared
        // mutable state (see WriteString's static buffer), and a per-thread instance keeps a server tick and
        // a test runner off each other. None of these functions call each other, so there is no re-entrancy
        // to worry about.
        //
        // Reusing a writer means the buffer carries stale bytes from the previous call instead of the zeros
        // a fresh `new byte[8]` gives. That is safe because Reset() rewinds the bit cursor and the reader is
        // bounded by writeByteIndex -- but "should be safe" is not good enough for the encoding every
        // replicated float and every StateHash goes through, so NetQuantizationScratchTests asserts the
        // shared path is bit-identical to a fresh-allocation path across the ranges and edges.
        [System.ThreadStatic] static NetPakWriter _scratchW;
        [System.ThreadStatic] static NetPakReader _scratchR;

        static NetPakWriter ScratchWriter()
        {
            _scratchW ??= new NetPakWriter { buffer = new byte[8] };
            _scratchW.Reset();
            return _scratchW;
        }
        static NetPakReader ScratchReader(NetPakWriter w)
        {
            _scratchR ??= new NetPakReader();
            // Reset() BEFORE SetBufferSegment is load-bearing and was not obvious: SetBufferSegment does not
            // rewind the read cursor, so a reused reader carries on from where the last decode stopped and
            // every call after the first reads past the data -- decoding the all-zeros pattern, which for a
            // ClampedFloat is the under-range sentinel (-1024 at 11 int bits), not an obviously wrong number.
            // The fresh-allocation versions never needed it because a new reader starts at zero.
            // WorldReplication.QuantizeBase, the one hand-inlined copy of this round-trip, already called
            // Reset() here -- DUPLICATE_AUDIT 1.11 noted that as an oddity; this is why it was there.
            _scratchR.Reset();
            _scratchR.SetBufferSegment(w.buffer, w.writeByteIndex);
            return _scratchR;
        }

        /// <summary>Round a value through the exact wire quantization (encode then decode) so a value stored
        /// authoritatively is already bit-identical to what every client reconstructs after the wire
        /// round-trip -- StateHash comparisons then need no tolerance, they're exact equality.</summary>
        public static float QuantizeClampedFloat(float value, int intBits, int fracBits)
        {
            var w = ScratchWriter();
            w.WriteClampedFloat(value, intBits, fracBits);
            w.Flush();
            ScratchReader(w).ReadClampedFloat(intBits, fracBits, out float result);
            return result;
        }

        /// <summary>Same idea as QuantizeClampedFloat, for WriteSignedNormalizedFloat -- what MoveInput's
        /// move axes go through on the wire. The client-side predictor quantizes its OWN input through this
        /// before integrating, so it consumes exactly the bytes the server will read (MP_PLAN §2.5b).</summary>
        public static float QuantizeSignedNormalizedFloat(float value, int bitCount)
        {
            var w = ScratchWriter();
            w.WriteSignedNormalizedFloat(value, bitCount);
            w.Flush();
            ScratchReader(w).ReadSignedNormalizedFloat(bitCount, out float result);
            return result;
        }

        /// <summary>Same idea as QuantizeClampedFloat, for WriteUnsignedNormalizedFloat -- what the B5
        /// SystemVitals owner block runs food/water/stamina/infection through (8 bits each). The server hashes
        /// the ROUND-TRIPPED value so its StateHashFor matches the owner replica's StateHash exactly (the
        /// replica only ever holds this quantized reconstruction), never a tolerance -- the signed-float
        /// mirror above, unsigned.</summary>
        public static float QuantizeUnsignedNormalizedFloat(float value, int bitCount)
        {
            var w = ScratchWriter();
            w.WriteUnsignedNormalizedFloat(value, bitCount);
            w.Flush();
            ScratchReader(w).ReadUnsignedNormalizedFloat(bitCount, out float result);
            return result;
        }

        /// <summary>Same idea as QuantizeClampedFloat, for WriteDegrees/ReadDegrees.</summary>
        public static float QuantizeDegrees(float value, int bitCount)
        {
            var w = ScratchWriter();
            w.WriteDegrees(value, bitCount);
            w.Flush();
            ScratchReader(w).ReadDegrees(out float result, bitCount);
            return result;
        }
    }
}
