using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>"New Rail": tile astraclaw's modelled 2 m track unit along a Tracks spline.
    ///
    /// Master 2026-10-09, on the modelled rail mockup: "get that as a new 'new rail' spline".
    ///
    /// ⭐ IT IS NOT A NEW ROAD MATERIAL, AND IT CANNOT BE. RoadField extrudes a 4-vert trapezoid
    /// cross-section per sample (`verts = pos ± side*halfWidth ± normal*halfDepth`) -- a flat tapered strip,
    /// which is exactly why the existing Tracks type is PAINTED rails on a ribbon. A profile with two rails, a
    /// sleeper and a tapered ballast bed has no trapezoid to be. So the rail is modelled geometry TILED along
    /// the spline, and the spline itself stays an ordinary Tracks road.
    ///
    /// ⭐⭐ WHICH IS ALSO WHAT KEEPS THE TRAINS RUNNING. Train.cs and the console both find track through
    /// RoadField.NearestTrack, which skips every road whose Material is not TracksMaterial. Had "New Rail"
    /// been a genuinely separate material, every train would have quietly failed to find the beautiful new
    /// track -- no error, nothing to see. Tiling onto a material-4 spline means train alignment is untouched
    /// by construction rather than by remembering to update it.
    ///
    /// ⚠ THE UNIT'S CONVENTIONS ARE THE OPPOSITE OF EVERY OTHER PROP HERE, which is why this does not reuse
    /// EditorFenceRoad's placement. Ripped props are Z-up with their length on local +Y and are stood up with
    /// a baked ex=270; this unit is authored +Y up, +Z along the track, already at drawing scale, with its
    /// root ON the spline centreline datum. So: no ex=270, no recentre, no rescale, and the basis is built
    /// directly from the direction rather than through a yaw. astraclaw's handoff says it in as many words --
    /// "avoid the proc/world sign convention in ProcIsland.YawForDir for a world direction" -- and that
    /// convention is precisely what tipped the ice on its side and mirrored the first fence curve.</summary>
    public partial class EditorRailSpline : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorObjects _objects;
        readonly Terrain _terr;
        readonly RoadField _roads;

        public const string Unit = "New_Rail_Unit";
        public const string Sleeper = "New_Rail_Sleeper";

        /// <summary>2.000 m, and NOT the unit's 2.5 m bounding box. Rails and ballast run Z=0..2; the one
        /// sleeper is centred at Z=0 and overhangs to -0.5, so the bbox is half a sleeper longer than the
        /// repeat. Stepping by the bbox would leave a 0.5 m hole in the rail at every joint -- the same
        /// mistake the fence made with its 16.25 m bbox against a 16.0 m pitch, which is why astraclaw wrote
        /// "the bbox is 2.5 m long; that is NOT the pitch" into the handoff.</summary>
        public const float Pitch = 2.0f;

        /// <summary>Half the unit's lateral extent, measured off the mesh (X -3.467..+3.467). Only used to
        /// decide how tight a curve the tile can follow -- see MinRadiusFor.</summary>
        public const float HalfWidth = 3.467f;

        /// <summary>How far the OUTER edge of two neighbouring tiles may part on a bend before it reads as a
        /// gap in the ballast. 5 cm: under the ~15 mm rail bevel it would be invisible, over ~10 cm you can
        /// see daylight through the bed.</summary>
        public const float MaxJointGap = 0.05f;

        /// <summary>⭐ THE CHORD POLICY astraclaw's handoff asks for, and the reason a wide tile is NOT the
        /// same problem as a narrow one. The fence's unit is 0.5 m across, so chording a curve with it costs
        /// millimetres; this one is 6.9 m across, and the outer corners of two tiles meeting at a turn of
        /// `Pitch / R` part by roughly `HalfWidth * Pitch / R`. Keeping that under MaxJointGap needs
        /// R >= HalfWidth * Pitch / MaxJointGap, which is ~139 m -- large, and entirely normal for rail, where
        /// mainline curves run to several hundred metres. Reported rather than refused: the mapper may want a
        /// tight yard curve and accept the joints.</summary>
        public static float MinRadius => HalfWidth * Pitch / MaxJointGap;

        bool _on;

        public bool Active => _on;
        public string ModeText => _on
            ? $"NEW RAIL · T = lay the modelled rail along the Tracks spline under the cursor · Shift+T = off"
            : "Shift+T = new rail";

        public EditorRailSpline(Editor editor, Camera3D cam, EditorObjects objects, Terrain terr, RoadField roads)
        {
            _editor = editor; _cam = cam; _objects = objects; _terr = terr; _roads = roads;
        }

        public void SetActive(bool on) { if (_on != on) _on = on; }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.T } && Input.IsKeyPressed(Key.Shift))
            { _on = !_on; GetViewport().SetInputAsHandled(); return; }
            if (!_on || _roads == null) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.T } && !Input.IsKeyPressed(Key.Shift))
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var at)) return;
                if (!_roads.NearestTrack(at, out int road, out _))
                { Log.Print("[editor-rail] no TRACKS spline nearby -- draw one with the road tool first (material 4)"); return; }
                var placed = new List<Node3D>();
                int n = LayAlong(_objects, _terr, _roads, road, placed);
                if (n == 0) return;
                _editor.PushUndo("lay new rail", () => _objects.RemovePlaced(placed));
                _editor.MarkDirty();
            }
        }

        /// <summary>Tile the unit along a track spline. Returns how many units went down.</summary>
        public static int LayAlong(EditorObjects objects, Terrain terr, RoadField roads, int road,
                                   List<Node3D> placed)
        {
            if (objects == null || roads == null) return 0;
            float total = roads.RoadLength(road);
            int n = Mathf.FloorToInt(total / Pitch);
            if (n <= 0) { Log.Print($"[editor-rail] {total:0.#} m is under one {Pitch:0.##} m unit -- nothing laid"); return 0; }

            float tightest = float.PositiveInfinity;
            Vector3 prevDir = Vector3.Zero;

            for (int i = 0; i < n; i++)
            {
                float s = i * Pitch;
                if (!roads.EvaluateAlong(road, s, out var p0, out _)) continue;
                if (!roads.EvaluateAlong(road, s + Pitch, out var p1, out _)) continue;
                var dir = p1 - p0;
                if (dir.LengthSquared() < 1e-8f) continue;
                dir = dir.Normalized();

                if (prevDir != Vector3.Zero)
                {
                    float turn = Mathf.Abs(prevDir.AngleTo(dir));
                    if (turn > 1e-4f) tightest = Mathf.Min(tightest, Pitch / turn);
                }
                prevDir = dir;

                // ⚠ THE ROOT GOES ON THE SPLINE, NOT ON THE GROUND. The unit's datum is the centreline and its
                // ballast is modelled BELOW it (Y down to -0.44). Seating it by its bbox -- which is what every
                // other prop here wants -- would lift the whole track half a metre into the air.
                var p = objects.Place(Unit, p0, BasisFor(terr, p0, dir));
                if (p != null) placed?.Add(p);
            }

            // ⭐ ONE TERMINAL SLEEPER, at station 2*N. Each unit carries the sleeper at its START, so N units
            // give N sleepers and the last 2 m of rail ends on nothing; this closes it. Putting a sleeper at
            // both ends of every unit instead would double all N-1 interior ones.
            if (roads.EvaluateAlong(road, n * Pitch, out var endP, out _)
                && roads.EvaluateAlong(road, Mathf.Max(0f, n * Pitch - Pitch), out var beforeP, out _))
            {
                var d = endP - beforeP;
                if (d.LengthSquared() > 1e-8f)
                {
                    var e = objects.Place(Sleeper, endP, BasisFor(terr, endP, d.Normalized()));
                    if (e != null) placed?.Add(e);
                }
            }

            Log.Print($"[editor-rail] road {road}: {total:0.#} m -> {n} unit(s) at {Pitch:0.##} m + 1 terminal sleeper"
                    + (float.IsPositiveInfinity(tightest) ? ", straight"
                       : tightest < MinRadius
                         ? $"  ⚠ tightest bend ~{tightest:0} m, under the {MinRadius:0} m a {HalfWidth * 2f:0.#} m-wide tile "
                           + $"can hold within {MaxJointGap * 100f:0} cm at its outer edge"
                         : $", tightest bend ~{tightest:0} m"));
            return n;
        }

        /// <summary>The basis astraclaw's handoff specifies verbatim: X = normal x direction, Y = normal,
        /// Z = direction.
        ///
        /// ⚠ BUILT DIRECTLY, NOT THROUGH A YAW. Every other placement here goes through
        /// EditorObjects.FromEuler / ProcIsland.YawForDir, which carry a Z-up mesh convention and a proc-frame
        /// sign flip. Routing a Y-up, +Z-forward mesh through either is how you get a mirrored or toppled
        /// run -- both of which this project has shipped.</summary>
        static Basis BasisFor(Terrain terr, Vector3 at, Vector3 dir)
        {
            var up = terr != null ? terr.NormalAt(at.X, at.Z) : Vector3.Up;
            var z = dir.Normalized();
            var x = up.Cross(z);
            if (x.LengthSquared() < 1e-8f) { up = Vector3.Up; x = up.Cross(z); }
            x = x.Normalized();
            var y = z.Cross(x).Normalized();   // re-orthogonalise: the terrain normal is not perpendicular to the run
            return new Basis(x, y, z);
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
