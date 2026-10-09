using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>"New Rail": the modelled 2 m unit tiled along a Tracks spline.
    ///
    /// Master 2026-10-09: "get that as a new 'new rail' spline". astraclaw supplied the unit with an explicit
    /// contract -- +Y up, +Z along the track, 2.000 m pitch, root on the spline centreline datum, no recentre
    /// or rescale -- and every one of those is a thing that fails QUIETLY if the wiring disagrees with it.
    /// So the checks below are against the CONTRACT, not against the code's own idea of itself.</summary>
    public sealed class RailSplineTests : GameTest
    {
        public override string Name => "editor.new_rail_spline";
        public override double TimeoutSimSeconds => 30;


        /// <summary>Lateral (X) centre of each cluster of geometry passing `keepY`, left to right.</summary>
        static List<float> LateralCentres(string resPath, System.Func<float, bool> keepY, float tol = 0.5f)
        {
            var xs = new List<float>();
            foreach (var v in Verts(resPath)) if (keepY(v.Y)) xs.Add(Mathf.Round(v.X * 1000f) / 1000f);
            return Cluster(xs, tol);
        }

        static IEnumerable<Vector3> Verts(string resPath)
        {
            string path = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(path)) yield break;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (!line.StartsWith("v ")) continue;
                var t = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 4)
                    yield return new Vector3(float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci));
            }
        }

        /// <summary>Group values within `tol` and return each group's centre, ascending. 0.5 m merges each
        /// railhead's two edges into one rail; 0.2 m keeps them apart, which is how the head's WIDTH is
        /// measured rather than assumed.</summary>
        static List<float> Cluster(List<float> xs, float tol = 0.5f)
        {
            var outp = new List<float>();
            if (xs.Count == 0) return outp;
            xs.Sort();
            float lo = xs[0], hi = xs[0];
            foreach (var x in xs)
            {
                if (x - hi < tol) { hi = x; continue; }
                outp.Add((lo + hi) / 2f); lo = hi = x;
            }
            outp.Add((lo + hi) / 2f);
            return outp;
        }

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var cam = new Camera3D();
            World.AddChild(cam);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            var field = new RoadField();
            World.AddChild(field);
            field.DebugSetMaterialWidth(RoadField.TracksMaterial, 4.0f);
            field.DebugSetMaterialDepth(RoadField.TracksMaterial, 0.44f);   // a real ribbon has depth -- see below
            yield return Ticks(2);

            // ---- 0. THE ASSET ARRIVED UNMODIFIED. astraclaw's contract is a set of numbers in the mesh, and
            // if a converter had been run over it they would all be wrong at once.
            var unit = ContentProvider.ParseObj("res://content/objects/New_Rail_Unit.obj");
            T.Check("the unit mesh loads", unit != null && unit.GetSurfaceCount() > 0);
            if (unit == null) yield break;
            var box = unit.GetAabb();
            T.Check($"...+Z along the track, 2.5 m bbox over a {EditorRailSpline.Pitch:0.##} m pitch "
                  + $"(Z {box.Position.Z:0.##}..{box.End.Z:0.##})",
                    Mathf.Abs(box.Position.Z - -0.5f) < 0.02f && Mathf.Abs(box.End.Z - 2.0f) < 0.02f);
            // ⭐ THE DATUM IS THE CHECK THAT CATCHES A RECENTRE. Ballast is modelled BELOW the centreline, so
            // the root is NOT at the bottom of the mesh -- floor-aligning it (what every other prop here
            // wants) would lift the whole track 0.44 m into the air and nothing would look obviously wrong.
            T.Check($"...root on the centreline datum, ballast below it (Y {box.Position.Y:0.##}..{box.End.Y:0.##}, "
                  + "rail top should be +0.31, buried ballast -0.44)",
                    box.Position.Y < -0.4f && Mathf.Abs(box.End.Y - 0.31f) < 0.02f);

            // ---- 0b. ⭐⭐ THE RAIL'S GAUGE MATCHES THE TRAIN THAT RUNS ON IT.
            //
            // ⚠⚠ AND THAT TRAIN IS Train.cs, NOT Train_Engine_0.obj. This check used to measure the
            // scenery PROP, whose lowest band sits at +/-1.439 -- the same as the rail -- so it passed by ONE
            // MILLIMETRE while the drivable train, whose wheels are Train.WheelOff at +/-1.47, is 62 mm
            // wider. Nothing drives the prop. [[feedback_find_the_position_source]]
            //
            // ⚠ Y > 0.19 isolates the RAILS. The sleeper tops sit at 0.18, so a band starting at 0.17
            // measures sleeper-plus-rail and reports the wrong centres -- my first pass did exactly that and
            // put V1's rails at -1.779/+1.618 when they were really -1.698/+1.536. astraclaw caught it. The
            // conclusion (too wide, off-centre) survived, the numbers did not.
            var railX = LateralCentres("res://content/objects/New_Rail_Unit.obj", y => y > 0.19f);
            var headX = LateralCentres("res://content/objects/New_Rail_Unit.obj", y => y > 0.19f, 0.2f);
            T.Check($"fixture: two rails found, each a railhead with two edges ({railX.Count} rails, "
                  + $"{headX.Count} edges)", railX.Count == 2 && headX.Count == 4);
            if (railX.Count == 2 && headX.Count == 4)
            {
                float headHalf = (headX[1] - headX[0]) / 2f;
                T.Check($"fixture: the railhead has real width ({headHalf * 2f:0.000} m) -- at 0 the check "
                      + $"below is vacuous", headHalf > 0.05f);

                // ⭐ THE FUNCTIONAL INVARIANT: the wheel has to land ON the railhead, and not merely near
                // it. Scored against the MIDDLE HALF rather than the full width, so a future export that
                // drifts to the edge still fails while it is still only ugly -- an assertion that only fires
                // once the train is already off the steel is an assertion that never helps.
                float wheel = Train.DebugWheelHalfGauge;
                float centre = (railX[1] - railX[0]) / 2f;
                float off = Mathf.Abs(wheel - centre);
                T.Check($"the train's wheel runs on the railhead's middle half (wheel +/-{wheel:0.000}, rail "
                      + $"centre +/-{centre:0.000}, {off * 1000f:0} mm out of a {headHalf * 1000f:0} mm half-head)",
                        off < headHalf * 0.5f);

                // ⭐ AND CENTRED. Equal gauge off-centre still runs the train down one rail: the painted
                // road_4 Tracks texture is exactly that (bands at 0.164/0.789 of the ribbon, midpoint 0.477),
                // and V1 of this unit faithfully reproduced its 0.08 m offset.
                T.Check($"...and the rail pair is centred on the spline (midpoint "
                      + $"{(railX[0] + railX[1]) / 2f:+0.000})",
                        Mathf.Abs((railX[0] + railX[1]) / 2f) < 0.02f);
            }

            // ---- 1. A TRACKS SPLINE, and the rail tiled along it.
            var pts = new List<Vector3>();
            for (int k = 0; k <= 10; k++) pts.Add(new Vector3(k * 10f, 0f, 0f));
            int road = field.AddRoadFromPolyline(pts, RoadField.TracksMaterial);
            T.Check($"fixture: a TRACKS road exists ({field.RoadLength(road):0.#} m, material "
                  + $"{field.RoadMaterialOf(road)})",
                    road >= 0 && field.RoadMaterialOf(road) == RoadField.TracksMaterial);

            int before = objs.PlacedCount;
            var placed = new List<Node3D>();
            int n = EditorRailSpline.LayAlong(objs, null, field, road, placed);
            int want = Mathf.FloorToInt(field.RoadLength(road) / EditorRailSpline.Pitch);
            T.Check($"laid {n} unit(s) at a {EditorRailSpline.Pitch:0.##} m pitch over "
                  + $"{field.RoadLength(road):0.#} m (expected {want})", n == want);
            T.Check($"...plus exactly ONE terminal sleeper ({objs.PlacedCount - before} props for {n} units)",
                    objs.PlacedCount - before == n + 1);

            // ⭐ NO DOUBLED INTERIOR SLEEPERS. Each unit carries its sleeper at the START, so N units give N
            // and the terminal closes the run. A sleeper at both ends of every unit is 2N-1 -- the same
            // doubling the fence had, and the reason astraclaw shipped a separate terminal piece at all.
            int sleepers = 0, units = 0;
            foreach (var _ in objs.PlacedOf(EditorRailSpline.Sleeper)) sleepers++;
            foreach (var _ in objs.PlacedOf(EditorRailSpline.Unit)) units++;
            T.Check($"...one sleeper per unit plus the terminal, none doubled ({units} units, {sleepers} terminal)",
                    units == n && sleepers == 1);

            // ---- 1b. ⭐⭐ ON THE SPLINE, WITH THE PAINTED RIBBON RETIRED. EvaluateAlong returns the SPLINE
            // point and the drawn ribbon's surface is RoadSurfaceOffset above it, so the modelled rail's
            // 0.31 m tops sat 0.13 m UNDER the painted ones and every screenshot was of the old track. Master
            // caught it by eye: "the spline ur showing here is the old one."
            //
            // ⚠ AND THE FIRST FIX WAS THE WRONG ONE. I lifted the unit by RoadSurfaceOffset, which made the
            // steel visible -- and moved the railhead 0.44 m off the datum astraclaw had already fitted to the
            // train. astraclaw's correction, verbatim: "Lifting above the old ribbon proves visibility, not
            // wheel contact." The unit goes at the raw spline point and the RIBBON is hidden instead.
            float lift = field.RoadSurfaceOffset(road);
            T.Check($"fixture: the ribbon has a real surface offset ({lift:0.###} m) -- at 0 this pair is vacuous",
                    lift > 0.1f);
            float wantY = 0f, gotY = float.MinValue;
            if (field.EvaluateAlong(road, 0f, out var sp, out _))
            {
                wantY = sp.Y;
                foreach (var x in objs.PlacedOf(EditorRailSpline.Unit))
                    if (Mathf.Abs(x.Origin.X - sp.X) < 0.5f) { gotY = x.Origin.Y; break; }
            }
            T.Check($"the unit sits on the RAW spline point, not lifted onto the ribbon (y {gotY:0.###}, "
                  + $"expected {wantY:0.###}; the lifted answer would be {wantY + lift:0.###})",
                    Mathf.Abs(gotY - wantY) < 0.01f);
            T.Check($"...and the painted ribbon is hidden, so the old rails do not draw under the new ones",
                    !field.RoadRibbonVisible(road));
            T.Check($"...while the road is STILL material {RoadField.TracksMaterial} -- hiding the paint must "
                  + $"not retire the track (got {field.RoadMaterialOf(road)})",
                    field.RoadMaterialOf(road) == RoadField.TracksMaterial);

            // ---- 2. THE PITCH IS 2.000, NOT THE 2.5 m BBOX. Stepping by the bbox leaves a half-metre hole in
            // the rail at every joint, which is the mistake the fence made with 16.25 against 16.0.
            var xs = new List<float>();
            foreach (var x in objs.PlacedOf(EditorRailSpline.Unit)) xs.Add(x.Origin.X);
            xs.Sort();
            float worst = 0f;
            for (int i = 1; i < xs.Count; i++) worst = Mathf.Max(worst, Mathf.Abs((xs[i] - xs[i - 1]) - EditorRailSpline.Pitch));
            T.Check($"units sit exactly {EditorRailSpline.Pitch:0.##} m apart (worst error {worst:0.####} m over "
                  + $"{xs.Count} units)", xs.Count > 1 && worst < 0.01f);

            // ---- 3. ⭐⭐ THE TRAINS CAN STILL FIND IT. This is the one that would have been silent: Train.cs
            // and the console both locate track through NearestTrack, which skips any road whose material is
            // not TracksMaterial. Had "New Rail" been given a material of its own, every train would have
            // failed to find the new track with no error anywhere.
            bool found = field.NearestTrack(new Vector3(50f, 0f, 0f), out int tr, out float along);
            T.Check($"a train can still find this track (NearestTrack -> road {tr} at {along:0.#} m)",
                    found && tr == road);

            // ⭐ CONTROL: the same spline on an ordinary road material must NOT be findable as track --
            // otherwise the check above passes on a NearestTrack that matches everything.
            var rp = new List<Vector3>();
            for (int k = 0; k <= 10; k++) rp.Add(new Vector3(k * 10f, 0f, 500f));
            int plain = field.AddRoadFromPolyline(rp);   // material 0
            bool foundPlain = field.NearestTrack(new Vector3(50f, 0f, 500f), out int pr, out _);
            T.Check($"control: a plain road is NOT track (material {field.RoadMaterialOf(plain)}, "
                  + $"NearestTrack -> {(foundPlain ? pr.ToString() : "none")})",
                    !foundPlain || pr != plain);

            // ⭐ CONTROL FOR THE RIBBON HIDE: a road nobody laid rail on must KEEP its paint. Without this,
            // "the ribbon is hidden" would pass just as well on a SetRoadRibbonVisible that hid every road.
            T.Check($"control: a road never laid on keeps its painted ribbon",
                    field.RoadRibbonVisible(plain));

            // ---- 4. THE BEND LIMIT IS DERIVED FROM THE TILE'S WIDTH, not guessed. A 6.9 m-wide rigid tile
            // chording a curve parts at its outer corners by about HalfWidth * Pitch / R.
            T.Check($"the bend limit follows from the tile ({EditorRailSpline.MinRadius:0} m for a "
                  + $"{EditorRailSpline.HalfWidth * 2f:0.#} m tile at {EditorRailSpline.MaxJointGap * 100f:0} cm)",
                    Mathf.Abs(EditorRailSpline.MinRadius
                              - EditorRailSpline.HalfWidth * EditorRailSpline.Pitch / EditorRailSpline.MaxJointGap) < 0.5f);

            // ---- 5. ⭐⭐ THE SEAM. Master, on the first New Rail render: "nice but you can see a seam at
            // each chunk" -- a dark hairline down the ballast at every joint. It is the CHORD WEDGE: two rigid
            // tiles meeting at a turn have flat parallel ends whose planes splay, so the OUTER corners part
            // while the inner ones overlap. Two other diagnoses (coincident end caps, a normals break) were
            // both wrong, and a DEAD-STRAIGHT control render -- zero wedge by construction, perfectly clean
            // slope -- is what settled it.
            //
            // ⭐ THIS MEASURES THE CORNERS, NOT THE FORMULA. The check walks the placed transforms and asks
            // how far consecutive outer corners actually are from each other. Re-deriving HalfWidth*turn here
            // would agree with OverlapFor by construction and could never fail -- see
            // [[feedback_tests_must_derive_rates_not_copy_them]].
            var arc = new List<Vector3>();
            const float ArcR = 200f;
            for (int k = 0; k <= 20; k++)
            {
                float ang = k * 0.01f;
                arc.Add(new Vector3(ArcR * Mathf.Sin(ang), 0f, 1000f + ArcR * (1f - Mathf.Cos(ang))));
            }

            // ⚠⚠ SIGNED, AND THAT IS THE WHOLE POINT. The first version of this took the unsigned
            // DistanceTo between consecutive corners and promptly failed the FIX at 73 mm while passing the
            // control -- because backing a tile off closes the outer corner and deepens the INNER one by
            // 2*HalfWidth*turn, and an unsigned distance cannot tell daylight from solid-inside-solid. Only a
            // gap is a defect; an overlap is invisible. So project onto the leading tile's own forward axis
            // and count the POSITIVE part. [[feedback_an_instrument_must_name_what_it_measured]]
            //
            // Returns (worst gap, worst overlap) in metres over every joint and both sides of the bed.
            (float gap, float overlap) WorstJoint(List<Node3D> tiles)
            {
                float g = 0f, o = 0f;
                for (int i = 1; i < tiles.Count; i++)
                {
                    var a = tiles[i - 1]; var b2 = tiles[i];
                    var fwd = a.GlobalTransform.Basis.Z.Normalized();
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        var endC = a.GlobalTransform * new Vector3(sgn * EditorRailSpline.HalfWidth, 0f, EditorRailSpline.Pitch);
                        var startC = b2.GlobalTransform * new Vector3(sgn * EditorRailSpline.HalfWidth, 0f, 0f);
                        float along = fwd.Dot(startC - endC);    // + = daylight, - = buried in the neighbour
                        g = Mathf.Max(g, along);
                        o = Mathf.Max(o, -along);
                    }
                }
                return (g, o);
            }

            // ⭐ CONTROL FIRST, and it re-runs the SHIPPED path with only the fix switched off, so a control
            // that passes would mean the test cannot see the bug at all.
            int ctlRoad = field.AddRoadFromPolyline(arc, RoadField.TracksMaterial);
            var ctlTiles = new List<Node3D>();
            EditorRailSpline.DebugNoJointOverlap = true;
            int ctlN = EditorRailSpline.LayAlong(objs, null, field, ctlRoad, ctlTiles);
            EditorRailSpline.DebugNoJointOverlap = false;
            ctlTiles.RemoveAt(ctlTiles.Count - 1);                       // drop the terminal sleeper
            var ctl = WorstJoint(ctlTiles);
            T.Check($"control: WITHOUT the overlap, a {ArcR:0} m bend opens {ctl.gap * 100f:0.0} cm of daylight "
                  + $"at its outer corners over {ctlN} tiles -- the seam master saw",
                    ctlN > 5 && ctl.gap > 0.02f);

            var fixRoad = field.AddRoadFromPolyline(arc, RoadField.TracksMaterial);
            var fixTiles = new List<Node3D>();
            int fixN = EditorRailSpline.LayAlong(objs, null, field, fixRoad, fixTiles);
            fixTiles.RemoveAt(fixTiles.Count - 1);
            var fx = WorstJoint(fixTiles);
            T.Check($"...and WITH it the same bend closes to {fx.gap * 1000f:0.#} mm over {fixN} tiles "
                  + $"({ctl.gap / Mathf.Max(fx.gap, 1e-6f):0}x tighter)",
                    fixN > 5 && fx.gap < 0.005f);

            // ⭐ AND THE COST LANDS WHERE IT IS INVISIBLE. Closing the outer corner necessarily buries the
            // inner one deeper -- about 2*HalfWidth*turn. Asserting that explicitly is what stops a future
            // "fix" from backing tiles off the WRONG WAY, which would read as closed on one metric while
            // tearing the inside open.
            T.Check($"...paid for on the INSIDE, buried not gapped ({fx.overlap * 100f:0.0} cm of overlap vs "
                  + $"{ctl.overlap * 100f:0.0} cm before)",
                    fx.overlap > ctl.overlap + 0.01f);

            // ⭐⭐ AND IT IS FREE ON A STRAIGHT. The straight fixture at the top of this test laid its units
            // at exactly {Pitch} apart (check 2), which is the same assertion as "every overlap was zero" --
            // so the fix cannot have disturbed the case that was already correct.
            T.Check($"...while the straight run is untouched: {EditorRailSpline.OverlapFor(0f) * 1000f:0.#} mm "
                  + $"of overlap at zero turn", Mathf.Abs(EditorRailSpline.OverlapFor(0f)) < 1e-6f);
        }
    }
}
