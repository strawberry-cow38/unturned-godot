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
        public Color? FallbackColor;   // unknown-id loot (no registered asset): tint by its spawn TABLE instead of white
        public string FallbackName;    // ...and label by the table name (e.g. "Military Canada", "Food")
        public static bool ShowLabels; // P toggles ALL item ESP name tags on/off (off by default; see PlayerController Key.P)
        double _t;
        MeshInstance3D _box;

        public static WorldItem Spawn(Node parent, Item item, Vector3 pos, Color? fallbackColor = null, string fallbackName = null)
        {
            var wi = new WorldItem { Item = item, FallbackColor = fallbackColor, FallbackName = fallbackName };
            parent.AddChild(wi);
            wi.GlobalPosition = pos;
            return wi;
        }

        public override void _Ready()
        {
            AddToGroup("worlditems");
            var asset = Item?.GetAsset();
            Color rar; string nm;
            if (asset != null) { rar = ItemAsset.RarityColorUI(asset.rarity); nm = asset.itemName; }
            else if (FallbackColor.HasValue) { rar = FallbackColor.Value; nm = FallbackName ?? "?"; }
            else { rar = Colors.White; nm = "?"; }

            _box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.28f, 0.28f, 0.28f) } };
            _box.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(rar.R, rar.G, rar.B), Roughness = 0.55f };
            _box.Position = new Vector3(0, 0.28f, 0);
            AddChild(_box);

            var label = new Label3D
            {
                Text = nm,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = rar.Lerp(Colors.White, 0.35f),
                PixelSize = 0.007f,
                Position = new Vector3(0, 0.72f, 0),
                NoDepthTest = true,
                FontSize = 64,
                OutlineSize = 10,
                Visible = ShowLabels,   // ESP name tag -- hidden until P toggles it on
            };
            AddChild(label);
            label.AddToGroup("esp_labels");   // PlayerController Key.P flips visibility on the whole group
        }

        public override void _Process(double delta)
        {
            var cam = GetViewport().GetCamera3D();   // behind the player (or way out) -> kill the spin/bob entirely, zero CPU (master); the mesh frustum-culls itself
            if (cam != null && (cam.IsPositionBehind(GlobalPosition) || cam.GlobalPosition.DistanceSquaredTo(GlobalPosition) > 40000f)) return;
            _t += delta;
            RotateY((float)delta * 1.1f);                                    // slow spin
            if (_box != null) _box.Position = new Vector3(0, 0.28f + 0.05f * Mathf.Sin((float)_t * 2.2f), 0);   // bob
        }
    }
}
