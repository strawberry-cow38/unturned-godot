using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // review #9: AnimalCatalog.SpeciesForAnimalId (PEI Fauna animal id -> species byte, used by AnimalField to
    // seed replicated wildlife) had no test -- UnifyTests set AnimalAgent.Species directly, bypassing it. Verify
    // the id->species mapping and the fail-safe-to-deer default (a missing/unknown id must never yield an
    // out-of-range species -> a missing render, never a crash or desync).
    public class AnimalCatalogSpecies : GameTest
    {
        public override string Name => "review.animal_catalog_species";
        public override IEnumerable<Step> Run()
        {
            T.Check("deer id 1 -> species 0", AnimalCatalog.SpeciesForAnimalId(1) == 0);
            T.Check("pig id 4 -> species 1", AnimalCatalog.SpeciesForAnimalId(4) == 1);
            T.Check("cow id 6 -> species 2", AnimalCatalog.SpeciesForAnimalId(6) == 2);
            T.Check("unknown id -> fail-safe deer (species 0), never out of range", AnimalCatalog.SpeciesForAnimalId(9999) == 0);
            T.Check("Get(species) maps back to the right rig", AnimalCatalog.Get(1).Rig == "pig" && AnimalCatalog.Get(2).Rig == "cow");
            T.Check("Get is bounds-safe for an out-of-range species (fail-safe to entry 0)", AnimalCatalog.Get(200).Rig == "deer");
            yield return Ticks(1);
        }
    }
}
