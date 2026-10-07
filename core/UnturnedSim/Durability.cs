using System;
using System.Collections.Generic;
using UnturnedGodot;

namespace SDG.Unturned
{
    /// <summary>
    /// DURABILITY (strawberry 2026-10-06: "every tool, weapon, clothing piece has durability. (like how food has
    /// "durability", which is its spoilage) clothes get damaged by being worn (over hours) and when taking incoming
    /// damage. tools get worn by being crafted with (its a chance to lower durability) weapons; melee and firearms have
    /// a chance to lower durability when "shot" (incl melee)").
    ///
    /// The number is the one food already uses: <see cref="Item.quality"/>, a byte 0-100. Nothing new is stored -- what
    /// is new is that guns, melee weapons, clothing and crafting tools now LOSE it, and that it now MEANS something.
    ///
    /// Her answers to the follow-ups, which this file is the rulebook for:
    ///   - at 0 an item is BROKEN, not gone: a broken gun will not fire, a broken melee weapon will not swing, a broken
    ///     tool does not count for a recipe, and broken clothes stay on but protect and insulate nothing.
    ///   - before 0, retail's penalties: under 50% a gun kicks and spreads more and hits softer, a melee weapon hits
    ///     softer. NO jamming ("thats a whole new can of worms").
    ///   - clothing protection and insulation scale with condition.
    ///   - no repair yet ("i have plans to add additional crafting materials that would effect those recipes").
    ///
    /// Engine-free on purpose: the server applies every loss (the owner's copy is overwritten by the next inventory
    /// echo, so a client-side write would simply be undone), and the client reads the same functions for the penalties.
    /// </summary>
    public static class Durability
    {
        public enum Kind : byte { None, Gun, Melee, Clothing, Tool }

        /// <summary>Items that some recipe uses as a TOOL (a blueprint input with Consume=false: the saw for planks).
        /// Filled by whoever loads the blueprints (BlueprintRegistry in the game, the test fixtures in core). A tool is a
        /// role, not an item type, so this cannot be read off the asset.</summary>
        public static readonly HashSet<ushort> ToolIds = new HashSet<ushort>();

        /// <summary>Record every item these recipes use as a tool. Additive -- a second catalogue adds its tools.</summary>
        public static void RegisterTools(IEnumerable<BlueprintDef> blueprints)
        {
            if (blueprints == null) return;
            foreach (var bp in blueprints)
                foreach (var ing in bp.Inputs)
                {
                    if (ing.Consume) continue;
                    ushort id = Crafting.Resolve(ing.Guid);
                    if (id != 0) ToolIds.Add(id);
                }
        }

        public static Kind KindOf(ItemAsset a)
        {
            if (a == null) return Kind.None;
            if (!string.IsNullOrEmpty(a.gunName) || a.type == EItemType.GUN) return Kind.Gun;
            if (!string.IsNullOrEmpty(a.meleeName) || a.type == EItemType.MELEE) return Kind.Melee;
            if (IsClothing(a.type)) return Kind.Clothing;
            if (ToolIds.Contains(a.id)) return Kind.Tool;
            return Kind.None;
        }

        public static Kind KindOf(ushort id) => KindOf(Assets.find(id));

        public static bool IsClothing(EItemType t) =>
            t == EItemType.HAT || t == EItemType.SHIRT || t == EItemType.PANTS || t == EItemType.VEST
            || t == EItemType.BACKPACK || t == EItemType.MASK || t == EItemType.GLASSES;

        /// <summary>Does this item carry a CONDITION the player should see (and that can break)? Food has its own
        /// meaning for the same byte (freshness) and is not one of these.</summary>
        public static bool HasCondition(ItemAsset a) => KindOf(a) != Kind.None;

        /// <summary>A conditioned item at 0. Never true of food or of anything without a condition.</summary>
        public static bool IsBroken(Item it) => it != null && it.quality == 0 && HasCondition(Assets.find(it.id));

        // ---------------------------------------------------------------- penalties (retail UseableGun / UseableMelee)

        /// <summary>Recoil and spread multiplier. Retail: quality &lt; 0.5 ? 1 + (1 - quality*2) : 1 -- so 1x at 50%,
        /// rising linearly to 2x at 0.</summary>
        public static float HandlingPenalty(byte quality)
        {
            float q = quality / 100f;
            return q < 0.5f ? 1f + (1f - q * 2f) : 1f;
        }

        /// <summary>Damage multiplier for a gun's bullet or a melee hit. Retail: quality &lt; 0.5 ? 0.5 + quality : 1 --
        /// so full damage down to 50%, then falling to half at 0.</summary>
        public static float DamageMultiplier(byte quality)
        {
            float q = quality / 100f;
            return q < 0.5f ? 0.5f + q : 1f;
        }

        // ---------------------------------------------------------------- weapons

        /// <summary>One USE of a weapon -- a shot fired, a melee swing. Retail: with probability <c>a.durability</c> the
        /// item loses <c>a.wear</c> points (at least 1), floored at 0. Returns the points lost (0 most of the time).
        /// Broken weapons are not used, so a call at 0 changes nothing.</summary>
        public static int UseWeapon(Item it, ItemAsset a, Func<double> roll)
        {
            if (it == null || a == null || it.quality == 0 || a.durability <= 0f) return 0;
            if (roll() >= a.durability) return 0;
            int loss = Math.Min((int)it.quality, Math.Max(1, (int)a.wear));
            it.quality = (byte)(it.quality - loss);
            return loss;
        }

