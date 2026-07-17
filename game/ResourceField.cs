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
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            string manifest = dir + "resources.txt";
            if (!File.Exists(manifest)) { GD.Print("[resources] no resources.txt -- skipping"); return; }
            int total = 0, types = 0, treeCols = 0;
            foreach (var line in File.ReadAllLines(manifest))
            {
                var sp = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 2 || !int.TryParse(sp[1], out int parts)) continue;
                string name = sp[0];
                string holiday = sp.Length >= 3 ? sp[2] : "NONE";   // Cane_00(candy cane)/Snow_Pile_00/Ornament_XMAS are CHRISTMAS-only
                if (holiday != "NONE" && holiday != activeHoliday) continue;   // out-of-season resource (same gate as the objects)
                bool isTree = name.StartsWith("Birch") || name.StartsWith("Maple") || name.StartsWith("Pine");   // only trees cast shadows
                string binPath = dir + name + ".bin";
                if (!File.Exists(binPath)) continue;
                var xf = ReadInstances(binPath);
                if (xf.Count == 0) continue;
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
                    for (int k = 0; k < xf.Count; k++)
                    {
                        var t = xf[k];
                        // part-0's mesh AABB is the WHOLE tree (incl. canopy) -> that gave a giant ~5m-radius cylinder
                        // floating at canopy height that missed the ground. Use a FIXED trunk (~0.5m radius, ~8m tall) at
                        // the base, scaled by the instance scale, on an ORTHONORMAL body (Jolt drops non-uniform-scaled shapes).
                        Vector3 sc = t.Basis.Scale;
                        float sr = Mathf.Max(Mathf.Abs(sc.X), Mathf.Abs(sc.Z)), sh = Mathf.Abs(sc.Y);
                        var body = new StaticBody3D { CollisionLayer = 1u << 0, Transform = new Transform3D(t.Basis.Orthonormalized(), t.Origin) };
                        body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Wood);
                        body.AddToGroup("tree");   // for the UG_TREECHECK raycast self-test
                        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.5f * sr, Height = 8f * sh }, Position = new Vector3(0f, 2.5f * sh, 0f) });
                        AddChild(body);
                        recs[k].Trunk = body;
                        recs[k].TrunkLayer = body.CollisionLayer;
                        treeCols++;
                    }
                }
                if (VisualInstances)
                {
                    // Bucket instances into spatial CELLS so each chunk frustum-culls independently (behind the player) + distance-culls,
                    // instead of one map-wide MultiMesh that's never culled. Trees keep their shadows within range (master); props cull closer.
                    const float Cell = 64f;
                    float cullRange = isTree ? 320f : 180f;
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
                        var mat = MakeMat(dir + name + "_" + i + "_tex.png", !isTree);
                        foreach (var kv in byCell)
                        {
                            var lst = kv.Value;
                            var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = lst.Count };
                            for (int k = 0; k < lst.Count; k++)
                            {
                                mm.SetInstanceTransform(k, xf[lst[k]]);
                                recs[lst[k]].Slots.Add((mm, k));
                            }
                            AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat,
                                CastShadow = isTree ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                                VisibilityRangeEnd = cullRange, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled });
                        }
                    }
                }
                total += xf.Count; types++;
                GD.Print($"[resources] {name}: {xf.Count} x {parts} part(s)");
            }
            GD.Print($"[resources] {total} instances across {types} types (MultiMesh), {treeCols} tree trunk colliders");
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
                    mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                }
            }
            else mat.AlbedoColor = new Color(0.35f, 0.45f, 0.28f);   // leafy-green fallback
            return mat;
        }
    }
}
