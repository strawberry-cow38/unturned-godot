using Godot;
using SDG.Unturned;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot
{
    // A dropped item in the world -- the item's REAL 3D model as a physics prop (master 2026-07-12). Extracted from
    // core.masterbundle (tools/extract_items.py): every PEI loot-table item -> a combined .txt (Wavefront OBJ) mesh +
    // primary albedo/flat-colour + best-fit AABB box, in content/items/, indexed by items_manifest.json. Spawns as a
    // RigidBody3D: gravity + src drag, a best-fit BOX collider colliding with world+props; FREEZES to static once it
    // settles (the vehicle-style jitter kill -- metal scrap was buzzing on flat ground). Interaction is LOOK-AT (master):
    // an interaction SPHERE (Area3D, bit 8) the player's eye-ray hits -> rarity-colour glow outline + name billboard ->
    // E to pick up (PlayerController drives the focus).
    public partial class WorldItem : RigidBody3D
    {
        public Item Item;
        public Color? FallbackColor;   // unknown-id loot (no registered asset / no model): tint by its spawn TABLE
        public string FallbackName;    // ...and label by the table name (e.g. "Military Canada", "Food")
        public static bool ShowLabels; // P force-shows ALL item name tags (else a tag shows only while looked-at)
        public static bool ShowLookSphere; // O toggles the player's look-END sphere visualizer (master's LookAtRadius)
        public static bool NoDropRotation; // --itemtest UG_NOROT diagnostic: spawn at identity to read the raw model orientation
        // P2b (SP/MP-unify, --spconsume): when true, the host does NOT render or focus its OWN world-item NODES
        // (LootField-streamed loot, salvage scrap) -- the WorldItemReplicaView PUPPET (built from the same server
        // entity, over the wire) is the sole visible + focusable copy on the host. Kills the passive-loot
        // double-materialization under --spconsume (the real SP node AND the view's puppet showing the same item
        // twice). The node stays a live physics body IN the "worlditems" group, so WorldItemNetSync still settles +
        // publishes it for remote joiners -- only the LOCAL visual/interaction is suppressed. Default false, so the
        // direct SP path and the live MP-client path (ClientWorldSession) are byte-identical.
        public static bool SuppressLocalVisual;
        public static Color FocusColor = Colors.White;   // the currently-focused item's rarity colour -- OutlineOverlay tints the rim with it

        public const uint ItemHitLayer = 1u << 7;     // the item's box collider layer -- the player's look-sphere tests against this
        const float LabelH = 0.4f;                    // name tag floats this far above the item origin (world space)

        float _losTimer;    // throttle the LOS visibility check (staggered) -- NOT a raycast-per-item every frame
        bool _shown = true;
        MeshInstance3D _mesh, _glow;
        Label3D _label;
        Color _rar;
        bool _focused;
        bool _suppressed;   // P2b: latched from SuppressLocalVisual at _Ready -> this node is a hidden, non-focusable physics-only body (the WorldItemReplicaView puppet is the visible copy on the host)

        Vector3 _velAvg, _angAvg;   // low-pass velocity/spin for settle detection (jitter cancels in the running average)
        float _settleT, _age;
        bool _settled;
        public bool Settled => _settled;   // L1 tests: has the dropped item come to rest?
        public bool LocalVisualSuppressed => _suppressed;   // P2b: true -> hidden + off the look-hit layer under --spconsume (the WorldItemReplicaView puppet is the visible/focusable copy)

        /// <summary>Is there a clear line from `eye` to this item? Used by the Nearby/AREA inventory page.
        /// Source: PlayerDashboardInventoryUI casts Physics.Linecast(eyesPosition, renderer.bounds.center,
        /// RayMasks.BLOCK_PICKUP) per pending drop and skips anything the ray hits.
        ///
        /// Deliberately does NOT reuse the render cull's answer (`Visible`), even though that already does an
        /// LOS pass: the render cull ALSO applies a ~60deg view cone, and the source's AREA rule has no cone at
        /// all — it's distance + line-of-sight only. Reusing it would silently drop every item behind the
        /// player from the Nearby page, which is a different feature wearing the same raycast.
        ///
        /// Shares the render cull's hitbox-sample technique: ANY clear sample point counts, so an item peeking
        /// past a corner still lists, and a fully-walled one costs the full 9 rays. Mask bit0 = large opaque
        /// world geometry (WorldBuilder puts small props + glass on bit6 precisely so they don't block this).</summary>
        public bool HasLineOfSightFrom(Vector3 eye)
        {
            if (!IsInsideTree() || _suppressed) return false;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return true;             // no physics world (headless/test) -> don't hide loot behind a missing raycast
            Transform3D gt = GlobalTransform;
            var pts = _hitPts ?? new[] { _boxCtr };     // model not built yet -> fall back to the body origin
            foreach (var lp in pts)
            {
                var q = PhysicsRayQueryParameters3D.Create(eye, gt * lp);
                q.CollisionMask = 1;                    // bit0 only, same as the render cull
                if (_excludeSelf != null) q.Exclude = _excludeSelf;
                if (space.IntersectRay(q).Count == 0) return true;
            }
            return false;
        }
        Vector3[] _hitPts;          // hitbox sample points (centre + 8 corners, local) for the full-hitbox LOS cull (master)
        PhysicsRayQueryParameters3D _losQuery;   // reused across the nine LOS rays; see the cull loop
        static readonly int[] Los3 = { 0, 1, 8 };   // centre + two diagonally opposite corners
        static readonly bool LosCompare = System.Environment.GetEnvironmentVariable("UG_LOSCOMPARE") == "1";
        /// <summary>UG_LOSPTS=3 caps the occlusion scan at three spread points instead of all nine.
        /// DEFAULT 9 -- the existing behaviour -- because the failure mode of cutting it is an item peeking
        /// round a corner going invisible, and that is not something a geometry argument can rule out. Flip
        /// the default only once an A/B render shows no pixels move on items.</summary>
        static readonly int LosScanPoints =
            int.TryParse(System.Environment.GetEnvironmentVariable("UG_LOSPTS"), out int v) && v == 3 ? 3 : 9;
        // TWO radii, because one number was doing two jobs. The old single 200 m test decided BOTH "is this item
        // drawn" AND "is it worth a ray", so the occlusion rays ran the full 200 m -- up to nine of them, 4x a
        // second, per item, at something under a pixel across. ETW put the scan at 3,118 ms and 27% of every
        // allocation in the game. Drawing still reaches 200 m so nothing pops out of the world; only the OCCLUSION
        // test is close-range, where a wall between you and an item is something you can actually see. Beyond it an
        // in-cone item is simply shown -- a distant item can now draw through a wall, at a range where it is a
        // speck. That is the trade, stated rather than hidden.
        const float DrawDist2 = 40000f;   // 200 m, unchanged: what you can SEE
        static readonly float RayDist2 =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_ITEMRAY"), out float _ir) && _ir > 0f ? _ir * _ir : 60f * 60f;
        Vector3 _boxCtr;
        Godot.Collections.Array<Rid> _excludeSelf;   // cached ray-exclude (this body) so the LOS rays don't re-alloc

        // ---- shared item-model cache: parse each id's mesh/tex/box ONCE, reuse across its many spawns/despawns ----
        class Model
        {
            public ArrayMesh Mesh; public Material Mat; public Color? FlatColor; public Color? Palette;
            public Vector3 Box; public Vector3 Center; public bool Ok;
            /// <summary>Cumulative triangle counts, one per sub-object, from the manifest's `rounds`. Null for
            /// the 1919 items that are a single object. Set only on the ammo bundles, whose triangles are
            /// contiguous per round and ordered with the top round LAST.</summary>
            public int[] Rounds;
            public string ObjPath;   // kept so a prefix mesh can be parsed on demand
        }
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
                string objPath = $"{ItemsRoot}/{e["obj"].AsString()}";
                var mesh = ContentProvider.ParseObj(objPath);
                if (mesh != null && mesh.GetSurfaceCount() > 0)
                {
                    m.Mesh = mesh;
                    m.ObjPath = objPath;
                    if (e.ContainsKey("rounds"))
                    {
                        var r = e["rounds"].AsGodotArray();
                        if (r.Count > 0)
                        {
                            m.Rounds = new int[r.Count];
                            for (int i = 0; i < r.Count; i++) m.Rounds[i] = r[i].AsInt32();
                        }
                    }
                    var box = e["box"].AsGodotArray(); var ctr = e["center"].AsGodotArray();
                    m.Box = new Vector3(box[0].AsSingle(), box[1].AsSingle(), box[2].AsSingle());
                    m.Center = new Vector3(ctr[0].AsSingle(), ctr[1].AsSingle(), ctr[2].AsSingle());
                    var texv = e["tex"];
                    if (texv.VariantType != Variant.Type.Nil)
                    {
                        var tp = ProjectSettings.GlobalizePath($"{ItemsRoot}/{texv.AsString()}");
                        if (System.IO.File.Exists(tp))
                        {
                            var img = ContentProvider.LoadImage(tp);
                            if (img != null)
                            {
                                // The item's BODY COLOUR, before mipmaps flatten it. These albedos are not UV
                                // textures -- extract_items.py writes a tiny palette strip (the throwables are 2x1:
                                // pixel 0 the painted body, pixel 1 the shared grey cap), so pixel 0 IS the item's
                                // colour. That is what makes "coloured depending on the colour of the one you threw"
                                // (strawberry 2026-09-05) a lookup rather than a hand-typed table of eight reds:
                                // White Smoke reads (212,212,212) and Black Smoke (53,53,53) straight off the asset.
                                if (img.GetWidth() > 0 && img.GetHeight() > 0) m.Palette = img.GetPixel(0, 0);
                                img.GenerateMipmaps();
                                m.Mat = new StandardMaterial3D
                                {
                                    AlbedoTexture = ImageTexture.CreateFromImage(img),
                                    TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,   // blocky Unturned pixels, like the rest of the port
                                    Roughness = 0.8f,
                                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // double-sided like all the port's ripped meshes (their winding is authored for it)
                                };
                            }
                        }
                    }
                    if (m.Mat == null && e.ContainsKey("color"))   // no albedo texture -> the material's flat _Color is its real look
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

        /// <summary>How many of a bundle's sub-objects a stack of `amount` shows (strawberry 2026-09-08:
        /// "stacks will show visually, 1/4-2/4 1 round 2/4-3/4 2 round 3/4-4/4 3 rounds. the stack shape stays
        /// the same just hide rounds in the stack").
        ///
        /// The bands are quarters of the item's OWN stackSize, so 5.56 (128) and buckshot (32) both read as
        /// thirds-of-a-pile without a per-item table. Below a quarter shows ONE round rather than none: a
        /// dropped item that renders nothing is indistinguishable from a bug, and the single most common case
        /// -- the one round the gun ejects on rack -- lands there.</summary>
        public static int VisibleRounds(int amount, int stackSize, int rounds)
        {
            if (rounds <= 1 || stackSize <= 0) return rounds;
            // ONE MORE BAND THAN ROUNDS, which is the rule his 3-round example already describes: quarters
            // for three rounds ("1/4-2/4 1 round 2/4-3/4 2 round 3/4-4/4 3 rounds"). Generalised that way a
            // 5-round pile reads in SIXTHS, and the 3-round case still lands exactly where he specified --
            // a plain "one round per 1/rounds of the stack" would have quietly re-banded the 3-round pile
            // into thirds and contradicted the spec it was derived from.
            // FLOOR, not ceil-1: his bands are inclusive at the LOW edge ("2/4-3/4 2 round" means exactly
            // half a stack already shows two). ceil(frac*(rounds+1))-1 is the same rule shifted one texel
            // over and gets every boundary exactly wrong -- 16/32 came out as 1 round instead of 2.
            float frac = (float)amount / stackSize;
            int n = Mathf.FloorToInt(frac * (rounds + 1));
            return Mathf.Clamp(n, 1, rounds);
        }

        /// <summary>The mesh a dropped stack of this id and amount should draw. The full mesh for every
        /// ordinary item; a triangle PREFIX for a bundle, which drops the top round(s) and leaves the rest
        /// resting on the ground (the installers assert that ordering, since a wrong one floats a round).</summary>
        static ArrayMesh MeshForAmount(Model m, int itemId, int amount)
        {
            if (m?.Rounds == null || m.Rounds.Length <= 1 || amount <= 0) return m?.Mesh;
            int stack = Assets.find((ushort)itemId)?.stackSize ?? 0;
            if (stack <= 0) return m.Mesh;
            int n = VisibleRounds(amount, stack, m.Rounds.Length);
            if (n >= m.Rounds.Length) return m.Mesh;
            return ContentProvider.ParseObjPrefix(m.ObjPath, m.Rounds[n - 1]) ?? m.Mesh;
        }

        /// <summary>Every id whose manifest entry carries `rounds` -- i.e. every multi-round bundle installed.
        ///
        /// Exists so the stack-visual test can enumerate what is ACTUALLY there. Its id list was hand-written
        /// twice; the first version silently passed while nine calibers had no bundle at all, and the second
        /// would have gone stale the moment ten more were added.</summary>
        public static List<int> BundleIds()
        {
            var ids = new List<int>();
            foreach (var key in Manifest().Keys)
            {
                string k = key.AsString();
                if (!int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
                var e = Manifest()[key].AsGodotDictionary();
                if (!e.ContainsKey("rounds")) continue;
                if (e["rounds"].AsGodotArray().Count > 1) ids.Add(id);
            }
            ids.Sort();
            return ids;
        }

        /// <summary>The manifest's `rounds` for an id -- cumulative triangle counts, one per round -- or null
        /// for the great majority of items that are a single object. Public so a test can enumerate what is
        /// actually installed instead of carrying its own copy of the list, which is the copy that goes stale
        /// the moment a caliber is added.</summary>
        public static int[] RoundsFor(int itemId)
        {
            var m = itemId > 0 ? GetModel(itemId) : null;
            return m != null && m.Ok ? m.Rounds : null;
        }

        /// <summary>The mesh a dropped stack of `itemId` x `amount` draws, resolved through the real manifest
        /// and the real obj on disk. Public so the stack-visual test can assert on the MESH -- its triangle
        /// count and its height -- rather than on a re-statement of the band arithmetic.</summary>
        public static ArrayMesh MeshForStack(int itemId, int amount)
        {
            var m = itemId > 0 ? GetModel(itemId) : null;
            return m != null && m.Ok ? MeshForAmount(m, itemId, amount) : null;
        }

        /// <summary>The item's own body colour, read off its extracted palette strip (pixel 0) -- see the note
        /// in GetModel. Null when the id has no model or no texture. Cached with the model, so a smoke grenade
        /// asking for its colour on every throw costs one dictionary lookup.</summary>
        public static Color? PaletteColor(int itemId)
        {
            var m = itemId > 0 ? GetModel(itemId) : null;
            return m != null && m.Ok ? (m.Palette ?? m.FlatColor) : null;
        }

        /// <summary>C5 (PEI_CLIENT_PLAN §3): VISUAL-ONLY reuse of the shared item-model cache for the
        /// joined client's WorldItemReplicaView -- the same mesh/texture/flat-colour the physical prop
        /// shows, with the rarity marker box fallback for ids without a model. No RigidBody3D, no
        /// collider, no pickup -- the replica view owns transform + lifecycle.</summary>
        /// <param name="amount">Stack size of the drop, so a bundle shows the right number of rounds.
        /// 0 (the default) means "not a stack" and draws the full mesh -- the Grenade/StoreShelf callers,
        /// which show a single object rather than a pile.</param>
        public static MeshInstance3D BuildReplicaVisual(ushort itemId, Color rarity, byte amount = 0)
        {
            var model = itemId > 0 ? GetModel(itemId) : null;
            if (model != null && model.Ok)
                return new MeshInstance3D
                {
                    Mesh = MeshForAmount(model, itemId, amount),
                    MaterialOverride = model.Mat ?? new StandardMaterial3D { AlbedoColor = model.FlatColor ?? rarity, Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                };
            return new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.24f, 0.24f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = rarity, Roughness = 0.55f },
            };
        }

        /// <summary>Client-side FOCUSABLE dropped-item replica (MP): the render-only replica visual + a hidden
        /// glow silhouette on the outline layer + a look-detection box collider on the item hit layer (bit 7).
        /// Mirrors the real WorldItem's look-at highlight so the joined client can see + aim at replicated drops --
        /// a bare replica node (WorldItemReplicaView's old shape) is invisible to the look-ray. Bit 7 + mask 0 ->
        /// it never blocks movement (player mask is bit0|bit6) or catches bullets (bit 7 isn't in the bullet mask).</summary>
        public static WorldItemPuppet BuildItemPuppet(ushort itemId, Color rarity, string name, byte amount = 0)
        {
            var p = new WorldItemPuppet { ItemId = itemId };
            var visual = BuildReplicaVisual(itemId, rarity, amount);
            p.AddChild(visual);

            var model = itemId > 0 ? GetModel(itemId) : null;
            Vector3 boxSize = (model != null && model.Ok) ? model.Box : new Vector3(0.24f, 0.24f, 0.24f);
            Vector3 boxCenter = (model != null && model.Ok) ? model.Center : Vector3.Zero;
            boxSize *= 1.15f;   // +15% like the real item's hitbox -> easier to look at + aim
            var body = new StaticBody3D { CollisionLayer = ItemHitLayer, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = boxSize }, Position = boxCenter });
            p.AddChild(body);

            var glow = OutlineOverlay.MakeOutline(visual.Mesh);   // only the offscreen mask camera renders this
            p.AddChild(glow);

            // name tag (hidden until focused) -- same style as the real WorldItem's _label. TopLevel so it floats in
            // WORLD space above the item, ignoring the puppet's 90deg drop rotation.
            var label = new Label3D
            {
                Text = string.IsNullOrEmpty(name) ? "?" : name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = rarity.Lerp(Colors.White, 0.35f),
                PixelSize = 0.006f, NoDepthTest = true, FontSize = 64, OutlineSize = 10,
                Visible = false, TopLevel = true,
            };
            p.AddChild(label);
            p.Configure(glow, label, rarity);
            return p;
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
            _excludeSelf = new Godot.Collections.Array<Rid> { GetRid() };
            var asset = Item?.GetAsset();
            string nm;
            if (asset != null) { _rar = ItemTool.RarityColorUI(asset.rarity); nm = asset.itemName; }
            else if (FallbackColor.HasValue) { _rar = FallbackColor.Value; nm = FallbackName ?? "?"; }
            else { _rar = Colors.White; nm = "?"; }

            // --- physics: gravity + src drag; freezes to static on settle (jitter kill, like vehicles) ---
            GravityScale = 1f;
            Mass = 1f;
            LinearDamp = 0.5f;                          // src Rigidbody.drag
            AngularDamp = 0.1f;                         // src Rigidbody.angularDrag
            CanSleep = true;
            ContinuousCd = true;                        // the terrain collider is a thin TRIMESH -> a small dropped item tunnels straight through it without continuous collision (strawberry: items fall through the ground). Verified: UG_TRIMESH itemtest -> items land WITH this, tunnel through WITHOUT.
            CenterOfMassMode = CenterOfMassModeEnum.Auto;   // COM = box centre (offset from the model origin) so it rests naturally
            CollisionLayer = 1u << 7;                   // worlditem layer (own bit -> player + LOS ray + other items ignore it)
            CollisionMask = (1u << 0) | (1u << 6);      // rest on world/terrain/buildings (bit0) + small/transparent props (bit6)

            int id = asset != null ? asset.id : (Item?.id ?? 0);
            var model = id > 0 ? GetModel(id) : null;

            _mesh = new MeshInstance3D();
            var col = new CollisionShape3D();
            Vector3 boxSize, boxCenter;
            if (model != null && model.Ok)
            {
                _mesh.Mesh = MeshForAmount(model, id, Item?.amount ?? 0);
                _mesh.MaterialOverride = model.Mat ?? new StandardMaterial3D { AlbedoColor = model.FlatColor ?? _rar, Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                // The COLLIDER stays the full pile's box even when the stack draws one round. Pickup reach and
                // the look-at hitbox are gameplay, and shrinking them would make a nearly-empty stack harder to
                // pick up than a full one -- a difficulty gradient nobody asked for, hidden inside a visual change.
                boxSize = model.Box; boxCenter = model.Center;
            }
            else
            {
                _mesh.Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.24f, 0.24f) };   // unknown id / no model -> rarity marker box
                _mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = _rar, Roughness = 0.55f };
                boxSize = new Vector3(0.24f, 0.24f, 0.24f); boxCenter = Vector3.Zero;
            }
            boxSize *= 1.15f;                           // +15% on every dropped item's phys hitbox (xyz) -> easier to look-at + pick up (master). scales the LOS samples below too.
            col.Shape = new BoxShape3D { Size = boxSize };
            col.Position = boxCenter;                   // mesh sits in model space; the best-fit box is offset to wrap it
            AddChild(_mesh);
            AddChild(col);
            _boxCtr = boxCenter;
            var hh = boxSize * 0.5f;                     // hitbox samples: centre + 8 corners (local) -> full-hitbox LOS cull
            _hitPts = new[] {
                boxCenter,
                boxCenter + new Vector3( hh.X,  hh.Y,  hh.Z), boxCenter + new Vector3(-hh.X,  hh.Y,  hh.Z),
                boxCenter + new Vector3( hh.X, -hh.Y,  hh.Z), boxCenter + new Vector3(-hh.X, -hh.Y,  hh.Z),
                boxCenter + new Vector3( hh.X,  hh.Y, -hh.Z), boxCenter + new Vector3(-hh.X,  hh.Y, -hh.Z),
                boxCenter + new Vector3( hh.X, -hh.Y, -hh.Z), boxCenter + new Vector3(-hh.X, -hh.Y, -hh.Z),
            };

            // look-at highlight: the item silhouette on the OUTLINE visual layer (main cams cull it; OutlineOverlay's mask
            // cam renders only it -> a fullscreen dilate draws the crisp rarity rim). White + unshaded = a clean solid mask.
            _glow = OutlineOverlay.MakeOutline(_mesh.Mesh);   // only the offscreen mask camera renders this
            AddChild(_glow);
            // interaction = the player's look-ray ENDS in a sphere; if that sphere touches this item's BOX hitbox (the
            // RigidBody collider on bit 7) it's pickupable (master's real LookAtRadius). No per-item sphere -- the box IS the hitbox.

            // src ItemManager.spawnItem drop pose: +90 X (Z-reflection of the src -90 X) lays the model flat right-side-up
            if (!NoDropRotation)
                Rotation = new Vector3(
                    Mathf.DegToRad(90f + (float)GD.RandRange(-15.0, 15.0)),
                    Mathf.DegToRad((float)GD.RandRange(0.0, 360.0)),
                    Mathf.DegToRad((float)GD.RandRange(-15.0, 15.0)));

            _label = new Label3D
            {
                Text = nm,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = _rar.Lerp(Colors.White, 0.35f),
                PixelSize = 0.006f,
                NoDepthTest = true,
                FontSize = 64,
                OutlineSize = 10,
                Visible = ShowLabels,   // name tag -- shown while looked-at (SetFocused) or force-on via P
                TopLevel = true,        // ignore the item's (rotated) transform -> float in WORLD space above the item
            };
            AddChild(_label);
            _label.AddToGroup("esp_labels");
            _label.GlobalPosition = GlobalPosition + Vector3.Up * LabelH;

            // P2b (SP/MP-unify, --spconsume): the host consumes world items as WorldItemReplicaView puppets, so its
            // OWN direct nodes (LootField loot, salvage scrap) must NOT also render + focus locally, or every passive
            // item appears TWICE (its real SP node AND the view's puppet). Hide the node and drop it OFF the look-hit
            // layer (bit 7) so the player's look-ray finds only the puppet. The node stays a live physics body still
            // IN the "worlditems" group (AddToGroup above), so WorldItemNetSync keeps settling + publishing it for
            // remote joiners -- only the LOCAL visual/interaction is suppressed. Removing bit 7 does not affect
            // resting: the item keeps its bit0|bit6 MASK, and it detects the world through that (Godot collides on
            // either direction of layer/mask). No-op when the flag is off (default SP + live MP client), byte-identical.
            _suppressed = SuppressLocalVisual;
            if (_suppressed)
            {
                Visible = false;
                CollisionLayer &= ~ItemHitLayer;
            }
            // GRASS DISPLACEMENT (master): a dropped item resting in grass presses a small dimple. Only the VISIBLE
            // copy enlists -- a --spconsume-suppressed node hands that off to its WorldItemReplicaView puppet, so the
            // same item isn't counted twice at one spot.
            if (!_suppressed) GrassDisplacers.Register(this, GrassDisplacers.ItemRadius);
            // Off the engine's per-node callback and onto the hub (see the _live block below). Last, so nothing
            // above can be skipped by an early return, and _tickReady is only set once _Ready has fully built the
            // state the body reads.
            SetProcess(false);
            _tickReady = true;
        }

        // PERF (ETW 2026-09-12): a `_Process` OVERRIDE costs a native->managed transition plus a StringName walk of the
        // whole class chain -- WorldItem -> RigidBody3D -> PhysicsBody3D -> CollisionObject3D -> Node3D -> Node, comparing
        // method names one at a time -- before the body runs at all. Measured on the pinned PEI spot: the dispatch was
        // 2,385 ms against 3,587 ms of actual work, and on ~all but 4 frames a second the body it finally reached was a
        // timer decrement. The same trace priced the mechanism directly: TickHub, which is ONE node doing a comparable
        // amount of work, paid 52 ms of dispatch for 6,418 ms of work. The tax is per-NODE, not per-unit-of-work.
        //
        // Items register here and are ticked from that one hub callback instead -- the StorageCrate pattern (containers
        // were ~19% of the main thread) and Vehicle's. The override stays as the body, so a direct caller still works;
        // SetProcess(false) only stops the ENGINE reaching it by name.
        static readonly System.Collections.Generic.List<WorldItem> _live = new();
        bool _tickReady;   // _EnterTree registers, but the body reads state _Ready builds -- never tick before then
        public static int LiveCount => _live.Count;   // wiring probe for tests
        public override void _EnterTree() { _live.Add(this); TickHub.Ensure(this); }
        public override void _ExitTree() { _live.Remove(this); }
        public static void TickAll(double delta)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var it = _live[i];
                if (!GodotObject.IsInstanceValid(it)) { _live.RemoveAt(i); continue; }
                // Pause / ProcessMode honoured exactly as the per-node callback did: CanProcess() is independent of
                // SetProcess(false), which is why turning the engine's callback off does not change pause behaviour.
                if (!it._tickReady || !it.IsInsideTree() || !it.CanProcess()) continue;
                it._Process(delta);
            }
        }

        // look-at focus (PlayerController drives this): rarity glow outline + name billboard on the item you're aiming at
        public void SetFocused(bool on)
        {
            if (_suppressed) return;   // P2b: a suppressed node is never the host's focus target -- the puppet is (defensive; it's also off the look-hit layer so the ray never reaches here)
            if (_focused == on) return;
            _focused = on;
            if (on) FocusColor = _rar;                            // OutlineOverlay tints the rim with the focused item's rarity
            if (_glow != null) _glow.Visible = on && _shown;
            // A Label3D is IN THE WORLD, so the nightvision pass AMPLIFIES it: white text at gain 2.6 saturates
            // into a blob and takes the surrounding image with it (strawberry 2026-09-09: "make sure we dont NUKE
            // the nightvision on dropped item labels and outlines"). Dim it going IN, and MULTIPLY the authored
            // rarity tint rather than replacing it, so a rare drop still reads as its own colour through a tube.
            if (_label != null)
            {
                _label.Visible = on || ShowLabels;
                _label.Modulate = _rar.Lerp(Colors.White, 0.35f) * NightVision.OverlayDim;
            }
        }

        float _prevVelY, _impactCd;   // landing detector: a fall that stops being one, with a bounce cooldown

        public override void _PhysicsProcess(double delta)
        {
            if (_settled) return;   // frozen static once it came to rest -> zero per-frame cost + no jitter
            _age += (float)delta;
            // IT LANDS WITH A SOUND. Detected as the ARREST of a fall rather than a contact callback: turning
            // ContactMonitor on for every dropped item in a looted town costs a per-body contact buffer forever,
            // and what we need is one event, not the contact set. A hard downward velocity that stops being one
            // in a single step is a landing; the cooldown stops a bouncing tin machine-gunning.
            float vy = LinearVelocity.Y;
            if (_impactCd > 0f) _impactCd -= (float)delta;
            else if (_prevVelY < -2.5f && vy - _prevVelY > 1.5f)
            {
                _impactCd = 0.15f;
                float hit = Mathf.Min(-_prevVelY, 12f);
                // Speed-scaled, the way retail scales its own collision audio (ParticleSystemCollisionAudio maps
                // min/maxSpeed onto min/maxVolume): a tin tipped off a shelf should not sound like one thrown off
                // a roof.
                float db = Mathf.Lerp(-16f, -4f, Mathf.InverseLerp(2.5f, 12f, hit));
                var surf = PlayerController.TryFootSurfaceAt(this, GlobalPosition, GetRid(), out var s) ? s : PlayerController.Surf.Concrete;
                GameAudio.PlayAt(this, GameAudio.Impact(surf), GlobalPosition, db, 3f, 22f, (float)GD.RandRange(0.94, 1.06));
            }
            _prevVelY = vy;
            _velAvg = _velAvg.Lerp(LinearVelocity, 0.15f);
            _angAvg = _angAvg.Lerp(AngularVelocity, 0.15f);
            if (_velAvg.LengthSquared() < 0.02f && _angAvg.LengthSquared() < 0.05f)
            {
                _settleT += (float)delta;
                if (_settleT > 0.25f)   // gated on the FILTERED velocity so the buzz can't keep the timer from completing (vehicle lesson)
                {
                    LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero;
                    FreezeMode = FreezeModeEnum.Static; Freeze = true;
                    _settled = true;
                    // Stop being CALLED, not just return early. The `if (_settled) return` above is cheap but
                    // it still costs a managed callback per item per step forever, and a town's worth of
                    // dropped loot never moves again once it lands. Nothing re-enables this because nothing
                    // can: the body is Frozen Static from here, so there is no motion left to track.
                    SetPhysicsProcess(false);
                    DespawnIfStuck();
                }
            }
            else if (_age > 8f) QueueFree();   // never settled (buzzing in the ground / on an edge) -> despawn (master: happens in vanilla too)
            else _settleT = 0f;
        }

        // master: items sometimes clip INTO the ground; if a world surface sits just above the item's centre when it
        // came to rest, it's stuck -> despawn it after a couple seconds (vanilla does the same).
        void DespawnIfStuck()
        {
            Vector3 c = GlobalTransform * _boxCtr;
            var q = PhysicsRayQueryParameters3D.Create(c, c + Vector3.Up * 0.5f);
            q.CollisionMask = 1;   // world/terrain
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            if (GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0)
            {
                var t = GetTree().CreateTimer(2.5);
                t.Timeout += () => { if (IsInstanceValid(this)) QueueFree(); };
            }
        }

        public override void _Process(double delta)
        {
            if (_suppressed) return;   // P2b: hidden physics-only body under --spconsume -> no LOS/visibility work, stays Visible=false (the puppet is rendered instead)
            if (_label != null && _label.Visible)   // TopLevel label -> keep it floating above the item in world space (billboards to the cam)
                _label.GlobalPosition = GlobalPosition + Vector3.Up * LabelH;
            _losTimer -= (float)delta;
            if (_losTimer <= 0f)   // recompute visibility ~4x/sec, STAGGERED per item (the raycast-storm was the town stutter, master)
            {
                ulong _pt = Time.GetTicksUsec();   // profiler: aggregate item-LOS raycast cost per window
                _losTimer = 0.22f + GD.Randf() * 0.14f;
                var cam = GetViewport().GetCamera3D();
                bool show = true;
                if (cam != null)
                {
                    // CONE cull (master): an item is only a candidate if it's inside the view cone (+ range) -- anything off
                    // to the side or behind is hidden with NO raycast, and distance culling comes free from the same test.
                    // ~60deg half-cone (a touch wider than the FOV so items don't pop right at the screen edge).
                    Vector3 toItem = GlobalPosition - cam.GlobalPosition;
                    float d2 = toItem.LengthSquared();
                    show = d2 < DrawDist2 && d2 > 1e-4f && toItem.Normalized().Dot(-cam.GlobalTransform.Basis.Z) > 0.5f;
                    // ...and only now, and only CLOSE, cast a ray (or a few for occluded) for a hard wall between.
                    if (show && d2 < RayDist2 && _hitPts != null)
                    {
                        // full-hitbox LOS (master): if ANY hitbox sample point (centre + corners) has clear LOS, keep it visible.
                        // Breaks on the first clear point, so a visible item usually costs ONE ray; only occluded items check all.
                        show = false;
                        var space = GetWorld3D().DirectSpaceState;
                        Transform3D gt = GlobalTransform;
                        // ONE query object, reused. Create() allocates a RefCounted per ray, and an occluded
                        // item casts all nine sample points -- so this path was allocating 9 query objects a
                        // call, 4x a second, per item, on top of the Dictionary IntersectRay hands back. The
                        // mask and the self-exclusion never change, so they are set once at build.
                        _losQuery ??= new PhysicsRayQueryParameters3D { CollisionMask = 1, Exclude = _excludeSelf };
                        _losQuery.From = cam.GlobalPosition;
                        int _rays = 0;
                        // Which sample points to try, and in what order. Break-on-first-clear means a VISIBLE
                        // item stops at the first entry, so the ordering matters more than the count: centre
                        // first (usually clear), then two DIAGONALLY OPPOSITE corners, which cover the widest
                        // spread for the fewest rays. Measured 2.03 rays/call overall -- ~87% of calls are one
                        // ray and out, and only the ~13% that are fully occluded ever reach the end of the list.
                        // So capping the scan only touches that 13%; it cannot make the common case slower.
                        bool Scan(int[] order)
                        {
                            int cnt = order?.Length ?? _hitPts.Length;
                            for (int i = 0; i < cnt; i++)
                            {
                                _losQuery.To = gt * _hitPts[order != null ? order[i] : i];
                                _rays++;
                                // DISPOSED, not dropped. IntersectRay returns a Godot.Collections.Dictionary --
                                // a native handle -- and every one that is merely abandoned is registered in
                                // Godot's disposables tracker and left for the finalizer. Those tracker nodes
                                // were 11.5% of the game's allocations and kept the finalizer thread at ~1.2 s
                                // per 40 s capture. We only ever read Count, so the handle can die here.
                                using var hit = space.IntersectRay(_losQuery);
                                if (hit.Count == 0) return true;
                            }
                            return false;
                        }
                        var order = LosScanPoints == 3 && _hitPts.Length >= 9 ? Los3 : null;
                        show = Scan(order);
                        // WITHIN-RUN A/B (UG_LOSCOMPARE=1). Comparing 3-point against 9-point across two
                        // separate runs is hopeless: items are still settling, so positions differ, different
                        // things are occluded, and two IDENTICAL 9-point runs disagreed by 3 visible items --
                        // the exact size of the effect being measured. Evaluating both answers on the same
                        // frame, same positions, same everything removes the noise entirely instead of trying
                        // to average it away. Doubles the ray cost, which is fine for a diagnostic run.
                        if (LosCompare && _hitPts.Length >= 9)
                        {
                            bool other = Scan(order == null ? Los3 : null);
                        }
                        // How many of the nine we actually spend. Break-on-first-clear means a VISIBLE item is
                        // 1 and an occluded one is 9, so rays-per-call is the number that says whether cutting
                        // the sample count is worth anything or is a rounding error. Measure, then decide.
                    }
                }
                if (Visible != show) Visible = show;   // hide the whole prop when occluded/behind -- physics keeps running so it still settles
                _shown = show;
                if (!show && _glow != null && _glow.Visible) _glow.Visible = false;
            }
        }
    }

    // Client-side FOCUSABLE dropped-item replica (MP). A render-only Node3D (WorldItemReplicaView owns its
    // transform/lifecycle) carrying the item mesh, a look-detection box collider, and a glow silhouette --
    // built by WorldItem.BuildItemPuppet, toggled by PlayerController.UpdateLookFocus. No pickup physics.
    public partial class WorldItemPuppet : Node3D, IPuppetFocusable
    {
        public uint NetId;   // the server world-item entity this puppet mirrors (VehiclePuppet.NetId pattern) -- the F-chain pickup request addresses the server by this id
        public ushort ItemId;   // what it is -- the pickup request remembers it so the echo that holsters it can force it into the hands

        MeshInstance3D _glow;
        Label3D _label;
        Color _rar = Colors.White;
        bool _focused;
        const float LabelH = 0.4f;   // name tag floats this far above the item (matches the real WorldItem)

        public void Configure(MeshInstance3D glow, Label3D label, Color rarity) { _glow = glow; _label = label; _rar = rarity; }

        public void SetLookFocused(bool on)
        {
            if (_focused == on) return;
            _focused = on;
            OutlineOverlay.ShowOutline(on, _rar, _glow);   // OutlineOverlay tints the rim with the item's rarity
            if (_label != null && IsInstanceValid(_label))
            {
                _label.Visible = on;
                _label.Modulate = _rar.Lerp(Colors.White, 0.35f) * NightVision.OverlayDim;   // the puppet's tag is the same Label3D in the same world: same tube treatment (see WorldItem.SetFocused)
                if (on) _label.GlobalPosition = GlobalPosition + Vector3.Up * LabelH;   // TopLevel -> place it in world space (a settled drop doesn't move)
            }
        }
    }
}
