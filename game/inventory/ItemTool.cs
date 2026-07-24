// The Godot-facing half of the item data model: the ItemAsset/Item/Assets model layer moved engine-free to
// core/UnturnedSim (MP_PLAN §3.3 -- the grid logic doubles as the MP server validator), and this keeps the one
// UI-only piece behind, named after its real source home (SDG.Unturned.ItemTool.getRarityColorUI).
namespace SDG.Unturned
{
    public static class ItemTool
    {
        // ItemTool.getRarityColorUI: the exact per-rarity UI colours
        public static Godot.Color RarityColorUI(EItemRarity r) => r switch
        {
            EItemRarity.UNCOMMON  => new Godot.Color(0.12156863f, 0.5294118f, 0.12156863f),
            EItemRarity.RARE      => new Godot.Color(0.29411766f, 20f / 51f, 50f / 51f),
            EItemRarity.EPIC      => new Godot.Color(0.5882353f, 0.29411766f, 50f / 51f),
            EItemRarity.LEGENDARY => new Godot.Color(40f / 51f, 10f / 51f, 50f / 51f),
            EItemRarity.MYTHICAL  => new Godot.Color(50f / 51f, 10f / 51f, 5f / 51f),
            _ => Godot.Colors.White,
        };

        // ItemTool.getQualityColor: food/water CONDITION colour ramp -- red (spoiled) -> yellow (half) -> green (fresh).
        // Byte-exact port of the source (Palette.COLOR_R #bf1f1f / COLOR_Y #dcb413 / COLOR_G #1f871f). q01 = quality/100.
        public static Godot.Color QualityColor(float q01)
        {
            q01 = Godot.Mathf.Clamp(q01, 0f, 1f);
            var r = new Godot.Color(191f / 255f, 31f / 255f, 31f / 255f);
            var y = new Godot.Color(220f / 255f, 180f / 255f, 19f / 255f);
            var g = new Godot.Color(31f / 255f, 135f / 255f, 31f / 255f);
            return q01 < 0.5f ? r.Lerp(y, q01 * 2f) : y.Lerp(g, (q01 - 0.5f) * 2f);
        }
    }
}
