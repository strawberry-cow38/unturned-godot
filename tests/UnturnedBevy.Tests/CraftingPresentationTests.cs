using NUnit.Framework;
using SDG.Unturned;
using UnturnedBevy;
using UnturnedGodot;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class CraftingPresentationTests
{
    [SetUp]
    public void LoadOriginalCatalog()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            string content = Path.Combine(dir.FullName, "game", "content");
            if (!Directory.Exists(content)) continue;
            new Catalog().Load(content);
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
}
