namespace SDG.Unturned
{
    /// <summary>DETERMINISTIC LOOT (strawberry 2026-09-10: "wire loot to roll on a deterministic seed thats
    /// chosen/rolled before pressing play on the map").
    ///
    /// THE TRAP THIS EXISTS TO AVOID: the obvious implementation is one seeded RNG that every container draws
    /// from in turn. That is deterministic in the narrow sense -- same seed, same sequence -- and still wrong,
    /// because what each container GETS then depends on the order containers happen to be built. That order
    /// differs between a fresh map load and a save restore, and between a server and a client walking the same
    /// map. Two players would stand at one crate holding one seed and see different loot.
    ///
    /// So each container gets its OWN stream, derived from the world seed and WHERE IT IS. A crate's contents
    /// then depend on which crate it is, not on when it was touched. Same reasoning as the destructible bitmap
    /// being keyed by deterministic placement index rather than spawn order.
    ///
    /// Position is the identity because it is the one thing a container has that is stable across load paths
    /// without plumbing a new index through the map format. Two crates cannot occupy one spot.</summary>
    public static class LootSeed
    {
        /// <summary>The world's seed. Established ONCE when the world is first saved and carried from then
        /// on, so a reload re-rolls nothing (strawberry 2026-09-11: "unset seed should be a random seed each
        /// time. set once at the first save creation").
        ///
        /// 0 is a legal VALUE -- a world whose seed happens to be 0 -- which is why "has it been set" is a
        /// separate flag rather than a magic number. Overloading 0 as "unset" would make exactly one world in
        /// 2^64 silently re-roll its loot on every load, and that is the kind of bug that is never
        /// reproduced.</summary>
        public static ulong World;

        /// <summary>False until a seed has been chosen, rolled or loaded. A fresh process has NOT got a seed
        /// -- it has got the default value of a field, which is not the same thing.</summary>
        public static bool Established;

        /// <summary>Adopt a known seed: from a save, or from a seed the host picked.</summary>
        public static void Set(ulong seed) { World = seed; Established = true; }

        /// <summary>Roll one if none has been established yet, and return it. Idempotent on purpose -- called
        /// at first save, and calling it again must never move an existing world's loot.
        ///
        /// The randomness source is passed in rather than taken here, because this assembly is engine-free
        /// and because a test needs to be able to hand it a known value.</summary>
        public static ulong EnsureEstablished(System.Func<ulong> roll)
        {
            if (!Established) Set(roll());
            return World;
        }

        /// <summary>Back to a fresh-process state. For tests and for starting a genuinely new world.</summary>
        public static void Clear() { World = 0; Established = false; }

        /// <summary>Quantisation for the position key: decimetres. Coarse enough that a float that arrives a
        /// hair different between two load paths still lands in the same bucket, fine enough that no two real
        /// containers share one.</summary>
        public const float Quantum = 0.1f;

        /// <summary>A stable per-location stream seed. Deliberately NOT a counter and NOT sequential: callers
        /// must be able to ask for one crate's seed without having asked for any other's, because on a save
        /// restore they will.</summary>
        public static uint For(float x, float y, float z)
        {
            long qx = Quantise(x), qy = Quantise(y), qz = Quantise(z);
            // splitmix-style avalanche. The inputs are small, adjacent and highly correlated -- two crates a
            // metre apart differ by 10 in one field -- so a plain sum or xor would leave neighbouring crates
            // with neighbouring seeds, and neighbouring seeds in most generators give correlated first draws.
            // That would show up in game as "the crates in this room all rolled the same thing".
            ulong h = World;
            h = Mix(h ^ ((ulong)qx * 0x9E3779B97F4A7C15UL));
            h = Mix(h ^ ((ulong)qy * 0xC2B2AE3D27D4EB4FUL));
            h = Mix(h ^ ((ulong)qz * 0x165667B19E3779F9UL));
            uint s = (uint)(h ^ (h >> 32));
            return s;
        }

        static long Quantise(float v) => (long)System.MathF.Round(v / Quantum);

        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
