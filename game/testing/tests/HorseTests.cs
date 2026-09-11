using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Same real server -> byte snapshot -> client rig path as the existing cow test.
    public class HorseMaterialize : UnifyAnimalMaterialize
    {
        public override string Name => "animal.horse_materialize";
        protected override ushort AnimalIdUnderTest => AnimalCatalog.HorseId;
        protected override byte SpeciesUnderTest => 3;
    }

    public class HorseFauna : GameTest
    {
        public override string Name => "animal.horse_fauna";
        public override IEnumerable<Step> Run()
        {
            var ids = new List<ushort> { 1, 4, 6 };
            AnimalCatalog.IncludeHorse(ids);
            AnimalCatalog.IncludeHorse(ids);
            T.Check("PEI Passive table gains one horse entry", ids.Count == 4 && ids[3] == AnimalCatalog.HorseId);
            T.Check("Fauna horse maps to appended network species", AnimalCatalog.SpeciesForAnimalId(ids[3]) == 3);
            T.Check("network horse resolves to both horse assets", AnimalCatalog.Get(3).Rig == "horse" && AnimalCatalog.Get(3).Tex == "Animal_Horse_tex.png");
            var unrelated = new List<ushort> { 2, 3, 5 };
            AnimalCatalog.IncludeHorse(unrelated);
            T.Check("unrelated Fauna tables retain their entries", unrelated.Count == 3);
            var empty = new List<ushort>();
            AnimalCatalog.IncludeHorse(empty);
            T.Check("empty Fauna tables stay empty", empty.Count == 0);
            yield return Ticks(1);
        }
    }
}
