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
        readonly Dictionary<int, Node3D> _display = new();   // grid cell (gx<<8|gy) -> its item model; a STABLE slot per cell so taking items doesn't re-organize the shelf
        float _syncT;

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
            var rng = new RandomNumberGenerator();
            int n = rng.RandiRange(MinItems, MaxItems);
            for (int i = 0; i < n; i++)
            {
                int id = LootTables.Roll(TableIndex);
                if (id < 0) continue;
                var item = Assets.makeLoot((ushort)id);
                if (item != null) Add(item);
            }
            if (ShowItems) SyncDisplay();   // open shelves show loot on tiers; solid props hold it hidden (F to see)
            GD.Print($"[store-shelf] {MeshName} table {TableIndex} ({LootTables.TableName(TableIndex)}) -> {Storage.getItemCount()} items{(ShowItems ? " on tiers" : " (F-open)")}");
        }

        // STABLE display: each grid cell maps to a FIXED shelf slot, so taking items never re-organizes the rest (master).
        // Diffs Storage vs what's shown -- despawns taken items, spawns added ones, leaves the rest put. Called on spawn +
        // polled ~2x/s (the crate close-out writes the edited grid back).
        void SyncDisplay()
        {
            var current = new Dictionary<int, (ushort id, ItemAsset a)>();
            for (byte i = 0; i < Storage.getItemCount(); i++)
            {
                var j = Storage.getItem(i);
                if (j?.item == null) continue;
                current[(j.x << 8) | j.y] = (j.item.id, Assets.find(j.item.id) as ItemAsset);
            }
            foreach (var key in new List<int>(_display.Keys))   // taken -> despawn its model, leave the rest in place
                if (!current.ContainsKey(key)) { if (IsInstanceValid(_display[key])) _display[key].QueueFree(); _display.Remove(key); }
            foreach (var kv in current)                         // added -> place at its fixed slot
                if (!_display.ContainsKey(kv.Key)) PlaceItem(kv.Key, kv.Value.id, kv.Value.a);
        }

        // place one item's real model at the STABLE slot derived from its grid cell -- oriented + scaled to sit neatly.
        void PlaceItem(int cellKey, ushort id, ItemAsset asset)
        {
            var mesh = ShelfMesh();
            if (mesh == null) return;
            var pr = Prof(MeshName);
            var box = StoodAabb(mesh);
            int slots = pr.TierY.Length * pr.PerTier;
            int gx = cellKey >> 8, gy = cellKey & 0xFF;
            int slot = (gx + gy * Width) % slots;                 // stable grid cell -> slot
            int tier = slot / pr.PerTier, col = slot % pr.PerTier;
            float fx = pr.PerTier > 1 ? col / (float)(pr.PerTier - 1) : 0.5f;
            float x0 = box.Position.X + box.Size.X * (1f - pr.WidthUse) * 0.5f;
            float xspan = box.Size.X * pr.WidthUse;

            Color rar = asset != null ? ItemTool.RarityColorUI(asset.rarity) : Colors.White;
            var vis = WorldItem.BuildReplicaVisual(id, rar);

            // ORIENT by shape + type: tools/guns/melee/fishing (long, awkward) + flat/thin slabs (candy bars) LIE FLAT
            // (thinnest axis up); tall/round items (cans, bottles, chemicals) STAND (longest axis up). Robust to authoring.
            var s = (vis.Mesh?.GetAabb() ?? new Aabb(Vector3.Zero, Vector3.One * 0.1f)).Size;
            float[] d = { s.X, s.Y, s.Z };
            int mn = 0, mx = 0;
            for (int k = 1; k < 3; k++) { if (d[k] < d[mn]) mn = k; if (d[k] > d[mx]) mx = k; }
            float mid = s.X + s.Y + s.Z - d[mn] - d[mx];
            // only consumables/drinks/chemicals STAND (when tall); everything else (guns, melee, tools like the flashlight,
            // clothing, misc) LIES FLAT -- a can/bottle/chemical and a flashlight are the SAME shape, so type decides.
            bool standable = asset != null && (asset.type == EItemType.FOOD || asset.type == EItemType.WATER || asset.type == EItemType.MEDICAL || asset.type == EItemType.SUPPLY);
            bool layFlat = !standable || d[mn] < mid * 0.55f;
            int up = layFlat ? mn : mx;
            Basis stand = up == 0 ? new Basis(new Vector3(0, 0, 1), Mathf.DegToRad(90f))
                        : up == 2 ? new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(-90f))
                        : Basis.Identity;
            Basis oriented = new Basis(Vector3.Up, Mathf.DegToRad(col * 11f)) * stand;

            // SCALE oversized items down to fit the slot (master's "cheat"): cap the footprint to the slot width + the
            // height to the tier gap.
            var ob = new Transform3D(oriented, Vector3.Zero) * (vis.Mesh?.GetAabb() ?? new Aabb());
            float slotW = (xspan / Mathf.Max(1, pr.PerTier)) * 0.92f;
            float tierGap = box.Size.Y * 0.26f;
            float sc = 1f, foot = Mathf.Max(ob.Size.X, ob.Size.Z);
            if (foot > slotW && foot > 0.0001f) sc = Mathf.Min(sc, slotW / foot);
            if (ob.Size.Y > tierGap && ob.Size.Y > 0.0001f) sc = Mathf.Min(sc, tierGap / ob.Size.Y);
            vis.Basis = oriented.Scaled(new Vector3(sc, sc, sc));

            // POSITION by the final (rotated+scaled) bounds: base flush on the tier (Y), footprint centered in the shelf
            // DEPTH (front-biased) so nothing pokes the back wall (Z), centered on the slot (X).
            var rb = new Transform3D(vis.Basis, Vector3.Zero) * (vis.Mesh?.GetAabb() ?? new Aabb());
            float tierSurfaceY = box.Position.Y + box.Size.Y * pr.TierY[tier];
            vis.Position = new Vector3(
                (x0 + xspan * fx) - (rb.Position.X + rb.Size.X * 0.5f),
                tierSurfaceY - rb.Position.Y,
                (box.Position.Z + box.Size.Z * (0.5f + 0.5f * pr.FrontZ)) - (rb.Position.Z + rb.Size.Z * 0.5f));
            AddChild(vis);
            _display[cellKey] = vis;
        }

        // test hook: drop fixed ids into sequential grid cells + display them (no asset DB / no roll) -- UG_SHELFDEMO harness.
        public void DebugDisplay(List<int> ids)
        {
            for (int i = 0; i < ids.Count; i++)
                PlaceItem(((i % Width) << 8) | (i / Width), (ushort)ids[i], Assets.find((ushort)ids[i]) as ItemAsset);
        }

        public override void _Process(double delta)   // poll the grid ~2x/s; SyncDisplay only touches CHANGED cells (stable)
        {
            if (!ShowItems) return;
            _syncT += (float)delta;
            if (_syncT < 0.4f) return;
            _syncT = 0f;
            SyncDisplay();
        }
    }
}
