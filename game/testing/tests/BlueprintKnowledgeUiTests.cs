using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>The CLIENT half of blueprint knowledge (strawberry 2026-10-04: "these recipes dont show unless a result of
    /// a search the user did in the craft menu, these appear grayed out with padlock over them"). The server half -- the
    /// refusal, the per-player set, the save -- is L0 (BlueprintKnowledgeTests); this is what the player sees.
    ///
    /// Fixture: the shipped catalog plus ONE locked row, a second plank -> stick recipe taught by Metal Scrap. A stick
    /// recipe already exists unlocked, so a search for sticks must show BOTH -- the open one plain, the locked one with
    /// a padlock -- and browsing must show only the open one. That pairing is what separates "hidden from browsing"
    /// from "hidden, full stop".</summary>
    public sealed class BlueprintKnowledgeUiTests : GameTest
    {
        public override string Name => "craft.blueprint_knowledge";
        public override double TimeoutSimSeconds => 30;
        const ushort Scrap = 67;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();
            var shipped = new List<string>();
            foreach (var l in System.IO.File.ReadAllLines(ProjectSettings.GlobalizePath("res://content/blueprints.tsv")))
                if (!string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")) shipped.Add(l);
            // the locked row: the plank -> stick line with a different yield (so it is a different recipe, with its own
            // derived key) and the scrap as its teacher
            string stickRow = shipped.Find(r => r.StartsWith("38\t"));
            T.Check("found the shipped plank -> stick row", stickRow != null);
            if (stickRow == null) yield break;
            var cols = new List<string>(stickRow.Split('\t'));
            cols[6] = cols[6].Replace(":2", ":5");
            while (cols.Count < 9) cols.Add("");
            cols.Add($"item:{Scrap}");
            string path = ProjectSettings.GlobalizePath("user://bp_knowledge_test.tsv");
            System.IO.File.WriteAllLines(path, new List<string>(shipped) { string.Join("\t", cols) });
            BlueprintRegistry.Load("user://bp_knowledge_test.tsv");
            BlueprintDef locked = null;
            foreach (var bp in BlueprintRegistry.All) if (bp.Locked) locked = bp;
            T.Check($"the fixture has exactly one locked recipe ({locked?.Key ?? "none"}) taught by {Assets.find(Scrap)?.itemName}",
                    locked != null && Assets.find(Scrap) != null);
            if (locked == null) yield break;

            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            var craft = p.DebugCraftMenu; var ui = p.DebugInvUI;
            int open = BlueprintRegistry.Index().Count - 1;

            // ---- 1. BROWSING HIDES IT.
            p.ShowMenu(MenuNavbar.Tab.Craft);
            yield return Ticks(2);
            var view = craft.DebugView();
            T.Check($"browsing shows only the known recipes ({view.Count} of {open + 1})", view.Count == open && !view.Contains(locked));

            // ---- 2. A TYPED SEARCH SHOWS IT, PADLOCKED, beside the open recipe for the same thing.
            string stickName = CraftingMenu.Title(locked).Split(" x")[0];
            craft.DebugSetSearch(stickName);
            view = craft.DebugView();
            int openSticks = 0; foreach (var bp in view) if (!bp.Locked) openSticks++;
            T.Check($"a search for \"{stickName}\" shows the locked recipe ({view.Count} hits, {openSticks} open)",
                    view.Contains(locked) && openSticks >= 1);
            T.Check($"...and exactly the locked one wears a padlock ({craft.DebugPadlocks()})", craft.DebugPadlocks() == 1);

            // ---- 3. THE BAG'S U / R IS NOT A SEARCH: it does not reveal it either.
            ushort plank = Assets.findByGuid(locked.Inputs[0].Guid)?.id ?? 0;
            p.ShowCraftingLookup(CraftingMenu.ItemLookup.Uses, plank);
            yield return Ticks(2);
            T.Check($"U on the plank lists only what you can do with it ({craft.DebugView().Count}, locked shown: {craft.DebugView().Contains(locked)})",
                    !craft.DebugView().Contains(locked) && craft.DebugView().Count >= 1);

            // ---- 4. YOU CANNOT CRAFT WHAT YOU DO NOT KNOW, even holding everything it needs.
            foreach (var ing in locked.Inputs) p.Inventory.tryAddItem(new Item(Assets.findByGuid(ing.Guid).id));
            int planks0 = p.Inventory.getItemCount(plank);
            craft.QueueCraft(locked, 1);
            yield return Ticks(2);
            T.Check($"queueing the locked recipe takes nothing ({planks0} -> {p.Inventory.getItemCount(plank)} planks)",
                    p.Inventory.getItemCount(plank) == planks0);

            // ---- 5. THE LEARN BUTTON: the scrap teaches it, is spent once, and the recipe joins the browsable list.
            T.Check("the scrap is a teaching item", PlayerController.TeachesBlueprint(Scrap));
            p.Inventory.tryAddItem(new Item(Scrap)); p.Inventory.tryAddItem(new Item(Scrap));
            int scrap0 = p.Inventory.getItemCount(Scrap);
            (byte pg, byte x, byte y) at = default; bool found = false;
            for (byte b = 0; b < PlayerInventory.OWNPAGES && !found; b++)
                for (byte i = 0; i < p.Inventory.items[b].getItemCount(); i++)
                    if (p.Inventory.items[b].getItem(i).item.id == Scrap) { var j = p.Inventory.items[b].getItem(i); at = (b, j.x, j.y); found = true; break; }
            T.Check("placed the scrap", found);
            ui.DebugLearn(at.pg, at.x, at.y);
            yield return Ticks(1);
            T.Check($"Learn taught it ({p.KnowsBlueprint(locked)}) and spent ONE scrap ({scrap0} -> {p.Inventory.getItemCount(Scrap)})",
                    p.KnowsBlueprint(locked) && p.Inventory.getItemCount(Scrap) == scrap0 - 1);
            ui.DebugLearn(at.pg, at.x, at.y);
            yield return Ticks(1);
            T.Check($"learning it again spends nothing ({p.Inventory.getItemCount(Scrap)} left)", p.Inventory.getItemCount(Scrap) == scrap0 - 1);

            p.ShowMenu(MenuNavbar.Tab.Craft);
            yield return Ticks(2);
            view = craft.DebugView();
            T.Check($"known now, it browses like any other ({view.Count} shown) with no padlock ({craft.DebugPadlocks()})",
                    view.Contains(locked) && craft.DebugPadlocks() == 0);
            craft.QueueCraft(locked, 1);
            yield return Ticks(2);
            T.Check($"...and it crafts (planks {planks0} -> {p.Inventory.getItemCount(plank)})", p.Inventory.getItemCount(plank) < planks0);

            // ---- 6. THE SERVER'S WORD WINS: adopting a set without it forgets it again (the MP path's only writer).
            p.AdoptKnownBlueprints(System.Array.Empty<string>());
            T.Check("adopting the server's set replaces the local one", !p.KnowsBlueprint(locked));

            p.ShowMenu(MenuNavbar.Tab.Inventory);
            BlueprintRegistry.ResetForTests();
            try { System.IO.File.Delete(path); } catch { }
        }
    }
}
