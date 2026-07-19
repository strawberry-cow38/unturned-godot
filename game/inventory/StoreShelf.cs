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

        // per-item icon POSE: FRONT (detail face -- medkit cross, MRE text, OJ label) + UP (top of the icon -- bottle
        // cap, tomato stem) axes in the Godot mesh frame, ripped from each prefab's "Icon" transform (the pose the game
        // photographs its inventory icon from -- tools/extract_item_fronts.py). Reproducing it stands items right-side-up
        // + aisle-facing and lays flat items detail-side up, straight from the game data (no hand-defining hundreds).
        struct Pose { public Vector3 Front, Up; public bool Ok; }
        static Dictionary<int, Pose> _poses;
        static Pose PoseOf(int id)
        {
            if (_poses == null)
            {
                _poses = new Dictionary<int, Pose>();
                string p = ProjectSettings.GlobalizePath("res://content/items/item_poses.json");
                try
                {
                    if (System.IO.File.Exists(p))
                        foreach (var kv in Json.ParseString(System.IO.File.ReadAllText(p)).AsGodotDictionary())
                        {
                            var o = kv.Value.AsGodotDictionary();
                            var f = o["f"].AsGodotArray(); var u = o["u"].AsGodotArray();
                            if (f.Count == 3 && u.Count == 3 && int.TryParse(kv.Key.ToString(), out int fid))
                                _poses[fid] = new Pose {
                                    Front = new Vector3(f[0].AsSingle(), f[1].AsSingle(), f[2].AsSingle()),
                                    Up = new Vector3(u[0].AsSingle(), u[1].AsSingle(), u[2].AsSingle()), Ok = true };
                        }
                }
                catch (System.Exception e) { GD.PrintErr($"[store-shelf] item_poses load failed: {e.Message}"); }
            }
            return _poses.TryGetValue(id, out var v) ? v : default;
        }

        static int NearestAxis(Vector3 v, out float sign)   // dominant local axis of a direction + its sign
        {
            float ax = Mathf.Abs(v.X), ay = Mathf.Abs(v.Y), az = Mathf.Abs(v.Z);
            int a = (ax >= ay && ax >= az) ? 0 : (ay >= az ? 1 : 2);
            sign = v[a] >= 0f ? 1f : -1f;
            return a;
        }

        // items master pinned to LIE flat even though their icon stands them -- produce that reads better lying in a bin.
        static readonly HashSet<int> _forceLie = new() { 329, 344, 335, 342 };   // carrot, wheat, corn, potato

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

            // ORIENT from the game's own inventory-icon POSE (item_poses.json, ripped from each prefab's "Icon" child).
            // The icon shows each item the "right" way -- upright, label out -- so reproducing it (local UP -> world +Y,
            // local FRONT -> the aisle) stands cans/bottles/veggies/cartons right-side-up + aisle-facing with no per-item
            // rules (tomato stem up, OJ label out, maple syrup standing). EXCEPTION: a flat SLAB (candy/chips/MRE/bandage/
            // gun) is shown face-on, so reproducing its icon would stand it on edge -- instead lay it flat, detail face UP.
            var s = (vis.Mesh?.GetAabb() ?? new Aabb(Vector3.Zero, Vector3.One * 0.1f)).Size;
            float[] d = { s.X, s.Y, s.Z };
            int[] ax = { 0, 1, 2 };
            System.Array.Sort(ax, (a, b) => d[a].CompareTo(d[b]));   // ax[0]=shortest .. ax[2]=longest
            Pose pose = PoseOf(id);
            bool flatSlab = d[ax[0]] < d[ax[1]] * 0.45f;   // one clearly-thin dim => a flat package/slab -> lie flat (don't stand it on edge)
            bool lieFlat = flatSlab || _forceLie.Contains(id);   // a slab, or an item master pinned to lie (produce)

            Basis oriented;
            if (pose.Ok && !lieFlat)
            {
                // STAND UPRIGHT as the icon poses it, but SNAP up/front to the nearest local axes so items stand clean-
                // vertical (not tilted like a 3/4 icon view): local UP-axis -> world +Y, local FRONT-axis -> +Z (aisle).
                int upA = NearestAxis(pose.Up, out float upSign);
                var absU = pose.Up.Abs();                                // diagonal/ambiguous icon-up (e.g. carrot @45deg) ->
                float top = Mathf.Max(absU.X, Mathf.Max(absU.Y, absU.Z));// stand on the LONGEST axis so it doesn't tip over
                float sum = absU.X + absU.Y + absU.Z;
                if (sum - top - Mathf.Min(absU.X, Mathf.Min(absU.Y, absU.Z)) > top * 0.7f)
                    { upA = ax[2]; upSign = pose.Up[ax[2]] >= 0f ? 1f : -1f; }
                int frA = NearestAxis(pose.Front, out float frSign);
                if (frA == upA) { frA = (upA + 1) % 3; frSign = 1f; }   // front must be a different axis than up
                int thA = 3 - upA - frA;                                 // the leftover axis
                var c = new Vector3[3];
                c[upA] = new Vector3(0, upSign, 0);    // up-axis    -> +Y
                c[frA] = new Vector3(0, 0, frSign);    // front-axis -> +Z (aisle)
                c[thA] = new Vector3(1, 0, 0);
                oriented = Basis.Identity;
                oriented.X = c[0]; oriented.Y = c[1]; oriented.Z = c[2];
                if (oriented.Determinant() < 0f)       // flip the leftover axis for a proper rotation (keeps up + front)
                {
                    var neg = -c[thA];
                    if (thA == 0) oriented.X = neg; else if (thA == 1) oriented.Y = neg; else oriented.Z = neg;
                }
            }
            else
            {
                // LIE FLAT, DETAIL-SIDE UP: use the icon FRONT (or shortest axis if no data) as the up direction, snapped
                // to the nearest local axis so it rests flat; longer remaining axis -> shelf WIDTH (keeps long items off
                // the back wall), the other -> depth.
                Vector3 front = pose.Ok ? pose.Front : Vector3.Zero;
                int upA; float upSign;
                if (front.LengthSquared() > 0.01f)
                {
                    float fx0 = Mathf.Abs(front.X), fy0 = Mathf.Abs(front.Y), fz0 = Mathf.Abs(front.Z);
                    upA = (fx0 >= fy0 && fx0 >= fz0) ? 0 : (fy0 >= fz0 ? 1 : 2);
                    upSign = front[upA] >= 0f ? 1f : -1f;
                }
                else { upA = ax[0]; upSign = 1f; }
                int o1 = (upA + 1) % 3, o2 = (upA + 2) % 3;
                int wide = d[o1] >= d[o2] ? o1 : o2, deep = d[o1] >= d[o2] ? o2 : o1;
                var c = new Vector3[3];
                c[upA]  = new Vector3(0, upSign, 0);   // detail axis -> up (+Y); the FRONT face ends up on top
                c[wide] = new Vector3(1, 0, 0);        // longer flat axis -> shelf width
                c[deep] = new Vector3(0, 0, 1);        // remaining -> depth
                oriented = Basis.Identity;
                oriented.X = c[0]; oriented.Y = c[1]; oriented.Z = c[2];
                if (oriented.Determinant() < 0f)       // fix handedness on the DEPTH axis (never the up/width) so detail stays up
                {
                    if (deep == 0) oriented.X = -oriented.X;
                    else if (deep == 1) oriented.Y = -oriented.Y;
                    else oriented.Z = -oriented.Z;
                }
            }

            if (System.Environment.GetEnvironmentVariable("UG_SHELFDBG") == "1")
                GD.Print($"[shelf-item] id={id} dims=({s.X:0.00},{s.Y:0.00},{s.Z:0.00}) flat={flatSlab} poseOk={pose.Ok} up={pose.Up} front={pose.Front} -> {(pose.Ok && !lieFlat ? "STAND" : "LIE")}");

            // SCALE oversized items down to fit the slot (master's "cheat"): cap the footprint to the slot width + the
            // height to the tier gap.
            var ob = new Transform3D(oriented, Vector3.Zero) * (vis.Mesh?.GetAabb() ?? new Aabb());
            float slotW = (xspan / Mathf.Max(1, pr.PerTier)) * 0.9f;
            float tierGap = box.Size.Y * 0.24f;
            float sc = 1f, foot = Mathf.Max(ob.Size.X, ob.Size.Z);
            if (foot > slotW && foot > 0.0001f) sc = Mathf.Min(sc, slotW / foot);
            if (ob.Size.Y > tierGap && ob.Size.Y > 0.0001f) sc = Mathf.Min(sc, tierGap / ob.Size.Y);
            vis.Basis = oriented.Scaled(new Vector3(sc, sc, sc));

            // POSITION by the final bounds: base flush on the tier + a tiny lift (master: sit a touch higher); centered
            // on the slot (X). Front of the shelf = HIGH Z (the aisle -- the visible side), back wall = LOW Z: bias items
            // toward the front and keep their back (low-Z) edge well off the wall (master: "move away from the back wall").
            var rb = new Transform3D(vis.Basis, Vector3.Zero) * (vis.Mesh?.GetAabb() ?? new Aabb());
            float tierSurfaceY = box.Position.Y + box.Size.Y * pr.TierY[tier];
            float zPos = (box.Position.Z + box.Size.Z * 0.76f) - (rb.Position.Z + rb.Size.Z * 0.5f);   // front-biased center (front = high Z)
            float minBack = box.Position.Z + box.Size.Z * 0.34f;                                        // back edge (low Z) stays this far off the wall
            if (zPos + rb.Position.Z < minBack) zPos = minBack - rb.Position.Z;
            float frontLip = box.Position.Z + box.Size.Z * 0.96f;                                        // don't overhang the front lip (high Z)
            if (zPos + rb.Position.Z + rb.Size.Z > frontLip) zPos = frontLip - (rb.Position.Z + rb.Size.Z);
            vis.Position = new Vector3(
                (x0 + xspan * fx) - (rb.Position.X + rb.Size.X * 0.5f),
                tierSurfaceY - rb.Position.Y + rb.Size.Y * 0.03f + 0.05f,   // lift so items sit ON the tier, not sunk in (master: "everything needs raising")
                zPos);
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
