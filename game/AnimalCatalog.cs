namespace UnturnedGodot
{
    // A5 (SP/MP-unify): the shared wildlife catalog -- maps the replicated species byte <-> the rig/texture/foot
    // an AnimalPuppet builds from, and the animal id (1=deer/4=pig/6=cow/7=horse) the server world spawns.
    // Server: AnimalNetSync stamps each agent's species byte into AnimalReplication. Client: AnimalPuppets
    // resolves the byte back to (rig, tex, foot) to build the puppet. Both sides index the same static table,
    // so only the byte crosses the wire (mirrors ZombieController.ESpeciality driving tint/clips client-side).
    public static class AnimalCatalog
    {
        public const ushort HorseId = 7; // unused in the port's animal catalog; species bytes remain append-only
        public struct Kind { public byte Species; public ushort AnimalId; public string Rig; public string Tex; public float Foot; }

        public static readonly Kind[] All =
        {
            new Kind { Species = 0, AnimalId = 1, Rig = "deer", Tex = "Animal_Deer_tex.png", Foot = 0f },
            new Kind { Species = 1, AnimalId = 4, Rig = "pig",  Tex = "Animal_Pig_tex.png",  Foot = 0f },
            new Kind { Species = 2, AnimalId = 6, Rig = "cow",  Tex = "Animal_Cow_tex.png",  Foot = 0f },
            new Kind { Species = 3, AnimalId = HorseId, Rig = "horse", Tex = "Animal_Horse_tex.png", Foot = 0f },
        };

        // Retail Fauna.dat cannot contain this port's horse. Expand herbivore tables
        // in memory, on both SP and dedicated paths; never modify installed map data.
        internal static void IncludeHorse(System.Collections.Generic.List<ushort> ids)
        {
            if (!ids.Contains(HorseId) && (ids.Contains(1) || ids.Contains(4) || ids.Contains(6)))
                ids.Add(HorseId);
        }

        /// <summary>PEI Fauna animal id -> species byte (fail-safe to deer, never a missing render).</summary>
        public static byte SpeciesForAnimalId(ushort animalId)
        {
            foreach (var k in All) if (k.AnimalId == animalId) return k.Species;
            return 0;
        }

        public static Kind Get(byte species) => (species < All.Length) ? All[species] : All[0];
    }
}
