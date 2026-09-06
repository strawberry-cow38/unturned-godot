using SDG.Unturned;

namespace SDG.Unturned
{
    /// <summary>FROZEN, as a percentage (strawberry 2026-09-06: "add a 'frozen' state ... frozen acts as a %").
    ///
    /// The whole feature in one line: a freezer drives `Item.frozen` up, everywhere else drives it down, and
    /// three other systems read it -- eating (blocked while frozen), cooking (cannot start until thawed, and
    /// thaws faster while it tries), and spoilage (halted completely at 100).
    ///
    /// WHY IT IS A SEPARATE AXIS FROM `quality`. Freshness and temperature are independent: a steak frozen the
    /// day it was cut and one frozen a week later are equally solid and not equally good. Folding them into one
    /// number would make "never spoils at 100 % frozen" mean "resets to fresh", which is a different and much
    /// worse game rule -- you could launder a rotten steak by freezing it.
    ///
    /// RATES ARE PER SECOND OF REAL TIME, not per in-game day like spoilage. Freezing is something a player
    /// stands and waits for, so it has to happen on the timescale of a visit to the fridge; spoilage is
    /// something they come back to days later. Same reason cooking is per-second.</summary>
    public static class Freezing
    {
        public const byte Max = 100;          // "frozen acts as a %" -- so the field IS the percentage, 0..100
        public const byte SolidAt = 100;      // "at 100% they NEVER spoil"

        // A full freeze from room temperature takes ~50 s of standing there; thawing on a shelf takes ~2.5x that,
        // because food coming out of a freezer staying usable for a while is the point of a freezer.
        public const float FreezePerSecond = 2.0f;
        public const float ThawPerSecond = 0.8f;

        /// <summary>Thawing inside something that is actively heating it. "drops faster when being cooked" --
        /// 8x the shelf rate, so a frozen steak in an oven is a short wait and not a punishment, and the oven
        /// visibly does something while the food is still solid.</summary>
        public const float CookingThawPerSecond = 6.4f;

        public static bool IsFrozen(Item i) => i != null && i.frozen > 0;
        public static bool IsSolid(Item i) => i != null && i.frozen >= SolidAt;

        /// <summary>Can this be eaten right now? "frozen food cannot be eaten until thawed" -- ANY frozen
        /// percentage blocks it, not just a solid one. Biting a 20 %-frozen steak is still biting ice.</summary>
        public static bool CanEat(Item i) => !IsFrozen(i);

        /// <summary>Can cooking progress start? "cooking from 0-100% starts after the food is thawed".</summary>
        public static bool CanCook(Item i) => !IsFrozen(i);

        /// <summary>Only FOOD freezes. A frozen rifle is not a mechanic, and letting `frozen` accumulate on
        /// arbitrary items would put a meaningless number on every tooltip in the game.</summary>
        public static bool Freezable(ItemAsset a) => a != null && a.type == EItemType.FOOD;

        // SUB-UNIT PROGRESS HAS TO BE REMEMBERED, and forgetting it is not a rounding nicety -- it is the
        // difference between the feature working and silently not.
        //
        // `frozen` is a whole-number percent and the sweep runs at 2 Hz, so one thaw step is 0.8 %/s * 0.5 s =
        // 0.4 %. Rounding that to the nearest byte gives back the number you started with, EVERY time, forever:
        // food froze fine (2 %/s clears the half-unit) and then never thawed at all. Asymmetric, silent, and it
        // would have looked like "the freezer is broken" rather than like arithmetic.
        //
        // So the fraction is carried between steps, keyed weakly on the item itself. ConditionalWeakTable rather
        // than a Dictionary because it must not keep a consumed steak alive: entries evaporate with the item.
        // Floor + carry also makes the effective rate exact over time instead of rate-plus-rounding-bias.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item, StrongBoxF> Carry = new();
        sealed class StrongBoxF { public float V; }

        /// <summary>Advance an item's frozen % at `perSecond`, keeping the sub-percent remainder across calls.
        /// This is what every stepping system should use; plain Advance is the pure rule, for tests and for
        /// one-shot arithmetic where there is no next step to carry into.</summary>
        public static void AdvanceCarried(Item item, float perSecond, float dt)
        {
            if (item == null) return;
            var box = Carry.GetValue(item, _ => new StrongBoxF());
            float v = item.frozen + box.V + perSecond * dt;
            if (v <= 0f) { item.frozen = 0; box.V = 0f; return; }
            if (v >= Max) { item.frozen = Max; box.V = 0f; return; }
            byte whole = (byte)UnityEngine.Mathf.FloorToInt(v);
            item.frozen = whole;
            box.V = v - whole;
        }

        public static byte Advance(byte frozen, float perSecond, float dt)
        {
            float v = frozen + perSecond * dt;
            if (v <= 0f) return 0;
            if (v >= Max) return Max;
            return (byte)UnityEngine.Mathf.RoundToInt(v);
        }

        public static byte Freeze(byte frozen, float dt) => Advance(frozen, FreezePerSecond, dt);
        public static byte Thaw(byte frozen, float dt) => Advance(frozen, -ThawPerSecond, dt);
        public static byte ThawWhileCooking(byte frozen, float dt) => Advance(frozen, -CookingThawPerSecond, dt);

        /// <summary>How fast this item spoils, as a MULTIPLIER on its normal per-day rate.
        ///
        /// Solid-frozen is a hard zero ("at 100% they NEVER spoil"), a fridge is a heavy slowdown rather than
        /// the halt it used to be ("fridge'd items spoil at a much slower rate"), and partial freezing scales
        /// smoothly in between so there is no cliff at 99 %.</summary>
        public static float SpoilMultiplier(byte frozen, bool refrigerated)
        {
            if (frozen >= SolidAt) return 0f;
            float m = refrigerated ? 0.15f : 1f;
            // Half-frozen food keeps about half as well again; at 0 this is a no-op.
            return m * (1f - frozen / (float)Max * 0.5f);
        }

        /// <summary>The word a player sees. Nothing at all when it is not frozen -- an unfrozen steak must not
        /// carry a "0 % frozen" label, the same reason plain cooked food has no quality word.</summary>
        public static string Label(byte frozen)
        {
            if (frozen == 0) return null;
            if (frozen >= SolidAt) return "Frozen";
            return "Partly Frozen";
        }
    }
}
