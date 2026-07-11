using Godot;
using System.IO;

namespace UnturnedGodot
{
    // PEI's baked FOLIAGE (Maps/PEI/Foliage.blob) placed as MultiMesh instances. First pass = grass
    // (blob asset 1, 612K instances). Positions baked by tools/foliage_export.py -> content/foliage/grass.bin;
    // mesh Grass_00.obj + Grass_00_Albedo.png both ripped from core.masterbundle via UnityPy. Blob world coords
    // match the objects (centered +-1024), so placement is negate-Z: godot = (x, y, -z). Trees follow once mapped.
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
                float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(x, y, -z)));   // negate-Z, identity rotation (grass tufts ~radially symmetric)
            }
            AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat });
            GD.Print($"[foliage] placed {count} grass instances (MultiMesh, 1 draw call)");
        }
    }
}
