using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // Loads the pre-extracted blueprint catalog (content/blueprints.tsv, made by the --extractblueprints harness)
    // into memory and answers "what can I craft right now". The port bundles only a handful of item .dats, so the
    // full recipe set lives in this catalog rather than being parsed from .dats at runtime.
    public static class BlueprintRegistry
    {
        static readonly List<BlueprintDef> _all = new();
        public static IReadOnlyList<BlueprintDef> All => _all;

        /// <summary>Load the catalog if nobody has yet.
        ///
        /// THIS IS THE FIX FOR A BUG THAT SHIPPED, and the shape of it matters. Load() was called from
        /// exactly two places: the --craftmenu render harness, and a UG_QUICKCRAFT=1 env-gated demo. Neither
        /// runs in an actual game. So in a real session the catalog was never read, Index() returned
        /// nothing, and the crafting menu showed "0 shown - 0 craftable now" with "nothing here" -- while my
        /// headless render of the very same menu showed 69 recipes, because the harness loads it and the
        /// game does not. A test that supplies its own precondition cannot see a missing one.
        ///
        /// So the guard lives HERE rather than as a third call site to remember. Every entry point that can
        /// ask for recipes goes through it, which means the failure cannot come back by someone adding a
        /// fourth path and forgetting. It is idempotent and costs one int comparison after the first call.</summary>
        /// <summary>Empty the catalog so a test can prove the self-load actually fires. Without this a test
        /// cannot distinguish "Index() loaded it" from "some earlier test in the same boot already had".</summary>
        public static void ResetForTests() => _all.Clear();

        public static void EnsureLoaded()
        {
            if (_all.Count == 0) Load();
        }

        public static int Load(string resPath = "res://content/blueprints.tsv")
        {
            _all.Clear();
            string path = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(path)) { GD.PrintErr($"[bp] catalog missing: {path}"); return 0; }
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var bp = BlueprintDef.FromTsv(line);
                if (bp != null) _all.Add(bp);
            }
            GD.Print($"[bp] loaded {_all.Count} blueprints from {resPath}");
            return _all.Count;
        }

        /// <summary>
        /// THE BROWSABLE INDEX: every Craft recipe this port can actually express, regardless of what you are
        /// carrying (strawberry: "indexed list of all available crafting recipes ... ONLY relevant items that are
        /// accessible right now, none of the bullshit recipes from curated maps").
        ///
        /// Applicable() answers "what can I make with this bag", which is a different question and is why the menu
        /// read as a supplies panel rather than an index. This one answers "what recipes exist for me at all".
        ///
        /// THE FILTER IS ITEM RESOLUTION, not a map whitelist, and that is deliberate. Measured against the
        /// catalog: 1875 rows -> 1569 have no inputs at all (Salvage/Repair/Fill target-ops, already excluded)
        /// -> 252 Craft recipes with inputs -> 195 whose owner AND every input resolve to an item this port ships.
        /// The 57 dropped are exactly the curated-map recipes: they name ingredients that do not exist here, so
        /// they could never be crafted and listing them is the "bullshit" being complained about. A recipe whose
        /// items all resolve is reachable by construction; one whose items do not is unreachable by construction.
        /// That is checkable, unlike a hand-kept list of which map an item spawns on.
        /// </summary>
        public static List<BlueprintDef> Index()
        {
            EnsureLoaded();
            var r = new List<BlueprintDef>();
            foreach (var bp in _all)
            {
                if (bp.Operation != "Craft" || bp.Inputs.Count == 0) continue;
                if (!Resolves(bp)) continue;
                r.Add(bp);
            }
            return r;
        }

        /// <summary>Owner item and every ingredient exist in this port's catalog.</summary>
        public static bool Resolves(BlueprintDef bp)
        {
            if (!ushort.TryParse(bp.OwnerItemId, out var oid) || SDG.Unturned.Assets.find(oid) == null) return false;
            foreach (var i in bp.Inputs)
                if (SDG.Unturned.Assets.findByGuid(i.Guid) == null) return false;
            foreach (var o in bp.Outputs)
                if (SDG.Unturned.Assets.findByGuid(o.Guid) == null) return false;
            return true;
        }

        // A PURE RECOLOUR -- one input, and the two names differ only by a colour word. 126 of the 195 usable
        // recipes are these (Blue Daypack <- White Daypack, Green Beach Chair <- Beach Chair, ...). They are real
        // and craftable, so they are NOT dropped, but they outnumber the 69 genuine crafts two to one and would
        // bury them in a flat list. Grouped in the UI instead of deleted -- dropping a third of the recipe set is
        // the player's call, not mine.
        static readonly System.Text.RegularExpressions.Regex _colour = new(
            @"\b(blue|green|orange|purple|red|yellow|white|black|pink|cyan|brown|grey|gray|tan|olive|khaki)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        public static bool IsRecolour(BlueprintDef bp)
        {
            if (bp.Inputs.Count != 1) return false;
            if (!ushort.TryParse(bp.OwnerItemId, out var oid)) return false;
            var outItem = SDG.Unturned.Assets.find(oid);
            var inItem = SDG.Unturned.Assets.findByGuid(bp.Inputs[0].Guid);
            if (outItem == null || inItem == null) return false;
            string a = _colour.Replace(outItem.itemName ?? "", "").Trim();
            string b = _colour.Replace(inItem.itemName ?? "", "").Trim();
            return a.Length > 0 && string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
        }

        // blueprints craftable right now from `inv` (item-satisfiability only; skill/station are the caller's gate)
        public static List<BlueprintDef> Applicable(Crafting.IInv inv)
        {
            EnsureLoaded();
            var r = new List<BlueprintDef>();
            foreach (var bp in _all)
            {
                if (bp.Inputs.Count == 0) continue;   // input-less (Salvage/target-ops) consume the OWNED item itself, not supplies -> not a supply-based craft
                if (Crafting.CanCraft(bp, inv, out _)) r.Add(bp);
            }
            return r;
        }
    }
}
