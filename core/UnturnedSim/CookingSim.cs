using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>How a piece of food was cooked. A LABEL, separate from how cooked it is -- strawberry
    /// 2026-09-05: "as well as a quality, just a flag between average (no label), microwaved, charcoal
    /// grilled".</summary>
    public enum ECookStyle : byte { Plain = 0, Microwaved = 1, CharcoalGrilled = 2 }

    /// <summary>The four appliances that cook (strawberry 2026-09-05). Each is a rate, an accepted input
    /// rule and a style it stamps on what comes out.</summary>
    public enum ECookerKind : byte { Oven = 0, Toaster = 1, Microwave = 2, Barbecue = 3 }

    /// <summary>Cooking: the engine-free half, so every rule here is a value a test can assert rather than
    /// something you have to boot Godot to observe.
    ///
    /// COOKED IS ITS OWN FIELD, NOT `quality`. Item.quality (0-100) is already FRESHNESS -- FoodSpoil ticks
    /// it down once per in-game day and a fridge halts it. Cooking a steak must not make it fresher, and
    /// leaving it in the fridge must not un-cook it, so `Item.cooked` is a second axis. They interact only
    /// where the game says they do (see Nutrition).
    ///
    /// ABOVE 100 IS BURNT, which is why `cooked` is a byte that keeps counting past 100 rather than a
    /// clamped percentage: "90-100% cooked is Cooked. above 100% is burnt" needs the overshoot to be a real
    /// value the food carries, not a separate flag that could disagree with it.</summary>
    public static class Cooking
    {
        public const byte CookedFrom = 90;    // 90..100 reads as "Cooked"
        public const byte CookedTo = 100;
        public const byte BurntFrom = 101;    // anything past 100
        public const byte MaxCooked = 255;    // the byte's own ceiling; a forgotten roast stops counting here

        /// <summary>Percent of "cooked" added per second, per appliance. The oven is the reference and the
        /// barbecue matches it ("same speed as an oven"); the toaster and microwave are the fast pair.</summary>
        public static float RatePerSecond(ECookerKind k) => k switch
        {
            ECookerKind.Toaster => 8f,     // fast, and it only ever has bread in it
            ECookerKind.Microwave => 8f,   // fast, at the cost of what it does to the food
            _ => 3.2f,                     // oven + barbecue: ~31 s from raw to Cooked
        };

        /// <summary>The label this appliance leaves on what it cooks.</summary>
        public static ECookStyle StyleOf(ECookerKind k) => k switch
        {
            ECookerKind.Microwave => ECookStyle.Microwaved,
            ECookerKind.Barbecue => ECookStyle.CharcoalGrilled,
            _ => ECookStyle.Plain,          // an oven and a toaster leave no label (strawberry: "average (no label)")
        };

        // ---------------------------------------------------------------- what goes in what

        /// <summary>Bread, for the toaster. An EXPLICIT set, and it has to be: the catalog contains
        /// "Gingerbread Top" (a SHIRT), "Gingerbread Mask" and "Mime's Baguette" (a BACKPACK), so any rule
        /// of the shape `name.Contains("bread")` lets you toast clothing. The sandwiches are here because
        /// they are bread with something in them; toasting one is the point.</summary>
        static readonly HashSet<ushort> Breads = new()
        {
            460,   // Bread
            461,   // Tuna Sandwich
            466,   // Grilled Cheese Sandwich
            467,   // BLT Sandwich
            468,   // Ham Sandwich
        };

        /// <summary>Metal, for the microwave. Also explicit, and for the mirror-image reason: the catalog
        /// has Chocolate Bar, Candy Bar, Granola Bar and Energy Bar, so `name.EndsWith("Bar")` detonates the
        /// microwave on a chocolate bar. The real set is the tins plus the raw metal stock.</summary>
        static readonly HashSet<ushort> Metals = new()
        {
            // canned food and drink -- the classic thing you must not microwave
            13, 77, 78, 79, 80, 87, 88, 89, 90, 465, 469,
            // raw metal supply
            65,    // Wire
            67,    // Metal Scrap
            68,    // Metal Sheet
            71,    // Nails
            72,    // Metal Can
            285,   // Metal Bar
        };

        /// <summary>Charcoal: the barbecue's only fuel (strawberry: "bbqs can only take charcoal as a fuel").
        ///
        /// THERE WAS NO CHARCOAL. Retail's catalog has no charcoal and no coal at all -- the only "coal"
        /// matches are Coalition uniforms -- so this is a NEW project item in the 9xxx range the port uses for
        /// its own additions (9101-9144 are the power/fluid parts). I nearly shipped `289` here from memory;
        /// 289 is a Blue Bedroll. Checked, not recalled.
        ///
        /// KNOWN GAP: it has no world source yet. `give Charcoal` works and loot cannot carry it, because loot
        /// comes from the real PEI Items.dat which has never heard of it. Whether it should be craftable from
        /// the Birch/Maple/Pine logs (37/39/41) or dropped into a table is strawberry's call, not mine.</summary>
        public const ushort CharcoalId = 9150;

        public static bool IsBread(ushort id) => Breads.Contains(id);
        public static bool IsMetal(ushort id) => Metals.Contains(id);

        /// <summary>May this appliance cook this item at all? Separate from <see cref="Detonates"/>: a
        /// microwave ACCEPTS a can (that is exactly how you get to blow it up), a toaster simply refuses
        /// anything that is not bread.</summary>
        public static bool Accepts(ECookerKind k, ItemAsset asset)
        {
            if (asset == null) return false;
            if (k == ECookerKind.Toaster) return IsBread(asset.id);
            return asset.type == EItemType.FOOD;   // ovens, microwaves and barbecues cook food; drink and gear are inert
        }

        /// <summary>Does putting this in and switching on blow the appliance up? Microwave + metal, and
        /// nothing else.</summary>
        public static bool Detonates(ECookerKind k, ItemAsset asset)
            => k == ECookerKind.Microwave && asset != null && IsMetal(asset.id);

        // ---------------------------------------------------------------- the bands

        public static bool IsRaw(byte cooked) => cooked < CookedFrom;
        public static bool IsCooked(byte cooked) => cooked >= CookedFrom && cooked <= CookedTo;
        public static bool IsBurnt(byte cooked) => cooked >= BurntFrom;

        /// <summary>Items whose cooked form has its OWN name rather than a prefix. Cooked bread is toast
        /// (strawberry 2026-09-06: "cooked bread -> toast") -- "Cooked Bread" is a description of toast, not
        /// the word for it. A table rather than a special case so the next one is an entry, and deliberately
        /// only applied to a PLAIN cook: a microwaved slice is microwaved bread, not microwaved toast.</summary>
        static readonly Dictionary<ushort, string> CookedNames = new()
        {
            [460] = "Toast",   // Bread
        };

        /// <summary>The word in front of the item's name. strawberry 2026-09-06: "just raw/uncooked, cooked:
        /// cooked quality and burnt" -- so the STATE always shows on food, while the QUALITY still adds nothing
        /// of its own when it is average (her original "average (no label)"). Cooked + average reads "Cooked";
        /// cooked + microwaved reads "Microwaved", which is the quality doing the talking.</summary>
        public static string Label(byte cooked, ECookStyle style)
        {
            if (IsBurnt(cooked)) return "Burnt";
            if (!IsCooked(cooked)) return "Raw";
            return style switch
            {
                ECookStyle.Microwaved => "Microwaved",
                ECookStyle.CharcoalGrilled => "Charcoal Grilled",
                _ => "Cooked",
            };
        }

        /// <summary>The full name to show for a food item in its current state. Non-food never gets a state
        /// word -- "Raw Bandage" is nonsense and would put a label on the 1900 items this will never touch.</summary>
        public static string DisplayName(string itemName, ushort id, byte cooked, ECookStyle style, bool isFood)
        {
            if (!isFood) return itemName;
            if (IsCooked(cooked) && style == ECookStyle.Plain && CookedNames.TryGetValue(id, out var own)) return own;
            string label = Label(cooked, style);
            return label.Length > 0 ? $"{label} {itemName}" : itemName;
        }

        /// <summary>Advance one item by `dt` seconds in this appliance. Returns the new cooked value; the
        /// caller stamps the style. Stops dead at MaxCooked so a machine left on overnight cannot wrap the
        /// byte back around to raw.</summary>
        public static byte Advance(byte cooked, ECookerKind k, float dt)
        {
            float next = cooked + RatePerSecond(k) * dt;
            return next >= MaxCooked ? MaxCooked : (byte)next;
        }

        // ---------------------------------------------------------------- what it is worth to eat

        /// <summary>The multiplier cooking applies to a food's Food value. Raw is the baseline the item
        /// already ships with, so it is 1.0 and eating raw is exactly what it is today -- this feature adds
        /// a reason to cook, it does not nerf everything that is not cooked.
        ///
        /// The microwave debuff and the charcoal buff are strawberry's ("microwaves ... give a food quality
        /// debuff", "bbqs ... give a charcoal grilled food buff"); the sizes are a choice.</summary>
        public static float Nutrition(byte cooked, ECookStyle style)
        {
            if (IsBurnt(cooked)) return 0.45f;      // burnt is still food, barely
            if (!IsCooked(cooked)) return 1f;       // raw: unchanged from today
            return style switch
            {
                ECookStyle.Microwaved => 1.15f,        // cooked, but the worst way to do it
                ECookStyle.CharcoalGrilled => 1.6f,    // the reward for keeping a bag of charcoal
                _ => 1.35f,                            // a plain oven-cooked meal
            };
        }
    }
}
