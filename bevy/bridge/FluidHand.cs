using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnityEngine;

namespace UnturnedBevy;

/// <summary>PlayerController.TryDrinkContainer / TryFillAtTap for a held fluid container, routed
/// through the existing v59 commands (DrinkFluid 69, FillAtTap 70). The source shows a toast
/// computed from its own copy at click time; here that text is computed on a CLONE of the
/// accepted owner item, so nothing local is mutated -- the bottle and the water bar change only
/// when the server's echo arrives.</summary>
public sealed partial class BridgeHost
{
    /// <summary>The native look-ray end (PlayerController._lookEnd), for WaterTap.AimedAt.</summary>
    Vector3? _lookEnd;

    void ReadLookEnd(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("lookEnd", out var end)) return;
        _lookEnd = end.ValueKind == JsonValueKind.Array ? Vec(end) : null;
    }

    /// <summary>The sink or bathtub the look point is in (host map taps), or null.</summary>
    string AimedTap() => _lookEnd is Vector3 p ? _services?.AimedTap(p) : null;

    (Item item, ItemAsset asset)? OwnedFluidAt(int page, int x, int y, int id)
    {
        if (page < 0 || page >= PlayerInventory.OWNPAGES || x < 0 || y < 0 || x > 255 || y > 255) return null;
        if (!_client.Inventories.TryGet(_client.PlayerId, out var owned) || owned.Inventory?.items[page] == null) return null;
        var grid = owned.Inventory.items[page];
        byte index = grid.getIndex((byte)x, (byte)y);
        var item = index == byte.MaxValue ? null : grid.getItem(index)?.item;
        var asset = item == null ? null : Assets.find(item.id);
        return item != null && item.id == id && asset != null && asset.IsFluidContainer ? (item, asset) : null;
    }

    /// <summary>FluidItem.Label off a seeded clone.</summary>
    internal static string FluidLabel(Item item, ItemAsset asset)
    {
        var copy = item.Clone(); FluidRules.Seed(copy, asset);
        var type = (FluidType)copy.fluidType; float amount = Math.Max(0f, copy.fluidAmount);
        if (amount <= 0.001f || type == FluidType.None) return $"{asset.itemName} -- empty ({FluidDef.Litres(asset.fluidCapacity)})";
        return $"{asset.itemName} -- {FluidDef.Litres(amount)} {FluidDef.WaterName(type, (WaterQuality)copy.fluidQuality)}";
    }

    /// <summary>FluidItem.Why.</summary>
    static string FluidRefusal(FluidRules.Refusal why, Item it) => why switch
    {
        FluidRules.Refusal.None => null,
        FluidRules.Refusal.NotContainer => "not a fluid container",
        FluidRules.Refusal.Empty => "container is empty",
        FluidRules.Refusal.Full => "container is full",
        FluidRules.Refusal.WontMix => $"won't mix {FluidDef.Name((FluidType)(it?.fluidType ?? 0))} and water",
        FluidRules.Refusal.Undrinkable => (FluidType)(it?.fluidType ?? 0) == FluidType.Water
            ? $"can't drink {FluidDef.WaterName(FluidType.Water, (WaterQuality)(it?.fluidQuality ?? 0)).ToLowerInvariant()}"
            : $"can't drink {FluidDef.Name((FluidType)(it?.fluidType ?? 0))}",
        _ => "can't do that",
    };

    /// <summary>LMB with a fluid container in hand: chug the whole bottle (FluidRules.DrinkAll).</summary>
    object DrinkHeldFluid(JsonElement args)
    {
        int page = Int(args, "page", -1), x = Int(args, "x", -1), y = Int(args, "y", -1), id = Int(args, "id");
        if (OwnedFluidAt(page, x, y, id) is not var (item, asset)) return new { queued = false, toast = "put it in your bag to drink it" };
        var preview = item.Clone();
        float drank = FluidRules.DrinkAll(preview, asset, out float hydration, out var why);
        if (drank <= 0f) return new { queued = false, toast = FluidRefusal(why, preview) };
        bool queued = _client.SendDrinkFluid((byte)page, (byte)x, (byte)y, (ushort)id);
        return new { queued, toast = $"drank {FluidDef.Litres(drank)}  (+{hydration * 100f:0}% water)" };
    }

    /// <summary>RMB with a fluid container in hand at a sink or bathtub: fill clean (FluidRules.FillClean).</summary>
    object FillHeldFluidAtTap(JsonElement args)
    {
        string tap = AimedTap();
        if (tap == null) return new { queued = false, toast = "aim at a tank, a sink or a bathtub to fill" };
        int page = Int(args, "page", -1), x = Int(args, "x", -1), y = Int(args, "y", -1), id = Int(args, "id");
        if (OwnedFluidAt(page, x, y, id) is not var (item, asset)) return new { queued = false, toast = "put it in your bag to fill it" };
        var preview = item.Clone();
        float moved = FluidRules.FillClean(preview, asset, out bool rinsed, out var why);
        if (moved <= 0f) return new { queued = false, toast = FluidRefusal(why, preview) };
        bool queued = _client.SendFillAtTap((byte)page, (byte)x, (byte)y, (ushort)id);
        return new { queued, toast = rinsed ? $"poured it out, filled {FluidDef.Litres(moved)} clean water" : $"filled {FluidDef.Litres(moved)} clean water" };
    }

    object FluidView(Item item)
    {
        var asset = Assets.find(item.id);
        return asset != null && asset.IsFluidContainer ? new { label = FluidLabel(item, asset) } : null;
    }
}
