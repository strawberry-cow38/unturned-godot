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
    public partial class ResourceField : Node3D
    {
        public void LoadResources()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            string manifest = dir + "resources.txt";
            if (!File.Exists(manifest)) { GD.Print("[resources] no resources.txt -- skipping"); return; }
            int total = 0, types = 0;
            foreach (var line in File.ReadAllLines(manifest))
            {
                var sp = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 2 || !int.TryParse(sp[1], out int parts)) continue;
                string name = sp[0];
                string binPath = dir + name + ".bin";
                if (!File.Exists(binPath)) continue;
                var xf = ReadInstances(binPath);
                if (xf.Count == 0) continue;
                for (int i = 0; i < parts; i++)
                {
                    string objP = dir + name + "_" + i + ".obj";
                    if (!File.Exists(objP)) continue;
                    var mesh = ObjMesh.Load(objP);
                    if (mesh == null) continue;
                    var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = xf.Count };
                    for (int k = 0; k < xf.Count; k++) mm.SetInstanceTransform(k, xf[k]);
                    AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = MakeMat(dir + name + "_" + i + "_tex.png") });
                }
                total += xf.Count; types++;
                GD.Print($"[resources] {name}: {xf.Count} x {parts} part(s)");
            }
            GD.Print($"[resources] {total} instances across {types} types (MultiMesh)");
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

        static StandardMaterial3D MakeMat(string texPath)
        {
            var mat = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.4f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // leaves are double-sided billboards
                Roughness = 1f,
            };
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
