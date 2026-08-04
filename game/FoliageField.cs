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

        /// <summary>The grass-displacement material. Falls back to null (and therefore to the plain lit material) if
        /// the shader is missing, so a bad path costs the EFFECT rather than the grass.</summary>
        static ShaderMaterial MakeGrassMaterial()
        {
            var sh = GD.Load<Shader>("res://content/grass_displace.gdshader");
            if (sh == null) { GD.PrintErr("[foliage] grass_displace.gdshader missing -- grass will not displace"); return null; }
            return new ShaderMaterial { Shader = sh };
        }

        /// <summary>Small-pebble size multiplier (master: "scale down the small pebbles foliage by 25% globally").
        /// 0.75 = 25% smaller, applied to every pebble instance's baked basis.</summary>
        const float PebbleScale = 0.75f;

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
            // GRASS ONLY gets the displacement shader (master: "lets add grass displacement, grass only"). Flowers and
            // pebbles keep the plain StandardMaterial3D -- a pebble that bends when you walk past would be worse than
            // no effect at all, and flowers were not asked for.
            bool isGrass = nm.StartsWith("grass");
            ShaderMaterial grassMat = isGrass ? MakeGrassMaterial() : null;

            string tp = dir + nm + "_tex.png";
            if (File.Exists(tp))
            {
                var img = new Image();
                if (img.Load(tp) == Error.Ok)
                {
                    img.GenerateMipmaps();
                    var tex = ImageTexture.CreateFromImage(img);
                    mat.AlbedoTexture = tex;
                    grassMat?.SetShaderParameter("albedo_tex", tex);
                    // master: GRASS + FLOWERS get bilinear (smoother blades/petals); pebbles (+ the rest of the port) stay Nearest.
                    mat.TextureFilter = (nm.StartsWith("grass") || nm.StartsWith("flowers"))
                        ? BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps
                        : BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                }
            }
            else mat.AlbedoColor = SolidColor.TryGetValue(nm, out var c) ? c : new Color(0.5f, 0.5f, 0.5f);

            // SMALL PEBBLES 25% SMALLER (master). Folded into the baked instance basis at parse rather than applied
            // to the mesh or the MultiMesh, because these transforms come straight out of PEI's Foliage.blob and the
            // blob is the only place the per-instance scale lives -- scaling the shared mesh would also shrink any
            // future prop reusing it, and MultiMesh has no node-level scale of its own to reach for.
            bool isPebble = nm.StartsWith("pebble");

            using var br = new BinaryReader(File.OpenRead(binPath));
            int count = br.ReadInt32();
            if (count <= 0) { GD.Print($"[foliage] {nm}: 0 instances"); return; }
            // Bucket into spatial CELLS -> one MultiMesh per cell, each with a distance cutoff, so foliage far from
            // the camera stops rendering (master: cull grass far from the player). Trees aren't foliage, untouched.
            // Retail draws foliage in 32m TILES out to FoliageSettings.drawDistance tiles, set per quality:
            // LOW 2, MEDIUM 3, HIGH 4, ULTRA 5 (GraphicsSettings.ApplyFoliageQuality) -- so ULTRA is
            // 5 * FoliageSystem.TILE_SIZE(32) = 160m. The old hand-picked 170 was already almost exactly
            // retail's max; this just makes it the source's number rather than a coincidence.
            // (Foliage sits on the SKY render layer in retail so the per-layer cull does NOT bound it --
            // the tile draw distance is the whole rule.)
            const float Cell = 96f, CullRange = 5 * 32f;
            var byCell = new System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<Transform3D>>();
            for (int i = 0; i < count; i++)
            {
                // 12 floats: Unity basis cols X/Y/Z then pos. Unity(LH)->Godot(RH) = negate Z on each axis' z + pos.z.
                float x0 = br.ReadSingle(), x1 = br.ReadSingle(), x2 = br.ReadSingle();
                float y0 = br.ReadSingle(), y1 = br.ReadSingle(), y2 = br.ReadSingle();
                float z0 = br.ReadSingle(), z1 = br.ReadSingle(), z2 = br.ReadSingle();
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                var basis = new Basis(new Vector3(x0, x1, -x2), new Vector3(y0, y1, -y2), new Vector3(-z0, -z1, z2));
                if (isPebble) basis = basis.Scaled(Vector3.One * PebbleScale);   // master: small pebbles 25% smaller
                var pos = new Vector3(px, py, -pz);
                var key = ((int)Mathf.Floor(pos.X / Cell), (int)Mathf.Floor(pos.Z / Cell));
                if (!byCell.TryGetValue(key, out var lst)) { lst = new System.Collections.Generic.List<Transform3D>(); byCell[key] = lst; }
                lst.Add(new Transform3D(basis, pos));
            }
            foreach (var kv in byCell)
            {
                var lst = kv.Value;
                var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = lst.Count };
                for (int k = 0; k < lst.Count; k++) mm.SetInstanceTransform(k, lst[k]);   // scale already folded in at parse (see PebbleScale)
                var fmi = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = (Material)grassMat ?? mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = CullRange,   // cell culls when the camera is beyond CullRange from it
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled };
                // The grass/flowers bilinear set above is chosen per material and must survive the scene-wide
                // NearestFilter sweep, which runs after the world is assembled and would otherwise stamp it back.
                // Pebbles are in this group too -- their Nearest is equally deliberate, and the sweep setting it
                // "correctly" by accident is not the same as it being chosen here.
                fmi.AddToGroup(NearestFilter.KeepFilterGroup);
                AddChild(fmi);
            }
            GD.Print($"[foliage] {nm}: {count} instances in {byCell.Count} cells (culled beyond {CullRange}m)");
        }
    }
}
