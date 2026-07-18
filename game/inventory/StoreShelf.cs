using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Master's loot-rework step 2: a store shelf (the real Unturned Shelf_1 gondola) that's ALSO a container. Walk up +
    // F to open its grid like a crate, but its contents are ALSO shown as the items' real 3D models sitting on the
    // shelf tiers -- no physics, neatly placed (the display-storage mechanic from InteractableStorage.isDisplay, but
    // for the whole grid). Contents are rolled from a PEI item drop table on spawn (like LootCrate). Placed in the
    // editor, tested in SP. LootTables.Load must have run first.
    public partial class StoreShelf : StorageCrate
    {
        public int TableIndex = 0;
        public int MinItems = 8, MaxItems = 16;   // a shelf holds more than a crate

        // --- tier layout (fractions of the standing shelf's AABB; tuned to land items on the real Shelf_1 surfaces) ---
        static readonly float[] TierY = { 0.20f, 0.50f, 0.80f };   // shelf-surface heights up the unit
        const int PerTier = 6;                                     // item slots across the 5 m width
        const float WidthUse = 0.82f;                              // fraction of the width used (leave end margins)
        const float FrontZ = 0.30f;                                // push items toward the front face (fraction of half-depth)

        static ArrayMesh _shelfMesh;
        static Material _shelfMat;
        const string MeshPath = "res://content/objects/Shelf_1.obj";
        const string TexPath = "res://content/objects/Shelf_1_tex.png";

        public StoreShelf() { Width = 8; Height = 6; }   // roomier grid than a crate

        public static StoreShelf Spawn(Node parent, Vector3 pos, int table, float yawDeg = 0f)
        {
            var s = new StoreShelf { TableIndex = table };
            parent.AddChild(s);
            s.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)), pos);
            return s;
        }

        static ArrayMesh ShelfMesh() => _shelfMesh ??= ObjMesh.Load(ProjectSettings.GlobalizePath(MeshPath));
        static Material ShelfMat()
        {
            if (_shelfMat != null) return _shelfMat;
            var mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            string tp = ProjectSettings.GlobalizePath(TexPath);
            if (System.IO.File.Exists(tp) && img.Load(tp) == Error.Ok)
            {
                mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                mm.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // tiny palette texel
            }
            else mm.AlbedoColor = new Color(0.62f, 0.62f, 0.64f);
            return _shelfMat = mm;
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
            if (mesh != null)
                AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = ShelfMat(), Basis = _upright });

            AddChild(new Label3D
            {
                Text = "Store Shelf",
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.8f, 0.85f, 0.95f),
                PixelSize = 0.007f, Position = new Vector3(0, 2.8f, 0),
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
            DisplayItems(ids);
            GD.Print($"[store-shelf] table {TableIndex} ({LootTables.TableName(TableIndex)}) -> {ids.Count} items on tiers");
        }

        // place each stored item's real model on a tier slot (static, no physics -- the "neatly placed" display).
        void DisplayItems(List<int> ids)
        {
            var mesh = ShelfMesh();
            if (mesh == null || ids.Count == 0) return;
            var box = StoodAabb(mesh);
            float x0 = box.Position.X + box.Size.X * (1f - WidthUse) * 0.5f;
            float xspan = box.Size.X * WidthUse;
            float zFront = box.Position.Z + box.Size.Z * (0.5f + 0.5f * FrontZ);   // toward the front face
            int slots = TierY.Length * PerTier;
            for (int i = 0; i < ids.Count && i < slots; i++)
            {
                int tier = i / PerTier, col = i % PerTier;
                float fx = PerTier > 1 ? col / (float)(PerTier - 1) : 0.5f;
                var pos = new Vector3(x0 + xspan * fx, box.Position.Y + box.Size.Y * TierY[tier], zFront);

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
