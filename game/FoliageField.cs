using Godot;
using System.IO;

namespace UnturnedGodot
{
    // PEI's baked FOLIAGE (Maps/PEI/Foliage.blob) as MultiMesh instances: grass + 4 flowers + 2 pebbles
    // (blob assets 0-6). NOTE: trees are NOT foliage -- they're the map's Resources (separate pipeline).
    // tools/foliage_all.py resolves each type's FoliageInstancedMeshInfoAsset (.asset, matched by the blob's
    // 16-byte GUID) -> its mesh + texture by container path, and bakes the blob's FULL per-instance transform
    // (9 basis + 3 pos = 12 floats) into content/foliage/<name>.bin. Unity(LH)->Godot(RH) = negate Z.
    public partial class FoliageField : Node3D
    {
        static readonly string[] Types =
            { "grass_00", "flowers_00", "flowers_01", "flowers_02", "flowers_03", "pebble_00", "pebble_sand_00" };

        // pebble materials are textureless solid-colour rocks -- real _Color from the .mat (source-accurate).
        static readonly System.Collections.Generic.Dictionary<string, Color> SolidColor = new()
        {
            { "pebble_00", new Color(0.456f, 0.456f, 0.456f) },
            { "pebble_sand_00", new Color(0.506f, 0.506f, 0.506f) },
        };

        // kept the name LoadGrass() so the Main.cs call site is unchanged; it now loads every foliage type.
        public void LoadGrass()
        {
            foreach (var nm in Types) LoadType(nm);
        }

        void LoadType(string nm)
        {
            string dir = ProjectSettings.GlobalizePath("res://content/foliage/");
            string binPath = dir + nm + ".bin", objPath = dir + nm + ".obj";
            if (!File.Exists(binPath) || !File.Exists(objPath)) { GD.Print($"[foliage] skip {nm} (missing files)"); return; }
            var mesh = ObjMesh.Load(objPath);
            if (mesh == null) { GD.Print($"[foliage] skip {nm} (mesh load failed)"); return; }

            var mat = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.4f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // billboards are double-sided
                Roughness = 1f,
                // foliage is LIT + receives shadows (master), but the mesh normals are baked straight UP (tools set vn=0,1,0)
                // so the flat billboards are lit like ground -- no ugly per-face directional darkness.
            };
            string tp = dir + nm + "_tex.png";
            if (File.Exists(tp))
            {
                var img = new Image();
                if (img.Load(tp) == Error.Ok)
                {
                    img.GenerateMipmaps();
                    mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                    mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;   // NN like the rest of the port
                }
            }
            else mat.AlbedoColor = SolidColor.TryGetValue(nm, out var c) ? c : new Color(0.5f, 0.5f, 0.5f);

            using var br = new BinaryReader(File.OpenRead(binPath));
            int count = br.ReadInt32();
            if (count <= 0) { GD.Print($"[foliage] {nm}: 0 instances"); return; }
            // Bucket into spatial CELLS -> one MultiMesh per cell, each with a distance cutoff, so foliage far from
            // the camera stops rendering (master: cull grass far from the player). Trees aren't foliage, untouched.
            const float Cell = 96f, CullRange = 170f;
            var byCell = new System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<Transform3D>>();
            for (int i = 0; i < count; i++)
            {
                // 12 floats: Unity basis cols X/Y/Z then pos. Unity(LH)->Godot(RH) = negate Z on each axis' z + pos.z.
                float x0 = br.ReadSingle(), x1 = br.ReadSingle(), x2 = br.ReadSingle();
                float y0 = br.ReadSingle(), y1 = br.ReadSingle(), y2 = br.ReadSingle();
                float z0 = br.ReadSingle(), z1 = br.ReadSingle(), z2 = br.ReadSingle();
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                var basis = new Basis(new Vector3(x0, x1, -x2), new Vector3(y0, y1, -y2), new Vector3(-z0, -z1, z2));
                var pos = new Vector3(px, py, -pz);
                var key = ((int)Mathf.Floor(pos.X / Cell), (int)Mathf.Floor(pos.Z / Cell));
                if (!byCell.TryGetValue(key, out var lst)) { lst = new System.Collections.Generic.List<Transform3D>(); byCell[key] = lst; }
                lst.Add(new Transform3D(basis, pos));
            }
            foreach (var kv in byCell)
            {
                var lst = kv.Value;
                var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = lst.Count };
                for (int k = 0; k < lst.Count; k++) mm.SetInstanceTransform(k, lst[k]);
                AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = CullRange,   // cell culls when the camera is beyond CullRange from it
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled });
            }
            GD.Print($"[foliage] {nm}: {count} instances in {byCell.Count} cells (culled beyond {CullRange}m)");
        }
    }
}
