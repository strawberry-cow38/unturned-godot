using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Original CraftingMenu browsing over accepted owner state. This is a
/// read projection: crafting/cancellation still uses the existing commands.</summary>
public static class CraftingView
{
    public sealed record Category(string Name, int Count);
    public sealed record Tile(int Index, string Name, ushort ItemId, string Icon, bool Known, bool Craftable);
    public sealed record Ingredient(ushort Id, string Name, int Amount, int Total, int Have, bool Consume);
    public sealed record Detail(int Index, string Name, ushort ItemId, string Icon, string Description,
        bool Known, string Skill, int SkillLevel, bool MeetsSkill, bool RequiresStation,
        int Quantity, int MaxQuantity, string Reason, bool Craftable, Ingredient[] Ingredients, int OutputAmount);
    public sealed record View(Category[] Categories, Tile[] Tiles, Detail Selected, string Header,
        string Category, string Search, string Lookup, ushort LookupItemId, string LookupLabel, int CraftableCount);

    public static View Read(Catalog catalog, PlayerInventory inventory, PlayerSkills skills,
        ICollection<string> known, string category = "All", string query = "", string lookup = "",
        ushort lookupItemId = 0, int selected = -1, int quantity = 1, bool opening = false,
        ICollection<string> stations = null)
    {
        var all = BlueprintRegistry.Index();
        query = (query ?? "").Trim();
        if (!SourceCraftingMenu.CatOrder.Contains(category)) category = "All";
        if (lookup is not ("uses" or "recipes") || lookupItemId == 0) lookup = "";
        if (lookup.Length > 0) query = "";
        string Cat(BlueprintDef bp) => BlueprintRegistry.IsSalvage(bp) ? "Salvage"
            : BlueprintRegistry.IsRecolour(bp) ? "Dyes" : SourceCraftingMenu.CategoryOf(SourceCraftingMenu.OutAsset(bp));
        bool Known(BlueprintDef bp) => Crafting.Knows(bp, known);
        bool Held(BlueprintDef bp) => !BlueprintRegistry.IsSalvage(bp)
            || (ushort.TryParse(bp.OwnerItemId, out var id) && id != 0 && inventory?.getItemCount(id) > 0);
        var categories = SourceCraftingMenu.CatOrder.Select(cat => new Category(cat,
            all.Count(bp => Known(bp) && Held(bp) && (cat == "All" ? !SourceCraftingMenu.OffAll(Cat(bp)) : Cat(bp) == cat)))).ToArray();
        if (opening && categories.First(c => c.Name == category).Count == 0) category = "All";
        bool Visible(BlueprintDef bp)
        {
            if (query.Length == 0 || lookup.Length > 0) { if (!Known(bp) || !Held(bp)) return false; }
            if (lookup == "uses") return SourceCraftingMenu.UsesItem(bp, lookupItemId);
            if (lookup == "recipes") return SourceCraftingMenu.MakesItem(bp, lookupItemId);
            if (query.Length > 0) return SourceCraftingMenu.Matches(bp, query);
            return category == "All" ? !SourceCraftingMenu.OffAll(Cat(bp)) : Cat(bp) == category;
        }
        var adapter = inventory == null ? null : new Crafting.PlayerInvAdapter(inventory);
        string Blocker(BlueprintDef bp, int qty)
        {
            if (adapter == null) return "Inventory unavailable";
            string reason = SourceCraftingMenu.CraftBlocker(bp, adapter, skills, known, stations ?? Array.Empty<string>(), qty);
            // Authoritative tool admission also excludes broken copies.
            if (reason == null && !Crafting.CanCraft(bp, adapter, skills, out var why)) reason = why;
            return reason;
        }
        var tiles = all.Where(Visible).OrderBy(SourceCraftingMenu.Title, StringComparer.OrdinalIgnoreCase).Select(bp =>
        {
            var output = SourceCraftingMenu.OutAsset(bp);
            return new Tile(catalog.Recipes.IndexOf(bp), SourceCraftingMenu.Title(bp), output?.id ?? 0,
                output?.iconPath, Known(bp), Blocker(bp, 1) == null);
        }).ToArray();
        // Rebuild keeps an already selected member of the indexed catalog,
        // including a salvage row whose last source item was just consumed.
        if (selected < 0 || selected >= catalog.Recipes.Count || !all.Contains(catalog.Recipes[selected]))
            selected = tiles.Length > 0 ? tiles[0].Index : -1;
        Detail detail = null;
        if (selected >= 0)
        {
            var bp = catalog.Recipes[selected]; var output = SourceCraftingMenu.OutAsset(bp);
            int max = int.MaxValue;
            foreach (var ing in bp.Inputs)
            {
                if (!ing.Consume || ing.Amount <= 0) continue;
                int have = inventory?.getItemCount(Crafting.Resolve(ing.Guid)) ?? 0;
                max = Math.Min(max, have / ing.Amount);
            }
            if (max == int.MaxValue) max = 1;
            int qty = Math.Clamp(quantity, 1, Math.Max(1, max));
            string reason = Blocker(bp, qty);
            detail = new(selected, SourceCraftingMenu.Title(bp), output?.id ?? 0, output?.iconPath,
                output?.description ?? "", Known(bp), bp.RequiresSkill ? bp.Skill : "", bp.SkillLevel,
                Crafting.MeetsSkill(bp, skills), bp.RequiresStation, qty, max, reason ?? "", reason == null,
                bp.Inputs.Select(i => new Ingredient(Crafting.Resolve(i.Guid), Assets.findByGuid(i.Guid)?.itemName ?? "?",
                    i.Amount, i.Amount * qty,
                    inventory?.getItemCount(Crafting.Resolve(i.Guid)) ?? 0, i.Consume)).ToArray(),
                bp.Outputs.Count > 0 ? bp.Outputs[0].Amount : 1);
        }
        string label = lookup.Length > 0 ? (lookup == "uses" ? "Uses of " : "Recipes for ")
            + (Assets.find(lookupItemId)?.itemName ?? $"item {lookupItemId}") : "CRAFTING";
        int canNow = all.Count(bp => Known(bp) && Blocker(bp, 1) == null);
        string header = lookup.Length > 0
            ? $"CRAFTING   ·   {label}   ·   {tiles.Length} recipe{(tiles.Length == 1 ? "" : "s")}   ·   {canNow} craftable now"
            : $"CRAFTING   ·   {tiles.Length} shown   ·   {canNow} craftable now";
        return new(categories, tiles, detail, header, category, query, lookup, lookupItemId,
            lookup.Length > 0 ? label : "", canNow);
    }
}

