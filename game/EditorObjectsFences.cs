using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // ==============================================================================================
    // FENCE SPLINES (strawberry 2026-10-14: "work on getting fences as splines"). One placed thing per fence LINE --
    // pickable, deletable, undoable -- and saved as the line it was laid along (style + points), not as its pieces: the
    // pieces are solved from the line, so the line is the thing worth keeping, and a reload re-solves it on whatever
    // ground is there by then.
    // ==============================================================================================
    public partial class EditorObjects
    {
        public const string FenceSplineName = "Fence Spline";

        /// <summary>Lay a fence of `style` along `pts` -- a curve through them, or straight legs with a post on every
        /// corner -- as one placed thing, with a broken section dropped in nearest each of `wrecks`. Null if nothing could be
        /// laid (too short, or the pieces are missing).</summary>
        public Node3D AddFenceSpline(FenceSpline.Style style, Vector3[] pts, bool smooth, Vector3[] wrecks = null)
        {
            if (style == null || pts == null || pts.Length < 2) return null;
            var terr = Terrain.Active;
            var b = new FenceSpline.Builder(terr != null ? (x, z) => terr.SampleHeight(x, z) : null);
            b.AddLine(style, pts, smooth, wrecks);
            var tmp = new Node3D();
            if (b.Build(tmp, this, "Fence", 0f) == 0 || tmp.GetChild(0) is not Node3D cell) { tmp.Free(); return null; }
            // ⚠ THE MESH IS CHILD 0 OF THE ROOT: PositionMarkers sizes the selection outline off child 0, so the built
            // cell's mesh and body are lifted straight onto the root rather than left one level down.
            var root = new Node3D { Name = "FenceSpline", Position = cell.Position };
            foreach (var ch in cell.GetChildren()) { cell.RemoveChild(ch); root.AddChild(ch); }
            tmp.Free();
            root.SetMeta("obj_name", FenceSplineName);
            root.SetMeta("fence_style", style.Name);
            root.SetMeta("fence_pts", pts);
            root.SetMeta("fence_smooth", smooth);
            root.SetMeta("fence_wrecks", wrecks ?? System.Array.Empty<Vector3>());
            _world.AddChild(root);
            foreach (var ch in root.GetChildren())
                if (ch is StaticBody3D body) { body.CollisionLayer |= PickLayer; _pickToObj[body.GetRid()] = root; }
            _placed.Add(root);
            return root;
        }

        string FencePath => Dir + $"editor_{_editor.MapName}_fences.txt";

        /// <summary>One line per fence: style, "curve" or "corners", the points (x y z, z negated like every other sidecar
        /// here), and -- if it has any -- a fourth field of the points its wrecks were dropped at, the same way.</summary>
        void SaveFences()
        {
            var fs = _placed.FindAll(t => IsInstanceValid(t) && t.HasMeta("fence_pts"));
            if (fs.Count == 0 && !System.IO.File.Exists(FencePath)) return;
            using var w = new System.IO.StreamWriter(FencePath, false);
            foreach (var f in fs)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append((string)f.GetMeta("fence_style")).Append('\t')
                  .Append((bool)f.GetMeta("fence_smooth") ? "curve" : "corners").Append('\t');
                foreach (var v in (Vector3[])f.GetMeta("fence_pts")) sb.Append($"{v.X:0.###} {v.Y:0.###} {(-v.Z):0.###} ");
                var wr = f.HasMeta("fence_wrecks") ? (Vector3[])f.GetMeta("fence_wrecks") : null;
                if (wr != null && wr.Length > 0)
                {
                    sb.Length--; sb.Append('\t');
                    foreach (var v in wr) sb.Append($"{v.X:0.###} {v.Y:0.###} {(-v.Z):0.###} ");
                }
                w.WriteLine(sb.ToString().TrimEnd());
            }
            if (fs.Count > 0) Log.Print($"[editor] saved {fs.Count} fence spline(s) -> {FencePath}");
        }

        void LoadFences()
        {
            if (!System.IO.File.Exists(FencePath)) return;
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(FencePath))
            {
                var tab = line.Split('\t');
                if (tab.Length < 3 || FenceSpline.ByName(tab[0]) is not FenceSpline.Style style) continue;
                var pts = ParsePoints(tab[2]);
                if (pts == null || pts.Length < 2) continue;
                var wrecks = tab.Length > 3 ? ParsePoints(tab[3]) : null;
                if (AddFenceSpline(style, pts, tab[1] == "curve", wrecks) != null) n++;
            }
            if (n > 0) Log.Print($"[editor] loaded {n} fence spline(s)");
        }

        /// <summary>"x y z x y z ..." with z negated back, or null if it does not parse.</summary>
        static Vector3[] ParsePoints(string field)
        {
            var p = field.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || p.Length % 3 != 0) return null;
            var pts = new Vector3[p.Length / 3];
            for (int i = 0; i < pts.Length; i++)
            {
                if (!float.TryParse(p[i * 3], out float x) || !float.TryParse(p[i * 3 + 1], out float y)
                    || !float.TryParse(p[i * 3 + 2], out float z)) return null;
                pts[i] = new Vector3(x, y, -z);
            }
            return pts;
        }
    }
}
