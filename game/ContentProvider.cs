using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace UnturnedGodot
{
    // Maps ORIGINAL Unity asset GUIDs (from the ripped .meta files) -> ripped Godot-native assets.
    // This is the swap seam the plan mandates: gameplay/.dat definitions reference assets by their
    // original GUID; the ContentProvider resolves that GUID to whatever we've got for it (ripped now,
    // our-own-art later) without any caller change.
    //
    // Content root is the directory holding the manifest; asset paths are relative to it. Reads via
    // Godot.FileAccess for res://|user:// and System.IO for an absolute dev path (the external ripped
    // asset store on the 4080), so the same provider serves both the in-repo slice and the full catalog.
    //
    // v0: static meshes ripped as Wavefront .obj (tools/unity_mesh_to_obj.py, byte-validated vs the
    // Unity localAABB). Parsed to an ArrayMesh at RUNTIME on purpose -- the shipping game streams content.
    public partial class ContentProvider : Node
    {
        string _root = "res://content";
        readonly Dictionary<string, string> _guidToPath = new();

        public int Count => _guidToPath.Count;

        static bool IsGodotPath(string p) => p.StartsWith("res://") || p.StartsWith("user://");

        static string ReadText(string path)
        {
            if (IsGodotPath(path))
            {
                using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
                return f?.GetAsText();
            }
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public void LoadManifest(string manifestPath = "res://content/manifest.json")
        {
            _root = IsGodotPath(manifestPath)
                ? manifestPath[..manifestPath.LastIndexOf('/')]
                : Path.GetDirectoryName(manifestPath);
            var text = ReadText(manifestPath);
            if (text == null) { GD.PushError($"[ContentProvider] manifest not found: {manifestPath}"); return; }
            var dict = Json.ParseString(text).AsGodotDictionary();
            foreach (var k in dict.Keys)
                _guidToPath[(string)k] = (string)dict[k];
        }

        public bool HasGuid(string guid) => _guidToPath.ContainsKey(guid);

        public IEnumerable<string> Guids => _guidToPath.Keys;

        // Resolve a mesh by its asset name (manifest path basename, no ext) -> GUID. For the showcase.
        public string FindGuidByName(string name)
        {
            foreach (var kv in _guidToPath)
                if (Path.GetFileNameWithoutExtension(kv.Value).Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            return null;
        }

        // --- textures: mesh_guid -> albedo .png (built by tools/build_texture_map.py) ---
        readonly Dictionary<string, string> _guidToTex = new();
        public IEnumerable<string> TexturedGuids => _guidToTex.Keys;
        public int TexturedCount => _guidToTex.Count;

        public void LoadTextureManifest(string manifestPath)
        {
            var text = ReadText(manifestPath);
            if (text == null) { GD.PushError($"[ContentProvider] texture manifest not found: {manifestPath}"); return; }
            var dict = Json.ParseString(text).AsGodotDictionary();
            foreach (var k in dict.Keys)
                _guidToTex[(string)k] = (string)dict[k];
        }

        public string GetTexturePath(string guid) => _guidToTex.TryGetValue(guid, out var p) ? p : null;

        string Resolve(string rel) => IsGodotPath(_root) ? $"{_root}/{rel}" : Path.Combine(_root, rel);

        // Resolve a mesh by its original Unity GUID -> a live Godot ArrayMesh.
        public ArrayMesh LoadMesh(string guid)
        {
            if (!_guidToPath.TryGetValue(guid, out var rel))
            {
                GD.PushError($"[ContentProvider] unknown GUID {guid}");
                return null;
            }
            return ParseObj(Resolve(rel));
        }

        public static ArrayMesh ParseObj(string path)
        {
            var txt = ReadText(path);
            if (txt == null) { GD.PushError($"[ContentProvider] obj not found: {path}"); return null; }
            var ci = CultureInfo.InvariantCulture;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var fv = new List<int>(); var ft = new List<int>(); var fn = new List<int>();

            foreach (var raw in txt.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var t = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                switch (t[0])
                {
                    case "v":  verts.Add(new Vector3(float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci))); break;
                    case "vn": norms.Add(new Vector3(float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci))); break;
                    case "vt": uvs.Add(new Vector2(float.Parse(t[1], ci), float.Parse(t[2], ci))); break;
                    case "f":
                        for (int i = 1; i <= 3 && i < t.Length; i++)
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
