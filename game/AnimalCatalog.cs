namespace UnturnedGodot
{
    // A5 (SP/MP-unify): the shared wildlife catalog -- maps the replicated species byte <-> the rig/texture/foot
    // an AnimalPuppet builds from, and the PEI animal id (Fauna.dat 1=deer/4=pig/6=cow) the server world spawns.
    // Server: AnimalNetSync stamps each agent's species byte into AnimalReplication. Client: AnimalPuppets
    // resolves the byte back to (rig, tex, foot) to build the puppet. Both sides index the same static table,
    // so only the byte crosses the wire (mirrors ZombieController.ESpeciality driving tint/clips client-side).
    public static class AnimalCatalog
    {
        public struct Kind { public byte Species; public ushort AnimalId; public string Rig; public string Tex; public float Foot; }

        public static readonly Kind[] All =
        {
            new Kind { Species = 0, AnimalId = 1, Rig = "deer", Tex = "Animal_Deer_tex.png", Foot = 0.70f },
            new Kind { Species = 1, AnimalId = 4, Rig = "pig",  Tex = "Animal_Pig_tex.png",  Foot = 0.22f },
            new Kind { Species = 2, AnimalId = 6, Rig = "cow",  Tex = "Animal_Cow_tex.png",  Foot = 0.52f },
        };

        /// <summary>PEI Fauna animal id -> species byte (fail-safe to deer, never a missing render).</summary>
        public static byte SpeciesForAnimalId(ushort animalId)
        {
            foreach (var k in All) if (k.AnimalId == animalId) return k.Species;
            return 0;
        }

        public static Kind Get(byte species) => (species < All.Length) ? All[species] : All[0];
    }
}