public sealed partial class BridgeHost
{
    CraftQueueEvent _craftQueue;
    long _craftQueueTick;
    object ReadCrafting(JsonElement request)
    {
        ProbeCraftingContext(request);
        _client.Inventories.TryGet(_client.PlayerId, out var owner);
        _client.Skills.TryGet(_client.PlayerId, out var skills);
        var view = CraftingView.Read(_catalog, owner?.Inventory, skills?.Skills, _client.KnownBlueprints,
            Text(request, "category", "All"), Text(request, "search"), Text(request, "lookup"),
            checked((ushort)Int(request, "itemId")), Int(request, "selected", -1), Int(request, "quantity", 1), Bool(request, "opening"),
            AcceptedCraftingStations);
        var queue = (_craftQueue.Jobs ?? System.Array.Empty<(ushort Bp, float Left, float Of)>()).Select((job, slot) =>
        {
            var bp = job.Bp < _catalog.Recipes.Count ? _catalog.Recipes[job.Bp] : null;
            var output = bp == null ? null : SourceCraftingMenu.OutAsset(bp);
            return new { slot, index = job.Bp, name = bp == null ? "Craft" : SourceCraftingMenu.Title(bp),
                icon = output?.iconPath, left = job.Left, total = job.Of };
        }).ToArray();
        return new { view, queue, total = _craftQueue.Total, tick = _craftQueueTick };
    }
}
