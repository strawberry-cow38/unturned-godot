using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A dropped item in the world -- the port's bounded stand-in for ItemManager's ItemData + InteractableItem. Holds
    // the Item, shows a small rarity-tinted marker + a billboarded name, gently bobs/spins, and lives in the
    // "worlditems" group so the player can find + pick it up by proximity. (Real Unturned drops the item's 3D model +
    // uses look-at interact; we use a marker + nearest-within-radius pickup, consistent with the inventory's tiles.)
    public partial class WorldItem : Node3D
    {
        public Item Item;
        double _t;
        MeshInstance3D _box;

        public static WorldItem Spawn(Node parent, Item item, Vector3 pos)
        {
            var wi = new WorldItem { Item = item };
            parent.AddChild(wi);
            wi.GlobalPosition = pos;
            return wi;
        }

        public override void _Ready()
        {
            AddToGroup("worlditems");
            var asset = Item?.GetAsset();
            Color rar = asset != null ? ItemAsset.RarityColorUI(asset.rarity) : Colors.White;

            _box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.28f, 0.28f, 0.28f) } };
            _box.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(rar.R, rar.G, rar.B), Roughness = 0.55f };
            _box.Position = new Vector3(0, 0.28f, 0);
            AddChild(_box);

            var label = new Label3D
            {
                Text = asset?.itemName ?? "?",
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = rar.Lerp(Colors.White, 0.35f),
                PixelSize = 0.007f,
                Position = new Vector3(0, 0.72f, 0),
                NoDepthTest = true,
                FontSize = 64,
                OutlineSize = 10,
            };
            AddChild(label);
        }

        public override void _Process(double delta)
        {
            _t += delta;
            RotateY((float)delta * 1.1f);                                    // slow spin
            if (_box != null) _box.Position = new Vector3(0, 0.28f + 0.05f * Mathf.Sin((float)_t * 2.2f), 0);   // bob
        }
    }
}
