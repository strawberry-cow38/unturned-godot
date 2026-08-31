using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A baked-in window opening: a lightweight marker carrying an opening's plane + half-extents so BarricadeMount.Window
    // can snap to a BAKED building, which has NO WallSurface at runtime. One is written per window into <name>_openings.txt
    // at bake (EditorBuildings.BakeSolved), in building-local .obj space; loaded as a LOCAL child of the placed prop root
    // by WindowMarkers.Attach (from EditorObjects.Place), so it rides the prop's placement transform exactly like the mesh.
    // The live-wall path (WallSurface in the "walls" group) is unchanged; AimWindow scans BOTH groups.
    public partial class WindowOpeningMarker : Node3D
    {
        public float HalfWidth, HalfHeight;   // opening half-extents along the marker's local X (width) / Y (height)
        public float HalfThickness;           // half the wall's thickness -> the barricade seats on the FACE, not the centre plane

        public override void _Ready() => AddToGroup("window_openings");

        public Vector3 WorldCentre => GlobalPosition;
        public Vector3 WorldNormal => GlobalTransform.Basis.Z.Normalized();   // marker local +Z faces out of the wall
        public Vector3 WorldWidthAxis => GlobalTransform.Basis.X.Normalized();
        public Vector3 WorldHeightAxis => GlobalTransform.Basis.Y.Normalized();
    }

    // Loads a baked building's window-opening sidecar and attaches markers, the same shape as WorldBuilder.LoadDoorCatalog:
    // parse the per-name file once into a cache, attach fresh child nodes at every placement.
    public static class WindowMarkers
    {
        static readonly Dictionary<string, List<float[]>> _cache = new();

        public static void Attach(Node3D root, string name, string dir)
        {
            if (!_cache.TryGetValue(name, out var rows))
            {
                rows = new List<float[]>();
                string path = dir + name + "_openings.txt";
                if (System.IO.File.Exists(path))
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 13 || p[0] != "opening") continue;
                        var f = new float[12]; bool ok = true;
                        for (int i = 0; i < 12; i++)
                            ok &= float.TryParse(p[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f[i]);
                        if (ok) rows.Add(f);
                    }
                _cache[name] = rows;
            }
            foreach (var f in rows)
            {
                Vector3 c = new(f[0], f[1], f[2]);
                Vector3 n = new Vector3(f[3], f[4], f[5]).Normalized();   // +Z (out of the wall)
                Vector3 ax = new Vector3(f[6], f[7], f[8]).Normalized();  // +X (width)
                Vector3 up = n.Cross(ax).Normalized();                    // +Y (height) = Z x X, right-handed
                var m = new WindowOpeningMarker { HalfWidth = f[9], HalfHeight = f[10], HalfThickness = f[11], TopLevel = false };
                m.Transform = new Transform3D(new Basis(ax, up, n), c);   // LOCAL: the file is in the prop's own .obj space
                root.AddChild(m);
            }
        }

        // Re-bake / hot-reload: forget a name so the next Attach re-reads its file (mirrors ObjMesh.Forget).
        public static void Forget(string name) => _cache.Remove(name);
    }
}
