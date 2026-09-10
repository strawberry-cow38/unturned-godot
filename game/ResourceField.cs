using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // PEI harvestable RESOURCES (Terrain/Trees.dat): trees, bushes, ore rocks, mushrooms, snow piles...
    // 1694 spawns across 26 types (version-8 flat format: GUID + point + EulerXYZ + scale + isGenerated).
    // tools/resource_extract.py bakes each ResourceAsset's `Resource` prefab Model_0 subtree (trunk +
    // Foliage_0 leaves as SEPARATE parts, since bark vs leaf need different textures) from core.masterbundle
    // into content/resources/<name>_<i>.obj + _tex.png, lists them in resources.txt ("<name> <partCount>"),
    // and exports per-spawn (pos, EulerXYZ, scale) = 9 floats -> <name>.bin. Placement uses the SAME prop
    // convention as Main.BuildObjectsTest (raw Unity mesh, double-sided; Basis(Y,180-ey)*Basis(X,ex)*Basis(Z,-ez),
    // pos.z negated). Tree roots sit ~1.2 below origin, so origin-at-spawn-point sinks them (punch-list #8).
    //
    // MP Phase 8 (§3.7): every instance gets a deterministic LOAD-ORDER INDEX (manifest order x .bin order --
    // identical on every peer, content-hash-matched), which is the implicit wire id ResourceReplication's
    // alive-bitmap keys on. SetAlive(index,false) despawns an instance (zero-scaled out of its MultiMesh +
    // collider off); dedicated servers build with VisualInstances=false (colliders + indices, no rendering).
    public partial class ResourceField : Node3D
    {
        /// <summary>Dedicated fx hygiene (§2.1/§5): false = skip all MultiMesh/material/texture work; the
        /// instance registry (indices for the wire) and tree trunk colliders (the sim needs them) remain.</summary>
        public bool VisualInstances = true;

        // Set by Main per map: PEI -> "resources", others -> "resources_<key>". The tree/rock ASSETS are shared
        // across maps; only the baked spawn set differs (Washington = 87% pine, PEI = maple-heavy).
        public static string MapDir = "resources";

        sealed class InstanceRec
        {
            public (MultiMesh Mm, int Slot) Canopy;   // the LEAF part's slot, for the per-instance axe shake
            public float Shake;                        // 0..1, decaying; written into that slot's custom data
            public readonly List<(MultiMesh Mm, int Slot)> Slots = new();   // one entry per part-mesh
            public Transform3D Xf;
            public StaticBody3D Trunk;      // trees only
            public uint TrunkLayer;
            public bool Alive = true;
        }
        readonly List<InstanceRec> _instances = new();

        /// <summary>Total placed resource instances, in the deterministic load order (the wire index space).</summary>
        public int InstanceCount => _instances.Count;

        public bool IsAlive(int index) => index >= 0 && index < _instances.Count && _instances[index].Alive;

        /// <summary>Test seam: the tree-trunk StaticBody3D for an instance (null for non-trees) -- L1s
        /// assert the §7-risk-7 collider toggle without reaching into the registry.</summary>
        public StaticBody3D DebugTrunk(int index) => index >= 0 && index < _instances.Count ? _instances[index].Trunk : null;

        /// <summary>Test seam: the placed transform for an instance -- what the MultiMesh slot was given. Paired with
        /// DebugTrunk so a test can check the thing you SEE and the thing you WALK INTO agree.</summary>
        public Transform3D DebugInstanceXf(int index) => index >= 0 && index < _instances.Count ? _instances[index].Xf : default;

        /// <summary>Fell (false) or respawn (true) one resource instance by its load-order index: the visual
        /// leaves/enters its MultiMesh (zero-scale -- MultiMesh has no per-instance visibility) and a tree's
        /// trunk collider toggles with it. Idempotent; never called on the SP direct path.</summary>
        // AXE-HIT LEAF SHAKE (strawberry 2026-09-09: "change the tree leaves to react when you hit it with an axe.
        // have them shake with each hit").
        //
        // PER INSTANCE, through the MultiMesh's custom-data channel, because the canopy material is SHARED: one
        // MakeSwayMat per part serves every cell's MultiMesh, so a uniform would shake every tree of that species
        // on the map at once. INSTANCE_CUSTOM.x is the only per-tree channel there is here.
        //
        // ...and the decay lives on the FIELD, not on the trees. TreeTrunk._Ready deliberately turns its own
        // _Process off -- an idle tree paid a full engine->C# dispatch per frame and it measured ~25% of the main
        // thread across PEI (ETW 2026-09-02). Ticking every trunk again to fade a shudder would hand that straight
        // back. One node ticks, over a list that is empty almost always, and it stops itself when the list drains.
        const float HitShakeDecay = 5.5f;    // e-folds/s -- a chop's shudder is gone in well under a second
        readonly List<int> _shaken = new();  // instance indices currently ringing; drives SetProcess
        public void HitShake(int index)
        {
            if (index < 0 || index >= _instances.Count) return;
            var r = _instances[index];
            if (r.Canopy.Mm == null || !r.Alive) return;
            if (r.Shake <= 0f) _shaken.Add(index);
            r.Shake = 1f;                    // each hit re-arms it to full rather than accumulating
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            if (_shaken.Count == 0) { SetProcess(false); return; }
            float k = Mathf.Exp(-HitShakeDecay * (float)delta);
            for (int i = _shaken.Count - 1; i >= 0; i--)
            {
                var r = _instances[_shaken[i]];
                r.Shake *= k;
                bool done = r.Shake < 0.01f;
                if (done) { r.Shake = 0f; _shaken.RemoveAt(i); }
                r.Canopy.Mm?.SetInstanceCustomData(r.Canopy.Slot, new Color(r.Shake, 0f, 0f, 0f));
            }
        }

        /// <summary>Test seam: read the shake back OUT of the MultiMesh rather than off our own bookkeeping. The
        /// claim is that the per-instance channel carries it to the shader; a value we merely remembered would
        /// prove nothing about what actually got written. -1 = this instance has no leaf part.</summary>
        internal float CanopyShakeForTest(int index)
        {
            if (index < 0 || index >= _instances.Count) return -1f;
            var c = _instances[index].Canopy;
            return c.Mm == null ? -1f : c.Mm.GetInstanceCustomData(c.Slot).R;
        }

        public void SetAlive(int index, bool alive)
        {
            if (index < 0 || index >= _instances.Count) return;
            var r = _instances[index];
            if (r.Alive == alive) return;
            r.Alive = alive;
            var hidden = new Transform3D(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), new Vector3(0f, -10000f, 0f));
            foreach (var (mm, slot) in r.Slots) mm.SetInstanceTransform(slot, alive ? r.Xf : hidden);
            if (r.Trunk != null) r.Trunk.CollisionLayer = alive ? r.TrunkLayer : 0;
        }

        public void LoadResources(string activeHoliday)
        {
            string dir = ProjectSettings.GlobalizePath($"res://content/{MapDir}/");
            string manifest = dir + "resources.txt";
            if (!File.Exists(manifest)) { Log.Print("[resources] no resources.txt -- skipping"); return; }
            // UG_NOLOD=1 keeps the old hardcoded 320/180 -- the A/B control for what retail's distances changed.
            if (System.Environment.GetEnvironmentVariable("UG_NOLOD") != "1")
                LodTable.LoadResources(dir + "lods.txt");   // retail per-asset LODGroup; layer cull is LodTable.DefaultCullDistance
            int total = 0, types = 0, treeCols = 0;
            foreach (var line in File.ReadAllLines(manifest))
            {
                var sp = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 2 || !int.TryParse(sp[1], out int parts)) continue;
                string name = sp[0];
                string holiday = sp.Length >= 3 ? sp[2] : "NONE";   // Cane_00(candy cane)/Snow_Pile_00/Ornament_XMAS are CHRISTMAS-only
                if (holiday != "NONE" && holiday != activeHoliday) continue;   // out-of-season resource (same gate as the objects)
                bool isTree = name.StartsWith("Birch") || name.StartsWith("Maple") || name.StartsWith("Pine");   // only trees cast shadows
                bool isOre = name.StartsWith("Metal");   // metal ore rocks -> pickaxe-harvestable (master)
                string binPath = dir + name + ".bin";
                if (!File.Exists(binPath)) continue;
                var xf = ReadInstances(binPath);
                if (xf.Count == 0) continue;
                // strawberry: "lower all tree models on their positions by a little bit". Applied to the SHARED list,
                // before either consumer reads it, so the visual instance and the trunk collider move together -- sink
                // one and not the other and the tree looks seated while its collider stands proud of the ground, which
                // is a bug you cannot see and only meet by walking into it.
                if (isTree) SinkTrees(xf);
                // the deterministic index space: instances register in manifest x .bin order on every peer
                var recs = new List<InstanceRec>(xf.Count);
                foreach (var t in xf)
                {
                    var rec = new InstanceRec { Xf = t };
                    recs.Add(rec);
                    _instances.Add(rec);
                }
                if (isTree)   // MultiMesh has no colliders -> add a trunk cylinder per tree so trees BLOCK bullets/movement (master), tagged Wood
                {
                    int baseIdx = _instances.Count - xf.Count;   // recs[k] lives at _instances[baseIdx + k] -> the trunk carries its own index for SetAlive
                    bool isMaple = name.StartsWith("Maple"), isPine = name.StartsWith("Pine");
                    ushort logItem = isMaple ? (ushort)39 : isPine ? (ushort)41 : (ushort)37;   // wood-type log: Birch 37 / Maple 39 / Pine 41
                    // retail ResourceAsset values (unturned.gameinfo.io): Birch 800hp/450s/7-10, Maple 1000/600/6-9, Pine 1200/750/5-8
                    float treeHp = isMaple ? 1000f : isPine ? 1200f : 800f;
                    float treeReset = isMaple ? 600f : isPine ? 750f : 450f;
                    int rMin = isMaple ? 6 : isPine ? 5 : 7, rMax = isMaple ? 9 : isPine ? 8 : 10;
                    // WIDER TRUNKS (master 2026-09-07: "widen the hitboxes of tree trunks"). The old collider was a
                    // flat 0.5 for every species, which is birch-sized: MEASURED off the trunk meshes in the band a
                    // player actually shoots and walks through (y 1..2, chest height), the real radii are
                    // Birch 0.48, Pine 0.80, Maple 0.83. So a pine's hitbox was under two thirds of its trunk and you
                    // could put a round through the visible bark.
                    //
                    // Chest height, NOT the mesh's widest point. Every trunk flares at the roots (knee band 0..1
                    // measures Birch 0.93, Pine 1.28, Maple 1.31) and taking that would hand a pine 48 cm of hitbox
                    // standing in open air at the height you aim at -- trading "shots pass through the trunk" for
                    // "shots stop short of it", which is the same bug wearing a hat.
                    //
                    // Floored at the old 0.5 so nothing gets NARROWER on a widen request; birch is within 2 cm of it
                    // either way. Re-measure with: max hypot(x,z) over verts with 1 <= y < 2 in <Species>_1.obj.
                    float trunkR = Mathf.Max(0.5f, isMaple ? 0.83f : isPine ? 0.80f : 0.48f);
                    for (int k = 0; k < xf.Count; k++)
                    {
                        var t = xf[k];
                        // part-0's mesh AABB is the WHOLE tree (incl. canopy) -> that gave a giant ~5m-radius cylinder
                        // floating at canopy height that missed the ground. Use a FIXED trunk (~0.5m radius, ~8m tall) at
                        // the base, scaled by the instance scale, on an ORTHONORMAL body (Jolt drops non-uniform-scaled shapes).
                        Vector3 sc = t.Basis.Scale;
                        float sr = Mathf.Max(Mathf.Abs(sc.X), Mathf.Abs(sc.Z)), sh = Mathf.Abs(sc.Y);
                        var body = new TreeTrunk { Field = this, Index = baseIdx + k, LogItem = logItem, Health = treeHp, Reset = treeReset, RewardMin = rMin, RewardMax = rMax, TreeName = name, ResDir = dir, TreeXf = t, CollisionLayer = 1u << 0, Transform = new Transform3D(t.Basis.Orthonormalized(), t.Origin) };
                        body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Wood);
                        body.AddToGroup("tree");   // for the UG_TREECHECK raycast self-test
                        body.TrunkRadius = trunkR;   // the stump reuses it, so the two never disagree about how thick the tree is
                        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = trunkR * sr, Height = 8f * sh }, Position = new Vector3(0f, 2.5f * sh, 0f) });
                        AddChild(body);
                        body.AddToGroup(ColliderBudget.Group);   // 1124 tree trunks, same streaming as the prop colliders
                        {   // same rule as props: collision lasts as long as the tree is drawn (trees compute far, so they keep it far)
                            float tcull = LodTable.ResourceCull(name, LodTable.SourceFov);
                            body.SetMeta(ColliderBudget.RadiusMeta, tcull > 0f ? tcull : (isTree ? 320f : 180f));
                        }
                        recs[k].Trunk = body;
                        recs[k].TrunkLayer = body.CollisionLayer;
                        treeCols++;
                    }
                }
                else if (isOre)   // metal ore rocks: a solid collider + OreRock harvest body (pickaxe -> Metal Scrap), master
                {
                    int baseIdx = _instances.Count - xf.Count;
                    for (int k = 0; k < xf.Count; k++)
                    {
                        var t = xf[k];
                        Vector3 sc = t.Basis.Scale;
                        float sr = Mathf.Max(Mathf.Abs(sc.X), Mathf.Abs(sc.Z)), sh = Mathf.Abs(sc.Y);
                        var body = new OreRock { Field = this, Index = baseIdx + k, CollisionLayer = 1u << 0, Transform = new Transform3D(t.Basis.Orthonormalized(), t.Origin) };
                        body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Metal);   // pickaxe/bullet hits read as metal
                        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2.6f * sr, 2.4f * sh, 2.6f * sr) }, Position = new Vector3(0f, 1.1f * sh, 0f) });
                        AddChild(body);
                        body.AddToGroup(ColliderBudget.Group);   // stream the collider like the tree trunks + props
                        {
                            float ocull = LodTable.ResourceCull(name, LodTable.SourceFov);
                            body.SetMeta(ColliderBudget.RadiusMeta, ocull > 0f ? ocull : 180f);
                        }
                        recs[k].Trunk = body;         // reuse the instance's collider slot -> SetAlive toggles it on deplete/regrow
                        recs[k].TrunkLayer = body.CollisionLayer;
                    }
                }
                if (VisualInstances)
                {
                    // Bucket instances into spatial CELLS so each chunk frustum-culls independently (behind the player) + distance-culls,
                    // instead of one map-wide MultiMesh that's never culled. Trees keep their shadows within range (master); props cull closer.
                    const float Cell = 64f;
                    // Retail draw distance for this asset: LayerMasks.RESOURCE gets the full defaultCullDistance
                    // (512m at the default draw-distance setting), tightened by the asset's own LODGroup. Trees
                    // compute to ~3000m so the layer stops them; small bushes/rocks bite well inside it. The old
                    // hardcoded 320/180 split is the fallback for anything missing from the table.
                    // NB the cull is per 64m CELL, so a cell's instances survive to roughly cullRange + Cell.
                    float cullRange = LodTable.ResourceCull(name, LodTable.SourceFov);
                    if (cullRange <= 0f) cullRange = isTree ? 320f : 180f;
                    // Trees stop 25% closer than retail (strawberry), because the IMPOSTORS below take over from
                    // there and run far past where the real meshes ever did. Only trees: a bush that vanishes early
                    // has nothing standing in for it.
                    if (isTree) cullRange *= TreeCullScale;
                    var byCell = new Dictionary<(int, int), List<int>>();
                    for (int k = 0; k < xf.Count; k++)
                    {
                        var key = ((int)Mathf.Floor(xf[k].Origin.X / Cell), (int)Mathf.Floor(xf[k].Origin.Z / Cell));
                        if (!byCell.TryGetValue(key, out var cl)) { cl = new List<int>(); byCell[key] = cl; }
                        cl.Add(k);
                    }
                    for (int i = 0; i < parts; i++)
                    {
                        string objP = dir + name + "_" + i + ".obj";
                        if (!File.Exists(objP)) continue;
                        var mesh = ObjMesh.Load(objP);
                        if (mesh == null) continue;
                        bool sways = (isTree && i == 0) || name.StartsWith("Bush");   // tree CANOPY (part 0 = the WIDE leaf mesh) + bushes sway; the THIN trunk (part 1)/clay/mushroom/ore stay stiff
                        Material mat = sways ? MakeSwayMat(dir + name + "_" + i + "_tex.png")
                                             : MakeMat(dir + name + "_" + i + "_tex.png", !isTree);
                        foreach (var kv in byCell)
                        {
                            var lst = kv.Value;
                            // UseCustomData BEFORE InstanceCount (same rule as TransformFormat) -- it is the
                            // per-instance channel the axe-hit shake rides, and only the swaying leaf part needs it.
                            var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = sways, InstanceCount = lst.Count };
                            for (int k = 0; k < lst.Count; k++)
                            {
                                mm.SetInstanceTransform(k, xf[lst[k]]);
                                recs[lst[k]].Slots.Add((mm, k));
                                if (sways && i == 0) recs[lst[k]].Canopy = (mm, k);   // part 0 is the leaf mesh
                            }
                            var mmi = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat,
                                CastShadow = isTree ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                                VisibilityRangeEnd = cullRange, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled };
                            mmi.AddToGroup(NearestFilter.KeepFilterGroup);   // keep the bilinear MakeMat set; the scene-wide sweep would stamp it back to Nearest
                            AddChild(mmi);
                        }
                    }
                    // Queued, not built: the impostor texture is RENDERED, and a SubViewport needs a frame to
                    // produce one. LoadResources is synchronous, so the bake happens in BuildTreeImpostorsAsync
                    // once the caller can await frames.
                    if (isTree && TreeImpostors)
                        _pendingImpostors.Add(new ImpostorSpec { Name = name, Dir = dir, Parts = parts, Xf = xf, ByCell = byCell, RealCull = cullRange });
                }
                total += xf.Count; types++;
                Log.Print($"[resources] {name}: {xf.Count} x {parts} part(s)");
            }
            Log.Print($"[resources] {total} instances across {types} types (MultiMesh), {treeCols} tree trunk colliders");
        }

        // ---------------------------------------------------------------------------------------------------
        // TREE IMPOSTORS (strawberry: "tree imposters with a very very high render dist ... and lower the actual
        // tree render dist by ~25%").
        //
        // Beyond the real trees' (now shortened) cull, each tree becomes ONE camera-facing quad wearing a picture
        // of itself. The swap is entirely engine-side: two MultiMeshInstances over the same transforms, the real
        // one ending at RealCull and the impostor one BEGINNING there. Godot's VisibilityRange does the handover,
        // so nothing per-frame decides which to draw, and BillboardMode.FixedY turns the quads to face the camera
        // in the shader -- no per-instance CPU work either. The whole feature costs one extra MultiMesh per cell.
        //
        // The picture is BAKED AT LOAD from the tree's own meshes through a SubViewport, not shipped as an asset.
        // A baked PNG in content/ would be one silent mismatch away from wrong -- swap a tree model and the far
        // field still shows the old one, with nothing to catch it. Rendering from the same .obj the near mesh uses
        // means they cannot disagree.
        //
        // Baked from UNSHADED copies of the real materials, so the texture is pure albedo with no lighting cooked
        // in. The quad is then lit normally at runtime; bake it lit and every distant tree would stay bright at
        // midnight.
        public static bool TreeImpostors = System.Environment.GetEnvironmentVariable("UG_TREEIMP") != "0";
        public static float TreeCullScale = EnvF("UG_TREECULL", 0.75f);      // real trees stop this fraction of the way out
        public static float ImpostorRange = EnvF("UG_TREEIMPDIST", 2000f);   // how far the billboards carry

        // THE HANDOVER MUST OVERLAP, NOT MEET.
        //
        // The first version ended the real trees and began the billboards at the SAME distance, which looks
        // correct and flickers on sight (strawberry, within minutes: "trees flicker in and out ... happens on the
        // tree -> imposter line"). Two things go wrong at a shared edge. Sitting exactly on it, sub-metre camera
        // jitter flips both nodes on the same frame and you get frames with NEITHER drawn. And the two
        // MultiMeshes do not even measure from the same point -- the billboard quads are centred half a tree-
        // height up, so their AABB crosses the threshold at a slightly different moment than the real mesh's.
        //
        // So the billboards now switch on BEFORE the real trees switch off. Through the overlap band both draw:
        // negligible overdraw at 300m+, and no jitter can ever produce a hole, because no single toggle can turn
        // the tree off. Cheaper and far more robust than trying to make two different AABBs agree to the metre.
        public static float ImpostorOverlap = EnvF("UG_TREEIMPOVERLAP", 0.88f);

        static float EnvF(string name, float fallback)
            => float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f ? v : fallback;
        public static int ImpostorTexW = 192, ImpostorTexH = 256;

        sealed class ImpostorSpec
        {
            public string Name, Dir;
            public int Parts;
            public List<Transform3D> Xf;
            public Dictionary<(int, int), List<int>> ByCell;
            public float RealCull;
        }
        readonly List<ImpostorSpec> _pendingImpostors = new();
        readonly List<(string Name, StandardMaterial3D Mat, float W, float H)> _impostorMats = new();
        /// <summary>Render-harness seam (--imptest): the baked billboard materials and the world size each was
        /// framed at, so a human can stand them up next to the real trees and judge them.</summary>
        public List<(string Name, StandardMaterial3D Mat, float W, float H)> DebugImpostorMaterialsForTest() => _impostorMats;
        public int PendingImpostorTypesForTest => _pendingImpostors.Count;

        /// <summary>Test seam: the handover distances each queued species WILL be given. Read from the queue
        /// rather than from the built nodes on purpose -- the bake needs a real renderer, so headless has no
        /// impostor nodes to inspect, and the overlap invariant is exactly what a headless suite CAN still
        /// check.</summary>
        public List<(string Name, float RealEnd, float ImpostorBegin, float ImpostorEnd)> DebugImpostorRangesForTest()
        {
            var outp = new List<(string, float, float, float)>();
            foreach (var s in _pendingImpostors) outp.Add((s.Name, s.RealCull, s.RealCull * ImpostorOverlap, ImpostorRange));
            return outp;
        }
        public int ImpostorInstancesForTest { get; private set; }

        /// <summary>Bake one billboard per tree species and hang the far-field MultiMeshes off it. Async because a
        /// SubViewport only produces a texture after the frame it renders on.</summary>
        public async System.Threading.Tasks.Task BuildTreeImpostorsAsync()
        {
            if (_pendingImpostors.Count == 0) return;
            int made = 0;
            foreach (var spec in _pendingImpostors)
            {
                var (tex, quadW, quadH) = await BakeImpostorAsync(spec);
                if (tex == null) continue;   // a species whose bake failed simply has no far field, rather than a black quad
                var mat = new StandardMaterial3D
                {
                    AlbedoTexture = tex,
                    Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                    AlphaScissorThreshold = 0.5f,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.FixedY,   // yaw only: a tree must not tip toward the camera
                    BillboardKeepScale = true,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
                    Roughness = 1f,
                    // The quad's own normal points at the viewer, which would make every tree in the far field
                    // flare identically as the sun swings past. Fixed up-ish normals read as foliage instead.
                    SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                };
                foreach (var kv in spec.ByCell)
                {
                    var lst = kv.Value;
                    var quad = new QuadMesh { Size = new Vector2(quadW, quadH), Orientation = PlaneMesh.OrientationEnum.Z };
                    var mm = new MultiMesh { Mesh = quad, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = lst.Count };
                    for (int k = 0; k < lst.Count; k++)
                    {
                        var t = spec.Xf[lst[k]];
                        // Centred on the trunk at half the baked height: a QuadMesh is centred on its origin, so
                        // planting it at the tree's base would bury the bottom half of the picture in the ground.
                        var basis = Basis.Identity.Scaled(new Vector3(t.Basis.Scale.Y, t.Basis.Scale.Y, 1f));
                        mm.SetInstanceTransform(k, new Transform3D(basis, t.Origin + new Vector3(0f, quadH * 0.5f * t.Basis.Scale.Y, 0f)));
                    }
                    var mmi = new MultiMeshInstance3D
                    {
                        Multimesh = mm, MaterialOverride = mat,
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,   // a flat card casts a flat wrong shadow, and nothing this far out needs one
                        VisibilityRangeBegin = spec.RealCull * ImpostorOverlap,   // EARLY -- see the overlap note
                        VisibilityRangeEnd = ImpostorRange,
                        // Disabled, matching the real trees. Dependencies was wrong: it fades nodes that name this
                        // one as their visibility PARENT, and nothing does, so it fades nothing while quietly
                        // differing from the mode on the node it hands over from.
                        VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
                    };
                    mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                    AddChild(mmi);
                    made += lst.Count;
                }
                _impostorMats.Add((spec.Name, mat, quadW, quadH));
                Log.Print($"[imposter] {spec.Name}: {spec.Xf.Count} billboards, on at {spec.RealCull * ImpostorOverlap:0}m, real trees off at {spec.RealCull:0}m, out to {ImpostorRange:0}m");
            }
            ImpostorInstancesForTest = made;
            Log.Print($"[imposter] {made} billboards across {_pendingImpostors.Count} species");
            _pendingImpostors.Clear();
        }

        // Returns the picture AND the world size it was framed at. Deliberately a return value rather than a
        // field the caller reads afterwards: the quad has to be exactly the box the camera framed, and a shared
        // field would silently hand the next species the previous one's dimensions.
        async System.Threading.Tasks.Task<(ImageTexture Tex, float W, float H)> BakeImpostorAsync(ImpostorSpec spec)
        {
            var meshes = new List<(ArrayMesh Mesh, StandardMaterial3D Mat)>();
            for (int i = 0; i < spec.Parts; i++)
            {
                string objP = spec.Dir + spec.Name + "_" + i + ".obj";
                if (!File.Exists(objP)) continue;
                var m = ObjMesh.Load(objP);
                if (m == null) continue;
                var lit = MakeMat(spec.Dir + spec.Name + "_" + i + "_tex.png", false);
                var flat = (StandardMaterial3D)lit.Duplicate();
                flat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;   // albedo only -- see the note above
                meshes.Add((m, flat));
            }
            if (meshes.Count == 0) return (null, 0f, 0f);

            var whole = new Aabb();
            for (int i = 0; i < meshes.Count; i++)
                whole = i == 0 ? meshes[i].Mesh.GetAabb() : whole.Merge(meshes[i].Mesh.GetAabb());
            if (whole.Size.Y <= 0.001f) return (null, 0f, 0f);
            float bakeW = Mathf.Max(whole.Size.X, whole.Size.Z), bakeH = whole.Size.Y;

            var vp = new SubViewport
            {
                Size = new Vector2I(ImpostorTexW, ImpostorTexH),
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                OwnWorld3D = true,   // its own World3D, or the real map's sun and fog land in the bake
            };
            AddChild(vp);
            foreach (var (mesh, mat) in meshes)
                vp.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
            var cam = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = bakeH,
                Near = 0.05f, Far = 4f * (bakeH + bakeW) + 10f,
                Current = true,
            };
            vp.AddChild(cam);
            var centre = whole.GetCenter();
            cam.LookAtFromPosition(centre + new Vector3(0f, 0f, 2f * (bakeH + bakeW)), centre, Vector3.Up);

            // Two frames: one for the viewport to be laid out and drawn, one for the texture to be readable.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var img = vp.GetTexture()?.GetImage();
            vp.QueueFree();
            if (img == null || img.IsEmpty()) return (null, 0f, 0f);
            // A fully transparent bake means the camera framed nothing -- return null so the species just has no
            // far field, instead of every distant tree becoming an invisible quad that still costs a draw.
            if (!HasAnyOpaque(img)) { Log.Err($"[imposter] {spec.Name}: bake came out empty, skipping"); return (null, 0f, 0f); }
            img.GenerateMipmaps();
            return (ImageTexture.CreateFromImage(img), bakeW, bakeH);
        }

        static bool HasAnyOpaque(Image img)
        {
            for (int y = 0; y < img.GetHeight(); y += 4)
                for (int x = 0; x < img.GetWidth(); x += 4)
                    if (img.GetPixel(x, y).A > 0.5f) return true;
            return false;
        }

        /// <summary>How far a tree is dropped below its spawn point, per unit of instance Y-scale (strawberry).
        /// SCALED rather than flat because these spawns run from saplings to full canopy at the same baked offset: a
        /// fixed nudge that seats a big pine leaves a small one hovering.</summary>
        /// 0.5 since 2026-09-06 (master: "sink all tree foliage down on their placed positions by like 30cm").
        /// Was 0.2; +0.3 is that request, and it stays SCALED so a sapling sinks proportionally rather than
        /// burying itself while a full canopy still hovers.
        internal const float TreeSink = 0.5f;

        internal static void SinkTrees(List<Transform3D> xf)
        {
            for (int i = 0; i < xf.Count; i++)
            {
                var t = xf[i];
                float sy = Mathf.Abs(t.Basis.Scale.Y);
                if (sy < 0.001f) sy = 1f;   // a degenerate scale must not silently sink the tree to nothing
                t.Origin = new Vector3(t.Origin.X, t.Origin.Y - TreeSink * sy, t.Origin.Z);
                xf[i] = t;
            }
        }

        static List<Transform3D> ReadInstances(string binPath)
        {
            var list = new List<Transform3D>();
            using var br = new BinaryReader(File.OpenRead(binPath));
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float ex = br.ReadSingle(), ey = br.ReadSingle(), ez = br.ReadSingle();
                float sx = br.ReadSingle(), sy = br.ReadSingle(), sz = br.ReadSingle();
                // identical to Main.BuildObjectsTest prop rotation (raw-mesh frame): Y(180-ey)*X(ex)*Z(-ez)
                var basis = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f - ey))
                          * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(ex))
                          * new Basis(new Vector3(0, 0, 1), Mathf.DegToRad(-ez));
                basis = basis.Scaled(new Vector3(sx, sy, sz));
                list.Add(new Transform3D(basis, new Vector3(px, py, -pz)));   // negate-Z position like every other placement
            }
            return list;
        }

        static StandardMaterial3D MakeMat(string texPath, bool unshaded)
        {
            var mat = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.4f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // leaves are double-sided billboards
                Roughness = 1f,
            };
            _ = unshaded;   // (kept for signature compat) resources are LIT + receive shadows per master; grass/flowers get up-normals instead
            if (File.Exists(texPath))
            {
                var img = new Image();
                if (ContentProvider.LoadOk(img, texPath))
                {
                    img.GenerateMipmaps();
                    mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                    // BILINEAR on trees and bushes (master), matching what grass and flowers already get in
                    // FoliageField. Resources are alpha-scissored leaf billboards, and nearest-neighbour on those
                    // leaves a hard stair-stepped edge on every frond that reads as artefacting rather than as the
                    // pixel look the rest of the port is going for. NearestFilter.Apply sweeps the whole scene after
                    // build and would undo this, so these instances are tagged for it to skip -- see NearestFilter.
                    mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps;
                }
            }
            else mat.AlbedoColor = new Color(0.35f, 0.45f, 0.28f);   // leafy-green fallback
            return mat;
        }

        // WIND-SWAY material (master 2026-08-24): wind_sway.gdshader (world-direction, height-weighted sway) with the
        // foliage's albedo. For tree LEAVES + bushes so they sway with WindField; the trunk keeps MakeMat (stiff).
        static Shader _swayShader;
        internal static ShaderMaterial MakeSwayMat(string texPath)
        {
            // GLOBALS BEFORE THE MATERIAL. wind_sway reads the `wind_vec` global, and a material that links it
            // before it is registered binds it INVALID -- Godot warns "uses global parameter 'wind_vec', but it
            // was removed at some point. Material will not display correctly" and the leaves then sway NOT AT ALL.
            // FoliageField's grass path has had this call since the bug was first found there; the tree/bush path
            // and the flower path never got it, so whether the canopy moved depended on load order. Seen live in a
            // 2026-09-08 weather render, which is why master could ask for wind variation on trees that were not
            // swaying in the first place. EnsureGlobals is idempotent.
            GrassDisplacers.EnsureGlobals();
            _swayShader ??= GD.Load<Shader>("res://content/wind_sway.gdshader");
            var m = new ShaderMaterial { Shader = _swayShader };
            if (File.Exists(texPath))
            {
                var img = new Image();
                if (ContentProvider.LoadOk(img, texPath)) { img.GenerateMipmaps(); m.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(img)); }
            }
            return m;
        }
    }

    // A choppable tree's trunk collider (harvesting). Gun/melee damage drains Health; when felled it despawns its tree
    // instance (SetAlive false), drops 1-3 wood-type logs + a stick, and regrows after a reset. The felling STRUCTURE
    // is the retail ResourceManager.damage path (drop log x reward + stick, then respawn); the damage ROUTING is ours
    // (PlayerController gun/melee), and item spawning uses our WorldItem.Spawn (master's flagged diff). ⚠ Health/reset
    // are tunable defaults -- the retail per-species ResourceAsset .dat values aren't in the tree data we have.
    public partial class TreeTrunk : StaticBody3D
    {
        public ResourceField Field;
        public int Index;
        public string TreeName, ResDir;                 // for loading the felling stump/debris meshes at runtime
        public Transform3D TreeXf;                      // the instance's full transform (pos+rot+scale) -> stump/debris match the tree
        public float TrunkRadius = 0.5f;                // this species' measured trunk radius (see where the trunk body is built)
        public ushort LogItem;                          // Birch 37 / Maple 39 / Pine 41
        ushort StickItem => (ushort)(LogItem + 1);      // catalog pairs Log then Stick: 38 / 40 / 42
        public float Health = 800f;                     // retail ResourceAsset health (set per-species by ResourceField)
        public float Reset = 450f;                       // retail asset.reset -- respawn seconds (per-species)
        public int RewardMin = 7, RewardMax = 10;        // retail Reward_Min/Max -- total items dropped (60% log / 40% stick)
        public bool Felled { get; private set; }
        float _maxHealth;

        public override void _Ready() { _maxHealth = Health; SetProcess(false); }   // PERF (ETW 2026-09-02): an idle tree paid a full engine->C# dispatch every frame to run `if (!_toppling) return;` -- ~25% of the main thread across PEI. Process only while toppling.

        // Gun/melee damage; fells the tree once Health hits 0.
        public void Chop(float amount, Vector3 point, Vector3 dir)
        {
            if (Felled) return;
            Field?.HitShake(Index);   // the leaves take every hit, not just the last one
            Health -= amount;
            if (Health > 0f) return;
            Felled = true;
            Field?.SetAlive(Index, false);   // zero-scale the tree out of its MultiMesh + drop the trunk collider to layer 0
            SpawnStump();                     // retail stumpGameObject: a stump is left where the tree stood
            SpawnDebris(dir);                 // retail debrisGameObject: a falling tree that topples + is cleaned up
            // NO LOGS YET (strawberry 2026-09-09: "only produce logs once theyve despawned"). They are dropped by
            // the debris cleanup timer instead, along the trunk that is lying there -- see SpawnDebris.
            GetTree().CreateTimer(Reset).Timeout += Regrow;   // retail asset.reset: it grows back
            Log.Print($"[tree] felled #{Index}");
        }

        // Retail ResourceManager.damage on death: Reward_Min..Reward_Max items rolled off the tree's spawn table
        // (60% wood-type Log / 40% Stick), scattered round the stump. Deterministic per tree so peers agree without a
        // wire; item spawning adapted to WorldItem.Spawn (master's flagged diff).
        void DropRewards()
        {
            var parent = GetParent() ?? (Node)this;
            Vector3 basePos = GlobalTransform.Origin;
            uint h = (uint)Index * 2654435761u; h ^= h >> 15;
            int n = RewardMin + (int)(h % (uint)Mathf.Max(1, RewardMax - RewardMin + 1));
            for (int i = 0; i < n; i++)
            {
                uint hi = h + (uint)(i + 1) * 2246822519u; hi ^= hi >> 13;
                ushort item = (hi % 100u) < 60u ? LogItem : StickItem;   // retail 60% log / 40% stick
                // ALONG THE TRUNK, not round the stump (strawberry: "produce logs along the length of where the
                // trunk landed instead of at the stump"). The wood is where the tree is lying, so the drop walks
                // the fallen length with a little lateral scatter -- which also means the direction you felled it
                // in decides where you go to pick it up.
                float along = (0.12f + 0.82f * ((hi >> 5) % 1000u) / 1000f) * _trunkLen;
                float side = (((hi >> 17) % 200u) / 100f - 1f) * 0.55f;
                Vector3 lat = new Vector3(-_fallDir.Z, 0f, _fallDir.X) * side;   // perpendicular, horizontal
                WorldItem.Spawn(parent, new SDG.Unturned.Item(item), basePos + _fallDir * along + lat + new Vector3(0f, 0.5f, 0f));
            }
        }

        Node3D _stump;
        StaticBody3D _stumpBody;

        // Retail stumpGameObject: the stump meshes stay where the tree stood until it regrows.
        void SpawnStump()
        {
            _stump = new Node3D();
            AddChild(_stump);
            LoadParts(_stump, "stump");
            _stump.GlobalTransform = TreeXf;
            SpawnStumpCollider();
        }

        /// <summary>A felled tree leaves something you can walk into (master 2026-09-07: "give tree stumps
        /// collision"). Felling sets the TRUNK body's layer to 0 -- correct, an 8 m cylinder must not keep
        /// blocking a tree that is lying on the ground -- and SpawnStump only ever added meshes, so the stump
        /// was scenery you walked through.
        ///
        /// It is the trunk's own base, so it collides where the trunk did. Measured, not guessed:
        /// Pine_0_1.obj (the standing trunk) and Pine_0_stump_0.obj share a halfwidth of 1.28 and a base of
        /// -1.20 -- the stump mesh IS the bottom 2.8 m of the trunk. So it reuses the trunk's own measured
        /// per-species radius (TrunkRadius, see where the trunk body is built) and the trunk's own -1.5 base,
        /// and only the TOP comes from the stump. Sharing that radius is the point: a stump you can walk
        /// through where the tree blocked you would be a worse bug than either.
        ///
        /// That top is read off the loaded mesh rather than hardcoded. Every stump asset in the map today tops
        /// at exactly +1.60, but reading the AABB means a species whose stump is a different height gets a
        /// collider that fits it instead of one that fits the pines.
        ///
        /// A SEPARATE body, and a child of the trunk (which is orthonormal), NOT of _stump: _stump carries the
        /// instance's full transform including a non-uniform scale, and Jolt silently drops non-uniformly
        /// scaled shapes -- the same trap the trunk body is orthonormalised to avoid.</summary>
        const float StumpBaseLocal = -1.5f;    // the standing trunk collider's own base
        const float StumpTopFallback = 1.6f;   // every *_stump_0.obj in the map tops here; used only if none loaded
        void SpawnStumpCollider()
        {
            Vector3 sc = TreeXf.Basis.Scale;
            float sr = Mathf.Max(Mathf.Abs(sc.X), Mathf.Abs(sc.Z)), sh = Mathf.Abs(sc.Y);
            if (sr <= 0.001f || sh <= 0.001f) return;

            float top = 0f;
            foreach (Node n in _stump.GetChildren())
                if (n is MeshInstance3D mi && mi.Mesh != null) top = Mathf.Max(top, mi.Mesh.GetAabb().End.Y);
            if (top <= 0.01f) top = StumpTopFallback;   // no mesh loaded (missing asset) -> the measured height

            float h = (top - StumpBaseLocal) * sh;
            _stumpBody = new StaticBody3D { CollisionLayer = 1u << 0, Name = "StumpCollider" };
            _stumpBody.AddChild(new CollisionShape3D {
                Shape = new CylinderShape3D { Radius = TrunkRadius * sr, Height = h },
                Position = new Vector3(0f, (top + StumpBaseLocal) * 0.5f * sh, 0f) });
            AddChild(_stumpBody);
            _stumpBody.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Wood);   // it is a tree: wood footsteps
            _stumpBody.AddToGroup(ColliderBudget.Group);   // streamed like the trunk it replaces, not budget-free
            if (HasMeta(ColliderBudget.RadiusMeta)) _stumpBody.SetMeta(ColliderBudget.RadiusMeta, GetMeta(ColliderBudget.RadiusMeta));
        }

        // Retail debrisGameObject: the felled tree topples in the chop direction, then is cleaned up. Retail uses a
        // RigidBody; a deterministic rotate-about-the-base reads the same and survives coarse render timesteps (a
        // rigidbody integrated at --fixed-fps 1 explodes + spins about its centre of mass, not the trunk base).
        Node3D _debris;
        bool _toppling;
        float _toppleDeg;                    // the lean, integrated (was a 0..1 progress along a fixed curve)
        float _toppleVel;                    // ...and its rate, rad/s
        // A TREE IS NOT A DROPPED PLANK (strawberry 2026-09-09: "have felled trees fall way more slowly"). 1.3 s
        // through 84 degrees is a fencepost being pushed over; a real trunk takes several seconds because the far
        // end has metres to travel.
        //
        // ...and it is a ROD HINGED AT ITS BASE, not something on a schedule (strawberry, same day: "with the
        // falling anim, start slow, then speed up until impact"). Gravity's torque on a leaning trunk goes as its
        // lever arm -- sin(lean) -- so an upright tree barely moves and the last thirty degrees carry all of the
        // speed. The old curve was `deg = FallDeg * t*t`: constant angular acceleration, which starts slow only in
        // the sense a parabola does and arrives at 0.698 rad/s. Integrating the real torque instead arrives at
        // 1.518 rad/s -- 2.2x the impact speed -- in the SAME 4.2 s, so the fall master already signed off on does
        // not get shorter, it gets back-loaded.
        //
        // ...and then the whip was too much (strawberry, same day again: "slow down the 'fast' part of the fall").
        // The first pass held the approved 4.2 s and put the pendulum shape inside it, which needed 1.287 rad/s^2 --
        // nearly twice a real tree's -- and arrived at 1.518 rad/s. Slowing the fast part while KEEPING the shape
        // means the fall has to get longer, because peak speed and duration are the same dial. So it now runs at the
        // honest constant for a 22 m rod, 3g/2L = 0.669 rad/s^2: 5.8 s end to end, arriving at 1.094 rad/s. Still
        // well up on the old constant-acceleration curve's 0.698, so it is still back-loaded -- just not a whipcrack.
        const float ToppleKick = 1.5f;      // degrees of initial lean -- the chop's own push. sin(0) is 0, so a
                                            // trunk released exactly upright would stand there forever.
        const float ToppleAccel = 0.669f;   // rad/s^2 = 3g/2L for a 22 m rod; 1.5 deg -> FallDeg in 5.83 s
        const float ToppleStep = 1f / 60f;  // integration substep. FIXED, so the fall is identical at 30 fps
                                            // offline and 200 fps live -- a scripted animation that varies with
                                            // frame rate is one that renders differently from how it plays.
        // ⚠ 84, not 90-something. Carrying the fall past 90 so the trunk finishes FLAT was tried (e6398305) and
        // rejected; see the pivot in _Process for why. Landing the trunk properly is still worth doing -- at 84 a
        // pine comes to rest with its tip about four metres up -- but not by moving where it swings about.
        // 90, not 84 (strawberry 2026-09-09: "have it lay horizontal at the end of the animation"). 84 left the
        // trunk nose-up by six degrees, which over a 22 m pine is 2.3 m of tip in the air. The hinge does NOT move
        // for this -- it is still the base centre, the same rotation, just carried to horizontal.
        const float FallDeg = 90f;           // where it comes to rest: flat
        // ...and it BOUNCES when it lands. A damped rebound about the landed angle, not a spring back up: the tip
        // lifts a few degrees and stops. Amplitude is small on purpose -- a fall that rebounds 10 degrees would
        // read as rubber.
        // strawberry 2026-09-09: "make it settle much faster. less bounces." 0.95 s -> 0.40, and the |sin| goes
        // from three humps to two, so it is two taps in under half a second rather than three over one.
        const float SettleTime = 0.40f, SettleDeg = 3.5f;
        const float SettleTaps = 2f;         // half-cycles of |sin| across the settle = how many times it bumps
        // ⚠ NOT a const: tree.harvest shortens it. The rewards moved onto this timer on 2026-09-09 ("leave their
        // wood where they landed" -- they have to wait for the tree to LAND, or they scatter from the stump along
        // a fall direction nothing has chosen yet), and the test has asserted an immediate drop ever since. Making
        // it settable lets the test drive the REAL timer quickly rather than assert past it: a seam that dropped
        // the rewards directly would pass with this wiring removed entirely.
        internal static double DebrisLife = 11.0;   // the fall got a second and a half longer, so this follows it: 9.0 was
                                            // set to leave ~3.8 s of the tree lying there after a 4.2 s fall, and
                                            // keeping that dwell is the point, not keeping the number
        bool _settling;
        float _settleT;
        Vector3 _toppleAxis = Vector3.Right; // horizontal axis; set from the chop direction
        Vector3 _fallDir = Vector3.Forward;  // the horizontal direction the TOP falls toward
        float _trunkLen = 8f;                // measured off the debris mesh, so logs land along the real trunk
        Transform3D _toppleBase;             // the debris' upright world transform (== TreeXf)

        void SpawnDebris(Vector3 dir)
        {
            _debris = new Node3D();
            LoadParts(_debris, "debris");
            (GetParent() ?? (Node)this).AddChild(_debris);
            _debris.GlobalTransform = TreeXf;
            _toppleBase = TreeXf;
            Vector3 fall = new Vector3(dir.X, 0f, dir.Z);
            fall = fall.LengthSquared() > 0.01f ? fall.Normalized() : Vector3.Forward;
            _fallDir = fall;
            _toppleAxis = Vector3.Up.Cross(fall).Normalized();   // top topples toward `fall`
            // How long the thing lying on the ground actually IS, measured off its own mesh rather than assumed:
            // the logs are scattered down its length, and a constant would put a birch's logs where a pine's tip
            // is. The debris is authored upright, so its Y extent is the trunk length once the placement scale is
            // applied.
            var box = new Aabb(); bool any = false;
            foreach (Node c in _debris.GetChildren())
                if (c is MeshInstance3D mi && mi.Mesh != null) { box = any ? box.Merge(mi.Mesh.GetAabb()) : mi.Mesh.GetAabb(); any = true; }
            if (any) _trunkLen = Mathf.Max(1f, box.Size.Y * Mathf.Max(0.01f, _toppleBase.Basis.Scale.Y));
            _toppleDeg = ToppleKick; _toppleVel = 0f; _toppling = true; _settling = false; SetProcess(true);
            // The logs arrive WITH the cleanup, not at the chop: you fell the tree, it lies there, and what it
            // leaves behind appears as it goes.
            GetTree().CreateTimer(DebrisLife).Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(this)) return;
                DropRewards();
                if (GodotObject.IsInstanceValid(_debris)) _debris.QueueFree();
            };
        }

        public override void _Process(double delta)
        {
            if (!GodotObject.IsInstanceValid(_debris)) { SetProcess(false); return; }
            float dt = (float)delta;
            // ⚠ BEFORE THE EARLY RETURN BELOW. The trunk's ring outlives the settle on purpose -- that is the whole
            // point of it -- so a per-frame write placed after the "nothing is animating" guard runs while the tree
            // is falling and stops the instant it lands, freezing the trunk mid-flex. Which is exactly what it did:
            // measured, the butt moved at PSNR 45 and adding a 5x uniform push changed the render in the FOURTH
            // decimal, because the value being pushed was frozen. Same shape as the 92e9a364 arm trim.
            TrunkReverb(dt);
            if (!_toppling && !_settling)
            {
                // The trunk is parked at FallDeg -- pass that, not 0, so the leaf spring sees zero angular rate
                // and rings DOWN from wherever the landing left it. Passing 0 here would read as another
                // instantaneous stop and kick off a second, unearned wobble.
                LeafReact(FallDeg, dt);
                if (_leafOff.Length() < 1e-4f && _leafVel.Length() < 1e-3f
                    && _trunkOff.Length() < 1e-4f && _trunkVel.Length() < 1e-3f) SetProcess(false);   // BOTH springs
                return;
            }
            float deg;
            if (_toppling)
            {
                for (float rem = dt; rem > 0f; )                     // substepped: same curve at any frame rate
                {
                    float h = Mathf.Min(rem, ToppleStep); rem -= h;
                    _toppleVel += ToppleAccel * Mathf.Sin(Mathf.DegToRad(_toppleDeg)) * h;   // torque ~ lever arm
                    _toppleDeg += Mathf.RadToDeg(_toppleVel) * h;
                    if (_toppleDeg >= FallDeg) { _toppleDeg = FallDeg; break; }
                }
                deg = _toppleDeg;
                LeafReact(deg, dt);                                  // ...and the canopy drags against that sweep
                if (_toppleDeg >= FallDeg)
                {
                    // IT HITS. Kick the trunk's own flex here, off the speed it actually arrived at, so a big
                    // trunk landing fast rings harder than a sapling tipping over.
                    _trunkVel = Vector3.Up * (TrunkKick * Mathf.Tau * TrunkFreq * Mathf.Min(1f, _toppleVel / 1.094f));
                    _toppling = false; _settling = true; _settleT = 0f;
                }
            }
            else
            {
                // Landed. A decaying rebound: |sin| gives repeated taps rather than a sine wave rolling THROUGH
                // the ground, and subtracting it means the trunk always lifts off the floor and drops back --
                // never sinks below where it came to rest.
                _settleT += dt;
                float k = _settleT / SettleTime;
                if (k >= 1f) { _settling = false; deg = FallDeg; }
                else deg = FallDeg - SettleDeg * Mathf.Exp(-4f * k) * Mathf.Abs(Mathf.Sin(Mathf.Pi * SettleTaps * k));
                LeafReact(deg, dt);   // the settle's own rocking drives the leaves too -- it is still trunk motion
            }
            var rot = new Basis(_toppleAxis, Mathf.DegToRad(deg));
            // PIVOT ABOUT THE BASE CENTRE -- and it stays there. ⚠ Two attempts to make this hinge "better" have
            // now been rejected by strawberry watching them, both on 2026-09-09:
            //   b8a24e68 moved the pivot to the FAR EDGE of the cut (up by the stump height, out by the trunk
            //     radius), on the reasoning that a real tree rolls over that edge. Geometrically that is true and
            //     it does close the gap at the butt -- but the whole trunk then swings out and down about a point
            //     a metre and a half off the ground, which is what "the hinge looks weird" was about.
            //   e6398305 kept that and added a SECOND pivot on the grounded far end so the trunk finished flat.
            //     Worse: a pivot that changes part-way down reads as the trunk detaching from the stump.
            // The base centre lifts the near edge of the cut a little, which is the "bit of a gap" that started
            // all this -- known, and preferred to either of the above. Fix that by moving the DEBRIS, not the
            // pivot: anything that shifts where the trunk swings about is the thing that looked wrong.
            Vector3 p = _toppleBase.Origin;                          // pivot about the stump/base, in WORLD space
            // ...AND IT RIDES UP OVER THE STUMP AS IT GOES (strawberry 2026-09-09: "it sinks into the ground when
            // it falls"). The pivot is the tree's BASE, and every tree is planted TreeSink into the ground, so the
            // rotation centre is half a metre BELOW the surface: everything near it sweeps under the ground on the
            // way over, and at horizontal the trunk's axis ends up buried. Measured, the butt goes under at about
            // 72 degrees and stays there.
            //
            // The fix is a TRANSLATION, not another pivot. The rotation centre is still _toppleBase.Origin -- three
            // versions of this animation have now been rejected for moving what it swings about, and this does not.
            // The debris just rises as it leans, weighted so it is exactly zero while the tree is upright (no pop at
            // the moment of felling) and full once it is down.
            //
            // sin CUBED, not sin. Plain sin has the trunk a third of the way up by 30 degrees, which opens daylight
            // between the butt and the cut while the tree still visibly looks attached to it -- and a gap at the
            // stump is the complaint that started this whole animation (b8a24e68). Cubing holds the lift near zero
            // through the first half and puts it in the last stretch, where the trunk is coming off the stump
            // anyway. Checked across the whole sweep: the butt's UNDERSIDE clears the ground at every lean
            // (+0.22 at 30 degrees, +0.31 at 60, resting at 90), so it still never digs in.
            //
            // The amount is what it takes to put the trunk ON the ground rather than through it: the sink, to undo
            // the planting, plus the trunk's own radius, to stand the log on its side instead of on its axis. Both
            // are the numbers already in use -- TreeSink, and the same per-species TrunkRadius the trunk collider
            // and the stump are built from -- so the log cannot come to rest disagreeing with its own hitbox.
            float _liftSn = Mathf.Sin(Mathf.DegToRad(deg));
            float lift = (ResourceField.TreeSink * Mathf.Max(0.01f, _toppleBase.Basis.Scale.Y)
                        + TrunkRadius * Mathf.Max(0.01f, _toppleBase.Basis.Scale.X)) * _liftSn * _liftSn * _liftSn;
            _debris.GlobalTransform = new Transform3D(rot, p - rot * p + Vector3.Up * lift) * _toppleBase;
        }

        // LEAVES REACT TO THE FALL (strawberry 2026-09-09: "have the leaves react to falling via the wind shader.
        // and a react on impact") -- REWORKED after "i also never saw the leaves move when the tree falls?"
        //
        // The first version was measurably large and perceptually invisible, which is worth writing down. It set
        // the leaf offset DIRECTLY from the canopy's speed: a smooth bulk shear, every leaf displaced the same way,
        // that faded out as the tree slowed. An A/B render (UG_TREELEAF=0) put it at PSNR 25 against the inert
        // build -- a big pixel difference -- and you still cannot see it, for two reasons:
        //   - it is a DISPLACEMENT, not an OSCILLATION. The canopy slides to an offset and creeps back. Nothing
        //     ever moves against anything else, so there is no motion cue; you just see a differently-shaped tree.
        //   - it peaks while the trunk is sweeping through 84 degrees. Against that, a smooth shear is invisible:
        //     the eye has no reference for where the canopy "should" be mid-fall.
        // Leaf motion is only ever visible when the TRUNK IS STILL. So the effect has to survive the landing and
        // ring, not fade out with the fall.
        //
        // So the leaves are a damped SPRING chasing the drag rather than being set to it. While the trunk swings
        // they lag behind it (the old look, unchanged); when it stops, the target snaps to zero and the spring
        // OVERSHOOTS and oscillates about rest -- which is the impact react, out of the same mechanism, bounded by
        // the fall's own lag so it cannot fly apart. The high-frequency per-leaf jitter now rides the spring's
        // SPEED, so the canopy flutters while it is moving instead of sliding as one block.
        const float CanopyLever = 0.75f;    // the leaves sit about three quarters of the way out
        const float DragPerMps = 0.0014f;   // metres of leaf offset per metre of local height, per m/s of canopy
                                            // speed. ⚠ THIS TRACKS THE FALL'S PEAK RATE and has been retuned twice
                                            // for it (0.0016 at 0.698 rad/s, 0.0010 at 1.518, 0.0014 at 1.094), to
                                            // land the lag near 0.018 each time -- under LeafMax, because a clamped
                                            // drag is a CONSTANT one and a constant offset is exactly what read as
                                            // nothing the first time round. Change ToppleAccel and check it again.
        const float LeafFreq = 2.5f;        // Hz -- the ring after it lands; slow enough to read at 30 fps
        const float LeafZeta = 0.12f;       // lightly damped ON PURPOSE: the ring has to outlive the 0.95 s settle,
                                            // because leaf motion only becomes visible once the TRUNK stops moving
        const float ShakeGain = 3.0f;       // spring speed -> the 0..1 per-leaf flutter
        const float LeafMax = 0.03f;        // hard cap on the offset (metres per metre of height), so a coarse
                                            // timestep or a daft trunk length can never launch the canopy
        ShaderMaterial _leafMat;            // the felled canopy's wind material; null until the debris is built
        Vector3 _leafOff, _leafVel;         // the spring: offset per metre of local height, and its rate
        float _lastDeg;

        // A/B seam: UG_TREELEAF=0 renders the identical fall with the leaves inert, so the reaction can be
        // measured ON SCREEN by differencing two runs rather than argued about from the amplitude. This is how
        // the first version was caught being large in pixels and invisible to a viewer.
        static int _leafOn = -1;
        static bool LeafOn => (_leafOn < 0 ? _leafOn = (System.Environment.GetEnvironmentVariable("UG_TREELEAF") == "0" ? 0 : 1) : _leafOn) == 1;

        void LeafReact(float deg, float dt)
        {
            if (_leafMat == null || !LeafOn) return;
            if (dt > 0.0001f)
            {
                // Where the leaves WANT to be: trailing the canopy's own travel. Tangential speed is the trunk's
                // angular rate times how far out the leaves sit, and the drag runs backwards along that tangent --
                // as the top sweeps down and forward the leaves trail up and behind it.
                Vector3 target = Vector3.Zero;
                if (deg > 0f)
                {
                    float omega = Mathf.DegToRad(deg - _lastDeg) / dt;
                    float th = Mathf.DegToRad(deg);
                    Vector3 vel = _fallDir * Mathf.Cos(th) - Vector3.Up * Mathf.Sin(th);   // d(trunk axis)/d(theta)
                    target = -vel * (omega * CanopyLever * _trunkLen * DragPerMps);
                }
                // Semi-implicit (rate first, then offset): stable at the coarse fixed timesteps renders run at,
                // where the plain explicit form of a 2.5 Hz spring starts winding itself up.
                float w = Mathf.Tau * LeafFreq;
                _leafVel += (w * w * (target - _leafOff) - 2f * LeafZeta * w * _leafVel) * dt;
                _leafOff += _leafVel * dt;
                if (_leafOff.Length() > LeafMax) _leafOff = _leafOff.Normalized() * LeafMax;
            }
            _lastDeg = deg;
            _leafMat.SetShaderParameter("gust_dir", _leafOff);
            _leafMat.SetShaderParameter("shake", Mathf.Min(1f, _leafVel.Length() * ShakeGain));
        }

        // THE WHOLE TRUNK REVERBS WHEN IT LANDS (strawberry 2026-09-09: "the whole trunk should reverb when it
        // lands"). The settle already rocks the trunk about its hinge, but that is the whole log swinging rigidly;
        // a landing trunk BENDS -- the butt is pinned by the ground and the far end keeps going, then whips back.
        //
        // Which is the same shader the leaves use, pointed at the other mesh. wind_sway displaces by the world
        // vector `gust_dir` scaled by LOCAL HEIGHT, and the trunk mesh is authored along its own Y from the cut
        // outward, so height IS distance from the stump: the butt barely moves, the tip moves most. That is a
        // cantilever, for free. Kicked vertically on impact and left to ring down.
        //
        // ⚠ sway = 0 on this material. wind_sway's ambient term is height-weighted too, and left at its default a
        // felled trunk would waft in the wind like a fern.
        // ⚠ TUNED AGAINST THE SETTLE, not in isolation, and RE-tuned when the settle changed. The rigid settle
        // swings the whole log about its hinge, which at a pine's 21 m tip dwarfs this flex, so the ring has to
        // outlast it to be seen at all -- but only just. At zeta 0.07 it ran ~2 s, which was right against a 0.95 s
        // settle and is the tree still bouncing long after a 0.40 s one. "Less bounces" is about the landing, not
        // only the rock. Halved the ring to match: ~1 s, still comfortably past the settle.
        const float TrunkFreq = 3.0f;       // Hz -- stiffer than the canopy's 2.5, it is a log
        const float TrunkZeta = 0.14f;      // rings ~1 s; outlasts the settle without dragging on past it
        const float TrunkKick = 0.016f;     // peak offset per metre of local height: ~34 cm at a pine's tip
        const float TrunkUniform = 5.0f;    // ...times this as a UNIFORM push, ~8 cm along the whole log. The flex
                                            // alone is height-weighted off the mesh origin, so it is smallest at
                                            // the butt -- which is the only part of a felled trunk you can still
                                            // see, the rest being under its own canopy. Measured: butt-only crop
                                            // moved at PSNR 45 on flex alone, against 33 for the whole frame.
        ShaderMaterial _trunkMat;
        Vector3 _trunkOff, _trunkVel;
        void TrunkReverb(float dt)
        {
            if (_trunkMat == null || !LeafOn || dt <= 0.0001f) return;
            float w = Mathf.Tau * TrunkFreq;
            _trunkVel += (-w * w * _trunkOff - 2f * TrunkZeta * w * _trunkVel) * dt;   // free ring: no driving term
            _trunkOff += _trunkVel * dt;
            _trunkMat.SetShaderParameter("gust_dir", _trunkOff);
            _trunkMat.SetShaderParameter("gust_base", _trunkOff * TrunkUniform);
        }

        // Load <ResDir>/<TreeName>_<suffix>_<i>.obj + _tex.png as MeshInstance3D children of `parent`, until a part is missing.
        void LoadParts(Node parent, string suffix)
        {
            for (int i = 0; i < 12; i++)
            {
                string objP = ResDir + $"{TreeName}_{suffix}_{i}.obj";
                if (!System.IO.File.Exists(objP)) break;
                var m = ObjMesh.Load(objP);
                if (m == null) break;
                string texP = ResDir + $"{TreeName}_{suffix}_{i}_tex.png";
                // THE FELLED CANOPY IS STILL FOLIAGE. Part 0 is the wide leaf mesh (same convention as the standing
                // tree, where part 0 sways and the thin trunk does not), so it gets the wind material rather than a
                // plain one: the leaves keep swaying once the tree is down, and the fall has something to push them
                // with. The trunk and the stump stay stiff. The shader does its own alpha scissor, which is what the
                // StandardMaterial3D branch below is going to all that trouble to work out.
                if (suffix == "debris" && i == 0)
                {
                    _leafMat = ResourceField.MakeSwayMat(texP);
                    parent.AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = _leafMat });
                    continue;
                }
                if (suffix == "debris" && i == 1)   // the TRUNK: same shader, driven by TrunkReverb, ambient sway OFF
                {
                    _trunkMat = ResourceField.MakeSwayMat(texP);
                    _trunkMat.SetShaderParameter("sway", 0f);   // a log does not waft
                    parent.AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = _trunkMat });
                    continue;
                }
                var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.9f };
                var img = new Image();
                if (ContentProvider.LoadOk(img, texP))
                {
                    // LEAF CUTOUTS (master 2026-09-06: "theres no transparency on the leaves of felled trees").
                    // The STANDING tree gets its alpha scissor from WorldBuilder.MatFor; the stump and the felled
                    // debris are loaded here instead and this material never had one, so every leaf card on a
                    // toppled tree rendered as an opaque rectangle -- the foliage was there, it was just square.
                    // Same test and threshold as MatFor rather than a second opinion: >1% of texels carrying real
                    // transparency means it is a cutout. Checked BEFORE mipmaps, because generating them rewrites
                    // the image and the count would then be read off the wrong data.
                    if (img.GetFormat() == Image.Format.Rgba8)
                    {
                        var data = img.GetData(); int tr = 0;
                        for (int k = 3; k < data.Length; k += 4) if (data[k] < 200) tr++;
                        if (tr > data.Length / 400) { mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; mat.AlphaScissorThreshold = 0.5f; }
                    }
                    img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps;
                }
                parent.AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = mat });
            }
        }

        /// <summary>Test seam: regrow NOW rather than after the reset timer, so a test can prove the stump's
        /// collider leaves with the stump instead of outliving it as an invisible wall at the tree's foot.</summary>
        public void DebugRegrowNow() => Regrow();

        void Regrow()
        {
            if (!IsInstanceValid(this)) return;
            if (IsInstanceValid(_stump)) _stump.QueueFree();   // the stump goes when the tree comes back
            if (IsInstanceValid(_stumpBody)) _stumpBody.QueueFree();   // ...and stops blocking with it, or a regrown tree keeps a ghost stump
            Health = _maxHealth;
            Felled = false;
            Field?.SetAlive(Index, true);   // restores the MultiMesh slot + the trunk's collision layer
        }
    }

    // A metal-ore ROCK's harvest collider (master 2026-08-24: "break em w a pickaxe, drops scrap"). Mirrors TreeTrunk
    // but for the Metal_* resources: a PICKAXE swing (PlayerController gates the tool) drains Health; at 0 the node
    // depletes (SetAlive false), drops Metal Scrap, and regrows after a reset -- retail ResourceManager.damage path.
    // ⚠ Health/reward/reset are tunable defaults; the retail Metal ResourceAsset .dat isn't on the box (same as trees).
    public partial class OreRock : StaticBody3D
    {
        public ResourceField Field;
        public int Index;
        public float Health = 500f;         // ~5 pickaxe hits at Resource_Damage 100 (tunable -- no retail Metal .dat on hand)
        public float Reset = 600f;          // respawn seconds
        public int RewardMin = 2, RewardMax = 4;
        const ushort ScrapItem = 67;        // Metal Scrap
        public bool Mined { get; private set; }
        float _maxHealth;

        public override void _Ready() => _maxHealth = Health;

        // Pickaxe damage (PlayerController gates the tool); depletes the node at 0 -> drops scrap + regrows.
        public void Mine(float amount, Vector3 point, Vector3 dir)
        {
            if (Mined) return;
            Health -= amount;
            if (Health > 0f) return;
            Mined = true;
            Field?.SetAlive(Index, false);   // zero-scale the rock out of its MultiMesh + drop the collider to layer 0
            DropScrap();
            GetTree().CreateTimer(Reset).Timeout += Regrow;
            Log.Print($"[ore] mined #{Index}");
        }

        // Reward_Min..Max Metal Scrap scattered round the node, deterministic per node so peers agree without a wire
        // (SP is MP); item spawning adapted to WorldItem.Spawn (as the trees do).
        void DropScrap()
        {
            var parent = GetParent() ?? (Node)this;
            Vector3 basePos = GlobalTransform.Origin;
            uint h = (uint)Index * 2654435761u; h ^= h >> 15;
            int n = RewardMin + (int)(h % (uint)Mathf.Max(1, RewardMax - RewardMin + 1));
            for (int i = 0; i < n; i++)
            {
                uint hi = h + (uint)(i + 1) * 2246822519u; hi ^= hi >> 13;
                float ang = (hi >> 7) % 628u / 100f, rad = 0.4f + ((hi >> 3) % 100u) / 100f;   // scatter ~0.4-1.4 m
                WorldItem.Spawn(parent, new SDG.Unturned.Item(ScrapItem), basePos + new Vector3(Mathf.Cos(ang) * rad, 0.8f, Mathf.Sin(ang) * rad));
            }
        }

        void Regrow()
        {
            if (!IsInstanceValid(this)) return;
            Health = _maxHealth;
            Mined = false;
            Field?.SetAlive(Index, true);   // restores the MultiMesh slot + the collider's layer
        }
    }
}
