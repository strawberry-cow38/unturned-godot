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
    // wearing a different hat -- and then asks whether asking for recipes read the catalog.
    //
    // THE PROBE CHANGED ON 2026-09-06 AND THE REASON IS THE POINT. It used to assert `idx.Count > 0`, which
    // worked only while the shipped catalog had rows in it. strawberry then asked for a "completely empty
    // crafting list", and an empty catalog makes "the self-load fired" and "the self-load never fired" produce
    // the SAME observation -- zero recipes either way. A check whose pass is indistinguishable from its failure
    // is not a check, so the probe moved to BlueprintRegistry.Loaded, which reports the read itself rather than
    // its yield. What the catalog contains is now a content question, asserted separately below.
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

            T.Check("...and reports itself unloaded, which is what the probe below reads",
                    !BlueprintRegistry.Loaded);

            // NO Load() call. This is the whole point.
            var idx = BlueprintRegistry.Index();
            T.Check($"asking for the index loads the catalog by itself (Loaded={BlueprintRegistry.Loaded})",
                    BlueprintRegistry.Loaded);

            // The other entry point a player can reach: "what can I make with this bag". Same requirement.
            BlueprintRegistry.ResetForTests();
            var inv = new PlayerInventory();
            inv.tryAddItem(new Item(67, 200));
            BlueprintRegistry.Applicable(new Crafting.PlayerInvAdapter(inv));
            T.Check($"Applicable() loads it too (Loaded={BlueprintRegistry.Loaded})", BlueprintRegistry.Loaded);

            // ONE READ, NOT ONE PER CALL. With the old count-based guard an empty catalog was re-read off disk
            // every time anything asked for recipes -- silent, and invisible to a test that only counted rows.
            int reads = BlueprintRegistry.LoadCountForTests;
            BlueprintRegistry.Index(); BlueprintRegistry.Index();
            BlueprintRegistry.Applicable(new Crafting.PlayerInvAdapter(inv));
            T.Check($"three more asks re-read the file zero times ({BlueprintRegistry.LoadCountForTests - reads})",
                    BlueprintRegistry.LoadCountForTests == reads);

            // WHAT SHIPS. The content half of the question, kept apart from the self-load half above so that
            // emptying or refilling the catalog can never silently defang that one -- which is exactly what this
            // block just went through: it asserted "empty on purpose" for a day, and then strawberry asked for
            // the wood recipes (2026-09-06), so the assertion follows the requirement rather than being deleted.
            var shipped = BlueprintRegistry.Index();
            T.Check($"the shipped catalog holds the six wood recipes ({shipped.Count})", shipped.Count == 6);

            // Every one of them: consumes exactly one wood input, needs the SAW without consuming it, yields 2.
            int toolConsumed = 0, wrongYield = 0, noSaw = 0;
            const string SawGuid = "fd6bee4579884ee9ad0b729baf423ab1";
            foreach (var bp in shipped)
            {
                bool sawPresent = false;
                foreach (var ing in bp.Inputs)
                {
                    if (!string.Equals(ing.Guid, SawGuid, System.StringComparison.OrdinalIgnoreCase)) continue;
                    sawPresent = true;
                    if (ing.Consume) toolConsumed++;   // "needs saw, NOT consumed"
                }
                if (!sawPresent) noSaw++;
                if (bp.Outputs.Count != 1 || bp.Outputs[0].Amount != 2) wrongYield++;
            }
            T.Check($"every wood recipe needs the saw ({noSaw} without one)", noSaw == 0);
            T.Check($"...and NONE of them eats it ({toolConsumed} would)", toolConsumed == 0);
            T.Check($"...and each yields 2 ({wrongYield} wrong)", wrongYield == 0);

            // SPECIES IS PRESERVED. A birch log must not saw into pine planks -- an easy transcription slip in a
            // guid table, invisible in the menu (both say "Plank"), and only ever noticed by a confused player.
            int crossSpecies = 0;
            foreach (var bp in shipped)
            {
                var outAsset = Assets.findByGuid(bp.Outputs[0].Guid);
                string species = null;
                foreach (var w in new[] { "Birch", "Maple", "Pine" })
                    if (outAsset != null && outAsset.itemName != null && outAsset.itemName.Contains(w)) species = w;
                if (species == null) { crossSpecies++; continue; }
                foreach (var ing in bp.Inputs)
                {
                    if (string.Equals(ing.Guid, SawGuid, System.StringComparison.OrdinalIgnoreCase)) continue;
                    var inAsset = Assets.findByGuid(ing.Guid);
                    if (inAsset?.itemName == null || !inAsset.itemName.Contains(species)) crossSpecies++;
                }
            }
            T.Check($"a log saws into planks of its OWN species ({crossSpecies} crossed)", crossSpecies == 0);

            // The archived retail rows are still there and still parse -- "cleared the list" must not have meant
            // "lost the extract". The index/filter tests load this same file as their fixture.
            int retail = BlueprintRegistry.Load("res://content/blueprints.retail.tsv");
            T.Check($"the retail extract survives beside it and still parses ({retail} rows)", retail > 100);
            BlueprintRegistry.ResetForTests();

            yield return Ticks(1);
        }
    }
}
