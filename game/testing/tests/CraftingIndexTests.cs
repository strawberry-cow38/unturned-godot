using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE CRAFTING INDEX AND ITS FILTER (strawberry: "indexed list of all available crafting recipes ... ONLY
    // relevant items that are accessible right now, none of the bullshit recipes from curated maps").
    //
    // The claim under test is not "the menu opens" -- it is that what the menu LISTS is reachable. A recipe whose
    // ingredients do not exist in this port can never be crafted, so listing it is the noise being complained
    // about, and the filter that removes it is the whole feature.
    public sealed class CraftingIndexTests : GameTest
    {
        public override string Name => "craft.index_filter";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            if (BlueprintRegistry.All.Count == 0) BlueprintRegistry.Load();
            int total = BlueprintRegistry.All.Count;
            T.Check($"the blueprint catalog loaded ({total} rows)", total > 100);

            var idx = BlueprintRegistry.Index();
            T.Check($"the index is non-empty ({idx.Count} recipes)", idx.Count > 0);

            // THE FILTER MUST ACTUALLY FILTER. If Index() returned everything, every check below still passes and
            // the feature is a no-op with a nice name. This is the one that says it did work.
            T.Check($"...and is a SUBSET of the catalog ({idx.Count} of {total} -- {total - idx.Count} excluded)",
                idx.Count < total);

            // Every listed recipe is a real craft with inputs. Salvage/Repair/Fill target-ops consume the OWNED
            // item rather than supplies and are a different interaction; they are excluded by design.
            int notCraft = 0, noInputs = 0;
            foreach (var bp in idx)
            {
                if (bp.Operation != "Craft") notCraft++;
                if (bp.Inputs.Count == 0) noInputs++;
            }
            T.Check($"every indexed recipe is a Craft ({notCraft} were not)", notCraft == 0);
            T.Check($"every indexed recipe has ingredients ({noInputs} had none)", noInputs == 0);

            // THE HEADLINE GUARANTEE: nothing in the list names an item this port does not ship.
            var unreachable = new List<string>();
            foreach (var bp in idx)
            {
                if (!ushort.TryParse(bp.OwnerItemId, out var oid) || Assets.find(oid) == null)
                { unreachable.Add($"owner {bp.OwnerItemId}"); continue; }
                foreach (var ing in bp.Inputs)
                    if (Assets.findByGuid(ing.Guid) == null) { unreachable.Add($"{Assets.find(oid)?.itemName}<-{ing.Guid}"); break; }
            }
            T.Check($"no indexed recipe names an item we do not ship ({unreachable.Count} bad)"
                    + (unreachable.Count > 0 ? ": " + string.Join(", ", unreachable.GetRange(0, System.Math.Min(5, unreachable.Count))) : ""),
                unreachable.Count == 0);

            // ...and the exclusion is doing that specific job: at least one Craft recipe WAS dropped for an
            // unresolvable ingredient. Without this, "0 bad" could mean the catalog simply has no bad recipes and
            // the resolve check is dead code.
            int droppedForItems = 0;
            foreach (var bp in BlueprintRegistry.All)
            {
                if (bp.Operation != "Craft" || bp.Inputs.Count == 0) continue;
                if (!BlueprintRegistry.Resolves(bp)) droppedForItems++;
            }
            T.Check($"the resolve check dropped real recipes, so it is live ({droppedForItems} Craft recipes excluded for missing items)",
                droppedForItems > 0);

            // RECOLOUR CLASSIFICATION, both directions -- a one-sided check would pass by calling everything a dye.
            int dyes = 0, real = 0;
            string dyeEx = null, realEx = null;
            foreach (var bp in idx)
            {
                if (BlueprintRegistry.IsRecolour(bp)) { dyes++; dyeEx ??= CraftingMenu.Title(bp); }
                else { real++; realEx ??= CraftingMenu.Title(bp); }
            }
            T.Check($"recolours are identified ({dyes}, e.g. {dyeEx ?? "<none>"})", dyes > 0);
            T.Check($"...and genuine crafts are NOT swept in with them ({real}, e.g. {realEx ?? "<none>"})", real > 0);

            // A craft whose ingredients differ in kind must never read as a recolour.
            foreach (var bp in idx)
                if (bp.Inputs.Count > 2 && BlueprintRegistry.IsRecolour(bp))
                { T.Check($"a multi-ingredient recipe was miscalled a recolour ({CraftingMenu.Title(bp)})", false); break; }

            // Titles must resolve. A Craft blueprint's output IS its owner item (the outputs column is empty on
            // every row), so a Title() that only read Outputs would print "item" for all 195.
            int untitled = 0;
            foreach (var bp in idx)
            {
                string t = CraftingMenu.Title(bp);
                if (string.IsNullOrWhiteSpace(t) || t == "Craft" || t == "item") untitled++;
            }
            T.Check($"every indexed recipe resolves a real name ({untitled} unnamed)", untitled == 0);

            // SEARCH MATCHES INGREDIENTS, not just the output name. Asserted because the useful query at a
            // workbench is "what can I do with this scrap", and a title-only search silently answers a different
            // question -- it would still LOOK like a working search box.
            var idx2 = BlueprintRegistry.Index();
            BlueprintDef withNamedIngredient = null;
            string ingName = null;
            foreach (var bp in idx2)
            {
                foreach (var ing in bp.Inputs)
                {
                    var a = Assets.findByGuid(ing.Guid);
                    // pick an ingredient whose name does NOT appear in the output name, or the check cannot tell
                    // ingredient-matching from title-matching.
                    if (a?.itemName is { Length: > 3 } n && !CraftingMenu.Title(bp).Contains(n, System.StringComparison.OrdinalIgnoreCase))
                    { withNamedIngredient = bp; ingName = n; break; }
                }
                if (withNamedIngredient != null) break;
            }
            T.Check($"found a recipe whose ingredient name differs from its output ({CraftingMenu.Title(withNamedIngredient ?? idx2[0])} <- {ingName ?? "?"})",
                withNamedIngredient != null);
            if (withNamedIngredient != null)
            {
                T.Check($"searching an INGREDIENT name finds the recipe (\"{ingName}\" -> {CraftingMenu.Title(withNamedIngredient)})",
                    CraftingMenu.MatchesForTest(withNamedIngredient, ingName));
                T.Check($"...and a string in neither the output nor any ingredient does NOT match",
                    !CraftingMenu.MatchesForTest(withNamedIngredient, "zzqqxx"));
            }

            yield break;
        }
    }
}
