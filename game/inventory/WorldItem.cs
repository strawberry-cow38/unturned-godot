using Godot;
using SDG.Unturned;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot
{
    // A dropped item in the world -- now the item's REAL 3D model as a physics prop (master 2026-07-12), replacing the
    // old rarity-tinted marker box. Extracted from core.masterbundle (tools/extract_items.py): every PEI loot-table item
    // -> a combined .txt (Wavefront OBJ) mesh + primary albedo .png + best-fit AABB box, in content/items/, indexed by
    // items_manifest.json. Spawns as a RigidBody3D: gravity + a best-fit BOX collider colliding with world+props (src
    // ItemManager.spawnItem: Rigidbody drag 0.5 / angularDrag 0.1, dropped rotation Euler(-90,rand,rand), Discrete CCD).
    // Still lives in the "worlditems" group for nearest-within-radius pickup + carries the ESP name label (P toggles) +
    // the throttled LOS visual cull (a town full of loot must not raycast-per-item every frame -- master).
    public partial class WorldItem : RigidBody3D
    {
        public Item Item;
        public Color? FallbackColor;   // unknown-id loot (no registered asset / no model): tint by its spawn TABLE
        public string FallbackName;    // ...and label by the table name (e.g. "Military Canada", "Food")
        public static bool ShowLabels; // P toggles ALL item ESP name tags on/off (off by default; PlayerController Key.P)
        public static bool NoDropRotation; // --itemtest UG_NOROT diagnostic: spawn at identity (no src drop pose) to read the raw model orientation

        float _losTimer;    // throttle the LOS visibility check (staggered) -- NOT a raycast-per-item every frame
        bool _shown = true;
        MeshInstance3D _mesh;

        // ---- shared item-model cache: parse each id's mesh/tex/box ONCE, reuse across its many spawns/despawns ----
        class Model { public ArrayMesh Mesh; public Material Mat; public Color? FlatColor; public Vector3 Box; public Vector3 Center; public bool Ok; }
        static readonly Dictionary<int, Model> _cache = new();
        static Godot.Collections.Dictionary _manifest;
        const string ItemsRoot = "res://content/items";

        static Godot.Collections.Dictionary Manifest()
        {
            if (_manifest != null) return _manifest;
            _manifest = new Godot.Collections.Dictionary();
            using var f = Godot.FileAccess.Open($"{ItemsRoot}/items_manifest.json", Godot.FileAccess.ModeFlags.Read);
            if (f != null)
            {
                var parsed = Json.ParseString(f.GetAsText());
                if (parsed.VariantType == Variant.Type.Dictionary) _manifest = parsed.AsGodotDictionary();
            }
            return _manifest;
        }

        static Model GetModel(int id)
        {
            if (_cache.TryGetValue(id, out var cached)) return cached;
            var m = new Model { Ok = false, Box = new Vector3(0.24f, 0.24f, 0.24f), Center = Vector3.Zero };
            var man = Manifest();
            var key = id.ToString(CultureInfo.InvariantCulture);
            if (man.ContainsKey(key))
            {
                var e = man[key].AsGodotDictionary();
                var mesh = ContentProvider.ParseObj($"{ItemsRoot}/{e["obj"].AsString()}");
                if (mesh != null && mesh.GetSurfaceCount() > 0)
                {
                    m.Mesh = mesh;
                    var box = e["box"].AsGodotArray(); var ctr = e["center"].AsGodotArray();
                    m.Box = new Vector3(box[0].AsSingle(), box[1].AsSingle(), box[2].AsSingle());
                    m.Center = new Vector3(ctr[0].AsSingle(), ctr[1].AsSingle(), ctr[2].AsSingle());
                    var texv = e["tex"];
                    if (texv.VariantType != Variant.Type.Nil)
                    {
                        var tp = ProjectSettings.GlobalizePath($"{ItemsRoot}/{texv.AsString()}");
                        if (System.IO.File.Exists(tp))
                        {
                            var img = Image.LoadFromFile(tp);
                            if (img != null)
                            {
                                img.GenerateMipmaps();
                                m.Mat = new StandardMaterial3D
                                {
                                    AlbedoTexture = ImageTexture.CreateFromImage(img),
                                    TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,   // blocky Unturned pixels, like the rest of the port
                                    Roughness = 0.8f,
                                };
                            }
                        }
                    }
                    if (m.Mat == null && e.ContainsKey("color"))   // no albedo texture -> the material's flat _Color is its real look (rope/bricks/suppressor...)
                    {
                        var c = e["color"].AsGodotArray();
                        if (c.Count >= 3) m.FlatColor = new Color(c[0].AsSingle(), c[1].AsSingle(), c[2].AsSingle());
                    }
                    m.Ok = true;
                }
            }
            _cache[id] = m;
            return m;
        }

        public static WorldItem Spawn(Node parent, Item item, Vector3 pos, Color? fallbackColor = null, string fallbackName = null)
        {
            var wi = new WorldItem { Item = item, FallbackColor = fallbackColor, FallbackName = fallbackName };
            parent.AddChild(wi);
            wi.GlobalPosition = pos;
            wi.ResetPhysicsInterpolation();   // global physics_interpolation is on -> don't smear from (0,0,0) to the spawn point
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

            // --- physics: gravity + src drag, sleeps at rest (a town full of loot must idle to ~0 cost) ---
            GravityScale = 1f;
            Mass = 1f;
            LinearDamp = 0.5f;                          // src Rigidbody.drag
            AngularDamp = 0.1f;                         // src Rigidbody.angularDrag
            CanSleep = true;
            CenterOfMassMode = CenterOfMassModeEnum.Auto;   // COM = box centre (offset from the model origin) so it rests naturally
            CollisionLayer = 1u << 7;                   // worlditem layer (own bit -> player + LOS ray + other items ignore it)
            CollisionMask = (1u << 0) | (1u << 6);      // rest on world/terrain/buildings (bit0) + small/transparent props (bit6)

            int id = asset != null ? asset.id : (Item?.id ?? 0);
            var model = id > 0 ? GetModel(id) : null;

            _mesh = new MeshInstance3D();
            var col = new CollisionShape3D();
            if (model != null && model.Ok)
            {
                _mesh.Mesh = model.Mesh;
                _mesh.MaterialOverride = model.Mat ?? new StandardMaterial3D { AlbedoColor = model.FlatColor ?? new Color(rar.R, rar.G, rar.B), Roughness = 0.7f };
                col.Shape = new BoxShape3D { Size = model.Box };
                col.Position = model.Center;            // mesh sits in model space; the best-fit box is offset to wrap it
            }
            else
            {
                // unknown id / no extracted model -> the old rarity marker box (keeps unmapped/nested-table ids readable)
                _mesh.Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.24f, 0.24f) };
                _mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(rar.R, rar.G, rar.B), Roughness = 0.55f };
                col.Shape = new BoxShape3D { Size = new Vector3(0.24f, 0.24f, 0.24f) };
            }
            AddChild(_mesh);
            AddChild(col);

            // src ItemManager.spawnItem drop pose: lay the vertically-authored model flat + random yaw + jitter, then settle.
            // src is Euler(-90 X,...) in UNITY; our mesh is Z-reflected (Unity->Godot), and a Z-reflection conjugates
            // Rx(-90) -> Rx(+90), so +90 lands it flat RIGHT-SIDE-UP (else every item settles upside down -- master).
            if (!NoDropRotation)
                Rotation = new Vector3(
                    Mathf.DegToRad(90f + (float)GD.RandRange(-15.0, 15.0)),
                    Mathf.DegToRad((float)GD.RandRange(0.0, 360.0)),
                    Mathf.DegToRad((float)GD.RandRange(-15.0, 15.0)));

            var label = new Label3D
            {
                Text = nm,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = rar.Lerp(Colors.White, 0.35f),
                PixelSize = 0.007f,
                Position = new Vector3(0, 0.4f, 0),
                NoDepthTest = true,
                FontSize = 64,
                OutlineSize = 10,
                Visible = ShowLabels,   // ESP name tag -- hidden until P toggles it on
            };
            AddChild(label);
            label.AddToGroup("esp_labels");
        }

        public override void _Process(double delta)
        {
            _losTimer -= (float)delta;
            if (_losTimer <= 0f)   // recompute visibility ~4x/sec, STAGGERED per item (the raycast-storm was the town stutter, master)
            {
                _losTimer = 0.22f + GD.Randf() * 0.14f;
                var cam = GetViewport().GetCamera3D();
                bool show = true;
                if (cam != null)
                {
                    show = !cam.IsPositionBehind(GlobalPosition) && cam.GlobalPosition.DistanceSquaredTo(GlobalPosition) < 40000f;
                    if (show)
                    {
                        var q = PhysicsRayQueryParameters3D.Create(cam.GlobalPosition, GlobalPosition + Vector3.Up * 0.3f);
                        q.CollisionMask = 1;   // only large world/terrain geometry (bit0) breaks line of sight
                        if (GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0) show = false;
                    }
                }
                if (Visible != show) Visible = show;   // hide the whole prop when occluded/behind -- physics keeps running so it still settles
                _shown = show;
            }
        }
    }
}
