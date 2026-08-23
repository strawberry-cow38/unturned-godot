using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE CRAFTING MENU WAS EMPTY IN THE REAL GAME AND EVERY TEST WAS GREEN.
    //
    // BlueprintRegistry.Load() was called from exactly two places: the --craftmenu render harness and a
    // UG_QUICKCRAFT=1 env-gated demo. Neither runs in an actual session, so a real player opened crafting to
    // "0 shown - 0 craftable now" and "nothing here". I had rendered that same menu headlessly the same day
    // and seen 69 recipes, because the harness calls Load() and the game does not.
    //
    // The existing index test could not catch it, and the reason is worth naming precisely -- it opens with
    //     if (BlueprintRegistry.All.Count == 0) BlueprintRegistry.Load();
    // so it SUPPLIES THE PRECONDITION IT IS MEANT TO BE CHECKING. A test that arranges the state the
    // product is supposed to arrange can never tell you the product stopped arranging it. That line is a
    // reasonable thing to write and it is why this bug reached a player.
    //
    // So this test never calls Load(). It CLEARS the registry first -- otherwise it would pass whenever any
    // earlier test in the same boot happened to load it, which is the same borrowed-precondition failure
    // wearing a different hat -- and then asks the question a player asks: is there anything to craft?
    public sealed class CraftCatalogLoadTests : GameTest
    {
        public override string Name => "craft.catalog_self_loads";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            BlueprintRegistry.ResetForTests();
            T.Check("the registry starts genuinely empty, so a pass cannot come from an earlier test",
                    BlueprintRegistry.All.Count == 0);

            // NO Load() call. This is the whole point.
            var idx = BlueprintRegistry.Index();
            T.Check($"asking for the index loads the catalog by itself ({idx.Count} recipes)", idx.Count > 0);
            T.Check($"...and the catalog behind it is populated ({BlueprintRegistry.All.Count} rows)",
                    BlueprintRegistry.All.Count > 100);

            // The other entry point a player can reach: "what can I make with this bag". Same requirement.
            BlueprintRegistry.ResetForTests();
            var inv = new PlayerInventory();
            inv.tryAddItem(new Item(67, 200));
            BlueprintRegistry.Applicable(new Crafting.PlayerInvAdapter(inv));
            T.Check($"Applicable() loads it too ({BlueprintRegistry.All.Count} rows)",
                    BlueprintRegistry.All.Count > 100);

            yield return Ticks(1);
        }
    }
}
