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
        ///
        /// ⚠⚠ 2026-10-07: THIS WAS 9150 AND THERE WERE TWO CHARCOALS. When master asked whether charcoal existed
        /// already, I added a second one -- 9333, in items_catalog.tsv, with a guid, a stack size, the black-powder
        /// recipe and the barbecue's new loot entry all pointing at it. 9150 is registered by ItemCatalog.Add(), so
        /// BOTH existed, and this constant still named the old one: the charcoal a player finds in a BBQ was 9333
        /// and the only thing the BBQ would burn was 9150. Nothing threw. You just could not light it.
        /// ⭐ The id space has FOUR issuers (this TSV, ItemCatalog.Add, DeployableDef, content/items/*.txt) and I had
        /// checked one of them. 9333 wins because it is the one with a guid -- a blueprint keys by guid, and an
        /// Add()-only item has none, so 9150 could never have been craftable with or into anything.</summary>
        public const ushort CharcoalId = 9333;

        /// <summary>Split logs (master 2026-10-07: "add a new firewood item ... firewood doesnt have a 'type' so it
        /// stacks regardless of the log that made it ... burns at the sane rate as logs").
        ///
        /// ⭐ IT NEEDS AN ID HERE BECAUSE IT HAS NO SPECIES. Every other wooden fuel is recognised by its NAME --
        /// SpeciesBurn looks for Maple/Birch/Pine -- and the entire point of firewood is that the species is gone,
        /// which is exactly what lets it stack. So the name test returns 0, IsWood would say it is not wood, and
        /// the thing whose whole purpose is burning would not burn. Named explicitly instead, like charcoal.</summary>
        public const ushort FirewoodId = 9342;

        /// <summary>What a species-less wood burns at: the MIDDLE species (birch, 1.0). "The same rate as logs" has
        /// to mean one of the three, and the one that is neither the dense nor the resinous end is the honest answer
        /// for a pile with no species on it -- so one firewood burns exactly as long as one birch log.
        ///
        /// ⭐ AND THE ARITHMETIC THAT FALLS OUT IS DELIBERATE, so do not "fix" it: 1 log gives 2 firewood that each
        /// burn a whole log's worth, and 12 fit the 2x1 a log stacks 4 into. Master 2026-10-07, asked directly:
        /// "say our character somehow magically dried out the wood too when splitting it: burns better than a wet
        /// log. chunks of firewood easier to carry than a full heavy log". What pays for it is that firewood is a
        /// DEAD END -- "it cant be used to craft anything else, its a one way 'i WILL burn this' choice" -- which
        /// craft.firewood asserts, so it stays a choice rather than quietly becoming a free upgrade.</summary>
        public const float SpeciesReference = 1.0f;

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
            => a != null
               && (a.id == FirewoodId                       // species-less by design -- see FirewoodId
                   || (SpeciesBurn(a.itemName) > 0f
                       && (a.type == EItemType.SUPPLY || a.type == EItemType.GENERIC)));

        /// <summary>How long one unit of this fuel burns, in seconds. Size counts (strawberry: "the size of
        /// wooden fuel having different burn times") and the GRID FOOTPRINT is the measure -- it is already in
        /// the catalog, it is what the player sees, and it means a 2x2 plate outlasts a 1x1 stick by exactly
        /// the factor it looks like it should. Charcoal is a flat rate: briquettes are briquettes.</summary>
        public static float BurnSecondsFor(ItemAsset a)
        {
            if (a == null) return 0f;
            if (a.id == CharcoalId) return 45f;
            // Firewood takes the reference species rather than 0, so its footprint still sets the number: at its
            // 2x1 that is 40 s, the same as a birch log, which is what "the same rate as logs" asks for.
            float species = a.id == FirewoodId ? SpeciesReference : SpeciesBurn(a.itemName);
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

        /// <summary>Will this appliance burn this item? A barbecue takes charcoal plus the small wooden fuels
        /// (master 2026-10-07: "bbq should burn logs, sticks, planks, firewood" -- it was charcoal-only before,
        /// strawberry 2026-09-06: "bbqs can only take charcoal as a fuel"); a campfire takes anything wooden,
        /// doors and barricades included.</summary>
        public static bool IsFuelFor(ECookerKind k, ItemAsset a) => k switch
        {
            ECookerKind.Barbecue => a != null && (a.id == CharcoalId || IsGrillWood(a)),
            ECookerKind.Campfire => IsWood(a),
            _ => false,
        };

        /// <summary>Wood a BARBECUE will take, as opposed to wood a bonfire will take.
        ///
        /// Master 2026-10-07: "bbq should burn logs, sticks, planks, firewood" -- which replaces the older
        /// charcoal-only rule. ⭐ DERIVED, NOT A LIST OF THE FOUR. Those four are exactly the wooden items whose
        /// type is SUPPLY. MEASURED, and the measurement is the whole argument: the 9 wooden-named SUPPLY items are
        /// precisely the three logs, three sticks and three planks, plus firewood -- master's list, with nothing
        /// else in it. Everything else wooden (Maple Doorway, Pine Fortification, Birch Wall) loads as GENERIC,
        /// ⚠ NOT as Structure/Barricade: the TSV says "Structure" but the runtime enum does not keep it, which is
        /// why the filter has to be "is SUPPLY" rather than "is not a structure". So the type does the work, a
        /// fifth kind of log is included the day it exists, and you still cannot feed a doorway into a kettle
        /// grill -- the difference between a barbecue and a bonfire, and the reason this is not just IsWood.</summary>
        public static bool IsGrillWood(ItemAsset a) => IsWood(a) && a.type == EItemType.SUPPLY;

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
        /// <summary>The food's name with any state word it SHIPPED WITH removed, and whether it had one.
        ///
        /// ⭐⭐ RETAIL ALREADY SAYS WHICH FOODS CARE ABOUT BEING COOKED, and it says it in the name. The catalog
        /// ships 31 of them as matched pairs -- Raw Trout / Cooked Trout, Raw Venison / Cooked Venison, Raw
        /// Goldfish / Cooked Goldfish -- out of 119 foods. Those are the meat and the fish: exactly master's
        /// "only food thats dangerous uncooked". An apple is just an Apple. So the test for "does this need a
        /// RAW label" is not a keyword list I invent and have to maintain against the catalog; it is whether the
        /// catalog already gave it one.</summary>
        public static (string name, bool statesCooking) BaseFoodName(string n)
        {
            if (n == null) return (null, false);
            if (n.StartsWith("Raw ", System.StringComparison.OrdinalIgnoreCase)) return (n.Substring(4), true);
            if (n.StartsWith("Cooked ", System.StringComparison.OrdinalIgnoreCase)) return (n.Substring(7), true);
            return (n, false);
        }

        public static string Label(byte cooked, ECookStyle style) => Label(cooked, style, true);

        /// <param name="statesCooking">Does this food's own name carry a state word (Raw X / Cooked X)? Only
        /// those get a RAW label; for everything else raw IS the normal condition and deserves no word.</param>
        public static string Label(byte cooked, ECookStyle style, bool statesCooking)
        {
            if (IsBurnt(cooked)) return "Burnt";
            if (!IsCooked(cooked)) return statesCooking ? "Raw" : "";
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
            // ⚠⚠ STRIP THE SHIPPED STATE WORD BEFORE ADDING ONE. The item is CALLED "Raw Goldfish", and this
            // used to paste a second Raw in front of it -- master's screenshot read "Frozen Raw Raw Goldfish".
            // Cooking it would have been worse still: "Cooked Raw Goldfish". The state word has to be the
            // shader of this name, not an accumulation of them.
            // ⭐ And it lands on retail's own spelling for free: a cooked "Raw Goldfish" now reads "Cooked
            // Goldfish", which is exactly what item 1350 is called.
            var (baseName, statesCooking) = BaseFoodName(itemName);
            string label = Label(cooked, style, statesCooking);
            return label.Length > 0 ? $"{label} {baseName}" : baseName;
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
