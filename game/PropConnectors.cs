using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // ROAD/RAIL CONNECTION POINTS ON PROPS (strawberry 2026-08-19: "giving certain props road/rail connection
    // points. for example all the road 'cap' props have snap points where roads will connect to").
    //
    // The catalogue is DERIVED from the meshes by tools/extract_road_connectors.py, not hand-typed: these
    // tiles carry a 16 m carriageway on a 24 m square, so an edge the road opens onto has surface vertices at
    // the strip boundary while a closed edge has only its corners. Hand-entered coordinates would also rot
    // the moment a prop is re-extracted; derived ones are re-derivable.
    //
    // Two props (Bridge_Line_1, Bridge_Line_Cap_1) are deliberately ABSENT -- the extractor could not read a
    // plausible carriageway off them and says so loudly rather than emitting a guess. A missing snap point is
    // a tool that does not help you there; a wrong one silently welds a road to the wrong place.
    public static class PropConnectors
    {
        public readonly struct Point
        {
            public readonly Vector3 Local, Dir;
            public Point(Vector3 l, Vector3 d) { Local = l; Dir = d; }
        }

        static Dictionary<string, List<Point>> _catalog;

        /// <summary>Mesh-local connection points for a prop, or null. Loaded once.</summary>
        public static List<Point> For(string prop)
        {
            _catalog ??= Load();
            return prop != null && _catalog.TryGetValue(prop, out var l) ? l : null;
        }

        static Dictionary<string, List<Point>> Load()
        {
            var d = new Dictionary<string, List<Point>>();
            string path = ProjectSettings.GlobalizePath("res://content/objects/road_connectors.txt");
            if (!System.IO.File.Exists(path)) { GD.Print("[connectors] no road_connectors.txt -- prop snapping off"); return d; }
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 7) continue;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (!d.TryGetValue(p[0], out var list)) d[p[0]] = list = new List<Point>();
                list.Add(new Point(
                    new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci)),
                    new Vector3(float.Parse(p[4], ci), float.Parse(p[5], ci), float.Parse(p[6], ci))));
                n++;
            }
            GD.Print($"[connectors] {n} connection points across {d.Count} props");
            return d;
        }

        // ---- the placed instances, in world space -------------------------------------------------------
        // Populated by WorldBuilder as props are placed, read by the road draw tool. CLEARED at the start of
        // every world build: a static that accumulates across builds is exactly the leak that took the test
        // suite from 2 failures to 12 earlier today (Terrain.SeaLevelY), and this one would quietly offer
        // snap points belonging to a map you are no longer editing.
        static readonly List<Vector3> _placed = new();
        public static int PlacedCount => _placed.Count;
        public static void ClearPlaced() => _placed.Clear();
        public static void AddPlaced(Vector3 world) => _placed.Add(world);

        /// <summary>Nearest placed connection point to a world position, for snapping.</summary>
        public static bool Nearest(Vector3 p, float maxDist, out Vector3 pos)
        {
            pos = Vector3.Zero;
            float best = maxDist * maxDist;
            bool found = false;
            foreach (var c in _placed)
            {
                float d = (c - p).LengthSquared();
                if (d < best) { best = d; pos = c; found = true; }
            }
            return found;
        }

        /// <summary>Register a placed prop's connection points, transformed into world space by its own
        /// placement basis -- so nothing here assumes anything about the Z-up to Y-up pitch.</summary>
        public static void Register(string prop, Transform3D xf)
        {
            var pts = For(prop);
            if (pts == null) return;
            foreach (var pt in pts) AddPlaced(xf * pt.Local);
        }
    }
}
