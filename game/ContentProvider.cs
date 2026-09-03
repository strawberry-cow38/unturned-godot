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

        // ---- LOAD CACHE (strawberry 2026-09-03: "loading optimizations when going into a map. vehicles is 50% of the loading!") ----
        // PEI spawns 88 vehicles from ~15 specs and every one re-tokenised its body/wheel/parts text meshes. Parsed
        // meshes are immutable here (nothing calls SurfaceSetMaterial/ClearSurfaces on them -- materials ride the
        // MeshInstance), so one ArrayMesh per path shared by every instance is exactly Godot's intended resource model.
        static readonly System.Collections.Generic.Dictionary<string, ArrayMesh> _meshCache = new();
        static readonly System.Collections.Generic.Dictionary<string, (ArrayMesh, ArrayMesh)> _splitCache = new();
        static readonly System.Collections.Generic.Dictionary<string, (ArrayMesh, ArrayMesh, ArrayMesh)> _split2Cache = new();
        static readonly System.Collections.Generic.Dictionary<string, ImageTexture> _texCache = new();
        static readonly System.Collections.Generic.Dictionary<string, Shader> _shaderCache = new();
        public static int MeshCacheCount => _meshCache.Count + _splitCache.Count + _split2Cache.Count;
        static string ZoneKey((Vector3 min, Vector3 max)[] zs)
        {
            if (zs == null) return "-";
            var sb = new System.Text.StringBuilder();
            foreach (var z in zs) sb.Append(z.min.X).Append(',').Append(z.min.Y).Append(',').Append(z.min.Z).Append('/').Append(z.max.X).Append(',').Append(z.max.Y).Append(',').Append(z.max.Z).Append(';');
            return sb.ToString();
        }
        public static ArrayMesh ParseObj(string path)
        {
            if (_meshCache.TryGetValue(path, out var cached)) return cached;
            var m = ParseObjUncached(path);
            if (m != null) _meshCache[path] = m;
            return m;
        }
        /// <summary>A decoded texture per absolute path (optionally mipmapped). Runtime Image.LoadFromFile has no mipmaps; callers that
        /// used to GenerateMipmaps per instance pass mipmaps:true and get the one shared texture.</summary>
        public static ImageTexture TextureCached(string absPath, bool mipmaps = false)
        {
            string key = mipmaps ? absPath + "|mip" : absPath;
            if (_texCache.TryGetValue(key, out var t)) return t;
            if (!System.IO.File.Exists(absPath)) return null;
            var img = Image.LoadFromFile(absPath);
            if (img == null) return null;
            if (mipmaps) img.GenerateMipmaps();
            t = ImageTexture.CreateFromImage(img);
            _texCache[key] = t;
            return t;
        }
        static readonly System.Collections.Generic.Dictionary<string, AudioStreamOggVorbis> _oggCache = new();
        /// <summary>One decoded OGG per (path, loop). Every vehicle used to decode its horn + engine + ignition + explosion
        /// streams itself -- 4 decodes x 88 vehicles on a PEI load. The Loop flag lives on the stream, hence part of the key.</summary>
        public static AudioStreamOggVorbis OggCached(string absPath, bool loop)
        {
            string key = loop ? absPath + "|loop" : absPath;
            if (_oggCache.TryGetValue(key, out var o)) return o;
            o = AudioStreamOggVorbis.LoadFromFile(absPath);
            if (o != null) { o.Loop = loop; _oggCache[key] = o; }
            return o;
        }
        /// <summary>One compiled Shader per source path (vehicle_paint.gdshader was a NEW Shader per vehicle -> 88 compiles on a PEI load).</summary>
        public static Shader ShaderCached(string absPath)
        {
            if (_shaderCache.TryGetValue(absPath, out var sh)) return sh;
            sh = new Shader { Code = System.IO.File.ReadAllText(absPath) };
            _shaderCache[absPath] = sh;
            return sh;
        }
        static ArrayMesh ParseObjUncached(string path)
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
                    case "vt": uvs.Add(new Vector2(float.Parse(t[1], ci), 1f - float.Parse(t[2], ci))); break;   // Unity vt is V-up (origin bottom-left); Godot samples V-down (top-left) -> flip V or the texture wraps upside-down
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

        // Parse an obj and split it into two meshes by an axis-aligned zone: every triangle whose 3 vertices ALL
        // lie inside [min,max] goes to `inside`, the rest to `outside`. Used to peel a baked-in sub-part (e.g. the
        // trailer's landing legs) out of a single mesh so it can be toggled independently. Either mesh may be null
        // if it got no triangles. Same UV V-flip + per-corner normal/uv as ParseObj.
        public static (ArrayMesh outside, ArrayMesh inside) ParseObjSplitByZone(string path, Vector3 min, Vector3 max)
            => ParseObjSplitByZone(path, new[] { (min, max) });

        // Split by MULTIPLE zones: a triangle is peeled only if all 3 of its verts fall in the SAME zone -> a triangle
        // straddling two zones (e.g. a strip bridging the L+R headlights) stays in the body, so the split doesn't bleed
        // across the gap between them.
        public static (ArrayMesh outside, ArrayMesh inside) ParseObjSplitByZone(string path, (Vector3 min, Vector3 max)[] zones)
        {
            string key = path + "|" + ZoneKey(zones);
            if (_splitCache.TryGetValue(key, out var c)) return c;
            var r = ParseObjSplitByZoneUncached(path, zones);
            if (r.outside != null || r.inside != null) _splitCache[key] = r;
            return r;
        }
        static (ArrayMesh outside, ArrayMesh inside) ParseObjSplitByZoneUncached(string path, (Vector3 min, Vector3 max)[] zones)
        {
            var txt = ReadText(path);
            if (txt == null) { GD.PushError($"[ContentProvider] obj not found: {path}"); return (null, null); }
            var ci = CultureInfo.InvariantCulture;
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
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
                    case "vt": uvs.Add(new Vector2(float.Parse(t[1], ci), 1f - float.Parse(t[2], ci))); break;
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
            bool InZone(Vector3 v, (Vector3 min, Vector3 max) z) => v.X >= z.min.X && v.X <= z.max.X && v.Y >= z.min.Y && v.Y <= z.max.Y && v.Z >= z.min.Z && v.Z <= z.max.Z;
            bool TriInside(Vector3 a, Vector3 b, Vector3 c) { foreach (var z in zones) if (InZone(a, z) && InZone(b, z) && InZone(c, z)) return true; return false; }
            var stOut = new SurfaceTool(); stOut.Begin(Mesh.PrimitiveType.Triangles);
            var stIn = new SurfaceTool(); stIn.Begin(Mesh.PrimitiveType.Triangles);
            int nOut = 0, nIn = 0;
            for (int f = 0; f + 2 < fv.Count; f += 3)
            {
                bool inside = TriInside(verts[fv[f]], verts[fv[f + 1]], verts[fv[f + 2]]);
                var st = inside ? stIn : stOut;
                if (inside) nIn++; else nOut++;
                for (int k = 0; k < 3; k++)
                {
                    int i = f + k;
                    if (ft[i] >= 0 && ft[i] < uvs.Count) st.SetUV(uvs[ft[i]]);
                    if (fn[i] >= 0 && fn[i] < norms.Count) st.SetNormal(norms[fn[i]]);
                    st.AddVertex(verts[fv[i]]);
                }
            }
            return (nOut > 0 ? stOut.Commit() : null, nIn > 0 ? stIn.Commit() : null);
        }

        // Split into THREE meshes in one pass: body + groupA + groupB. A triangle goes to A if all 3 verts fall in one of
        // groupA's zones, else B if in one of groupB's, else the body. Lets a mesh peel two differently-materialed sets at
        // once (e.g. the trailer's landing legs AND its baked-in taillights).
        public static (ArrayMesh body, ArrayMesh a, ArrayMesh b) ParseObjSplit2(string path, (Vector3 min, Vector3 max)[] groupA, (Vector3 min, Vector3 max)[] groupB)
        {
            string key = path + "|" + ZoneKey(groupA) + "|" + ZoneKey(groupB);
            if (_split2Cache.TryGetValue(key, out var c)) return c;
            var r = ParseObjSplit2Uncached(path, groupA, groupB);
            if (r.body != null || r.a != null || r.b != null) _split2Cache[key] = r;
            return r;
        }
        static (ArrayMesh body, ArrayMesh a, ArrayMesh b) ParseObjSplit2Uncached(string path, (Vector3 min, Vector3 max)[] groupA, (Vector3 min, Vector3 max)[] groupB)
        {
            var txt = ReadText(path);
            if (txt == null) { GD.PushError($"[ContentProvider] obj not found: {path}"); return (null, null, null); }
            var ci = CultureInfo.InvariantCulture;
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
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
                    case "vt": uvs.Add(new Vector2(float.Parse(t[1], ci), 1f - float.Parse(t[2], ci))); break;
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
            bool InZone(Vector3 v, (Vector3 min, Vector3 max) z) => v.X >= z.min.X && v.X <= z.max.X && v.Y >= z.min.Y && v.Y <= z.max.Y && v.Z >= z.min.Z && v.Z <= z.max.Z;
            bool In(Vector3 a, Vector3 b2, Vector3 c, (Vector3 min, Vector3 max)[] zs) { if (zs == null) return false; foreach (var z in zs) if (InZone(a, z) && InZone(b2, z) && InZone(c, z)) return true; return false; }
            var stBody = new SurfaceTool(); stBody.Begin(Mesh.PrimitiveType.Triangles);
            var stA = new SurfaceTool(); stA.Begin(Mesh.PrimitiveType.Triangles);
            var stB = new SurfaceTool(); stB.Begin(Mesh.PrimitiveType.Triangles);
            int nBody = 0, nA = 0, nB = 0;
            for (int f = 0; f + 2 < fv.Count; f += 3)
            {
                var v0 = verts[fv[f]]; var v1 = verts[fv[f + 1]]; var v2 = verts[fv[f + 2]];
                SurfaceTool st; if (In(v0, v1, v2, groupA)) { st = stA; nA++; } else if (In(v0, v1, v2, groupB)) { st = stB; nB++; } else { st = stBody; nBody++; }
                for (int k = 0; k < 3; k++)
                {
                    int i = f + k;
                    if (ft[i] >= 0 && ft[i] < uvs.Count) st.SetUV(uvs[ft[i]]);
                    if (fn[i] >= 0 && fn[i] < norms.Count) st.SetNormal(norms[fn[i]]);
                    st.AddVertex(verts[fv[i]]);
                }
            }
            return (nBody > 0 ? stBody.Commit() : null, nA > 0 ? stA.Commit() : null, nB > 0 ? stB.Commit() : null);
        }

        // Split a gun mesh into its body + emissive SIGHT-DOT surfaces. Some Unturned pistols (ace/avenger/cobra/
        // desert_falcon) model their tritium 3-dot iron sights as tris painted a single SATURATED marker colour in the
        // albedo (red/green/white) -- the ONLY saturated colour on the gun, every other face being dark gunmetal.
        // Retail draws them flat, but they read as glowing dots, so we peel those faces onto their own surface(s) to
        // render emissive. Markers are SCATTERED across the UV sheet (not one region), so we sample the albedo at each
        // face's UV centroid and test the actual RGB -- a UV-region split (like the prop lenses) would miss them.
        // Returns the body + one (colour, mesh) per distinct marker colour; markers is empty for a gun with no painted
        // dots (colt, every rifle) or a null albedo. Same V-flip + per-corner uv/normal as ParseObj.
        public static (ArrayMesh body, List<(Color color, ArrayMesh mesh)> markers) ParseObjSplitByAlbedoMarker(string path, Image albedo)
        {
            var txt = ReadText(path);
            if (txt == null) { GD.PushError($"[ContentProvider] obj not found: {path}"); return (null, new List<(Color, ArrayMesh)>()); }
            var ci = CultureInfo.InvariantCulture;
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
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
                    case "vt": uvs.Add(new Vector2(float.Parse(t[1], ci), 1f - float.Parse(t[2], ci))); break;
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
            int W = albedo?.GetWidth() ?? 0, H = albedo?.GetHeight() ?? 0;
            // a marker texel is BRIGHT and either saturated (red/green) or near-white; dark gunmetal never qualifies.
            Color? MarkerAt(Vector2 uv)
            {
                if (albedo == null || W == 0 || H == 0) return null;
                int x = Mathf.Clamp((int)Mathf.Round(uv.X * (W - 1)), 0, W - 1);
                int y = Mathf.Clamp((int)Mathf.Round(uv.Y * (H - 1)), 0, H - 1);
                Color c = albedo.GetPixel(x, y);
                if (c.A < 0.5f) return null;
                float mx = Mathf.Max(c.R, Mathf.Max(c.G, c.B)), mn = Mathf.Min(c.R, Mathf.Min(c.G, c.B));
                if (mx > 0.85f && (mx - mn > 0.4f || mn > 0.85f)) return c;
                return null;
            }
            var stBody = new SurfaceTool(); stBody.Begin(Mesh.PrimitiveType.Triangles); int nBody = 0;
            var markerSt = new Dictionary<(int, int, int), (Color rep, SurfaceTool tool)>();   // grouped by quantised colour so a multi-colour gun still splits right
            for (int f = 0; f + 2 < fv.Count; f += 3)
            {
                Vector2 cuv = Vector2.Zero; int nUv = 0;
                for (int k = 0; k < 3; k++) { int ti = ft[f + k]; if (ti >= 0 && ti < uvs.Count) { cuv += uvs[ti]; nUv++; } }
                Color? mc = nUv == 3 ? MarkerAt(cuv / 3f) : null;
                SurfaceTool st;
                if (mc.HasValue)
                {
                    var c = mc.Value;
                    var key = ((int)Mathf.Round(c.R * 4f), (int)Mathf.Round(c.G * 4f), (int)Mathf.Round(c.B * 4f));
                    if (!markerSt.TryGetValue(key, out var e)) { e = (c, new SurfaceTool()); e.tool.Begin(Mesh.PrimitiveType.Triangles); markerSt[key] = e; }
                    st = e.tool;
                }
                else { st = stBody; nBody++; }
                for (int k = 0; k < 3; k++)
                {
                    int i = f + k;
                    if (ft[i] >= 0 && ft[i] < uvs.Count) st.SetUV(uvs[ft[i]]);
                    if (fn[i] >= 0 && fn[i] < norms.Count) st.SetNormal(norms[fn[i]]);
                    st.AddVertex(verts[fv[i]]);
                }
            }
            var markers = new List<(Color color, ArrayMesh mesh)>();
            foreach (var e in markerSt.Values) markers.Add((e.rep, e.tool.Commit()));
            return (nBody > 0 ? stBody.Commit() : null, markers);
        }
    }
}
