using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>How a piece of food was cooked. A LABEL, separate from how cooked it is -- strawberry
    /// 2026-09-05: "as well as a quality, just a flag between average (no label), microwaved, charcoal
    /// grilled".</summary>
    public enum ECookStyle : byte { Plain = 0, Microwaved = 1, CharcoalGrilled = 2 }

    /// <summary>The four appliances that cook (strawberry 2026-09-05). Each is a rate, an accepted input
    /// rule and a style it stamps on what comes out.</summary>
    public enum ECookerKind : byte { Oven = 0, Toaster = 1, Microwave = 2, Barbecue = 3, Campfire = 4 }

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
            ECookerKind.Campfire => 2f,    // "slower than a bbq" -- ~50 s, the field option
            _ => 3.2f,                     // oven + barbecue: ~31 s from raw to Cooked
        };

        /// <summary>The label this appliance leaves on what it cooks.</summary>
        public static ECookStyle StyleOf(ECookerKind k) => k switch
        {
            ECookerKind.Microwave => ECookStyle.Microwaved,
            ECookerKind.Barbecue => ECookStyle.CharcoalGrilled,
            // A CAMPFIRE leaves no label either: strawberry 2026-09-06 "no food buff". Read as the QUALITY flag
            // being average, the same vocabulary as the bbq's "charcoal grilled food buff" and the microwave's
            // "food quality debuff" -- so a campfire cooks food properly, it just earns no special word for it.
            _ => ECookStyle.Plain,          // oven, toaster, campfire (strawberry: "average (no label)")
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
        /// NO WORLD SOURCE, BY DECISION -- strawberry 2026-09-06: "spawn only later in spawn tables". So this
        /// is `give Charcoal` until then, and deliberately NOT craftable: a blueprint invented now would be the
        /// thing that has to be unpicked when the spawn entry lands. PEI loot cannot carry it meanwhile because
        /// loot comes from the real Items.dat, which has never heard of item 9150.</summary>
        public const ushort CharcoalId = 9150;

        /// <summary>The three wood species, and how long each burns relative to the others. Hardwood outlasts
        /// softwood: maple is the dense one, pine the resinous fast one, birch in between. strawberry
        /// 2026-09-06: "the three wood types have varying burn times".</summary>
        public static float SpeciesBurn(string name)
        {
            if (name == null) return 0f;
            if (HasWord(name, "Maple")) return 1.25f;
            if (HasWord(name, "Birch")) return 1.0f;
            if (HasWord(name, "Pine")) return 0.8f;
            return 0f;
        }

        /// <summary>Whole-word match, and it is the whole reason this is not a Contains. The catalog holds
        /// "Maplestrike" (a GUN), "Maplestrike Iron Sights" and "Pineapple" (a HAT) -- every one of them a
        /// substring hit for a wood species, and none of them something you put in a fire.</summary>
        static bool HasWord(string s, string word)
        {
            int i = s.IndexOf(word, System.StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                bool leftOk = i == 0 || !char.IsLetter(s[i - 1]);
                int end = i + word.Length;
                bool rightOk = end >= s.Length || !char.IsLetter(s[end]);
                if (leftOk && rightOk) return true;
                i = s.IndexOf(word, i + 1, System.StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>Is this anything wooden? strawberry 2026-09-06: "wood as a fuel should be anything wooden.
        /// sticks, planks, deployables."
        ///
        /// A RULE rather than a frozen id list, unlike bread and metal, and the difference is what she asked
        /// for: bread and metal are closed sets someone chose, while "anything wooden" is an open category --
        /// the catalog already carries ~60 of them (logs, sticks, planks, barricades, doors, gates, hatches,
        /// ladders, plates, frames, sidings, pipes, signs, shutters) and a list would go stale the day one is
        /// added. Two conditions, and both are load-bearing: a whole-word species match (see HasWord for the
        /// three items that make a substring wrong), AND a type of SUPPLY or GENERIC -- so a Maple DOOR burns
        /// and a Maple-anything that is food, clothing or a weapon does not.</summary>
        public static bool IsWood(ItemAsset a)
            => a != null && SpeciesBurn(a.itemName) > 0f
               && (a.type == EItemType.SUPPLY || a.type == EItemType.GENERIC);

        /// <summary>How long one unit of this fuel burns, in seconds. Size counts (strawberry: "the size of
        /// wooden fuel having different burn times") and the GRID FOOTPRINT is the measure -- it is already in
        /// the catalog, it is what the player sees, and it means a 2x2 plate outlasts a 1x1 stick by exactly
        /// the factor it looks like it should. Charcoal is a flat rate: briquettes are briquettes.</summary>
        public static float BurnSecondsFor(ItemAsset a)
        {
            if (a == null) return 0f;
            if (a.id == CharcoalId) return 45f;
            float species = SpeciesBurn(a.itemName);
            if (species <= 0f) return 0f;
            int area = System.Math.Max(1, a.size_x * a.size_y);
            return WoodBurnPerCell * species * area;
        }

        /// <summary>Seconds a 1x1 of the middle species (birch) burns. Everything else scales off it.</summary>
        public const float WoodBurnPerCell = 20f;

        /// <summary>What a MAINS appliance draws to run (strawberry 2026-09-06: "stove requires power io input,
        /// 2kw to cook/globalpower on. toaster requires 1000w. microwave 1.5kw"). 0 = it burns fuel instead, so
        /// the two families are exclusive: a thing either draws watts or it takes something you put in it.</summary>
        public static float PowerWatts(ECookerKind k) => k switch
        {
            ECookerKind.Oven => 2000f,
            ECookerKind.Toaster => 1000f,
            ECookerKind.Microwave => 1500f,
            _ => 0f,   // barbecue + campfire burn fuel
        };

        public static bool NeedsPower(ECookerKind k) => PowerWatts(k) > 0f;

        /// <summary>Does this appliance burn fuel at all? An oven, a toaster and a microwave run on the mains
        /// (see PowerWatts) and are not modelled as needing anything to put in them.</summary>
        public static bool NeedsFuel(ECookerKind k) => k == ECookerKind.Barbecue || k == ECookerKind.Campfire;

        /// <summary>Will this appliance burn this item? A barbecue takes charcoal and nothing else (strawberry:
        /// "bbqs can only take charcoal as a fuel"); a campfire takes anything wooden.</summary>
        public static bool IsFuelFor(ECookerKind k, ItemAsset a) => k switch
        {
            ECookerKind.Barbecue => a != null && a.id == CharcoalId,
            ECookerKind.Campfire => IsWood(a),
            _ => false,
        };

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
