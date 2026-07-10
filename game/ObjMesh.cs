using Godot;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot
{
    // Minimal runtime Wavefront .obj loader for the extracted Unturned object meshes
    // (UnityPy mesh.export() = raw Unity coords). Converts to Godot: negate Z + reverse
    // winding, matching the terrain's (x,y,z)->(x,y,-z) convention. UVs are V-flipped
    // (Unity V-up -> Godot V-down).
    public static class ObjMesh
    {
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public static ArrayMesh Load(string globalPath)
        {
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var outV = new List<Vector3>();
            var outN = new List<Vector3>();
            var outU = new List<Vector2>();
            foreach (var raw in System.IO.File.ReadLines(globalPath))
            {
                if (raw.Length < 2) continue;
                if (raw[0] == 'v' && raw[1] == ' ')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    pos.Add(new Vector3(F(p[1]), F(p[2]), -F(p[3])));
                }
                else if (raw[0] == 'v' && raw[1] == 'n')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    nrm.Add(new Vector3(F(p[1]), F(p[2]), -F(p[3])));
                }
                else if (raw[0] == 'v' && raw[1] == 't')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    uv.Add(new Vector2(F(p[1]), 1f - F(p[2])));   // Unity V-up -> Godot V-down
                }
                else if (raw[0] == 'f' && raw[1] == ' ')
                {
                    var p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                    int n = p.Length - 1;
                    var vi = new int[n]; var ti = new int[n]; var ni = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        var s = p[i + 1].Split('/');
                        vi[i] = int.Parse(s[0]) - 1;
                        ti[i] = (s.Length > 1 && s[1].Length > 0) ? int.Parse(s[1]) - 1 : -1;
                        ni[i] = (s.Length > 2 && s[2].Length > 0) ? int.Parse(s[2]) - 1 : -1;
                    }
                    for (int i = 1; i + 1 < n; i++)   // fan triangulate, reversed winding (0, i+1, i)
                    {
                        foreach (int k in new[] { 0, i + 1, i })
                        {
                            outV.Add(pos[vi[k]]);
                            outN.Add(ni[k] >= 0 && ni[k] < nrm.Count ? nrm[ni[k]] : Vector3.Up);
                            outU.Add(ti[k] >= 0 && ti[k] < uv.Count ? uv[ti[k]] : Vector2.Zero);
                        }
                    }
                }
            }
            if (outV.Count == 0) return null;
            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = outV.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = outN.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = outU.ToArray();
            var m = new ArrayMesh();
            m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return m;
        }
    }
}
