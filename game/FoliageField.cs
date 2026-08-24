using Godot;
using System.IO;

namespace UnturnedGodot
{
    // A map's baked FOLIAGE (Maps/<map>/Foliage.blob) as MultiMesh instances: grass + flowers + pebbles.
    // NOTE: trees are NOT foliage -- they're the map's Resources (separate pipeline). tools/foliage_map.py reads
    // the blob's per-asset GUIDs (.NET System.Guid order), resolves each to its FoliageInstancedMeshInfoAsset,
    // and bakes the FULL per-instance transform (9 basis + 3 pos = 12 floats) into content/<MapDir>/<name>.bin.
    // Unity(LH)->Godot(RH) = negate Z. MAP-AWARE: each map has DIFFERENT types (PEI grass+4 flowers+2 pebbles;
    // Washington grass_00/01 + pebble_00/shore), so we DISCOVER them from the dir instead of a fixed list.
    public partial class FoliageField : Node3D
    {
        // Set by Main per map: PEI -> "foliage", others -> "foliage_<key>". A map with no baked dir gets NO
        // foliage (better a bare field than another map's grass at the wrong heights).
        public static string MapDir = "foliage";

        /// <summary>The grass-displacement material. Falls back to null (and therefore to the plain lit material) if
        /// the shader is missing, so a bad path costs the EFFECT rather than the grass.</summary>
        static ShaderMaterial MakeGrassMaterial()
        {
            var sh = GD.Load<Shader>("res://content/grass_displace.gdshader");
            if (sh == null) { GD.PrintErr("[foliage] grass_displace.gdshader missing -- grass will not displace"); return null; }
            GrassDisplacers.EnsureGlobals();   // the grass shader's globals MUST exist BEFORE this material is built, or it links them invalid ("removed at some point") + renders with NO displacement at all
            return new ShaderMaterial { Shader = sh };
        }

        /// <summary>The up-normal material for flowers + pebbles -- same look as the plain StandardMaterial3D below,
        /// but with the cull_disabled back-face normal forced world-up so it isn't permanently dark (the non-grass
        /// side of the grass fix). Falls back to null (and therefore to the plain lit material) if the shader is
        /// missing, so a bad path costs the EFFECT rather than the foliage.</summary>
        static ShaderMaterial MakeFoliageUpMaterial()
        {
            var sh = GD.Load<Shader>("res://content/foliage_up.gdshader");
            if (sh == null) { GD.PrintErr("[foliage] foliage_up.gdshader missing -- flowers/pebbles keep the dark-backface bug"); return null; }
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

        // kept the name LoadGrass() so the Main.cs call site is unchanged; it now discovers + loads every baked
        // foliage type for the current map (glob the map's dir), or nothing if the map has none.
        public void LoadGrass()
        {
            string dir = ProjectSettings.GlobalizePath($"res://content/{MapDir}/");
            if (!Directory.Exists(dir)) { GD.Print($"[foliage] no baked foliage for this map ({MapDir}) -- skipping"); return; }
            foreach (var bin in Directory.GetFiles(dir, "*.bin"))
                LoadType(Path.GetFileNameWithoutExtension(bin));
        }

        void LoadType(string nm)
        {
            string dir = ProjectSettings.GlobalizePath($"res://content/{MapDir}/");
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
            // pebbles get the non-displacing foliage_up shader instead (up-normal fix, no bend) -- a pebble that bends
            // when you walk past would be worse than no effect at all, and flowers were not asked for.
            bool isGrass = nm.StartsWith("grass");
            ShaderMaterial grassMat = isGrass ? MakeGrassMaterial() : null;
            // FLOWERS + PEBBLES get the same up-normal fix via their own shader (see MakeFoliageUpMaterial) --
            // no displacement, just the cull_disabled backface-darkness correction that grass already has.
            ShaderMaterial foliageUpMat = isGrass ? null : MakeFoliageUpMaterial();
            foliageUpMat?.SetShaderParameter("do_sway", nm.StartsWith("flowers"));   // master: flowers sway in the wind, pebbles stay put

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
                    if (foliageUpMat != null)
                    {
                        foliageUpMat.SetShaderParameter("albedo_tex", tex);
                        foliageUpMat.SetShaderParameter("use_texture", true);
                    }
                    // master: GRASS + FLOWERS get bilinear (smoother blades/petals); pebbles (+ the rest of the port) stay Nearest.
                    mat.TextureFilter = (nm.StartsWith("grass") || nm.StartsWith("flowers"))
                        ? BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps
                        : BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                }
            }
            else
            {
                var solid = SolidColor.TryGetValue(nm, out var c) ? c : new Color(0.5f, 0.5f, 0.5f);
                mat.AlbedoColor = solid;
                if (foliageUpMat != null)
                {
                    foliageUpMat.SetShaderParameter("albedo_color", solid);
                    foliageUpMat.SetShaderParameter("use_texture", false);
                }
            }

