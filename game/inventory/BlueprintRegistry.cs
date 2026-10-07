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

        /// <summary>Has the catalog been read off disk yet? Separate from `_all.Count > 0` because THE SHIPPING
        /// CATALOG IS NOW EMPTY (strawberry 2026-09-06: "completely empty crafting list"), and a count-based guard
        /// cannot tell "never loaded" from "loaded, and it genuinely has no rows". That mattered twice over: it made
        /// EnsureLoaded re-read the file on every single call, and it would have quietly gutted the regression test
        /// below, whose whole job is to prove the self-load fires -- with an empty file its pass and its failure look
        /// identical. The flag is what keeps that check honest.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>How many times the catalog has actually been read off disk. Exposed so a test can assert the
        /// guard PREVENTS re-reads, rather than merely that a read happened -- the old guard's failure was extra
        /// reads, which no row count can see.</summary>
        public static int LoadCountForTests { get; private set; }

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
        public static void ResetForTests() { _all.Clear(); Loaded = false; }

        public static void EnsureLoaded()
        {
            if (!Loaded) Load();
        }

        public static int Load(string resPath = "res://content/blueprints.tsv")
        {
            _all.Clear();
            Loaded = true;   // set even on a missing/empty file: the attempt is what EnsureLoaded must not repeat
            LoadCountForTests++;
            string path = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(path)) { Log.Err($"[bp] catalog missing: {path}"); return 0; }
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var bp = BlueprintDef.FromTsv(line);
                if (bp != null) _all.Add(bp);
            }
            GenerateAmmoRecipes();
            GenerateSalvageRecipes();
            SDG.Unturned.Durability.RegisterTools(_all);   // a recipe's non-consumed input is a TOOL, and tools wear (Durability)
            Log.Print($"[bp] loaded {_all.Count} blueprints from {resPath} ({SDG.Unturned.Durability.ToolIds.Count} tools)");
            return _all.Count;
        }

        /// <summary>Reload a round of every ammunition the game carries, from metal scrap and gunpowder.
        ///
        /// Master 2026-10-07: "add crafting recipes for every ammo with balances ratios of metal scrap + gunpowder".
        ///
        /// ⭐⭐ GENERATED, NOT WRITTEN OUT. "Every ammo" is a statement about a SET that changes -- the MAC-10's
        /// .45 arrived three days ago -- so a hand-written block of TSV rows would be true on the day it was typed
        /// and quietly incomplete after the next gun. Walking `Assets.all()` for `isAmmo` means the answer is
        /// recomputed from the thing being described, and a new cartridge gets its recipe by existing.
        ///
        /// ⭐ THE RATIO COMES FROM THE ROUND'S OWN STACK SIZE, which is the only size signal every ammo already
        /// carries: a cartridge that stacks 180 (9mm) is small, one that stacks 32 (a 12-gauge shell) is big. So
        /// `128 / stackSize` is "how much round is this, relative to a 5.56", and both the batch you get and the
        /// materials you spend ride it. That is a derivation rather than a table of my opinions, and it stays
        /// balanced for a cartridge nobody has added yet.
        ///
        /// ⚠ Skips anything that already has a Craft recipe producing it, so an authored recipe always wins -- a
        /// generator that overwrote hand-tuned content would be the worst of both.</summary>
        static void GenerateAmmoRecipes()
        {
            const string ScrapGuid = "", PowderName = "Gunpowder";
            var scrap = SDG.Unturned.Assets.find(67);          // Metal Scrap
            var powder = SDG.Unturned.Assets.find(9327);       // Gunpowder
            if (scrap == null || powder == null || string.IsNullOrEmpty(scrap.guid) || string.IsNullOrEmpty(powder.guid))
            { Log.Print("[bp] ammo recipes skipped: metal scrap or gunpowder missing a guid"); return; }

            // What already has a recipe -- authored content wins.
            var alreadyMade = new HashSet<ushort>();
            foreach (var bp in _all)
                if (bp.Operation == "Craft")
                    foreach (var o in bp.Outputs)
                    { var a = SDG.Unturned.Assets.findByGuid(o.Guid); if (a != null) alreadyMade.Add(a.id); }

            int made = 0;
            foreach (var a in SDG.Unturned.Assets.all())
            {
                if (a == null || !a.isAmmo || string.IsNullOrEmpty(a.guid)) continue;
                if (alreadyMade.Contains(a.id)) continue;

                int stack = Mathf.Clamp(a.stackSize, 8, 256);
                float size = 128f / stack;                                  // 1.0 = a 5.56; 4.0 = a 12-gauge shell
                int batch = Mathf.Max(5, stack / 8);                        // worth the trip to the bench
                int scrapCost = Mathf.Max(1, Mathf.CeilToInt(batch * size / 6f));
                int powderCost = Mathf.Max(1, Mathf.CeilToInt(batch * size / 8f));

                var bp = new BlueprintDef
                {
                    OwnerItemId = a.id.ToString(),
                    Operation = "Craft",
                    Name = a.itemName,
                    Skill = "", SkillLevel = 0,
                    Seconds = Mathf.Clamp(batch * 0.25f, 2f, 12f),
                };
                bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = scrap.guid, Amount = scrapCost, Consume = true });
                bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = powder.guid, Amount = powderCost, Consume = true });
                bp.Outputs.Add(new BlueprintDef.Ingredient { Guid = a.guid, Amount = batch, Consume = true });
                _all.Add(bp);
                made++;
            }
            if (made > 0) Log.Print($"[bp] generated {made} ammo recipe(s) from metal scrap + gunpowder");
        }

        /// <summary>Break things down for parts.
        ///
        /// Master 2026-10-07: "add salvage recipes for smoke grenades, flares, frag grenades -> metal scrap +
        /// gunpowder. firefighter shirt and pants -> asbestos + cloth. firefighter helmet -> asbestos + scrap.
        /// military helmets -> 2 scrap. all 'hats' (caps tophats etc) scrap for cloth. most shirts and pants
        /// recycle for cloth too. backpacks too."
        ///
        /// ⭐ THE SHAPE OF THE REQUEST IS "A FEW NAMED THINGS, THEN WHOLE CATEGORIES", so the code is the same:
        /// explicit overrides first, then a sweep by item TYPE for everything that did not get one. "All hats" and
        /// "most shirts and pants" are categories that grow, and a hand-written list of them would be wrong by the
        /// next clothing drop.
        ///
        /// ⚠ Authored as `Craft` recipes that CONSUME the item, not as the `Salvage` operation: Salvage rows carry
        /// no inputs and nothing in this port acts on that operation, so they would load, list, and do nothing.
        /// What makes this a salvage is that the thing itself is the ingredient.
        ///
        /// ⚠ An item that already has a Craft recipe consuming it is skipped, so authored content wins.</summary>
        static void GenerateSalvageRecipes()
        {
            var scrap = SDG.Unturned.Assets.find(67);        // Metal Scrap
            var cloth = SDG.Unturned.Assets.find(66);        // Cloth
            var powder = SDG.Unturned.Assets.find(9327);     // Gunpowder
            var asbestos = SDG.Unturned.Assets.find(9340);   // Asbestos
            if (scrap == null || cloth == null || powder == null || asbestos == null) return;

            var consumedAlready = new HashSet<ushort>();
            foreach (var bp in _all)
                if (bp.Operation == "Craft")
                    foreach (var i in bp.Inputs)
                    { var a = SDG.Unturned.Assets.findByGuid(i.Guid); if (a != null && i.Consume) consumedAlready.Add(a.id); }

            int made = 0;
            void Salvage(ushort id, params (SDG.Unturned.ItemAsset mat, int n)[] yields)
            {
                var src = SDG.Unturned.Assets.find(id);
                if (src == null || string.IsNullOrEmpty(src.guid) || consumedAlready.Contains(id)) return;
                var bp = new BlueprintDef
                {
                    OwnerItemId = id.ToString(), Operation = "Craft",
                    Name = $"Salvage {src.itemName}", Skill = "", SkillLevel = 0, Seconds = 3f,
                };
                bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = src.guid, Amount = 1, Consume = true });
                foreach (var y in yields)
                    if (y.mat != null && !string.IsNullOrEmpty(y.mat.guid))
                        bp.Outputs.Add(new BlueprintDef.Ingredient { Guid = y.mat.guid, Amount = y.n, Consume = true });
                if (bp.Outputs.Count == 0) return;
                _all.Add(bp); consumedAlready.Add(id); made++;
            }

            // ---- THE NAMED ONES, which must land BEFORE the sweep or the sweep would give them plain cloth ----
            Salvage(233, (asbestos, 1), (cloth, 2));   // Firefighter Top
            Salvage(234, (asbestos, 1), (cloth, 2));   // Firefighter Bottom
            Salvage(241, (asbestos, 1), (scrap, 2));   // Firefighter Helmet
            foreach (ushort mh in new ushort[] { 309, 1010, 1335, 1519 }) Salvage(mh, (scrap, 2));   // military helmets

            // ---- KITCHENWARE BREAKS DOWN INTO WHAT IT IS MADE OF (master 2026-10-07) -----------------------
            //
            // ⭐ The lists come from LootTables, which is what STOCKS these containers -- so "what counts as
            // crockery" is answered in one place. Two private copies would disagree the first time either moved.
            var ceramic = SDG.Unturned.Assets.find(9338);
            var glass = SDG.Unturned.Assets.find(9341);
            if (ceramic != null && glass != null)
            {
                foreach (var id in LootTables.Crockery)
                {
                    // ⚠ The drinking glass is in the crockery list because that is where it is STOCKED, and it is
                    // the one piece in it that is not ceramic. Breaking it into ceramic would be the tidy answer
                    // and the wrong one.
                    if (id == 9301) Salvage(id, (glass, 1));
                    else Salvage(id, (ceramic, 1));
                }
                Salvage(1928, (ceramic, 1));   // Ceramic Plate -- the retail item, genuinely ceramic
                Salvage(1930, (ceramic, 1));   // Ceramic Bowl
            }
            // "forks, spoons, pots, pans etc -> scrap"
            foreach (var id in LootTables.Cutlery) Salvage(id, (scrap, 1));
            foreach (var id in LootTables.Cookware) Salvage(id, (scrap, 2));   // a pot is more metal than a fork

            // ---- THEN THE CATEGORIES ------------------------------------------------------------------------
            var byType = new List<SDG.Unturned.ItemAsset>(SDG.Unturned.Assets.all());
            byType.Sort((x, y) => x.id.CompareTo(y.id));   // stable order, so the catalog is the same every run
            foreach (var a in byType)
            {
                if (a == null || string.IsNullOrEmpty(a.guid)) continue;
                switch (a.type)
                {
                    case SDG.Unturned.EItemType.HAT: Salvage(a.id, (cloth, 1)); break;
                    case SDG.Unturned.EItemType.SHIRT:
                    case SDG.Unturned.EItemType.PANTS: Salvage(a.id, (cloth, 2)); break;
                    case SDG.Unturned.EItemType.BACKPACK: Salvage(a.id, (cloth, 2)); break;
                    case SDG.Unturned.EItemType.THROWABLE:
                        // ⚠ Not a snowball. It is a Throwable by type and gives neither metal nor propellant, and
                        // a recipe turning one into gunpowder is the kind of thing a type sweep produces if nobody
                        // reads what is in the category.
                        if (a.id == 1132) break;
                        Salvage(a.id, (scrap, 1), (powder, 1));
                        break;
                }
            }
            if (made > 0) Log.Print($"[bp] generated {made} salvage recipe(s)");
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
