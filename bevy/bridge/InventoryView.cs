using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>InventoryUI's presentation over the accepted owner inventory. This
/// class does not seed, move, split, consume or otherwise edit a replicated Item.</summary>
public static class InventoryView
{
    public sealed record Address(byte Page, byte X, byte Y, ushort Id, ulong Token);
    public sealed record Button(string Action, string Label, string Tooltip = "", int Recipe = -1);
    public sealed record Fluid(byte Type, byte Quality, float Amount, float Capacity, bool AutoDrink,
        string Contents, float[] Color);
    public sealed record Destination(byte Page, byte X, byte Y, byte Rotation);
    public sealed record EquipPlan(byte Slot, Address Displaced, Destination DisplacedTo, bool DropDisplaced);
    public sealed record ItemView(Address Address, string Name, string Description, string Info,
        float[] RarityColor, string Condition, byte? ClothingSlot, string HandAction, int EquipSlot,
        bool LooseAmmo, bool Magazine, int MagazineCapacity, ushort RoundId, string LoadedRound,
        float Fuel, Fluid Fluid, Button[] Actions, float[] ConditionColor,
        byte StorePage, Destination PaperdollTake, EquipPlan Equip);
    public sealed record QuickCraft(int Index, string Name, ushort ItemId, string Icon);
    public sealed record Load(bool Consumed, bool Allowed, string Message, int Batch, ushort RoundId);
    public sealed record View(ItemView[] Items, QuickCraft[] QuickCrafts, object Cooker, Load Load, InventoryTiles.View Tiles, uint AcceptedTick);

    static ushort RoundId(string round) => string.IsNullOrEmpty(round) ? (ushort)0
        : Assets.all().FirstOrDefault(a => a.isAmmo && a.magRound == round)?.id ?? 0;
    public static ItemJar At(PlayerInventory inventory, int page, int x, int y)
    {
        if (inventory == null || page < 0 || page >= inventory.items.Length || x is < 0 or > 255 || y is < 0 or > 255) return null;
        var items = inventory.items[page]; byte index = items?.getIndex((byte)x, (byte)y) ?? byte.MaxValue;
        return index == byte.MaxValue ? null : items.getItem(index);
    }
    static byte? Clothing(ItemAsset asset) => asset.type switch {
        EItemType.HAT => 0, EItemType.GLASSES => 1, EItemType.MASK => 2, EItemType.SHIRT => 3,
        EItemType.VEST => 4, EItemType.BACKPACK => 5, EItemType.PANTS => 6, _ => null
    };
    static string Condition(ItemAsset a, Item item)
    {
        int q = item.quality;
        if (a.type == EItemType.FOOD)
            return $"Condition: {q}%" + (q < FoodSpoil.SickThreshold ? "  ·  spoiled" : q >= 90 ? "  ·  fresh" : "");
        if (!Durability.HasCondition(a)) return "";
        var kind = Durability.KindOf(a);
        string tag = q == 0 ? kind == Durability.Kind.Clothing ? "  ·  BROKEN: no protection" : "  ·  BROKEN: unusable"
            : q < 50 && kind == Durability.Kind.Gun ? "  ·  worn: kicks, spreads, hits softer"
            : q < 50 && kind == Durability.Kind.Melee ? "  ·  worn: hits softer"
            : q < 100 && kind == Durability.Kind.Clothing ? $"  ·  protects at {q}%" : "";
        return $"Condition: {q}%{tag}";
    }
    internal static string ShownName(ItemAsset a, Item item)
    {
        string name = Cooking.DisplayName(a.itemName, a.id, item.cooked, (ECookStyle)item.cookStyle, a.type == EItemType.FOOD);
        return Freezing.Label(item.frozen) is string frozen ? $"{frozen} {name}" : name;
    }


