using Godot;
using System;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Testing
{
    /// <summary>Approved cactus asset library, not world spawning or harvesting.
    /// Reads every shipped model through the real ObjMesh loader: no missing-bin skip can make this pass.
    /// The low/broad barrel (variant A) was explicitly removed; remaining indices are preserved.</summary>
    public sealed class CactusModelTests : GameTest
    {
        public override string Name => "content.cactus_models";
        public override double TimeoutSimSeconds => 20;

        static readonly (string Name, int Tris, float Height)[] Models =
        {
            ("Cactus_Column_0", 120, 2.24f), ("Cactus_Column_1", 120, 1.66f), ("Cactus_Column_2", 120, 2.54f),
            ("Cactus_Branched_0", 280, 4.43f), ("Cactus_Branched_1", 200, 4.98f), ("Cactus_Branched_2", 360, 4.01f),
            ("Cactus_Barrel_0", 120, 1.29f), ("Cactus_Barrel_2", 120, 1.67f),
            ("Cactus_Paddles_0", 192, 2.797176f), ("Cactus_Paddles_1", 224, 1.629988f), ("Cactus_Paddles_2", 160, 2.967798f),
            ("Cactus_Cluster_0", 192, 1.87f), ("Cactus_Cluster_1", 128, 1.58f), ("Cactus_Cluster_2", 256, 2.10f),
        };

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/resources");
            string inventory = Path.Combine(dir, "cacti.txt");
            T.Check("cactus inventory exists", File.Exists(inventory));
            if (!File.Exists(inventory)) yield break;
            var names = new HashSet<string>();
            bool rowsOk = true;
            foreach (string line in File.ReadAllLines(inventory))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                rowsOk &= p.Length == 3 && p[1] == "1" && p[2] == "NONE" && names.Add(p[0]);
            }
            bool inventoryOk = rowsOk && names.Count == Models.Length;
            foreach (var spec in Models) inventoryOk &= names.Contains(spec.Name);
            T.Check("exactly the 14 approved single-part models, no substitutes", inventoryOk);
            T.Check("removed low/broad Barrel_1 has no mesh or texture left behind",
                !File.Exists(Path.Combine(dir, "Cactus_Barrel_1_0.obj"))
                && !File.Exists(Path.Combine(dir, "Cactus_Barrel_1_0_tex.png")));

            foreach (var spec in Models)
            {
                string obj = Path.Combine(dir, spec.Name + "_0.obj");
                string tex = Path.Combine(dir, spec.Name + "_0_tex.png");
                T.Check(spec.Name + ": native mesh and texture files exist", File.Exists(obj) && File.Exists(tex));
                if (!File.Exists(obj) || !File.Exists(tex)) continue;
                var mesh = ObjMesh.Load(obj);
                T.Check(spec.Name + ": real loader produces one nonempty surface", mesh != null && mesh.GetSurfaceCount() == 1);
                if (mesh == null || mesh.GetSurfaceCount() != 1) continue;
                var arrays = mesh.SurfaceGetArrays(0);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                T.Check(spec.Name + ": complete expected geometry, not an empty/fallback mesh", vertices.Length == spec.Tris * 3);
                var box = mesh.GetAabb();
                T.Check(spec.Name + ": Y-up height and buried root datum survive loading",
                    Mathf.Abs(box.End.Y - spec.Height) < .001f && box.Position.Y >= -.081f && box.Position.Y <= -.039f);
                bool attributes = normals.Length == vertices.Length && uv.Length == vertices.Length;
                for (int i = 0; i < Math.Min(normals.Length, uv.Length); i++)
                {
                    var n = normals[i]; var u = uv[i];
                    attributes &= float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z)
                        && Mathf.Abs(n.Length() - 1f) < .0001f
                        && u.X >= 0f && u.X <= 1f && u.Y >= 0f && u.Y <= 1f
                        && Mathf.FloorToInt(u.X * 8f) != 5; // no pale spike-colour sample
                }
                T.Check(spec.Name + ": explicit normals and spike-free palette UVs survive loading", attributes);
                var image = new Image();
                bool loaded = ContentProvider.LoadOk(image, tex);
                bool palette = loaded && image.GetWidth() == 32 && image.GetHeight() == 4;
                if (palette)
                {
                    var green = image.GetPixel(2, 2);
                    palette = Mathf.Abs(green.R * 255f - 54f) < .5f && Mathf.Abs(green.G * 255f - 109f) < .5f
                        && Mathf.Abs(green.B * 255f - 55f) < .5f && green.A > .99f;
                }
                T.Check(spec.Name + ": real texture decoder reads the foliage palette, not a fallback", palette);
                T.Check(spec.Name + ": model shipping did not add world placement bins", !File.Exists(Path.Combine(dir, spec.Name + ".bin")));
                image.Dispose();
            }
            yield return Ticks(1);
        }
    }
}