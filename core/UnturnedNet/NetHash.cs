namespace UnturnedGodot.Net
{
    /// <summary>
    /// Small order-independent hash combiner for IReplicatedSystem.StateHash() implementations (MP_PLAN
    /// §2.3/§6: "each IReplicatedSystem exposes a test-only StateHash() ... the canonical MP regression test
    /// shape: run a scripted scenario through the harness, assert server hash == every client hash"). Systems
    /// typically store entities in a Dictionary, whose enumeration order is not guaranteed to match between
    /// the server and a client replica -- callers MUST sort by NetId (or another stable key) before folding
    /// values in with MixXxx, so the combined hash only depends on the set of (id, state) pairs, never on
    /// iteration order.
    /// </summary>
    public static class NetHash
    {
        public const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        public static ulong MixUInt64(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= FnvPrime;
            }
            return hash;
        }

        public static ulong MixUInt32(ulong hash, uint value) => MixUInt64(hash, value);
        public static ulong MixByte(ulong hash, byte value) => MixUInt64(hash, value);
        public static ulong MixFloat(ulong hash, float value) => MixUInt32(hash, System.BitConverter.SingleToUInt32Bits(value));
    }
}
