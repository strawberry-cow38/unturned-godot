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
        float _losTimer;    // throttle the LOS visibility check (staggered) so a town full of loot isn't a raycast-per-item EVERY frame (master: town stutter)
        bool _shown = true;

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
            _losTimer -= (float)delta;
            if (_losTimer <= 0f)   // recompute visibility ~4x/sec, STAGGERED per item -- NOT a LOS raycast every frame per item (that raycast-storm was the town stutter, master)
            {
                _losTimer = 0.22f + GD.Randf() * 0.14f;
                var cam = GetViewport().GetCamera3D();
                bool show = true;
                if (cam != null)
                {
                    // Derender when we can't see it (master): behind the camera, far out, OR occluded by terrain/walls (LOS raycast).
                    show = !cam.IsPositionBehind(GlobalPosition) && cam.GlobalPosition.DistanceSquaredTo(GlobalPosition) < 40000f;
                    if (show)
                    {
                        var q = PhysicsRayQueryParameters3D.Create(cam.GlobalPosition, GlobalPosition + Vector3.Up * 0.3f);
                        q.CollisionMask = 1;   // world/terrain static geometry between the camera and the item = no line of sight
                        if (GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0) show = false;
                    }
                }
                if (Visible != show) Visible = show;
                _shown = show;
            }
            if (!_shown) return;   // no _Process work while it's not on screen
            // NO spin (master: killed everywhere) -- just a gentle bob while it's visible
            _t += delta;
            if (_box != null) _box.Position = new Vector3(0, 0.28f + 0.05f * Mathf.Sin((float)_t * 2.2f), 0);
        }
    }
}
