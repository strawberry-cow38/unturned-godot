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
    }
}
