using Godot;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot
{
    // Maps ORIGINAL Unity asset GUIDs (from the ripped .meta files) -> ripped Godot-native assets.
    // This is the swap seam the plan mandates: gameplay/.dat definitions reference assets by their
    // original GUID; the ContentProvider resolves that GUID to whatever we've got for it (ripped now,
    // our-own-art later) without any caller change.
    //
    // v0: static meshes ripped as Wavefront .obj (tools/unity_mesh_to_obj.py, byte-validated vs the
    // Unity localAABB). Parsed to an ArrayMesh at RUNTIME on purpose -- the shipping game streams
    // content, so loading is a runtime concern, not an editor-import one.
    public partial class ContentProvider : Node
    {
        const string ContentRoot = "res://content/";
        readonly Dictionary<string, string> _guidToPath = new();

        public int Count => _guidToPath.Count;

        public void LoadManifest(string manifestResPath = ContentRoot + "manifest.json")
        {
            using var f = Godot.FileAccess.Open(manifestResPath, Godot.FileAccess.ModeFlags.Read);
            if (f == null) { GD.PushError($"[ContentProvider] manifest not found: {manifestResPath}"); return; }
            var parsed = Json.ParseString(f.GetAsText());
            var dict = parsed.AsGodotDictionary();
            foreach (var k in dict.Keys)
                _guidToPath[(string)k] = (string)dict[k];
        }

        public bool HasGuid(string guid) => _guidToPath.ContainsKey(guid);

        // Resolve a mesh by its original Unity GUID -> a live Godot ArrayMesh.
        public ArrayMesh LoadMesh(string guid)
        {
            if (!_guidToPath.TryGetValue(guid, out var rel))
            {
                GD.PushError($"[ContentProvider] unknown GUID {guid}");
                return null;
            }
            return ParseObj(ContentRoot + rel);
        }

        static ArrayMesh ParseObj(string resPath)
        {
            using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
            if (f == null) { GD.PushError($"[ContentProvider] obj not found: {resPath}"); return null; }
            var ci = CultureInfo.InvariantCulture;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var fv = new List<int>(); var ft = new List<int>(); var fn = new List<int>();

            while (!f.EofReached())
            {
                var line = f.GetLine();
                if (line.Length == 0 || line[0] == '#') continue;
                var t = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                switch (t[0])
                {
                    case "v":  verts.Add(new Vector3(float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci))); break;
                    case "vn": norms.Add(new Vector3(float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci))); break;
                    case "vt": uvs.Add(new Vector2(float.Parse(t[1], ci), float.Parse(t[2], ci))); break;
                    case "f":
                        for (int i = 1; i <= 3; i++)
                        {
                            var p = t[i].Split('/');
                            fv.Add(int.Parse(p[0], ci) - 1);
                            ft.Add(p.Length > 1 && p[1].Length > 0 ? int.Parse(p[1], ci) - 1 : -1);
                            fn.Add(p.Length > 2 && p[2].Length > 0 ? int.Parse(p[2], ci) - 1 : -1);
                        }
                        break;
                }
            }

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int i = 0; i < fv.Count; i++)
            {
                if (ft[i] >= 0 && ft[i] < uvs.Count) st.SetUV(uvs[ft[i]]);
                if (fn[i] >= 0 && fn[i] < norms.Count) st.SetNormal(norms[fn[i]]);
                st.AddVertex(verts[fv[i]]);
            }
            return st.Commit();
        }
    }
}
