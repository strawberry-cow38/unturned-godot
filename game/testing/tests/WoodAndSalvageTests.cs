using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Redwood and oak lumber, the firewood recipes, and salvage hiding itself when you have nothing to
    /// salvage (strawberry 2026-10-10: "work on adding redwood and oak sticks, planks and logs, as well as the
    /// recipes for firewood etc. we should hide salvage recipes in the crafting menu unless we have the item to
    /// salvage").
    ///
    /// ⭐ The salvage section's CONTROLS are the point of it. "The Salvage tab is empty" passes just as well when
    /// the registry never loaded, so SalvageCount is asserted first; and "hiding worked" passes if the filter ate
    /// the whole menu, so the All count is held EQUAL across the same change. Either control alone would have let
    /// a much worse bug through.</summary>
    public sealed class WoodAndSalvage : GameTest
    {
        public override string Name => "craft.wood_salvage";
        public override double TimeoutSimSeconds => 30;

        const ushort RedLog = 9348, RedStick = 9349, RedPlank = 9350;
        const ushort OakLog = 9351, OakStick = 9352, OakPlank = 9353;
        const ushort Saw = 141, Axe = 16, FireAxe = 104, Firewood = 9342;
        const ushort BirchLog = 37, MapleLog = 39, PineLog = 41;
        const ushort SalvageMe = 233;   // Firefighter Top: a retail item with a generated salvage recipe

        static Crafting.DictInv Inv(params (ushort id, int n)[] have)
        {
            var inv = new Crafting.DictInv();
            foreach (var (id, n) in have) inv.Add(id, n);
            return inv;
        }

        /// <summary>The recipe that turns `from` into `into`, as the menu would find it.</summary>
        static BlueprintDef Recipe(ushort from, ushort into)
        {
            foreach (var bp in BlueprintRegistry.Index())
            {
                bool usesFrom = false, makesInto = false;
                foreach (var i in bp.Inputs) if (i.Consume && Crafting.Resolve(i.Guid) == from) usesFrom = true;
                foreach (var o in bp.Outputs) if (Crafting.Resolve(o.Guid) == into) makesInto = true;
                if (usesFrom && makesInto) return bp;
            }
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();

            // ---- THE ITEMS EXIST AND CARRY THEIR OWN IDENTITY ---------------------------------------------
            foreach (var (id, want) in new (ushort, string)[]
                     { (RedLog, "Redwood Log"), (RedStick, "Redwood Stick"), (RedPlank, "Redwood Plank"),
                       (OakLog, "Oak Log"), (OakStick, "Oak Stick"), (OakPlank, "Oak Plank") })
            {
                var a = Assets.find(id);
                T.Check($"item {id} is {want} (got '{a?.itemName ?? "null"}')", a != null && a.itemName == want);
                // ⚠ The guid must round-trip: every recipe addresses items BY GUID, so a row with a typo'd guid
                // parses fine, lists fine, and silently never resolves an ingredient.
                T.Check($"...and its guid resolves back to {id}",
                        a != null && Crafting.Resolve(a.guid) == id);
            }

            // ⚠ A NEW WOOD MUST STACK LIKE THE OLD ONES. Held against BIRCH rather than a typed 4/6/8: the
            // question is "does redwood behave like the species that already shipped", and a literal would
            // still pass if someone retuned birch and left the new woods behind.
            foreach (var (newId, like, what) in new (ushort, ushort, string)[]
                     { (RedLog, BirchLog, "redwood log"), (OakLog, BirchLog, "oak log"),
                       (RedPlank, 62, "redwood plank"), (OakPlank, 62, "oak plank"),
                       (RedStick, 38, "redwood stick"), (OakStick, 38, "oak stick") })
            {
                var a = Assets.find(newId); var b = Assets.find(like);
                T.Check($"{what} stacks like its birch counterpart ({a?.stackSize} vs {b?.stackSize})",
                        a != null && b != null && a.stackSize == b.stackSize && a.stackSize > 1);
            }

            // ---- THE LUMBER CHAIN: log -> 2 planks -> 2 sticks, species preserved --------------------------
            foreach (var (species, log, plank, stick) in new (string, ushort, ushort, ushort)[]
                     { ("redwood", RedLog, RedPlank, RedStick), ("oak", OakLog, OakPlank, OakStick) })
            {
                var toPlank = Recipe(log, plank);
                T.Check($"{species}: a log->plank recipe exists", toPlank != null);
                if (toPlank == null) continue;

                var inv = Inv((log, 1));
                T.Check($"{species}: ...and refuses without the saw", !Crafting.CanCraft(toPlank, inv, out _));
                inv.Add(Saw, 1);
                T.Check($"{species}: ...crafts with it", Crafting.DoCraft(toPlank, inv));
                T.Check($"{species}: one log yields TWO planks (got {inv.Count(plank)})", inv.Count(plank) == 2);
                T.Check($"{species}: the log is spent (have {inv.Count(log)})", inv.Count(log) == 0);
                // ⭐ The saw is a TOOL. If this ever reads 0 the recipe is eating the player's saw every craft.
                T.Check($"{species}: the saw is NOT consumed (have {inv.Count(Saw)})", inv.Count(Saw) == 1);

                var toStick = Recipe(plank, stick);
                T.Check($"{species}: a plank->stick recipe exists", toStick != null);
                if (toStick == null) continue;
                T.Check($"{species}: ...crafts", Crafting.DoCraft(toStick, inv));
                T.Check($"{species}: one plank yields TWO sticks (got {inv.Count(stick)})", inv.Count(stick) == 2);
            }

            // ⚠ SPECIES DOES NOT LEAK. The whole chain addresses items by guid, and a copied row with one guid
            // left unchanged would quietly turn oak logs into redwood planks while every count above still passed.
            {
                var inv = Inv((OakLog, 1), (Saw, 1));
                Crafting.DoCraft(Recipe(OakLog, OakPlank), inv);
                T.Check($"an oak log makes NO redwood planks ({inv.Count(RedPlank)})", inv.Count(RedPlank) == 0);
            }

            // ---- FIREWOOD: every species, one shared output, split with an axe ----------------------------
            foreach (var (species, log) in new (string, ushort)[]
                     { ("birch", BirchLog), ("maple", MapleLog), ("pine", PineLog),
                       ("redwood", RedLog), ("oak", OakLog) })
            {
                var bp = RecipeUsingTool(log, Firewood, Axe);
                T.Check($"firewood: {species} has a recipe", bp != null);
                if (bp == null) continue;
                var inv = Inv((log, 1));
                T.Check($"firewood: {species} refuses without an axe", !Crafting.CanCraft(bp, inv, out _));
                inv.Add(Axe, 1);
                T.Check($"firewood: {species} splits", Crafting.DoCraft(bp, inv));
                T.Check($"firewood: {species} yields 2 (got {inv.Count(Firewood)})", inv.Count(Firewood) == 2);
                T.Check($"firewood: {species} keeps the axe", inv.Count(Axe) == 1);
                // ⭐ The generator pairs every log with BOTH axes. The five rows I hand-wrote first had one.
                var withFireAxe = RecipeUsingTool(log, Firewood, FireAxe);
                T.Check($"firewood: {species} also has a Fire Axe recipe", withFireAxe != null);
                if (withFireAxe != null)
                {
                    var fire = Inv((log, 1), (FireAxe, 1));
                    T.Check($"firewood: {species} splits with the Fire Axe too", Crafting.DoCraft(withFireAxe, fire));
                    T.Check($"firewood: {species} yields 2 that way as well ({fire.Count(Firewood)})",
                            fire.Count(Firewood) == 2);
                }
            }
            // The item's own description promises this: "No species left on it, so it stacks with any other
            // firewood." Five recipes, ONE output item -- so they stack.
            T.Check("firewood: nothing is made FROM firewood", RecipeConsuming(Firewood) == null);

            // ---- SALVAGE HIDES UNTIL YOU HOLD THE ITEM ----------------------------------------------------
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            var craft = p.DebugCraftMenu;
            var pinv = p.Inventory;
            T.Check($"CONTROL: salvage recipes exist at all ({BlueprintRegistry.SalvageCount})",
                    BlueprintRegistry.SalvageCount > 0);
            var mine = BlueprintRegistry.SalvageFor(SalvageMe);
            T.Check($"CONTROL: item {SalvageMe} has a salvage recipe", mine != null);

            // ⚠⚠ OPEN IT. ComputeData() runs in Open() and NOWHERE else, so a menu that was never opened has an
            // empty recipe list -- and "the Salvage tab is empty" then passes with the feature reverted. That is
            // exactly how this test first went green-adjacent; the control below is what caught it.
            craft.Open();
            yield return Ticks(2);
            T.Check($"CONTROL: the recipe IS in the menu's dataset (category '{craft.DebugCategoryOf(mine)}')",
                    craft.DebugCategoryOf(mine) == "Salvage");

            craft.DebugSetCategory("Salvage");
            yield return Ticks(2);
            int emptyRows = craft.DebugView().Count;
            int allEmpty = craft.DebugCountFor("All");
            T.Check($"with nothing to salvage the Salvage tab is empty ({emptyRows} rows)", emptyRows == 0);
            T.Check($"...and the count agrees with the grid ({craft.DebugCountFor("Salvage")})",
                    craft.DebugCountFor("Salvage") == 0);

            // ⚠ ISOLATE THE ADD FROM THE FILTER. "No rows" is what a failed tryAddItem looks like too, and the
            // empty-tab check above would have passed either way -- so prove the item is actually held first.
            pinv.tryAddItem(new Item(SalvageMe, 1));
            yield return Ticks(2);
            T.Check($"the item is actually in the bag ({pinv.getItemCount(SalvageMe)})",
                    pinv.getItemCount(SalvageMe) > 0);
            T.Check($"...and the menu is looking at that same inventory",
                    ReferenceEquals(craft.Inv, pinv));
            craft.Open();   // recompute against the new bag contents, the same way opening the menu does
            craft.DebugSetCategory("Salvage");
            yield return Ticks(2);
            var rows = craft.DebugView();
            bool only = rows.Count == 1 && ReferenceEquals(rows[0], mine);
            T.Check($"holding one reveals exactly its own recipe ({rows.Count} row(s))", only);
            T.Check($"...and the count still agrees ({craft.DebugCountFor("Salvage")})",
                    craft.DebugCountFor("Salvage") == rows.Count);

            // ⭐⭐ THE CONTROL THAT MATTERS. "Hidden" is also what a filter that ate everything looks like, so the
            // NON-salvage half of the menu must be byte-identical across the same change.
            T.Check($"CONTROL: the All category is untouched ({allEmpty} -> {craft.DebugCountFor("All")})",
                    craft.DebugCountFor("All") == allEmpty);
        }

        /// <summary>The recipe turning `from` into `into` while HOLDING `tool` (not consuming it), or null.</summary>
        static BlueprintDef RecipeUsingTool(ushort from, ushort into, ushort tool)
        {
            foreach (var bp in BlueprintRegistry.Index())
            {
                bool usesFrom = false, makesInto = false, holdsTool = false;
                foreach (var i in bp.Inputs)
                {
                    if (i.Consume && Crafting.Resolve(i.Guid) == from) usesFrom = true;
                    if (!i.Consume && Crafting.Resolve(i.Guid) == tool) holdsTool = true;
                }
                foreach (var o in bp.Outputs) if (Crafting.Resolve(o.Guid) == into) makesInto = true;
                if (usesFrom && makesInto && holdsTool) return bp;
            }
            return null;
        }

        /// <summary>Any recipe that CONSUMES `id`, or null.</summary>
        static BlueprintDef RecipeConsuming(ushort id)
        {
            foreach (var bp in BlueprintRegistry.Index())
                foreach (var i in bp.Inputs)
                    if (i.Consume && Crafting.Resolve(i.Guid) == id) return bp;
            return null;
        }
    }
}
