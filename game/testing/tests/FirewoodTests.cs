using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Firewood: split a log with an axe, stack it whatever it came from, burn it like a log.
    ///
    /// Master 2026-10-07: "add a new firewood item. crafted with logs + axe (tool) 1 log:2 firewood. firewood
    /// doesnt have a 'type' so it stacks regardless of the log that made it. burns at the sane rate as logs,
    /// stacks to 12 per 2x1. firewood can also spawn in bbqs".
    ///
    /// ⭐ THIS WALKS THE PLAYER'S PATH, because that is the only thing that would have caught the bug next door:
    /// a log and an axe go in the bag, the recipe is taken from the REACHABLE index (not the raw catalog), it is
    /// queued through the entry point the quick-craft tile uses, the clock is advanced, and the bag is read back.
    /// Each of master's five clauses is a check, and the species-less stack is tested with two DIFFERENT species
    /// rather than two of the same -- "it stacks" is true of any item, "it stacks across species" is the ask.</summary>
    public class Firewood : GameTest
    {
        public override string Name => "craft.firewood";

        const ushort BirchLog = 37, MapleLog = 39, PineLog = 41, CampAxe = 16, FireAxe = 104, Scrap = 67;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();
            BlueprintRegistry.Load();
            yield return Ticks(1);

            // ---- THE ITEM ------------------------------------------------------------------------------------
            var fw = Assets.find(Cooking.FirewoodId);
            T.Check($"Firewood exists (#{Cooking.FirewoodId})", fw != null);
            if (fw == null) yield break;
            T.Check($"...2x1 and a SUPPLY ({fw.size_x}x{fw.size_y} {fw.type})",
                    fw.size_x == 2 && fw.size_y == 1 && fw.type == EItemType.SUPPLY);
            T.Check($"...stacks to 12 ({fw.stackSize})", fw.stackSize == 12);
            // ⚠ A guid is not cosmetic: a blueprint names its items BY GUID, so an item without one cannot be an
            // ingredient or a product of anything. That is why the old Add()-only charcoal was uncraftable.
            T.Check("...and carries a guid, so a recipe can name it", !string.IsNullOrEmpty(fw.guid));

            // ---- THE RECIPES, taken from the reachable index -------------------------------------------------
            var chops = new List<BlueprintDef>();
            foreach (var bp in BlueprintRegistry.Index())
                if (bp.Outputs.Count == 1 && Assets.findByGuid(bp.Outputs[0].Guid)?.id == Cooking.FirewoodId)
                    chops.Add(bp);
            // ⭐ One per (log, axe). 5 woods x 2 axes since redwood and oak joined the generator on 2026-10-10 --
            // expressed as the product so the next wood moves this with it, instead of someone bumping a bare 6.
            const int Woods = 5, Axes = 2;   // Birch/Maple/Pine/Redwood/Oak x Camp Axe/Fire Axe
            T.Check($"every (log, axe) pair is REACHABLE -- {Woods} species x {Axes} axes ({chops.Count})",
                    chops.Count == Woods * Axes);

            BlueprintDef Chop(ushort logId, ushort axeId)
            {
                foreach (var bp in chops)
                {
                    bool log = false, axe = false;
                    foreach (var i in bp.Inputs)
                    {
                        var a = Assets.findByGuid(i.Guid);
                        if (a == null) continue;
                        if (a.id == logId && i.Consume) log = true;
                        if (a.id == axeId && !i.Consume) axe = true;   // the AXE MUST NOT BE CONSUMED: master's "(tool)"
                    }
                    if (log && axe) return bp;
                }
                return null;
            }

            var birchChop = Chop(BirchLog, CampAxe);
            T.Check("a Birch Log + Camp Axe recipe exists, with the axe as a TOOL (not consumed)", birchChop != null);
            T.Check("...and a Pine Log + Fire Axe one too", Chop(PineLog, FireAxe) != null);
            if (birchChop == null) yield break;
            T.Check($"...1 log makes 2 firewood ({birchChop.Inputs[0].Amount} -> {birchChop.Outputs[0].Amount})",
                    birchChop.Outputs[0].Amount == 2);
            // Falls out of the tool flag rather than being wired: Durability.RegisterTools walks every recipe's
            // non-consumed inputs, so declaring the axe a tool is what makes chopping wear it.
            T.Check($"...and both axes are registered as wearing TOOLS ({Durability.ToolIds.Count} tools)",
                    Durability.ToolIds.Contains(CampAxe) && Durability.ToolIds.Contains(FireAxe));

            // ---- CRAFT IT, through the entry point the quick-craft tile uses ---------------------------------
            var inv = new PlayerInventory();
            T.Check("fixture: a birch log and a camp axe are in the bag",
                    inv.tryAddItem(new Item(BirchLog)) != null && inv.tryAddItem(new Item(CampAxe)) != null);
            var menu = new CraftingMenu { Inv = inv };
            menu.QueueCraft(birchChop, 1);
            T.Check($"queuing took the log ({inv.getItemCount(BirchLog)} left)", inv.getItemCount(BirchLog) == 0);
            T.Check($"...and LEFT THE AXE ALONE ({inv.getItemCount(CampAxe)})", inv.getItemCount(CampAxe) == 1);
            T.Check("...with nothing produced yet", inv.getItemCount(Cooking.FirewoodId) == 0);

            menu.DebugTick(4.5f);   // the recipe's 4 s
            T.Check($"after it finishes: 2 firewood ({inv.getItemCount(Cooking.FirewoodId)})",
                    inv.getItemCount(Cooking.FirewoodId) == 2);
            T.Check($"...and the axe survived the job ({inv.getItemCount(CampAxe)})", inv.getItemCount(CampAxe) == 1);

            // ---- IT HAS NO SPECIES: two different logs land in ONE stack -------------------------------------
            // ⭐ THE ACTUAL ASK. Crafting twice from the same log would stack for any item; the claim is that a
            // PINE log and a BIRCH log give back the same indistinguishable firewood.
            var pineChop = Chop(PineLog, CampAxe);
            T.Check("a Pine Log + Camp Axe recipe exists", pineChop != null);
            if (pineChop == null) yield break;
            T.Check("fixture: a pine log goes in", inv.tryAddItem(new Item(PineLog)) != null);
            menu.QueueCraft(pineChop, 1);
            menu.DebugTick(4.5f);
            T.Check($"pine gave firewood too ({inv.getItemCount(Cooking.FirewoodId)} total)",
                    inv.getItemCount(Cooking.FirewoodId) == 4);
            int jars = 0;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
                for (byte i = 0; i < inv.items[b].getItemCount(); i++)
                    if (inv.items[b].getItem(i)?.item?.id == Cooking.FirewoodId) jars++;
            T.Check($"...and birch firewood and pine firewood are ONE stack, not two ({jars} jar(s))", jars == 1);

            // ---- IT BURNS, AT A LOG'S RATE -------------------------------------------------------------------
            var birch = Assets.find(BirchLog);
            T.Check("firewood counts as wood a campfire will take", Cooking.IsWood(fw));
            T.Check($"...and burns for exactly as long as a birch log ({Cooking.BurnSecondsFor(fw):0.#}s vs {Cooking.BurnSecondsFor(birch):0.#}s)",
                    birch != null && Mathf.Abs(Cooking.BurnSecondsFor(fw) - Cooking.BurnSecondsFor(birch)) < 0.01f);
            // CONTROL that must fail: without it, a BurnSecondsFor that returned the same number for everything
            // would satisfy the check above.
            var scrap = Assets.find(Scrap);
            T.Check($"control: metal scrap does not burn ({Cooking.BurnSecondsFor(scrap):0.#}s)",
                    scrap != null && Cooking.BurnSecondsFor(scrap) == 0f && !Cooking.IsWood(scrap));

            // ---- A BARBECUE BURNS IT, AND SO DOES A CAMPFIRE -------------------------------------------------
            // Master 2026-10-07: "bbq should burn logs, sticks, planks, firewood". The four named things are
            // checked by NAME here and the rule is derived from their type, so if the two ever disagree this is
            // the check that notices -- and the control is a wooden DOORWAY, which a campfire takes and a grill
            // must not, because without it "IsGrillWood" passing for everything wooden would look correct.
            foreach (var (id, what) in new[] { (BirchLog, "a log"), ((ushort)38, "a stick"), ((ushort)62, "a plank"),
                                               (Cooking.FirewoodId, "firewood"), (Cooking.CharcoalId, "charcoal") })
            {
                var a = Assets.find(id);
                T.Check($"a barbecue burns {what} (#{id} {a?.itemName})",
                        a != null && Cooking.IsFuelFor(ECookerKind.Barbecue, a));
            }
            var doorway = Assets.find(32);   // Maple Doorway -- wooden, but a STRUCTURE
            T.Check($"control: a barbecue will NOT burn {doorway?.itemName} ({doorway?.type})",
                    doorway != null && Cooking.IsWood(doorway) && !Cooking.IsFuelFor(ECookerKind.Barbecue, doorway));
            T.Check("...though a campfire still will", doorway != null && Cooking.IsFuelFor(ECookerKind.Campfire, doorway));

            // ---- IT IS A ONE-WAY CHOICE ----------------------------------------------------------------------
            // Master 2026-10-07: "firewood cant be used to craft anything else, its a one way 'i WILL burn this'
            // choice" -- that is what pays for the doubled burn time, so it is an invariant, not an accident of
            // nobody having written a recipe yet.
            var usesFirewood = new List<string>();
            foreach (var bp in BlueprintRegistry.All)
                foreach (var i in bp.Inputs)
                    if (Assets.findByGuid(i.Guid)?.id == Cooking.FirewoodId) usesFirewood.Add(bp.Name ?? "(unnamed)");
            T.Check($"nothing crafts WITH firewood -- burning it is the only use ({usesFirewood.Count}: {string.Join(", ", usesFirewood)})",
                    usesFirewood.Count == 0);

            yield break;
        }
    }
}
