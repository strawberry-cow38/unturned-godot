using Godot;
using System.IO;

namespace UnturnedGodot
{
    // PEI's baked FOLIAGE (Maps/PEI/Foliage.blob) placed as MultiMesh instances. Grass = blob asset 1
    // (GUID c928fb99..., 612K instances). tools/foliage_fix.py bakes the FULL per-blade transform (basis+pos,
    // 12 floats) -> content/foliage/grass.bin; mesh + PEI texture resolved by the container paths the real
    // FoliageInstancedMeshInfoAsset references (Grass_00_Mesh.fbx + pei/grass_00.png) and ripped via UnityPy.
    // Blob world coords match the objects (centered +-1024). NOTE: trees are NOT foliage -- PEI's blob is only
    // grass + 4 flower types + 2 pebble types; trees live in the map's Resources (separate pipeline, TODO).
    public partial class FoliageField : Node3D
    {
        public void LoadGrass()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/foliage/");
            string binPath = dir + "grass.bin";
            if (!File.Exists(binPath)) { GD.Print("[foliage] no grass.bin -- skipping"); return; }
            var mesh = ObjMesh.Load(dir + "Grass_00.obj");
            if (mesh == null) { GD.Print("[foliage] no Grass_00.obj -- skipping"); return; }

            var mat = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.4f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // grass blades are double-sided
                Roughness = 1f,
            };
            string tp = dir + "Grass_00_Albedo.png";
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
            else mat.AlbedoColor = new Color(0.36f, 0.55f, 0.25f);

            using var br = new BinaryReader(File.OpenRead(binPath));
            int count = br.ReadInt32();
            var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = count };
            for (int i = 0; i < count; i++)
            {
                // 12 floats = the blob's BAKED transform: Unity basis columns X(x0,x1,x2) Y(y0,y1,y2) Z(z0,z1,z2)
                // then position. The bake stamps each blade with a random Y-spin (0-360 deg) + uniform scale
                // (0.8-1.2) per the FoliageInstancedMeshInfoAsset, so we must keep it -- identity looked stiff.
                // Unity(LH) -> Godot(RH) = negate Z: basisX=(x0,x1,-x2) basisY=(y0,y1,-y2) basisZ=(-z0,-z1,z2), pos.z=-pz.
                float x0 = br.ReadSingle(), x1 = br.ReadSingle(), x2 = br.ReadSingle();
                float y0 = br.ReadSingle(), y1 = br.ReadSingle(), y2 = br.ReadSingle();
                float z0 = br.ReadSingle(), z1 = br.ReadSingle(), z2 = br.ReadSingle();
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                var basis = new Basis(
                    new Vector3(x0, x1, -x2),
                    new Vector3(y0, y1, -y2),
                    new Vector3(-z0, -z1, z2));
                mm.SetInstanceTransform(i, new Transform3D(basis, new Vector3(px, py, -pz)));
            }
            AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat });
            GD.Print($"[foliage] placed {count} grass instances (MultiMesh, 1 draw call)");
        }
    }
}
