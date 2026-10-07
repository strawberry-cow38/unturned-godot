using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Stackable crafting materials, the black-powder chain, and charcoal in a barbecue.
    ///
    /// Master 2026-10-07: "make duct tape, rope, metal scrap, nails stackable. add gunpowder, sulfur, screws,
    /// springs, pipes, gears, hinges as new items ... add a recipe for sulfur + charcoal to make black powder.
    /// black powder + fertilizer = gunpowder".
    ///
    /// ⚠ The recipes are keyed by GUID, not id, so the thing that silently breaks is a typo'd guid: the row loads,
    /// the blueprint exists, and no inventory on earth ever satisfies it. Every assertion here resolves the guid
    /// back to a REAL asset rather than trusting the string.</summary>
    public class CraftMaterials : GameTest
    {
        public override string Name => "craft.materials";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.Load();
            yield return Ticks(1);

            // ---- STACKABLE (master named four; the new components stack too) --------------------------------
            // ⚠ 69 is "Tape" in the catalog -- the retail name for what master calls duct tape. Asserted by ID so
            // this does not quietly start testing a different item if anything is ever renamed.
            foreach (var (id, least) in new[] { ((ushort)69, 10), ((ushort)64, 10), ((ushort)67, 20), ((ushort)71, 100),
                                                ((ushort)9338, 20), ((ushort)9339, 20), ((ushort)9340, 20) })
            {
                var a = Assets.find(id);
                T.Check($"item {id} exists", a != null);
                if (a != null)
                    T.Check($"{a.itemName} ({id}) stacks to at least {least} (got {a.stackSize})", a.stackSize >= least);
            }
            // CONTROL: stacking is not now universal -- a gun must still be one per slot, or "stackSize >= n" is
            // passing because something set every item in the game stackable.
            var rifle = Assets.find(4);
            T.Check($"control: a non-material is still unstackable ({rifle?.itemName} stack {rifle?.stackSize})",
                    rifle == null || rifle.stackSize <= 1);

            // ---- THE NEW COMPONENTS EXIST AND ARE REAL ITEMS ------------------------------------------------
            var wanted = new (ushort id, string name)[]
            {
                (9327, "Gunpowder"), (9328, "Sulfur"), (9329, "Screws"), (9330, "Springs"),
                (9331, "Gears"), (9332, "Hinges"), (9333, "Charcoal"), (9334, "Black Powder"), (9335, "Pipe"),
                (9338, "Ceramic"), (9339, "Circuitry"), (9340, "Asbestos"),
            };
            foreach (var w in wanted)
            {
                var a = Assets.find(w.id);
                T.Check($"{w.name} ({w.id}) is in the catalog as \"{a?.itemName}\"", a != null && a.itemName == w.name);
            }

            // ---- THE TWO RECIPES ---------------------------------------------------------------------------
            // Resolved by walking the loaded blueprints and matching on the OUTPUT's resolved asset, which is the
            // only form that proves the guid in the TSV actually names the item we meant.
            BlueprintDef Making(ushort outId)
            {
                foreach (var bp in BlueprintRegistry.All)
                    foreach (var o in bp.Outputs)
                        if (Assets.findByGuid(o.Guid)?.id == outId) return bp;
                return null;
            }
            bool Takes(BlueprintDef bp, ushort inId)
            {
                if (bp == null) return false;
                foreach (var i in bp.Inputs) if (Assets.findByGuid(i.Guid)?.id == inId) return true;
                return false;
            }

            var powder = Making(9334);
            T.Check("there is a recipe that makes Black Powder", powder != null);
            T.Check("...from Sulfur", Takes(powder, 9328));
            T.Check("...and Charcoal", Takes(powder, 9333));

            var gun = Making(9327);
            T.Check("there is a recipe that makes Gunpowder", gun != null);
            T.Check("...from Black Powder", Takes(gun, 9334));
            T.Check("...and Fertilizer", Takes(gun, 332));
            // ⭐ THE CHAIN IS TWO STEPS, which is what master asked for. If anyone later folds it into one recipe
            // that takes sulfur straight to gunpowder, this is the check that notices.
            T.Check("gunpowder does NOT come straight from sulfur -- the chain stays two steps", !Takes(gun, 9328));

            // ---- CHARCOAL IN A BARBECUE --------------------------------------------------------------------
            int coal = 0, food = 0, bad = 0;
            for (int i = 0; i < 400; i++)
            {
                int id = LootTables.Roll(LootTables.Barbecue);
                if (id == 9333) coal++;
                else if (id < 0) bad++;
                else { var a = Assets.find((ushort)id); if (a != null && a.type == EItemType.FOOD) food++; else bad++; }
            }
            GD.Print($"[craft-test] barbecue rolls: {coal} charcoal, {food} food, {bad} neither");
            T.Check($"a barbecue holds charcoal ({coal}/400)", coal > 0);
            T.Check($"...and still holds the food it used to ({food}/400)", food > 0);
            T.Check($"...and nothing else ({bad} unexpected)", bad == 0);

            // ---- MAKESHIFT SCOPE = BINOCULARS + TAPE (master 2026-10-07) -----------------------------------
            var scope = Making(476);
            T.Check("there is a recipe that makes the Makeshift Scope", scope != null);
            T.Check("...from Binoculars", Takes(scope, 333));
            T.Check("...and Tape", Takes(scope, 69));
            // ⚠ The binoculars are CONSUMED -- you are sacrificing them for the lens. A non-consumed input would
            // make this a tool recipe that prints free scopes, which is the opposite of the trade being described.
            bool binosConsumed = false;
            if (scope != null)
                foreach (var i in scope.Inputs)
                    if (Assets.findByGuid(i.Guid)?.id == 333) binosConsumed = i.Consume;
            T.Check("...and the binoculars are consumed, not used as a tool", binosConsumed);

            // ---- AN AMMO RECIPE FOR EVERY AMMO (master 2026-10-07) -----------------------------------------
            //
            // ⭐ Asserted over the SET, not over a list I wrote down. "Every ammo" is the requirement, so the test
            // enumerates isAmmo the same way the generator does -- if someone adds a cartridge and the generator
            // stops covering it, this fails without anyone remembering to add a case.
            int ammo = 0, covered = 0; string uncovered = null;
            int minScrap = int.MaxValue, maxScrap = 0, minBatch = int.MaxValue, maxBatch = 0;
            foreach (var a in Assets.all())
            {
                if (a == null || !a.isAmmo || string.IsNullOrEmpty(a.guid)) continue;
                ammo++;
                BlueprintDef made = null;
                foreach (var bp in BlueprintRegistry.All)
                {
                    if (bp.Operation != "Craft") continue;
                    foreach (var o in bp.Outputs) if (Assets.findByGuid(o.Guid)?.id == a.id) { made = bp; break; }
                    if (made != null) break;
                }
                if (made == null) { uncovered ??= $"{a.itemName} ({a.id})"; continue; }
                covered++;

                int scrapAmt = 0, powderAmt = 0, batch = 0;
                foreach (var i in made.Inputs)
                {
                    var ia = Assets.findByGuid(i.Guid);
                    if (ia?.id == 67) scrapAmt = i.Amount;
                    else if (ia?.id == 9327) powderAmt = i.Amount;
                }
                foreach (var o in made.Outputs) if (Assets.findByGuid(o.Guid)?.id == a.id) batch = o.Amount;

                if (scrapAmt <= 0 || powderAmt <= 0)
                    uncovered ??= $"{a.itemName} is made without scrap+powder";
                minScrap = Mathf.Min(minScrap, scrapAmt); maxScrap = Mathf.Max(maxScrap, scrapAmt);
                minBatch = Mathf.Min(minBatch, batch); maxBatch = Mathf.Max(maxBatch, batch);
            }
            GD.Print($"[craft-test] ammo: {covered}/{ammo} craftable, scrap {minScrap}-{maxScrap}, batch {minBatch}-{maxBatch}");
            T.Check($"there is ammo in the catalog to check ({ammo})", ammo > 0);
            T.Check($"every ammo has a recipe ({covered}/{ammo}, first gap: {uncovered ?? "none"})",
                    covered == ammo && uncovered == null);
            T.Check($"every one costs metal scrap AND gunpowder ({uncovered ?? "ok"})", uncovered == null);

            // ⭐ THE RATIO IS BALANCED, i.e. it actually VARIES with the round. A generator that returned the same
            // cost for everything would satisfy "every ammo has a recipe" perfectly.
            T.Check($"the cost varies by round rather than being flat (scrap {minScrap}-{maxScrap})", maxScrap > minScrap);
            T.Check($"...and so does the batch ({minBatch}-{maxBatch})", maxBatch > minBatch);

            // ⚠ A BIG round must cost MORE PER ROUND than a small one -- the actual meaning of "balanced ratios".
            // 12-gauge buckshot (113, stacks 32) vs 9mm (5007, stacks 180).
            float PerRound(ushort id)
            {
                foreach (var bp in BlueprintRegistry.All)
                {
                    if (bp.Operation != "Craft") continue;
                    int batch = 0, cost = 0;
                    foreach (var o in bp.Outputs) if (Assets.findByGuid(o.Guid)?.id == id) batch = o.Amount;
                    if (batch <= 0) continue;
                    foreach (var i in bp.Inputs) { var ia = Assets.findByGuid(i.Guid); if (ia?.id == 67 || ia?.id == 9327) cost += i.Amount; }
                    return cost / (float)batch;
                }
                return -1f;
            }
            float shell = PerRound(113), pistol = PerRound(5007);
            GD.Print($"[craft-test] per-round cost: 12ga {shell:0.00}, 9mm {pistol:0.00}");
            if (shell > 0f && pistol > 0f)
                T.Check($"a 12-gauge shell costs more per round than a 9mm ({shell:0.00} vs {pistol:0.00})", shell > pistol);

            // ---- SALVAGE (master 2026-10-07) ----------------------------------------------------------------
            //
            // Helper: the recipe that CONSUMES this item, and what it yields.
            (BlueprintDef bp, Dictionary<ushort,int> yields) SalvageOf(ushort id)
            {
                foreach (var bp in BlueprintRegistry.All)
                {
                    if (bp.Operation != "Craft") continue;
                    bool consumesIt = false;
                    foreach (var i in bp.Inputs) if (i.Consume && Assets.findByGuid(i.Guid)?.id == id) { consumesIt = true; break; }
                    if (!consumesIt) continue;
                    var y = new Dictionary<ushort,int>();
                    foreach (var o in bp.Outputs) { var oa = Assets.findByGuid(o.Guid); if (oa != null) y[oa.id] = o.Amount; }
                    return (bp, y);
                }
                return (null, null);
            }
            bool Yields(ushort item, ushort mat, int atLeast)
            {
                var (bp, y) = SalvageOf(item);
                return bp != null && y.TryGetValue(mat, out int n) && n >= atLeast;
            }

            // THE NAMED ONES. ⚠ Each also asserts it did NOT fall through to the plain category rule -- the whole
            // risk with "overrides first, then a sweep" is an override that silently never ran.
            T.Check("firefighter top salvages to asbestos", Yields(233, 9340, 1));
            T.Check("...and cloth", Yields(233, 66, 1));
            T.Check("firefighter bottom salvages to asbestos", Yields(234, 9340, 1));
            T.Check("firefighter helmet salvages to asbestos", Yields(241, 9340, 1));
            T.Check("...and scrap, not cloth", Yields(241, 67, 1) && !Yields(241, 66, 1));
            foreach (ushort mh in new ushort[] { 309, 1010, 1335, 1519 })
                T.Check($"military helmet {mh} salvages to 2 scrap", Yields(mh, 67, 2));

            // THE CATEGORIES, counted rather than spot-checked: "all hats" is the requirement.
            int hats = 0, hatsCovered = 0, clothes = 0, clothesCovered = 0, packs = 0, packsCovered = 0;
            foreach (var a in Assets.all())
            {
                if (a == null || string.IsNullOrEmpty(a.guid)) continue;
                if (a.type == EItemType.HAT) { hats++; if (SalvageOf(a.id).bp != null) hatsCovered++; }
                else if (a.type == EItemType.SHIRT || a.type == EItemType.PANTS) { clothes++; if (SalvageOf(a.id).bp != null) clothesCovered++; }
                else if (a.type == EItemType.BACKPACK) { packs++; if (SalvageOf(a.id).bp != null) packsCovered++; }
            }
            GD.Print($"[craft-test] salvage: hats {hatsCovered}/{hats}, shirts+pants {clothesCovered}/{clothes}, backpacks {packsCovered}/{packs}");
            T.Check($"every hat can be salvaged ({hatsCovered}/{hats})", hats > 0 && hatsCovered == hats);
            T.Check($"shirts and pants can be salvaged ({clothesCovered}/{clothes})", clothes > 0 && clothesCovered == clothes);
            T.Check($"backpacks can be salvaged ({packsCovered}/{packs})", packs > 0 && packsCovered == packs);

            // THROWABLES -> scrap + gunpowder, and the snowball exclusion.
            T.Check("a frag grenade salvages to scrap", Yields(254, 67, 1));
            T.Check("...and gunpowder", Yields(254, 9327, 1));
            T.Check("a smoke grenade salvages to gunpowder", Yields(267, 9327, 1));
            T.Check("a flare salvages to gunpowder", Yields(259, 9327, 1));
            // ⭐ CONTROL: a type sweep that read nothing would have turned a SNOWBALL into gunpowder.
            T.Check("control: a snowball does NOT salvage into gunpowder", !Yields(1132, 9327, 1));

            // ---- METAL GIVES SCRAP, FIREPROOF GIVES ASBESTOS (master 2026-10-07) --------------------------
            //
            // ⭐ Both are derived from the item's OWN data -- `armor < 1` (a damage multiplier, so below 1 means it
            // actually stops something, i.e. there is a plate in it) and `proofFire`. So the test asserts the
            // RULE over the whole catalog rather than spot-checking garments I happened to think of.
            int armoured = 0, armouredWithScrap = 0, fireproof = 0, fireproofWithAsbestos = 0;
            string missScrap = null, missAsb = null;
            foreach (var a in Assets.all())
            {
                if (a == null || string.IsNullOrEmpty(a.guid)) continue;
                bool wearable = a.type == EItemType.HAT || a.type == EItemType.SHIRT || a.type == EItemType.PANTS
                             || a.type == EItemType.BACKPACK || a.type == EItemType.VEST;
                if (!wearable) continue;
                if (a.armor < 0.95f)
                {
                    armoured++;
                    if (Yields(a.id, 67, 1)) armouredWithScrap++; else missScrap ??= $"{a.itemName} ({a.id}, armor {a.armor:0.00})";
                }
                if (a.proofFire)
                {
                    fireproof++;
                    if (Yields(a.id, 9340, 1)) fireproofWithAsbestos++; else missAsb ??= $"{a.itemName} ({a.id})";
                }
            }
            GD.Print($"[craft-test] armoured {armouredWithScrap}/{armoured} give scrap; fireproof {fireproofWithAsbestos}/{fireproof} give asbestos");
            T.Check($"there is armoured gear to check ({armoured})", armoured > 0);
            T.Check($"every armoured garment also gives scrap ({armouredWithScrap}/{armoured}, first gap: {missScrap ?? "none"})",
                    armoured > 0 && armouredWithScrap == armoured);
            T.Check($"every fireproof garment gives asbestos ({fireproofWithAsbestos}/{fireproof}, first gap: {missAsb ?? "none"})",
                    fireproof == 0 || fireproofWithAsbestos == fireproof);
            // ⭐ CONTROL: a PLAIN garment must NOT give scrap, or "armoured gives scrap" is passing because
            // everything does and the rule is not discriminating at all.
            var plainShirt = Assets.find(3);   // Orange Hoodie
            T.Check($"control: a plain hoodie gives cloth but NOT scrap", Yields(3, 66, 1) && !Yields(3, 67, 1));

            // ---- GAS MASK + FILTER (master 2026-10-07) ----------------------------------------------------
            T.Check("a gasmask salvages to asbestos", Yields(1270, 9340, 1));
            T.Check("...and scrap", Yields(1270, 67, 1));
            T.Check("a filter salvages to asbestos", Yields(1271, 9340, 1));
            T.Check("...and scrap", Yields(1271, 67, 1));

            // ---- THE VEST SLOT IS A GRAB BAG (master 2026-10-07) ------------------------------------------
            // "did u make all vests scrap? bc theres sweatervests and ponchos lol" -- and worse, the slot also
            // holds a Rose and a Parrot. Pinned with real examples so the rule cannot quietly widen again.
            var sweater = Assets.find(215); var poncho = Assets.find(410);
            GD.Print($"[craft-test] vests: sweatervest armor {sweater?.armor:0.00}, poncho {poncho?.armor:0.00}, "
                   + $"police {Assets.find(10)?.armor:0.00}, rose {Assets.find(531)?.armor:0.00}, parrot {Assets.find(606)?.armor:0.00}");
            T.Check("a sweatervest gives cloth", Yields(215, 66, 1));
            T.Check("...but NOT scrap -- it is a jumper", !Yields(215, 67, 1));
            T.Check("a poncho gives cloth and not scrap", Yields(410, 66, 1) && !Yields(410, 67, 1));
            T.Check("a police vest DOES give scrap", Yields(10, 67, 1));
            T.Check("a military vest gives scrap", Yields(310, 67, 1));
            // ⭐ The slot furniture is not clothing and salvages into nothing at all.
            T.Check("a Rose does not salvage", SalvageOf(531).bp == null);
            T.Check("a Parrot does not salvage", SalvageOf(606).bp == null);

            // ---- KITCHENWARE BREAKS INTO WHAT IT IS MADE OF (master 2026-10-07) ----------------------------
            // ⚠⚠ EVERY PLACEHOLDER MUST CARRY A GUID. Blueprints key by guid, so an item without one is
            // unreferenceable: it loads, shows in the catalog, and silently cannot appear in any recipe. My first
            // batch (9300-9326) was written without the guid column and that is exactly how it failed -- "a plate
            // salvages to ceramic" with the plate having no identity to put in the recipe.
            int noGuid = 0; string firstNoGuid = null;
            for (ushort id = 9300; id <= 9341; id++)
            {
                var a = Assets.find(id);
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.guid)) { noGuid++; firstNoGuid ??= $"{a.itemName} ({id})"; }
            }
            T.Check($"every placeholder item has a guid ({noGuid} missing, first: {firstNoGuid ?? "none"})", noGuid == 0);

            {
                var cer = Assets.find(9338); var gl = Assets.find(9341); var plate = Assets.find(9302);
                var (pbp, py) = SalvageOf(9302);
                GD.Print($"[craft-test] diag: ceramic={(cer != null ? cer.itemName : "NULL")} glass={(gl != null ? gl.itemName : "NULL")} "
                       + $"plate={(plate != null ? plate.itemName : "NULL")} plateGuid={(plate?.guid ?? "-")} "
                       + $"salvage={(pbp != null ? pbp.Name : "NONE")} yields={(py == null ? "-" : string.Join(",", py.Keys))}");
            }
            T.Check("a plate salvages to ceramic", Yields(9302, 9338, 1));
            T.Check("a bowl salvages to ceramic", Yields(9303, 9338, 1));
            T.Check("a cup salvages to ceramic", Yields(9300, 9338, 1));
            T.Check("the retail Ceramic Plate salvages to ceramic", Yields(1928, 9338, 1));
            T.Check("the retail Ceramic Bowl salvages to ceramic", Yields(1930, 9338, 1));
            // ⚠ The drinking glass sits in the CROCKERY list because that is where it is stocked, and it is the one
            // piece in that list that is not ceramic. Breaking it into ceramic would be the tidy answer and wrong.
            T.Check("a drinking glass salvages to GLASS", Yields(9301, 9341, 1));
            T.Check("...and not to ceramic", !Yields(9301, 9338, 1));
            T.Check("a fork salvages to scrap", Yields(9304, 67, 1));
            T.Check("a spoon salvages to scrap", Yields(9305, 67, 1));
            T.Check("a pot salvages to more scrap than a fork does", Yields(9307, 67, 2));
            T.Check("a pan salvages to scrap", Yields(9308, 67, 2));

            yield break;
        }
    }
}
