using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // PEI's 19 zombie NAVMESH POCKETS (source: LevelNavigation "Flags"). Each pocket is a town/POI-sized box with a
    // baked navmesh + a zombie population cap. Parsed byte-exact from Maps/PEI/Environment:
    //   Bounds.dat      -> per-pocket EXPANDED navmesh AABB (real region = size - 64 per axis, BOUNDS_SIZE)
    //   Flags_Data.dat  -> per-pocket zombie config (difficultyGUID, maxZombies, spawnZombies, hyperAgro, maxBoss)
    //   Flags.dat/Navigation_N.dat = vanilla's baked mesh (we bake our OWN in Godot instead, from our collision)
    // Vanilla PEI = 19 pockets, all default difficulty, maxZombies 64, spawn on. Godot space: center = (x, y, -z).
    public struct NavPocket
    {
        public Vector3 Center;      // Godot-space center (XZ = flag point; Y = bounds mid)
        public Vector3 HalfExtent;  // Godot-space half-size of the navmesh region (bounds.size - 64, halved)
        public int MaxZombies;
        public bool SpawnZombies;
        public bool HyperAgro;
        public int MaxBoss;
        public string DifficultyGuid;
        public Aabb Box => new Aabb(Center - HalfExtent, HalfExtent * 2f);
    }

    public static class ZombieNav
    {
        public const uint WorldLayer = 1u << 0;   // LOS-blocking world geometry (terrain + solid buildings) -- what zombies can't walk through
        const float AgentRadius = 0.5f;            // wall buffer so zombies don't clip/bunch through walls (master)
        const float AgentHeight = 2.0f;
        const float CellSize = 0.25f;

        // Parse the 3 flag files -> the 19 pockets. Empty list if the PEI Environment isn't present.
        public static List<NavPocket> LoadPockets(string peiRoot)
        {
            var list = new List<NavPocket>();
            string env = System.IO.Path.Combine(peiRoot, "Environment");
            string fB = System.IO.Path.Combine(env, "Bounds.dat");
            string fD = System.IO.Path.Combine(env, "Flags_Data.dat");
            if (!System.IO.File.Exists(fB)) { GD.Print("[zombienav] no Bounds.dat -- no pockets"); return list; }

            var bounds = new List<(Vector3 c, Vector3 s)>();
            { var r = new Rdr(fB); byte v = r.B(); if (v > 0) { int n = r.B(); for (int i = 0; i < n; i++) { var c = r.V3(); var s = r.V3(); bounds.Add((c, s)); } } }

            var fdata = new List<(string g, int mz, bool sz, bool ha, int mb)>();
            if (System.IO.File.Exists(fD))
            { var r = new Rdr(fD); byte v = r.B(); if (v > 0) { int n = r.B(); for (int i = 0; i < n; i++) { string g = r.Str(); int mz = v > 1 ? r.B() : 64; bool sz = v > 2 ? r.Bool() : true; bool ha = v >= 4 && r.Bool(); int mb = v >= 5 ? r.I32() : -1; fdata.Add((g, mz, sz, ha, mb)); } } }

            for (int i = 0; i < bounds.Count; i++)
            {
                var (c, s) = bounds[i];
                var fd = i < fdata.Count ? fdata[i] : (g: "", mz: 64, sz: true, ha: false, mb: -1);
                var he = new Vector3(Mathf.Max(1f, (s.X - 64f) * 0.5f), Mathf.Max(1f, (s.Y - 64f) * 0.5f), Mathf.Max(1f, (s.Z - 64f) * 0.5f));
                list.Add(new NavPocket
                {
                    Center = new Vector3(c.X, c.Y, -c.Z),
                    HalfExtent = he,
                    MaxZombies = fd.mz, SpawnZombies = fd.sz, HyperAgro = fd.ha, MaxBoss = fd.mb, DifficultyGuid = fd.g,
                });
            }
            GD.Print($"[zombienav] {list.Count} PEI navmesh pockets loaded (Bounds/Flags_Data)");
            return list;
        }

        // Bake (or load if already saved) a navmesh per pocket + register it as a NavigationRegion3D under worldRoot.
        // World geometry is PARSED ONCE (whole subtree), then each pocket bakes from it with its own FilterBakingAabb.
        // Baked meshes are saved to res://content/navmesh/ so subsequent loads skip the (slow) Recast bake -- master's
        // "build it once when we load the map, save it to a file" (editor-time baking comes later).
        public static void BuildOrLoad(Node worldRoot, List<NavPocket> pockets, bool overlay = false)
        {
            if (pockets.Count == 0) return;
            const string dir = "res://content/navmesh";
            string absDir = ProjectSettings.GlobalizePath(dir);
            try { System.IO.Directory.CreateDirectory(absDir); } catch { }

            NavigationMeshSourceGeometryData3D src = null;   // parse the world geometry lazily, only if we actually need to bake
            int baked = 0, loaded = 0, polys = 0;
            for (int i = 0; i < pockets.Count; i++)
            {
                string path = $"{dir}/pei_pocket_{i}.res";
                NavigationMesh nm = null;
                if (Godot.FileAccess.FileExists(path)) { nm = ResourceLoader.Load<NavigationMesh>(path); if (nm != null) loaded++; }
                if (nm == null)
                {
                    nm = MakeMesh(pockets[i].Box);
                    if (src == null) { src = new NavigationMeshSourceGeometryData3D(); NavigationServer3D.ParseSourceGeometryData(nm, src, worldRoot); }
                    NavigationServer3D.BakeFromSourceGeometryData(nm, src);
                    baked++;
                    try { ResourceSaver.Save(nm, path); } catch (System.Exception e) { GD.Print($"[zombienav] save skip {i}: {e.Message}"); }
                }
                polys += nm.GetPolygonCount();
                var region = new NavigationRegion3D { NavigationMesh = nm };
                worldRoot.AddChild(region);
                if (overlay) { var ov = NavDebug.NavmeshOverlay(nm, new Color(0.1f, 0.9f, 1f, 0.55f)); if (ov != null) worldRoot.AddChild(ov); }   // translucent floor overlay for the verify screenshot
            }
            GD.Print($"[zombienav] pockets ready: {baked} baked + {loaded} loaded, {polys} total nav polygons (agent r={AgentRadius}m buffer)");
        }

        static NavigationMesh MakeMesh(Aabb box) => new NavigationMesh
        {
            AgentRadius = AgentRadius,
            AgentHeight = AgentHeight,
            CellSize = CellSize,
            CellHeight = 0.2f,
            GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
            GeometryCollisionMask = WorldLayer,
            FilterBakingAabb = box,   // confine the baked mesh to this pocket (source: navmesh only inside the Flag bounds)
        };

        // tiny Unturned River-style reader (little-endian, single-byte-length UTF8 strings)
        sealed class Rdr
        {
            readonly byte[] _d; int _o;
            public Rdr(string path) { _d = System.IO.File.ReadAllBytes(path); }
            public byte B() => _d[_o++];
            public bool Bool() => _d[_o++] != 0;
            public float F() { float v = System.BitConverter.ToSingle(_d, _o); _o += 4; return v; }
            public int I32() { int v = System.BitConverter.ToInt32(_d, _o); _o += 4; return v; }
            public Vector3 V3() => new Vector3(F(), F(), F());
            public string Str() { int n = _d[_o++]; string s = System.Text.Encoding.UTF8.GetString(_d, _o, n); _o += n; return s; }
        }
    }
}
