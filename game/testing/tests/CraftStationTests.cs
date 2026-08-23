using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // craft.stations: a recipe that needs a crafting station (workbench tag) is only satisfiable when that tag is in
    // the player's granted set (from a nearby placed station within range + LOS -- PlayerController.CraftingStationTags).
    // A recipe with no station tags crafts anywhere. This tests the pure Crafting.HasStations gate.
    public class CraftStationTest : GameTest
    {
        public override string Name => "craft.stations";
        public override IEnumerable<Step> Run()
        {
            const string WORKBENCH = "7b82c125a5a54984b8bb26576b59e977";   // the Workbench crafting tag

            var anywhere = new BlueprintDef { Operation = "Craft", Name = "anywhere" };   // no station tags
            var needsBench = new BlueprintDef { Operation = "Craft", Name = "bench" };
            needsBench.StationTags.Add(WORKBENCH);

            var none = new HashSet<string>();
            var atBench = new HashSet<string> { WORKBENCH };

            T.Check("no-station recipe is craftable anywhere", Crafting.HasStations(anywhere, none));
            T.Check("bench recipe blocked with no station nearby", !Crafting.HasStations(needsBench, none));
            T.Check("bench recipe unlocked AT the workbench", Crafting.HasStations(needsBench, atBench));
            T.Check("wrong station doesn't unlock it", !Crafting.HasStations(needsBench, new HashSet<string> { "deadbeef" }));
            T.Check("null station set: anywhere ok, bench blocked", Crafting.HasStations(anywhere, null) && !Crafting.HasStations(needsBench, null));
            yield break;
        }
    }
}