    public static View Read(Catalog catalog, InventoryReplication replicas, ushort owner,
        OwnerEquipmentState held, PlayerSkills skills, ICollection<string> known, object cooker = null,
        (int Page, int X, int Y)? loadFrom = null, (int Page, int X, int Y)? loadTo = null, uint acceptedTick = 0,
        ICollection<string> stations = null)
    {
        replicas.TryGet(owner, out var entry); var inventory = entry?.Inventory;
        var result = new List<ItemView>();
        var source = new SourceInventoryUI(inventory);
        float[] Color(Godot.Color c) => [c.R, c.G, c.B, c.A];
        Address ItemAddress(byte page, ItemJar jar)
        {
            replicas.TryGetItemToken(owner, page, jar.x, jar.y, out ulong token);
            return new(page, jar.x, jar.y, jar.item.id, token);
        }
        EquipPlan Equip(ItemJar jar)
        {
            int slot = inventory.EquipHandSlotFor(jar.GetAsset());
            if (slot < 0) return null;
            var target = inventory.items[slot]; var occupant = target.getItemCount() > 0 ? target.getItem(0) : null;
            if (occupant == null || ReferenceEquals(jar, occupant)
                || inventory.items[jarPage(jar)].checkSpaceSwap(jar.x, jar.y, jar.size_x, jar.size_y, jar.rot,
                    occupant.size_x, occupant.size_y, 0)) return new((byte)slot, null, null, false);
            bool space = source.ResolveMoveDest(occupant, 255, out var page, out var x, out var y, out var rotation);
            return new((byte)slot, ItemAddress((byte)slot, occupant), space ? new(page, x, y, rotation) : null, !space);
        }
        byte jarPage(ItemJar jar) => inventory.items.First(p => p.items.Contains(jar)).page;
        if (inventory != null) foreach (var page in inventory.items) foreach (var jar in page.items)
        {
            var item = jar.item; var a = item?.GetAsset(); if (a == null) continue;
            replicas.TryGetItemToken(owner, page.page, jar.x, jar.y, out ulong token);
            var address = new Address(page.page, jar.x, jar.y, item.id, token);
            string hand = "";
            if (HandRules.CanEquipItemAsset(a)) hand = held?.Token == token && token != 0 ? "Dequip"
                : a.IsFluidContainer || a.IsConsumable || a.IsFuelContainer ? "Hold" : "Equip";
            var actions = new List<Button>();
            if (hand.Length > 0) actions.Add(new(hand.ToLowerInvariant(), hand));
            if (BlueprintRegistry.All.Any(bp => bp.Locked && bp.TaughtByItems().Contains(item.id))) actions.Add(new("learn", "Learn"));
            if (a.IsFuelContainer) actions.Add(new("emptyFuel", "Empty"));
            if (a.IsFluidContainer && page.page < PlayerInventory.OWNPAGES)
                actions.Add(new("autoDrink", item.autoDrink ? "Autodrink: ON" : "Autodrink: OFF"));
            if (inventory.items[PlayerInventory.STORAGE].width > 0 && inventory.items[PlayerInventory.STORAGE].height > 0)
                actions.Add(new("transfer", PlayerInventory.IsContainerView(page.page) ? "Take" : "Store"));
            if (a.IsMagazine && item.amount > 0) actions.Add(new("unload", "Unload"));
            var salvage = page.page < PlayerInventory.OWNPAGES ? BlueprintRegistry.SalvageFor(item.id) : null;
            if (salvage != null) actions.Add(new("salvage", "Salvage", SourceInventoryUI.SalvageTip(salvage), catalog.Recipes.IndexOf(salvage)));
            actions.Add(new("drop", "Drop")); actions.Add(new("close", "Close"));
            var color = ItemTool.RarityColorUI(a.rarity);
            // Source FluidItem.Read lazily initializes fresh items. Seed a clone
            // here: merely opening a remote context menu must not edit its bag.
            Fluid fluid = null;
            if (a.IsFluidContainer) { var copy = item.Clone(); FluidRules.Seed(copy, a);
                var type = (FluidType)copy.fluidType; var quality = (WaterQuality)copy.fluidQuality;
                float amount = Math.Max(0, copy.fluidAmount);
                string contents = amount <= 0.001f || type == FluidType.None
                    ? $"Contents: empty  ·  holds {FluidDef.Litres(a.fluidCapacity)}"
                    : $"Contents: {FluidDef.Litres(amount)} {FluidDef.WaterName(type, quality)}  ·  of {FluidDef.Litres(a.fluidCapacity)}";
                fluid = new(copy.fluidType, copy.fluidQuality, amount, a.fluidCapacity, item.autoDrink, contents,
                    Color(type == FluidType.None ? new Godot.Color(.75f,.75f,.75f)
                        : SourceInventoryUI.Lerp(FluidDef.WaterColor(type,quality),new(1,1,1),.3f))); }
            string round = a.IsMagazine ? MagRules.EffectiveRound(item, a) ?? a.magRound : a.magRound;
            result.Add(new(address, ShownName(a, item), a.description ?? "", $"{a.rarity}  ·  {a.type}  ·  {a.size_x}x{a.size_y}",
                [color.R, color.G, color.B, color.A], Condition(a, item), Clothing(a), hand, inventory.EquipHandSlotFor(a),
                a.isAmmo, a.IsMagazine, a.IsMagazine ? MagRules.Capacity(a) : 0, RoundId(round), round ?? "",
                Math.Max(0, item.fuelLevel), fluid, actions.ToArray(),
                Color(SourceInventoryUI.Lerp(item.quality == 0 && a.type != EItemType.FOOD ? new(.85f,.2f,.18f)
                    : SourceInventoryUI.QualityColor(item.quality / 100f), new(1,1,1), .3f)),
                source.StoreTarget(jar), source.FirstFreeOwnCell(a, out var takePage, out var takeX, out var takeY)
                    ? new(takePage, takeX, takeY, 0) : null, Equip(jar)));
        }
        // Source BuildQuickCraftSection preserves registry order, stops at18,
        // excludes salvage/recolour and only displays known, ready recipes.
        var quick = inventory == null ? [] : BlueprintRegistry.Applicable(new Crafting.PlayerInvAdapter(inventory))
            .Where(bp => !BlueprintRegistry.IsRecolour(bp) && !BlueprintRegistry.IsSalvage(bp)
                && Crafting.HasStations(bp, stations ?? Array.Empty<string>()) && Crafting.MeetsSkill(bp, skills) && Crafting.Knows(bp, known))
            .Take(18).Select(bp => new QuickCraft(catalog.Recipes.IndexOf(bp), SourceCraftingMenu.Title(bp),
                SourceCraftingMenu.OutAsset(bp)?.id ?? 0, SourceCraftingMenu.OutAsset(bp)?.iconPath)).ToArray();
        Load load = null;
        if (loadFrom is { } from && loadTo is { } to)
        {
            var bullet = At(inventory, from.Page, from.X, from.Y); var magazine = At(inventory, to.Page, to.X, to.Y);
            var b = bullet?.GetAsset(); var m = magazine?.GetAsset();
            bool consumed = b?.isAmmo == true && m?.IsMagazine == true;
            var check = consumed ? MagRules.CheckLoad(magazine.item, m, b) : default;
            bool allowed = consumed && check == MagLoadResult.Ok;
            load = new(consumed, allowed, consumed ? MagRules.Message(check) : "",
                allowed ? Math.Min(bullet.item.amount, MagRules.Capacity(m) - magazine.item.amount) : 0, b?.id ?? 0);
        }
        return new(result.ToArray(), quick, cooker, load, InventoryTiles.Read(inventory), acceptedTick);
    }
}