        // ---------------------------------------------------------------- tools

        /// <summary>strawberry: "tools get worn by being crafted with (its a chance to lower durability)".</summary>
        public const float ToolWearChance = 0.25f;
        public const int ToolWearPoints = 3;

        /// <summary>One craft that needed this tool. Returns the points lost.</summary>
        public static int UseTool(Item it, Func<double> roll)
        {
            if (it == null || it.quality == 0) return 0;
            if (roll() >= ToolWearChance) return 0;
            int loss = Math.Min((int)it.quality, ToolWearPoints);
            it.quality = (byte)(it.quality - loss);
            return loss;
        }

        // ---------------------------------------------------------------- clothing

        /// <summary>A garment worn continuously goes from 100 to 0 in this many in-game days ("clothes get damaged by
        /// being worn (over hours)").</summary>
        public const float ClothingDaysToZero = 10f;

        /// <summary>Points a worn piece loses each time a hit lands on the part of the body it covers (retail
        /// DamageTool.getPlayerArmor takes exactly 1).</summary>
        public const int ClothingHitPoints = 1;

        /// <summary>The share of a hit that gets THROUGH one piece. Retail getPlayerArmor:
        /// armor + (1 - armor) * (1 - quality/100). At full condition it is the piece's armor value (0.8 = 20% stopped);
        /// at 0 it is 1 -- nothing stopped. A piece with no armor (1.0) passes everything at any condition.</summary>
        public static float PassThrough(ItemAsset a, byte quality)
        {
            if (a == null) return 1f;
            float q = quality / 100f;
            return a.armor + (1f - a.armor) * (1f - q);
        }

        /// <summary>The same scaling for a WHOLE-BODY multiplier (falls, explosions): a multiplier m at full condition
        /// relaxes toward 1 as the piece wears, and is 1 when it is broken.</summary>
        public static float ScaleMultiplier(float m, byte quality) => m + (1f - m) * (1f - quality / 100f);

        /// <summary>Insulation scales straight with condition; a broken garment keeps no warmth.</summary>
        public static float InsulationScale(byte quality) => quality / 100f;

        /// <summary>Where a hit landed, for deciding which worn pieces cover it.</summary>
        public enum Zone : byte { None, Head, Torso, Legs, Whole }

        /// <summary>The worn slots covering a zone (retail getPlayerArmor: legs = pants; arms/spine = shirt, spine also
        /// the vest; the skull = hat). The port's hits know head/torso/legs, so arms fold into torso. Masks and
        /// glasses ride with the head. Whole = every worn piece (a blast).</summary>
        public static IEnumerable<EItemType> Covering(Zone z)
        {
            switch (z)
            {
                case Zone.Head: yield return EItemType.HAT; yield return EItemType.MASK; yield return EItemType.GLASSES; break;
                case Zone.Torso: yield return EItemType.SHIRT; yield return EItemType.VEST; yield return EItemType.BACKPACK; break;
                case Zone.Legs: yield return EItemType.PANTS; break;
                case Zone.Whole:
                    foreach (var t in new[] { EItemType.HAT, EItemType.MASK, EItemType.GLASSES, EItemType.SHIRT, EItemType.VEST,
                                              EItemType.BACKPACK, EItemType.PANTS })
                        yield return t;
                    break;
            }
        }

        // ---------------------------------------------------------------- the worn look on the wire

        /// <summary>Pack a player's seven worn slots' conditions into 4-bit steps, for other clients to DRAW (the
        /// appearance block's WornCond). Order: hat, glasses, mask, shirt, vest, backpack, pants. A piece with any
        /// condition left never packs to 0 -- 0 is reserved for broken, so a 2% shirt cannot look broken to others.</summary>
        public static uint PackWorn(PlayerInventory inv)
        {
            if (inv == null) return 0;
            uint p = 0;
            var slots = new[] { inv.wornHat, inv.wornGlasses, inv.wornMask, inv.wornShirt, inv.wornVest, inv.wornBackpack, inv.wornPants };
            for (int i = 0; i < slots.Length; i++)
            {
                byte q = slots[i]?.quality ?? 100;
                uint n = q == 0 ? 0u : (uint)Math.Max(1, (int)Math.Round(q * 15 / 100.0));
                p |= n << (i * 4);
            }
            return p;
        }

        /// <summary>The condition (0-100) a packed slot stands for. slot: 0 hat .. 6 pants, as PackWorn.</summary>
        public static byte UnpackWorn(uint pack, int slot)
        {
            uint n = (pack >> (slot * 4)) & 0xF;
            return (byte)Math.Round(n * 100 / 15.0);
        }

        // ---------------------------------------------------------------- loot

        /// <summary>A spawned item's condition: uniform in [min, max] (retail Item ctor) when <paramref name="bias"/> is
        /// 0. The bias (-1..+1, from the map editor's per-table setting) bends the draw toward worse or better without
        /// moving the ends: u^(2^(-2*bias)) -- +1 squares-roots twice (most spawns near max), -1 raises to the 4th
        /// (most near min).</summary>
        public static byte RollCondition(byte min, byte max, float bias, Random rng)
        {
            int lo = Math.Min(min, max), hi = Math.Max(min, max);
            double u = rng.NextDouble();
            if (bias != 0f) u = Math.Pow(u, Math.Pow(2.0, -2.0 * Math.Clamp(bias, -1f, 1f)));
            return (byte)Math.Clamp(lo + (int)Math.Floor(u * (hi - lo + 1)), lo, hi);
        }
    }
}
