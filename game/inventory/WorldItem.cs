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
        public static Color FocusColor = Colors.White;   // the currently-focused item's rarity colour -- OutlineOverlay tints the rim with it

        public const uint ItemHitLayer = 1u << 7;     // the item's box collider layer -- the player's look-sphere tests against this
        const float LabelH = 0.4f;                    // name tag floats this far above the item origin (world space)

        float _losTimer;    // throttle the LOS visibility check (staggered) -- NOT a raycast-per-item every frame
        bool _shown = true;
        MeshInstance3D _mesh, _glow;
        Label3D _label;
        Color _rar;
        bool _focused;

        Vector3 _velAvg, _angAvg;   // low-pass velocity/spin for settle detection (jitter cancels in the running average)
        float _settleT, _age;
        bool _settled;
        public bool Settled => _settled;   // L1 tests: has the dropped item come to rest?
        Vector3[] _hitPts;          // hitbox sample points (centre + 8 corners, local) for the full-hitbox LOS cull (master)
        Vector3 _boxCtr;
        Godot.Collections.Array<Rid> _excludeSelf;   // cached ray-exclude (this body) so the LOS rays don't re-alloc

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

        /// <summary>C5 (PEI_CLIENT_PLAN §3): VISUAL-ONLY reuse of the shared item-model cache for the
        /// joined client's WorldItemReplicaView -- the same mesh/texture/flat-colour the physical prop
        /// shows, with the rarity marker box fallback for ids without a model. No RigidBody3D, no
        /// collider, no pickup -- the replica view owns transform + lifecycle.</summary>
        public static MeshInstance3D BuildReplicaVisual(ushort itemId, Color rarity)
        {
            var model = itemId > 0 ? GetModel(itemId) : null;
            if (model != null && model.Ok)
                return new MeshInstance3D
                {
                    Mesh = model.Mesh,
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
        public static WorldItemPuppet BuildItemPuppet(ushort itemId, Color rarity, string name)
        {
            var p = new WorldItemPuppet();
            var visual = BuildReplicaVisual(itemId, rarity);
            p.AddChild(visual);

            var model = itemId > 0 ? GetModel(itemId) : null;
            Vector3 boxSize = (model != null && model.Ok) ? model.Box : new Vector3(0.24f, 0.24f, 0.24f);
            Vector3 boxCenter = (model != null && model.Ok) ? model.Center : Vector3.Zero;
            boxSize *= 1.15f;   // +15% like the real item's hitbox -> easier to look at + aim
            var body = new StaticBody3D { CollisionLayer = ItemHitLayer, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = boxSize }, Position = boxCenter });
            p.AddChild(body);

            var glow = new MeshInstance3D
            {
                Mesh = visual.Mesh, Visible = false,
                Layers = OutlineOverlay.OutlineLayer,   // only the offscreen mask camera renders this
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            };
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
                _mesh.Mesh = model.Mesh;
                _mesh.MaterialOverride = model.Mat ?? new StandardMaterial3D { AlbedoColor = model.FlatColor ?? _rar, Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
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
            _glow = new MeshInstance3D
            {
                Mesh = _mesh.Mesh, Visible = false,
                Layers = OutlineOverlay.OutlineLayer,   // only the offscreen mask camera renders this
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            };
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
        }

        // look-at focus (PlayerController drives this): rarity glow outline + name billboard on the item you're aiming at
        public void SetFocused(bool on)
        {
            if (_focused == on) return;
            _focused = on;
            if (on) FocusColor = _rar;                            // OutlineOverlay tints the rim with the focused item's rarity
            if (_glow != null) _glow.Visible = on && _shown;
            if (_label != null) _label.Visible = on || ShowLabels;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_settled) return;   // frozen static once it came to rest -> zero per-frame cost + no jitter
            _age += (float)delta;
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
                    show = d2 < 40000f && d2 > 1e-4f && toItem.Normalized().Dot(-cam.GlobalTransform.Basis.Z) > 0.5f;
                    if (show && _hitPts != null)   // in the cone -> only NOW cast a ray (or a few for occluded) to check for a hard wall between
                    {
                        // full-hitbox LOS (master): if ANY hitbox sample point (centre + corners) has clear LOS, keep it visible.
                        // Breaks on the first clear point, so a visible item usually costs ONE ray; only occluded items check all.
                        show = false;
                        var space = GetWorld3D().DirectSpaceState;
                        Transform3D gt = GlobalTransform;
                        foreach (var lp in _hitPts)
                        {
                            var q = PhysicsRayQueryParameters3D.Create(cam.GlobalPosition, gt * lp);
                            q.CollisionMask = 1;   // only large world/terrain geometry (bit0) breaks line of sight
                            q.Exclude = _excludeSelf;
                            if (space.IntersectRay(q).Count == 0) { show = true; break; }
                        }
                    }
                }
                if (Visible != show) Visible = show;   // hide the whole prop when occluded/behind -- physics keeps running so it still settles
                _shown = show;
                if (!show && _glow != null && _glow.Visible) _glow.Visible = false;
                Prof.Add("item_LOS", _pt);
            }
        }
    }

    // Client-side FOCUSABLE dropped-item replica (MP). A render-only Node3D (WorldItemReplicaView owns its
    // transform/lifecycle) carrying the item mesh, a look-detection box collider, and a glow silhouette --
    // built by WorldItem.BuildItemPuppet, toggled by PlayerController.UpdateLookFocus. No pickup physics.
    public partial class WorldItemPuppet : Node3D, IPuppetFocusable
    {
        public uint NetId;   // the server world-item entity this puppet mirrors (VehiclePuppet.NetId pattern) -- the F-chain pickup request addresses the server by this id

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
            if (on) WorldItem.FocusColor = _rar;   // OutlineOverlay tints the rim with the item's rarity
            if (_glow != null && IsInstanceValid(_glow)) _glow.Visible = on;
            if (_label != null && IsInstanceValid(_label))
            {
                _label.Visible = on;
                if (on) _label.GlobalPosition = GlobalPosition + Vector3.Up * LabelH;   // TopLevel -> place it in world space (a settled drop doesn't move)
            }
        }
    }
}
