using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>"Tunnel": run the shipped tunnel bore along a road spline and portal both ends.
    ///
    /// Master 2026-10-09, straight after the bridge landed: "now work on tunnel splines. same method."
    /// Same method, and the differences are the interesting part.
    ///
    /// ⭐⭐ THE BORE IS SWEPT, NOT TILED, and that is the correction master asked for: "theres gaps and
    /// probably overlaps inside the tunnel. you need cuts/blending." Tiling a rigid section works for the
    /// fence, the rail and the bridge deck because their joint overlap is buried in solid geometry. A tunnel
    /// is hollow and you stand inside it, so the overlap that closes the outer shell (half-width 12 m)
    /// over-closes the bore (8 m) and leaves a lip at every joint on the one surface anybody sees. No single
    /// value settles both walls, because they are at different radii. A sweep has no joint at all -- see
    /// TunnelMesh. The scaled-prop tiling that preceded it is gone.
    ///
    /// ⭐ AND RETAIL ALREADY SHIPS THE END-CLOSER. Tunnel_Line_Cap_0 is the portal: the same section with a
    /// 16-triangle facade across its Y=+12 end. The bridge had to have one generated because its spans are
    /// open-ended tubes; here there is nothing to invent.
    ///
    /// ⚠ THE PAINTED RIBBON STAYS. A bridge deck carries its own roadway, so the tool hides the road under
    /// it; a tunnel is an arch with NO FLOOR -- no face anywhere in its Z=-1 plane -- and the road you drive
    /// through it is the road. Hiding the ribbon here would leave the tunnel sitting over bare terrain.</summary>
    public partial class EditorTunnelSpline : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorObjects _objects;
        readonly Terrain _terr;
        readonly RoadField _roads;

        public const string BoreUnit = "Tunnel_Line_0";
        public const string PortalUnit = "Tunnel_Line_Cap_0";

        /// <summary>The shipped section's own length (Y -12..+12), measured off the mesh. Both the scale
        /// factor and the portal's footprint are derived from it rather than typed twice.</summary>
        public const float SourceLength = 24.0f;


        /// <summary>Half the section's width (X -12..+12). Drives how much each joint has to close on a bend
        /// -- and at 12 m this is the widest thing the port tiles, so the overlap matters more here than
        /// anywhere else.</summary>
        public const float HalfWidth = 12.0f;

        /// <summary>The portal piece's length along the run; it IS a section, so it occupies run rather than
        /// extending past it. The bore is laid between the two.</summary>
        public const float PortalLength = SourceLength;

        /// <summary>How far apart the swept rings sit. 2 m keeps the chord error under a millimetre even on
        /// the tightest real highway bend (301 m -> 2^2/(8*301) = 0.17 cm), and a ring costs a handful of
        /// triangles rather than a whole prop, so there is no reason to space them like tiles.</summary>
        public const float Step = 2.0f;

        bool _on;

        public bool Active => _on;
        public string ModeText => _on
            ? "TUNNEL · U = bore the tunnel along the road spline under the cursor, portals at both ends · Shift+U = off"
            : "Shift+U = tunnel";

        public EditorTunnelSpline(Editor editor, Camera3D cam, EditorObjects objects, Terrain terr, RoadField roads)
        {
            _editor = editor; _cam = cam; _objects = objects; _terr = terr; _roads = roads;
        }

        public void SetActive(bool on) { if (_on != on) _on = on; }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.U } && Input.IsKeyPressed(Key.Shift))
            { _on = !_on; GetViewport().SetInputAsHandled(); return; }
            if (!_on || _roads == null) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.U } && !Input.IsKeyPressed(Key.Shift))
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var at)) return;
                if (!_roads.NearestRoad(at, out int road, out _))
                { Log.Print("[editor-tunnel] no road spline nearby -- draw one with the road tool first"); return; }
                var placed = new List<Node3D>();
                if (LayAlong(_objects, _terr, _roads, road, placed) == 0) return;
                _editor.PushUndo("bore tunnel", () => _objects.RemovePlaced(placed));
                _editor.MarkDirty();
            }
        }

        /// <summary>Bore the tunnel along a road spline, portalling both ends. Returns the bore sections laid.</summary>
        public static int LayAlong(EditorObjects objects, Terrain terr, RoadField roads, int road,
                                   List<Node3D> placed)
        {
            if (objects == null || roads == null) return 0;
            float total = roads.RoadLength(road);
            float lift = roads.RoadSurfaceOffset(road);

            // ⭐ THE PORTALS OCCUPY RUN, THEY DO NOT EXTEND PAST IT. A portal is a full 24 m section with a
            // facade on one end, so laying bore under it would stack two identical shells and z-fight the
            // whole length of both. The bore therefore runs BETWEEN them.
            bool portals = total >= PortalLength * 2f;
            float boreFrom = portals ? PortalLength : 0f;
            float boreTo = portals ? total - PortalLength : total;
            if (!portals && total < Step * 2f)
            { Log.Print($"[editor-tunnel] {total:0.#} m is too short to sweep -- nothing bored"); return 0; }

            // ⭐⭐ SWEPT, NOT TILED. Master on the tiled first version: "theres gaps and probably overlaps
            // inside the tunnel. you need cuts/blending." Consecutive rings of a sweep SHARE their vertices,
            // so every station is mitered exactly and there is no joint to open or bury -- see TunnelMesh for
            // why no single overlap value could have settled both walls of a hollow section at once.
            var profile = objects.TunnelProfile();
            if (profile == null)
            { Log.Print("[editor-tunnel] could not read the section profile off Tunnel_Line_0 -- nothing bored"); return 0; }

            var centre = new System.Collections.Generic.List<Vector3>();
            for (float t = boreFrom; t <= boreTo + 1e-3f; t += Step)
            {
                if (!roads.EvaluateAlong(road, Mathf.Min(t, boreTo), out var cp, out _, snapTerrain: false)) break;
                cp.Y += lift;
                if (centre.Count == 0 || cp.DistanceSquaredTo(centre[centre.Count - 1]) > 1e-6f) centre.Add(cp);
            }
            int n = centre.Count;
            if (n >= 2)
            {
                var mesh = TunnelMesh.Sweep(profile, centre);
                var node = objects.AddGeneratedTunnel(mesh, centre.ToArray());
                if (node != null) placed?.Add(node); else n = 0;
            }
            else n = 0;

            int ports = 0;
            if (portals)
            {
                // Each portal's facade is its local +Y end, so it is turned to face OUT of the tunnel: the
                // entry portal looks back down the run, the exit portal looks forward along it.
                if (roads.EvaluateAlong(road, PortalLength * 0.5f, out var a0, out var at0, snapTerrain: false))
                {
                    a0.Y += lift;
                    var p = objects.Place(PortalUnit, a0, StandBasis(-at0.Normalized()));
                    if (p != null) { placed?.Add(p); ports++; }
                }
                if (roads.EvaluateAlong(road, total - PortalLength * 0.5f, out var b0, out var bt0, snapTerrain: false))
                {
                    b0.Y += lift;
                    var p = objects.Place(PortalUnit, b0, StandBasis(bt0.Normalized()));
                    if (p != null) { placed?.Add(p); ports++; }
                }
            }

            if (n == 0 && ports == 0)
            { Log.Print($"[editor-tunnel] {total:0.#} m laid nothing -- the spline could not be walked"); return 0; }

            Log.Print($"[editor-tunnel] road {road}: {total:0.#} m -> swept bore of {n} ring(s) at {Step:0.##} m"
                    + $" + {ports} portal(s)"
                    + (portals ? "" : $"  ⚠ under {PortalLength * 2f:0.#} m, too short to portal")
                    + ", rings share vertices so no joint can gap; painted road kept (a tunnel has no floor)");
            return n;
        }

        /// <summary>Stand the Z-up section on the run. No grade tilt and no terrain seating: a tunnel follows
        /// the road's own line through a hill, which is the whole reason it is a tunnel.</summary>
        public static Basis StandBasis(Vector3 dir)
        {
            var flat = new Vector3(dir.X, 0f, dir.Z);
            if (flat.LengthSquared() < 1e-8f) return EditorObjects.FromEuler(270f, 0f, 0f);
            flat = flat.Normalized();
            return EditorObjects.FromEuler(270f, ProcIsland.YawForDir(flat.X, -flat.Z), 0f);
        }

        bool RaycastTerrain(Vector2 screen, out Vector3 point)
        {
            point = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 4000f;
            var q = PhysicsRayQueryParameters3D.Create(from, to, 1u << 0);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"];
            return true;
        }
    }
}
