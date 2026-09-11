using System;

namespace SDG.Unturned
{
    /// <summary>TWO insulation values per garment, in degrees C (strawberry 2026-09-10: "each clothing piece
    /// has two separate insulation values, heat:cold"). Separate because they are separate problems -- a parka
    /// is excellent in snow and actively bad in a desert, and one number cannot say that.
    ///
    /// KEYED OFF THE GARMENT WORD IN THE ITEM NAME, not a hand-authored id table. The real catalog names things
    /// "Orange Parka", "Green Hoodie", "Red T-Shirt" -- the garment is the last word and the colour is the
    /// first, so one rule covers all 8 parkas and all 19 hoodies and keeps covering them when someone adds a
    /// Blue one. An id table would need ~400 rows hand-written, would be wrong the first time a variant landed,
    /// and would be the kind of content nobody maintains.
    ///
    /// These numbers are the PORT'S OWN. Retail has no ambient temperature and its .dat files carry no
    /// insulation key, so there is nothing to extract and nothing here is pretending to be extracted -- same
    /// reasoning that kept the weather offsets out of WeatherType and off ClothingDef.</summary>
    public static class ClothingInsulation
    {
        /// <summary>(cold, heat): how many degrees this garment is worth against each. Heat protection is
        /// mostly about shade and breathability, which is why a cap beats a helmet in the sun and a t-shirt
        /// beats a parka.</summary>
        public static (float cold, float heat) For(string itemName, EItemType slot)
        {
            string word = LastWord(itemName);
            switch (word)
            {
                // --- cold-weather kit: the whole point of the two-axis split ---
                case "parka":       return (8f, 0f);
                case "jacket":      return (5f, 0f);
                case "hoodie":      return (4f, 0f);
                case "poncho":      return (4f, 1f);   // also sheds rain, which Proof_Water handles separately
                case "toque":
                case "balaclava":
                case "scarf":
                case "sweatervest": return (3f, 0f);
                case "hood":        return (2f, 0f);

                // --- ordinary clothes ---
                case "jersey":      return (2f, 0f);
                case "shirt":
                case "top":         return (1.5f, 0f);
                case "jeans":
                case "pants":
                case "bottom":      return (2f, 0f);
                case "vest":        return (2f, 0f);

                // --- hot-weather kit: negative cold value would be wrong (a t-shirt does not make you COLDER
                //     than being bare), so these simply give little cold and real heat relief ---
                case "t-shirt":     return (0.5f, 1f);
                case "shorts":
                case "trunks":      return (0f, 2f);
                case "cap":
                case "beret":       return (0.5f, 1.5f);   // shade on your head is worth more in the sun than fabric

                // --- a helmet is the one piece that HURTS in the heat: sealed, and usually steel ---
                case "helmet":      return (1f, -1f);

                case "hat":         return (1f, 1f);
                case "mask":
                case "bandana":     return (0.5f, 0f);

                // --- carries nothing thermally ---
                case "goggles":
                case "glasses":
                case "monocle":
                case "tie":
                case "bowtie":      return (0f, 0f);
                case "daypack":
                case "travelpack":
                case "dufflebag":   return (0.5f, 0f);
            }
            return SlotDefault(slot);
        }

        /// <summary>What an unrecognised garment is worth, from its slot alone. Something rather than nothing:
        /// a new item type should behave like a plausible member of its slot rather than like being naked,
        /// because the failure of returning 0 is silent and only shows up as "this coat does nothing".</summary>
        public static (float cold, float heat) SlotDefault(EItemType slot) => slot switch
        {
            EItemType.SHIRT => (1.5f, 0f),
            EItemType.PANTS => (1.5f, 0f),
            EItemType.VEST => (1f, 0f),
            EItemType.HAT => (0.5f, 1f),
            EItemType.MASK => (0.5f, 0f),
            EItemType.BACKPACK => (0.5f, 0f),
            EItemType.GLASSES => (0f, 0f),
            _ => (0f, 0f),
        };

        static string LastWord(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            name = name.Trim();
            int sp = name.LastIndexOf(' ');
            return (sp < 0 ? name : name.Substring(sp + 1)).ToLowerInvariant();
        }
    }
}
