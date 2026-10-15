using SDG.Unturned;
using UnturnedGodot;

namespace UnturnedBevy;

/// <summary>Original MakeTile facts, including worn tiles that have no grid
/// address. Every value is read from accepted owner state; lazy fluid seeding
/// and ActiveAutoDrink run on private clones, never on the replica.</summary>
public static class InventoryTiles
{
    public sealed record Denomination(ushort Id, int Value, string Icon);
    public sealed record Fill(float Amount, float Capacity, float[] Color, bool Fuel);
    public sealed record Tile(ushort Id, string Name, string Icon, byte Width, byte Height,
        ushort Amount, byte Quality, float[] RarityColor, bool Condition, bool Food, bool Broken,
        float[] QualityColor, byte Frozen, bool Preserved, Fill Fill, bool ActiveAutoDrink,
        bool Money, Denomination[] Denominations);
    public sealed record Cell(byte Page, byte X, byte Y, Tile Tile);
    public sealed record Worn(byte Slot, Tile Tile);
    public sealed record View(Cell[] Cells, Worn[] Worn, Denomination[] Currency);

    static float[] Color(Godot.Color c) => [c.R,c.G,c.B,c.A];
    static Denomination Denom(ushort id) => new(id,Currency.ValueOf(id),Assets.find(id)?.iconPath);

    static Item ActiveBottle(PlayerInventory inventory)
    {
        if (inventory == null) return null;
        // Keep source page/row order. ActiveAutoDrink seeds eligible bottles;
        // invoking it against the shared replica would turn a read into a write.
        var clone = new PlayerInventory();
        var originals = new Dictionary<Item,Item>();
        for (byte page = 0; page < PlayerInventory.OWNPAGES; page++)
            foreach (var jar in inventory.items[page].items)
            {
                var copy = jar.item.Clone(); originals.Add(copy,jar.item);
                clone.items[page].addItem(jar.x,jar.y,jar.rot,copy);
            }
        var active = FluidRules.ActiveAutoDrink(clone);
        return active == null ? null : originals[active];
    }

    static Tile Describe(Item item, Item active)
    {
        var a = item?.GetAsset(); if (a == null) return null;
        bool food = a.type == EItemType.FOOD, condition = Durability.HasCondition(a);
        bool broken = item.quality == 0 && condition;
        var quality = broken ? new Godot.Color(.85f,.2f,.18f) : SourceInventoryUI.QualityColor(item.quality / 100f);
        Fill fill = null;
        if (a.IsFuelContainer)
            fill = new(Math.Max(0,item.fuelLevel),a.fuelCapacity,[.95f,.78f,.2f,1],true);
        if (a.IsFluidContainer)
        {
            var copy = item.Clone(); FluidRules.Seed(copy,a);
            var type = (FluidType)copy.fluidType;
            fill = new(Math.Max(0,copy.fluidAmount),a.fluidCapacity,
                Color(type == FluidType.None ? new(.49f,.49f,.49f) : FluidDef.WaterColor(type,(WaterQuality)copy.fluidQuality)),false);
        }
        bool money = Currency.IsCurrency(item.id);
        var denominations = money ? Currency.Breakdown(item.amount).Select(Denom).ToArray() : [];
        var rarity = ItemTool.RarityColorUI(a.rarity);
        return new(item.id,InventoryView.ShownName(a,item),a.iconPath,a.size_x,a.size_y,item.amount,item.quality,
            [rarity.R,rarity.G,rarity.B,rarity.A],condition,food,broken,Color(quality),food ? item.frozen : (byte)0,
            food && item.preserved && item.frozen == 0,fill,ReferenceEquals(item,active),money,denominations);
    }

    public static View Read(PlayerInventory inventory)
    {
        // Display-only preview amounts can change every slider frame. Supply the
        // original ordered denominations once per read instead of baking IDs or
        // making a preview mutate the accepted stack to obtain its new artwork.
        var currency = Currency.Denominations.Select(Denom).ToArray();
        if (inventory == null) return new([],[],currency);
        var active = ActiveBottle(inventory);
        var cells = new List<Cell>();
        foreach (var page in inventory.items)
            foreach (var jar in page.items)
                if (Describe(jar.item,active) is { } tile) cells.Add(new(page.page,jar.x,jar.y,tile));
        Item[] worn = [inventory.wornHat,inventory.wornGlasses,inventory.wornMask,inventory.wornShirt,
            inventory.wornVest,inventory.wornBackpack,inventory.wornPants];
        return new(cells.ToArray(),worn.Select((item,slot) => new Worn((byte)slot,Describe(item,active))).ToArray(),currency);
    }
}
