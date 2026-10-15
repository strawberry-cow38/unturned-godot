using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class CraftingPresentationTests
{
    Catalog _catalog;
    [SetUp]
    public void LoadOriginalCatalog()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            string content = Path.Combine(dir.FullName, "game", "content");
            if (!Directory.Exists(content)) continue;
            _catalog = new Catalog(); _catalog.Load(content);
            return;
        }
        throw new DirectoryNotFoundException();
    }

    [Test]
    public void OutputQuantityPrecedesOwnerAndDefinitionName()
    {
        var bp = new BlueprintDef { OwnerItemId = "4", Name = "Internal blueprint label" };
        bp.Outputs.Add(new() { Guid = Assets.find(62).guid, Amount = 2 });
        Assert.That(SourceCraftingMenu.Title(bp), Is.EqualTo("Birch Plank x2"));
        bp.Outputs[0] = new() { Guid = Assets.find(62).guid, Amount = 1 };
        Assert.That(SourceCraftingMenu.Title(bp), Is.EqualTo("Birch Plank"));
    }

    [Test]
    public void MissingOutputResolvesOwnerThenAuthoredNameThenOperation()
    {
        var bp = new BlueprintDef { OwnerItemId = "4", Name = "Internal blueprint label" };
        bp.Outputs.Add(new() { Guid = "missing-output-guid", Amount = 2 });
        Assert.That(SourceCraftingMenu.Title(bp), Is.EqualTo("AR-15"), "Retain the original catalog's display-name override");
        bp.OwnerItemId = "missing-owner";
        Assert.That(SourceCraftingMenu.Title(bp), Is.EqualTo("Internal blueprint label"));
        bp.Name = "";
        bp.Operation = "RepairTargetItem";
        Assert.That(SourceCraftingMenu.Title(bp), Is.EqualTo("RepairTargetItem"));
    }

    [Test]
    public void BrowseUsesOriginalIndexBucketsAndNeverListsUnresolvedOrTargetOperations()
    {
        var inventory = new PlayerInventory();
        var view = CraftingView.Read(_catalog, inventory, null, new HashSet<string>());
        Assert.That(view.Categories.Select(c => c.Name), Is.EqualTo(SourceCraftingMenu.CatOrder));
        Assert.That(view.Tiles.Length, Is.EqualTo(view.Categories.Single(c => c.Name == "All").Count));
        Assert.That(view.Tiles.Length, Is.GreaterThan(20));
        Assert.That(view.Selected.Index, Is.EqualTo(view.Tiles[0].Index), "source Rebuild selects the first indexed result on open");
        foreach (var tile in view.Tiles)
        {
            var bp = _catalog.Recipes[tile.Index];
            Assert.That(BlueprintRegistry.Index(), Does.Contain(bp));
            Assert.That(BlueprintRegistry.IsRecolour(bp) || BlueprintRegistry.IsSalvage(bp), Is.False);
        }
        Assert.That(view.Tiles.Select(t => t.Name), Is.Ordered.Using<string>(StringComparer.OrdinalIgnoreCase));
    }

    [Test]
    public void TypedSearchFindsUnknownAndUnheldSalvageWhileBrowsingAndLookupsHideThem()
    {
        var inventory = new PlayerInventory(); var known = new HashSet<string>();
        var bp = BlueprintRegistry.Index().First(b => !BlueprintRegistry.IsSalvage(b) && !BlueprintRegistry.IsRecolour(b));
        int index = _catalog.Recipes.IndexOf(bp); bp.Unlocks.Add("learn");
        Assert.That(CraftingView.Read(_catalog, inventory, null, known).Tiles.Any(t => t.Index == index), Is.False);
        var typed = CraftingView.Read(_catalog, inventory, null, known, query: SourceCraftingMenu.Title(bp), selected: index);
        Assert.That(typed.Tiles.Any(t => t.Index == index && !t.Known && !t.Craftable), Is.True);
        Assert.That(typed.Selected.Reason, Does.StartWith("Not unlocked"));
        ushort input = Crafting.Resolve(bp.Inputs[0].Guid);
        Assert.That(CraftingView.Read(_catalog, inventory, null, known, lookup: "uses", lookupItemId: input).Tiles.Any(t => t.Index == index), Is.False);
        known.Add(bp.Key);
        Assert.That(CraftingView.Read(_catalog, inventory, null, known, lookup: "uses", lookupItemId: input).Tiles.Any(t => t.Index == index), Is.True);
        var salvage = BlueprintRegistry.Index().First(BlueprintRegistry.IsSalvage); int salvageIndex = _catalog.Recipes.IndexOf(salvage);
        Assert.That(CraftingView.Read(_catalog, inventory, null, known, category: "Salvage").Tiles, Is.Empty);
        Assert.That(CraftingView.Read(_catalog, inventory, null, known, query: SourceCraftingMenu.Title(salvage)).Tiles.Any(t => t.Index == salvageIndex), Is.True);
    }

    [Test]
    public void IngredientDetailsClampQuantityAndExcludeViewedStorageFromOwnedCounts()
    {
        var inventory = new PlayerInventory(); inventory.items[2].loadSize(30, 30);
        var bp = BlueprintRegistry.Index().First(b => !b.RequiresStation && !b.Locked
            && b.Inputs.Any(i => i.Consume) && !BlueprintRegistry.IsRecolour(b) && !BlueprintRegistry.IsSalvage(b));
        int index = _catalog.Recipes.IndexOf(bp); var input = bp.Inputs.First(i => i.Consume);
        ushort ingredient = Crafting.Resolve(input.Guid);
        foreach (var ing in bp.Inputs)
            for (int n = 0; n < ing.Amount * (ing.Consume ? 3 : 1); n++)
                Assert.That(inventory.tryAddItemAuto(new Item(Crafting.Resolve(ing.Guid), 1), out _), Is.Not.EqualTo(PlayerInventory.AutoPlace.None));
        inventory.items[PlayerInventory.STORAGE].loadSize(30, 30);
        inventory.items[PlayerInventory.STORAGE].tryAddItem(new Item(ingredient, 1));
        var view = CraftingView.Read(_catalog, inventory, null, new HashSet<string>(), query: SourceCraftingMenu.Title(bp), selected: index, quantity: 999);
        Assert.That(view.Selected.Quantity, Is.EqualTo(3)); Assert.That(view.Selected.MaxQuantity, Is.EqualTo(3));
        Assert.That(view.Selected.OutputAmount, Is.EqualTo(bp.Outputs.Count > 0 ? bp.Outputs[0].Amount : 1));
        var row = view.Selected.Ingredients.First(i => i.Id == ingredient);
        Assert.That(row.Have, Is.EqualTo(3 * input.Amount));
        Assert.That(row.Total, Is.EqualTo(3 * input.Amount)); Assert.That(view.Selected.Craftable, Is.True);
        foreach (var tool in view.Selected.Ingredients.Where(i => !i.Consume))
        {
            Assert.That(tool.Have, Is.EqualTo(tool.Amount));
            Assert.That(tool.Total, Is.EqualTo(tool.Amount * 3), "retain the source table's color comparison even though tools are needed once");
        }
        Assert.That(inventory.getItemCount(ingredient), Is.EqualTo(3 * input.Amount), "a read never spends ingredients");
        Assert.That(inventory.items[PlayerInventory.STORAGE].items.Sum(j => j.item.id == ingredient ? j.item.amount : 0), Is.EqualTo(1));
        var emptySearch = CraftingView.Read(_catalog, inventory, null, new HashSet<string>(), query: "nothing matches this query");
        Assert.That(emptySearch.Tiles, Is.Empty);
        Assert.That(emptySearch.CraftableCount, Is.EqualTo(view.CraftableCount), "header availability counts the whole source index, not the filtered tiles");
        Assert.That(emptySearch.CraftableCount, Is.GreaterThan(0));
    }
    [Test]
    public void ReopeningRetainsBrowseAndIndexedSelectionAndOnlyFallsBackForAnEmptyCategory()
    {
        var inventory = new PlayerInventory(); var known = new HashSet<string>();
        var initial = CraftingView.Read(_catalog, inventory, null, known);
        var category = initial.Categories.First(c => c.Name != "All" && c.Count > 0).Name;
        var emptyCategory = initial.Categories.First(c => c.Count == 0).Name;
        var reopen = CraftingView.Read(_catalog, inventory, null, known, category, "Birch",
            selected: initial.Selected.Index, quantity: 1, opening: true);
        Assert.That(reopen.Category, Is.EqualTo(category));
        Assert.That(reopen.Search, Is.EqualTo("Birch"));
        Assert.That(reopen.Selected.Index, Is.EqualTo(initial.Selected.Index), "source keeps a valid indexed selection even outside the filtered results");
        Assert.That(CraftingView.Read(_catalog, inventory, null, known, emptyCategory, opening: true).Category,
            Is.EqualTo("All"));
        Assert.That(CraftingView.Read(_catalog, inventory, null, known, emptyCategory, opening: false).Category,
            Is.EqualTo(emptyCategory), "the fallback belongs to Open, not ordinary refresh");
    }

}