public sealed partial class BridgeHost
{
    void ConfigureInventoryTransactions()
    {
        // Bind while this catalog owns the original registry. A second bridge
        // can load its own catalog later in the same test/host process.
        var recipes = Assets.all().Select(a => (a.id, index: _catalog.Recipes.IndexOf(BlueprintRegistry.SalvageFor(a.id))))
            .Where(p => p.index >= 0).ToDictionary(p => p.id, p => p.index);
        _server.Transactions.SalvageRecipe = id => recipes.GetValueOrDefault(id, -1);
    }
    byte? _inventoryCookerKind;
    bool _inventoryCookerOn;
    byte _inventoryCookerFuel;
    void InventoryOpened(StorageOpenedEvent e)
    { _inventoryCookerKind = e.IsCooker ? e.CookerKind : null; _inventoryCookerOn = e.CookerOn; _inventoryCookerFuel = e.CookerFuel; }
    void InventoryClosed() { _inventoryCookerKind = null; _inventoryCookerOn = false; _inventoryCookerFuel = 0; }
    void InventoryCooker(CookerStateEvent e)
    { if (_inventoryCookerKind != null && e.NetId == _storageView.Current.NetId) { _inventoryCookerOn = e.On; _inventoryCookerFuel = e.Fuel; } }
    object ReadInventory(JsonElement request)
    {
        ProbeCraftingContext(request);
        _client.Equipment.TryGet(_client.PlayerId, out var held); _client.Skills.TryGet(_client.PlayerId, out var skills);
        (int, int, int)? Address(string key) => request.TryGetProperty(key, out var cell) && cell.ValueKind == JsonValueKind.Object
            ? (Int(cell, "page", -1), Int(cell, "x", -1), Int(cell, "y", -1)) : null;
        object cooker = _inventoryCookerKind is byte kind ? new { netId = _storageView.Current.NetId,
            kind = ((ECookerKind)kind).ToString(), on = _inventoryCookerOn, fuel = _inventoryCookerFuel / 255f,
            needsFuel = Cooking.NeedsFuel((ECookerKind)kind) } : null;
        return InventoryView.Read(_catalog, _client.Inventories, _client.PlayerId, held, skills?.Skills,
            _client.KnownBlueprints, cooker, Address("loadFrom"), Address("loadTo"), unchecked((uint)_client.Applier.LastAppliedServerTick),
            AcceptedCraftingStations);
    }
}
