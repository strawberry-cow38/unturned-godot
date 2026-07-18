using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Master's loot-rework step 2: a store shelf (a real Unturned OPEN-tier shelf prop) that's ALSO a container. Walk up +
    // F to open its grid like a crate, but its contents are ALSO shown as the items' real 3D models sitting on the shelf
    // tiers -- no physics, neatly placed (the display-storage mechanic from InteractableStorage.isDisplay, generalized to
    // the whole grid). Contents roll from a PEI item drop table on spawn (like LootCrate). MeshName picks which shelf prop
    // + its tier PROFILE (Shelf_1 store gondola, Shelf_0 wood/metal shelf, ...). Only fits OPEN-tier shelves; solid-front
    // props (bookcases/fridges) use a plain container instead. LootTables.Load must have run first.
    public partial class StoreShelf : StorageCrate
    {
        public int TableIndex = 0;
        public int MinItems = 8, MaxItems = 16;
        public string MeshName = "Shelf_1";
        public bool ShowItems = true;        // open shelves show their loot on the tiers; solid props (fridge/wardrobe) don't
        public string LabelText = "Store Shelf";

        // per-shelf-type tier layout: TierY = shelf-surface heights as fractions of the STANDING AABB; PerTier = item
        // slots across the width; WidthUse = fraction of width used (end margins); FrontZ = how far toward the front face.
        class Profile { public float[] TierY; public int PerTier; public float WidthUse; public float FrontZ; public int Min, Max; }
        static readonly Dictionary<string, Profile> Profiles = new()
        {
            ["Shelf_1"] = new Profile { TierY = new[] { 0.20f, 0.50f, 0.80f }, PerTier = 6, WidthUse = 0.82f, FrontZ = 0.30f, Min = 8, Max = 16 },   // store gondola (5m wide)
            ["Shelf_0"] = new Profile { TierY = new[] { 0.18f, 0.48f, 0.78f }, PerTier = 3, WidthUse = 0.78f, FrontZ = 0.45f, Min = 5, Max = 10 },   // wood/metal shelf (~1.9m wide, deep)
        };
        static Profile Prof(string mesh) => Profiles.TryGetValue(mesh, out var p) ? p : Profiles["Shelf_1"];

        static readonly Dictionary<string, ArrayMesh> _meshes = new();
        static readonly Dictionary<string, Material> _mats = new();

        public StoreShelf() { Width = 8; Height = 6; }   // roomier grid than a crate

        public static StoreShelf Spawn(Node parent, Vector3 pos, string meshName, int table, float yawDeg = 0f, bool showItems = true, string label = "Store Shelf")
        {
            var pr = Prof(meshName);
            var s = new StoreShelf { MeshName = meshName, TableIndex = table, MinItems = pr.Min, MaxItems = pr.Max, ShowItems = showItems, LabelText = label };
            parent.AddChild(s);
            s.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)), pos);
            return s;
        }

        ArrayMesh ShelfMesh()
        {
            if (_meshes.TryGetValue(MeshName, out var m)) return m;
            m = ObjMesh.Load(ProjectSettings.GlobalizePath($"res://content/objects/{MeshName}.obj"));
            _meshes[MeshName] = m; return m;
        }
        Material ShelfMat()
        {
            if (_mats.TryGetValue(MeshName, out var cached)) return cached;
            var mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            string tp = ProjectSettings.GlobalizePath($"res://content/objects/{MeshName}_tex.png");
            if (System.IO.File.Exists(tp) && img.Load(tp) == Error.Ok)
            {
                mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                mm.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // tiny palette texel
            }
            else mm.AlbedoColor = new Color(0.62f, 0.62f, 0.64f);
            _mats[MeshName] = mm; return mm;
        }

        // the standing shelf's AABB in root space (mesh is authored lying down; +270 X stands it up, matching the editor).
        Basis _upright = new Basis(Vector3.Right, Mathf.DegToRad(270f));
        Aabb StoodAabb(ArrayMesh mesh)
        {
            var a = mesh.GetAabb();
            var pts = new Vector3[8];
            for (int i = 0; i < 8; i++)
                pts[i] = _upright * (a.Position + a.Size * new Vector3((i & 1), (i >> 1) & 1, (i >> 2) & 1));
            Vector3 mn = pts[0], mx = pts[0];
            foreach (var p in pts) { mn = mn.Min(p); mx = mx.Max(p); }
            return new Aabb(mn, mx - mn);
        }

        protected override void BuildVisual()
        {
            var mesh = ShelfMesh();
            float top = 2.8f;
            if (mesh != null)
            {
                AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = ShelfMat(), Basis = _upright });
                var box = StoodAabb(mesh); top = box.Position.Y + box.Size.Y + 0.3f;   // float the label just above the standing prop (fridge/counter are shorter)
            }
            AddChild(new Label3D
            {
                Text = LabelText,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.8f, 0.85f, 0.95f),
                PixelSize = 0.007f, Position = new Vector3(0, top, 0),
                NoDepthTest = true, FontSize = 56, OutlineSize = 10,
            });
        }

        public override void _Ready()
        {
            base._Ready();   // Storage grid + BuildVisual (shelf mesh + label) + "crates" group
            var ids = new List<int>();
            var rng = new RandomNumberGenerator();
            int n = rng.RandiRange(MinItems, MaxItems);
            for (int i = 0; i < n; i++)
            {
                int id = LootTables.Roll(TableIndex);
                if (id < 0) continue;
                var item = Assets.makeLoot((ushort)id);
                if (item != null) { Add(item); ids.Add(id); }
            }
            if (ShowItems) DisplayItems(ids);   // open shelves show loot on tiers; solid props hold it hidden (F to see)
            GD.Print($"[store-shelf] {MeshName} table {TableIndex} ({LootTables.TableName(TableIndex)}) -> {ids.Count} items{(ShowItems ? " on tiers" : " (F-open)")}");
        }

        // place each stored item's real model on a tier slot (static, no physics -- the "neatly placed" display).
        void DisplayItems(List<int> ids)
        {
            var mesh = ShelfMesh();
            if (mesh == null || ids.Count == 0) return;
            var pr = Prof(MeshName);
            var box = StoodAabb(mesh);
            float x0 = box.Position.X + box.Size.X * (1f - pr.WidthUse) * 0.5f;
            float xspan = box.Size.X * pr.WidthUse;
            float zFront = box.Position.Z + box.Size.Z * (0.5f + 0.5f * pr.FrontZ);   // toward the front face
            int slots = pr.TierY.Length * pr.PerTier;
            for (int i = 0; i < ids.Count && i < slots; i++)
            {
                int tier = i / pr.PerTier, col = i % pr.PerTier;
                float fx = pr.PerTier > 1 ? col / (float)(pr.PerTier - 1) : 0.5f;
                var pos = new Vector3(x0 + xspan * fx, box.Position.Y + box.Size.Y * pr.TierY[tier], zFront);

                var asset = Assets.find((ushort)ids[i]) as ItemAsset;
                Color rar = asset != null ? ItemTool.RarityColorUI(asset.rarity) : Colors.White;
                var vis = WorldItem.BuildReplicaVisual((ushort)ids[i], rar);
                vis.Position = pos;
                vis.RotationDegrees = new Vector3(90f, col * 37f, 0f);   // lay the model upright-ish on the shelf (drop pose); yaw varies so it's not a clone row
                AddChild(vis);
            }
        }
    }
}
