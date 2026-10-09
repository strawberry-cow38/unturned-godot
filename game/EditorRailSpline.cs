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

        /// <summary>⚠⚠ THE SEAM MASTER SAW, AND WHY IT IS NOT AN ASSET BUG. Master 2026-10-09, on the first
        /// New Rail render: "nice but you can see a seam at each chunk" -- a dark hairline down the ballast
        /// slope at every 2 m joint.
        ///
        /// ⭐ IT IS THE CHORD WEDGE, measured, not guessed. Two RIGID tiles meeting at a turn of θ = Pitch/R
        /// have parallel-ended profiles whose planes splay by θ about the centreline, so the OUTER corners
        /// part by HalfWidth*θ while the inner ones overlap by the same. On the showcase's 203 m bend that is
        /// 3.467 * 2/203 = 3.4 cm of open slot -- plenty to see into, and nothing an exporter can fix, because
        /// a flat-ended tile simply cannot follow an arc.
        ///
        /// ⚠ TWO WRONG DIAGNOSES CAME FIRST, both killed by a control. (1) The unit carries 10 cap triangles
        /// at each end, so every interior joint stacks two coincident faces -- certain z-fighting, and the
        /// obvious culprit. Stripping them made the seam markedly WORSE, because the caps are what the slot's
        /// far wall is made of. (2) A normals break at the tiling seam: the two end profiles turn out to carry
        /// identical normals on every side and top vertex, differing only in the caps' own ±Z, which is correct.
        /// What settled it was a DEAD-STRAIGHT control run -- zero wedge by construction -- which came back
        /// with a perfectly clean slope. See [[feedback_calibrate_the_instrument]]: the first instrument I
        /// pointed at this counted edge pixels across a band that was mostly SLEEPER edges, so it could not
        /// have responded to a 1-px joint crease either way, and it duly reported nothing.</summary>
        public const float MaxJointGap = 0.05f;

        /// <summary>⭐ WHAT CLOSES IT: back each tile off along its own axis by exactly the wedge it opens,
        /// so consecutive OUTER corners land on each other. Returns the overlap for a joint of `turn` radians.
        ///
        /// ⭐⭐ AND IT IS FREE ON A STRAIGHT, which is the property worth having: turn = 0 gives overlap 0, so
        /// a straight run is placed exactly as before -- bit-identical, and the control render proves that case
        /// was already clean. Only a curve pays, and it pays precisely what it costs.
        ///
        /// The inner corners then overlap by 2*HalfWidth*turn, which is invisible: it is solid inside solid.
        /// The rail tops of the two tiles cross at θ rather than lying coplanar, so the overlap is a transverse
        /// crossing and NOT a z-fight -- the surfaces part by turn*overlap/2, under 0.2 mm at the showcase bend.
        ///
        /// ⚠ Clamped to half the pitch so the walk always advances. It only binds past turn ≈ 0.29 rad, i.e.
        /// a 7 m radius, which is far tighter than the tool warns about anyway.</summary>
        public static float OverlapFor(float turn) => Mathf.Min(HalfWidth * Mathf.Abs(turn), Pitch * 0.5f);

        /// <summary>⭐ THE CHORD POLICY astraclaw's handoff asks for, reported rather than refused. With the
        /// overlap above, the outer gap is closed at every radius, so this no longer measures daylight -- what
        /// is left on a tight bend is the FACETING: a rigid 2 m chord deviates from the true arc by
        /// Pitch^2/(8R), and each joint kinks the railhead by Pitch/R. The old threshold is kept as the line
        /// where that faceting starts to read, which is ~139 m -- large, and entirely normal for rail, where
        /// mainline curves run to several hundred metres. The mapper may still want a tight yard curve.</summary>
        public static float MinRadius => HalfWidth * Pitch / MaxJointGap;

        /// <summary>L1 seam: lay with the joint overlap DISABLED, i.e. the placement rule as it shipped
        /// before the seam fix. The curve test needs a control that re-runs the real path with only the fix
        /// removed -- a control computed from the formula would agree with the fix by construction and could
        /// never fail. See [[feedback_test_must_reject_the_bug]].</summary>
        public static bool DebugNoJointOverlap;

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
            if (total < Pitch) { Log.Print($"[editor-rail] {total:0.#} m is under one {Pitch:0.##} m unit -- nothing laid"); return 0; }

            // ⚠⚠ NO LIFT. The unit is datumed on the native centreline and carries its own ballast down to
            // -0.44, so it goes at the spline point exactly as authored. I lifted it by the ribbon's
            // RoadSurfaceOffset first, because the painted strip was burying it -- that made the rails
            // VISIBLE and put the railhead 0.44 m off its datum, which is a different thing from correct.
            // The ribbon is hidden instead, below.
            //
            // ⚠ THE WALK IS NOT i * Pitch ANY MORE. Each tile is backed off by OverlapFor(turn) so its outer
            // corner lands on its neighbour's -- see MaxJointGap for the seam that forced it -- so stations
            // drift behind the arc length and the count cannot be computed up front. On a straight every
            // overlap is 0 and the stations are exactly i * Pitch, as before.
            float tightest = float.PositiveInfinity;
            float totalOverlap = 0f;
            int n = 0;
            float s = 0f;
            Vector3 lastP = Vector3.Zero, lastDir = Vector3.Zero;

            while (s + Pitch <= total + 1e-3f)
            {
                if (!roads.EvaluateAlong(road, s, out var p0, out _)) break;
                if (!roads.EvaluateAlong(road, s + Pitch, out var p1, out _)) break;
                var dir = p1 - p0;
                if (dir.LengthSquared() < 1e-8f) break;
                dir = dir.Normalized();

                // ⚠ THE ROOT GOES ON THE SPLINE, NOT ON THE GROUND. The unit's datum is the centreline and its
                // ballast is modelled BELOW it (Y down to -0.44). Seating it by its bbox -- which is what every
                // other prop here wants -- would lift the whole track half a metre into the air.
                var p = objects.Place(Unit, p0, BasisFor(terr, p0, dir));
                if (p != null) placed?.Add(p);
                lastP = p0; lastDir = dir; n++;

                // The turn INTO the next tile, which is what this joint has to absorb. Measured from the
                // spline rather than from the previous tile, so the overlap is applied at the joint that
                // actually bends rather than one tile late.
                float turn = 0f;
                if (roads.EvaluateAlong(road, s + Pitch, out var q0, out _)
                    && roads.EvaluateAlong(road, s + Pitch * 2f, out var q1, out _))
                {
                    var nd = q1 - q0;
                    if (nd.LengthSquared() > 1e-8f) turn = Mathf.Abs(dir.AngleTo(nd.Normalized()));
                }
                if (turn > 1e-4f) tightest = Mathf.Min(tightest, Pitch / turn);

                float overlap = DebugNoJointOverlap ? 0f : OverlapFor(turn);
                totalOverlap += overlap;
                s += Pitch - overlap;
            }

            if (n == 0) { Log.Print($"[editor-rail] {total:0.#} m laid nothing -- the spline could not be walked"); return 0; }

            // ⭐ ONE TERMINAL SLEEPER, closing the last tile. Each unit carries the sleeper at its START, so N
            // units give N sleepers and the last 2 m of rail ends on nothing; this closes it. Putting a sleeper
            // at both ends of every unit instead would double all N-1 interior ones.
            //
            // ⚠ PLACED OFF THE LAST TILE, NOT OFF THE SPLINE. It used to be evaluated at station n*Pitch, which
            // was the same point only while the walk stepped by exactly Pitch. With overlaps the arc length has
            // drifted, and re-evaluating would park the closing sleeper short of the rail it is meant to close.
            {
                var e = objects.Place(Sleeper, lastP + lastDir * Pitch, BasisFor(terr, lastP + lastDir * Pitch, lastDir));
                if (e != null) placed?.Add(e);
            }

            // ⭐ AND RETIRE THE PAINTED STRIP. The modelled track replaces it; leaving both draws the old
            // rails underneath the new ones. The ROAD stays material-4 so Train.cs can still find it.
            roads.SetRoadRibbonVisible(road, false);

            // The overlap is REPORTED, not silent: it is the one number that says whether the seam fix did
            // anything on this spline, and a run that prints 0.0 cm on a bend means the walk never saw the turn.
            Log.Print($"[editor-rail] road {road}: {total:0.#} m -> {n} unit(s) at {Pitch:0.##} m + 1 terminal sleeper, painted ribbon hidden"
                    + (float.IsPositiveInfinity(tightest) ? ", straight (no joint overlap needed)"
                       : $", tightest bend ~{tightest:0} m, joints closed by {totalOverlap / n * 100f:0.0} cm avg"
                         + (tightest < MinRadius
                            ? $"  ⚠ under {MinRadius:0} m a {HalfWidth * 2f:0.#} m-wide rigid tile starts to read as FACETS "
                              + $"({Pitch * Pitch / (8f * tightest) * 1000f:0.#} mm off the arc per chord)"
                            : "")));
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