            // SMALL PEBBLES 25% SMALLER (master). Folded into the baked instance basis at parse rather than applied
            // to the mesh or the MultiMesh, because these transforms come straight out of PEI's Foliage.blob and the
            // blob is the only place the per-instance scale lives -- scaling the shared mesh would also shrink any
            // future prop reusing it, and MultiMesh has no node-level scale of its own to reach for.
            bool isPebble = nm.StartsWith("pebble");

            using var br = new BinaryReader(File.OpenRead(binPath));
            // FORMAT, v1 AND v2. v1 (every file the python bakers have ever written) is `int32 count` then 12
            // floats per instance. v2 adds a per-instance flag saying whether a human placed it, which the editor
            // needs so a re-bake cannot wipe hand-placed foliage -- retail draws exactly that distinction
            // (`clearWhenBaked = false; // Manually placed, should not be cleared`).
            //
            // Detected by SIGN rather than by a magic string: a v1 count is always >= 0, so a negative first int
            // cannot be a v1 file and is free to mean "header follows". That keeps every existing baked .bin
            // loading untouched -- a format break here would mean re-baking every map to gain a flag.
            int count = br.ReadInt32();
            int version = 1;
            if (count < 0) { version = br.ReadInt32(); count = br.ReadInt32(); }
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
            var manualByCell = new System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<bool>>();
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
                bool manual = version >= 2 && br.ReadByte() != 0;   // v1 predates the flag: everything in it was baked
                if (!byCell.TryGetValue(key, out var lst)) { lst = new System.Collections.Generic.List<Transform3D>(); byCell[key] = lst; }
                if (!manualByCell.TryGetValue(key, out var mfl)) { mfl = new System.Collections.Generic.List<bool>(); manualByCell[key] = mfl; }
                lst.Add(new Transform3D(basis, pos));
                mfl.Add(manual);
            }
            foreach (var kv in byCell)
            {
                var lst = kv.Value;
                var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = lst.Count };
                for (int k = 0; k < lst.Count; k++) mm.SetInstanceTransform(k, lst[k]);   // scale already folded in at parse (see PebbleScale)
                var fmi = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = (Material)(isGrass ? grassMat : foliageUpMat) ?? mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = CullRange,   // cell culls when the camera is beyond CullRange from it
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled };
                // The grass/flowers bilinear set above is chosen per material and must survive the scene-wide
                // NearestFilter sweep, which runs after the world is assembled and would otherwise stamp it back.
                // Pebbles are in this group too -- their Nearest is equally deliberate, and the sweep setting it
                // "correctly" by accident is not the same as it being chosen here.
                fmi.AddToGroup(NearestFilter.KeepFilterGroup);
                AddChild(fmi);
                RegisterAuthoringCell(nm, kv.Key, fmi, mesh, (Material)(isGrass ? grassMat : foliageUpMat) ?? mat, lst, manualByCell[kv.Key]);
            }
            GD.Print($"[foliage] {nm}: {count} instances in {byCell.Count} cells (culled beyond {CullRange}m)");
        }
    }
}
