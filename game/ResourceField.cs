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
            if (!File.Exists(manifest)) { GD.Print("[resources] no resources.txt -- skipping"); return; }
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
                        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.5f * sr, Height = 8f * sh }, Position = new Vector3(0f, 2.5f * sh, 0f) });
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
                            var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = lst.Count };
                            for (int k = 0; k < lst.Count; k++)
                            {
                                mm.SetInstanceTransform(k, xf[lst[k]]);
                                recs[lst[k]].Slots.Add((mm, k));
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
                GD.Print($"[resources] {name}: {xf.Count} x {parts} part(s)");
            }
            GD.Print($"[resources] {total} instances across {types} types (MultiMesh), {treeCols} tree trunk colliders");
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
                GD.Print($"[imposter] {spec.Name}: {spec.Xf.Count} billboards, on at {spec.RealCull * ImpostorOverlap:0}m, real trees off at {spec.RealCull:0}m, out to {ImpostorRange:0}m");
            }
            ImpostorInstancesForTest = made;
            GD.Print($"[imposter] {made} billboards across {_pendingImpostors.Count} species");
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
            if (!HasAnyOpaque(img)) { GD.PrintErr($"[imposter] {spec.Name}: bake came out empty, skipping"); return (null, 0f, 0f); }
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
        internal const float TreeSink = 0.2f;

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
                if (img.Load(texPath) == Error.Ok)
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
        static ShaderMaterial MakeSwayMat(string texPath)
        {
            _swayShader ??= GD.Load<Shader>("res://content/wind_sway.gdshader");
            var m = new ShaderMaterial { Shader = _swayShader };
            if (File.Exists(texPath))
            {
                var img = new Image();
                if (img.Load(texPath) == Error.Ok) { img.GenerateMipmaps(); m.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(img)); }
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
        public ushort LogItem;                          // Birch 37 / Maple 39 / Pine 41
        ushort StickItem => (ushort)(LogItem + 1);      // catalog pairs Log then Stick: 38 / 40 / 42
        public float Health = 800f;                     // retail ResourceAsset health (set per-species by ResourceField)
        public float Reset = 450f;                       // retail asset.reset -- respawn seconds (per-species)
        public int RewardMin = 7, RewardMax = 10;        // retail Reward_Min/Max -- total items dropped (60% log / 40% stick)
        public bool Felled { get; private set; }
        float _maxHealth;

        public override void _Ready() => _maxHealth = Health;

        // Gun/melee damage; fells the tree once Health hits 0.
        public void Chop(float amount, Vector3 point, Vector3 dir)
        {
            if (Felled) return;
            Health -= amount;
            if (Health > 0f) return;
            Felled = true;
            Field?.SetAlive(Index, false);   // zero-scale the tree out of its MultiMesh + drop the trunk collider to layer 0
            SpawnStump();                     // retail stumpGameObject: a stump is left where the tree stood
            SpawnDebris(dir);                 // retail debrisGameObject: a falling tree that topples + is cleaned up
            DropRewards();
            GetTree().CreateTimer(Reset).Timeout += Regrow;   // retail asset.reset: it grows back
            GD.Print($"[tree] felled #{Index}");
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
                float ang = (hi >> 7) % 628u / 100f, rad = 0.4f + ((hi >> 3) % 100u) / 100f;   // scatter ~0.4-1.4 m around the stump
                WorldItem.Spawn(parent, new SDG.Unturned.Item(item), basePos + new Vector3(Mathf.Cos(ang) * rad, 0.8f, Mathf.Sin(ang) * rad));
            }
        }

        Node3D _stump;

        // Retail stumpGameObject: the stump meshes stay where the tree stood until it regrows.
        void SpawnStump()
        {
            _stump = new Node3D();
            AddChild(_stump);
            LoadParts(_stump, "stump");
            _stump.GlobalTransform = TreeXf;
        }

        // Retail debrisGameObject: the felled tree topples in the chop direction, then is cleaned up. Retail uses a
        // RigidBody; a deterministic rotate-about-the-base reads the same and survives coarse render timesteps (a
        // rigidbody integrated at --fixed-fps 1 explodes + spins about its centre of mass, not the trunk base).
        Node3D _debris;
        bool _toppling;
        float _topple;                       // 0..1 fall progress
        const float ToppleTime = 1.3f;       // seconds to lie down
        Vector3 _toppleAxis = Vector3.Right; // horizontal axis; set from the chop direction
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
            _toppleAxis = Vector3.Up.Cross(fall).Normalized();   // top topples toward `fall`
            _topple = 0f; _toppling = true;
            GetTree().CreateTimer(7.0).Timeout += () => { if (GodotObject.IsInstanceValid(_debris)) _debris.QueueFree(); };   // clean up the fallen tree
        }

        public override void _Process(double delta)
        {
            if (!_toppling || !GodotObject.IsInstanceValid(_debris)) return;
            _topple = Mathf.Min(1f, _topple + (float)delta / ToppleTime);
            float ang = Mathf.DegToRad(84f) * (_topple * _topple);   // ease-in: gravity accelerates the fall
            var rot = new Basis(_toppleAxis, ang);
            Vector3 p = _toppleBase.Origin;                          // pivot about the stump/base, in WORLD space
            _debris.GlobalTransform = new Transform3D(rot, p - rot * p) * _toppleBase;
            if (_topple >= 1f) _toppling = false;
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
                var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.9f };
                var img = new Image();
                if (img.Load(ResDir + $"{TreeName}_{suffix}_{i}_tex.png") == Error.Ok) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps; }
                parent.AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = mat });
            }
        }

        void Regrow()
        {
            if (!IsInstanceValid(this)) return;
            if (IsInstanceValid(_stump)) _stump.QueueFree();   // the stump goes when the tree comes back
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
            GD.Print($"[ore] mined #{Index}");
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
