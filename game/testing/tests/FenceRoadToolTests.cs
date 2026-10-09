using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>The fence-road tool lays a run of guardrail along a line, in two separable parts.
    ///
    /// Master 2026-10-09: "make a new fence road tool. split the prop of fence road and fence road broken into
    /// the posts (brown wood) and the guardrail (metal, silver) first".
    ///
    /// ⭐ THE SPLIT IS ASSERTED AGAINST THE ORIGINAL, not against itself. "Posts has 50 triangles" is a number
    /// copied out of the thing it is checking; "posts plus rail is exactly Fence_Road_0, with nothing lost and
    /// nothing duplicated" is a property the split can actually fail. That check is what catches a splitter
    /// that drops the triangles it could not classify -- which is the failure this kind of tool really has.</summary>
    public sealed class FenceRoadToolTests : GameTest
    {
        public override string Name => "editor.fence_road_tool";
        public override double TimeoutSimSeconds => 30;

        static List<(Vector3 a, Vector3 b, Vector3 c)> Tris(string objName)
        {
            string path = ProjectSettings.GlobalizePath($"res://content/objects/{objName}.obj");
            var v = new List<Vector3>();
            var tris = new List<(Vector3, Vector3, Vector3)>();
            foreach (var line in System.IO.File.ReadLines(path))
            {
                var t = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                if (t[0] == "v")
                    v.Add(new Vector3(float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture),
                                      float.Parse(t[2], System.Globalization.CultureInfo.InvariantCulture),
                                      float.Parse(t[3], System.Globalization.CultureInfo.InvariantCulture)));
                else if (t[0] == "f" && t.Length >= 4)
                {
                    Vector3 P(string c) => v[int.Parse(c.Split('/')[0]) - 1];
                    tris.Add((P(t[1]), P(t[2]), P(t[3])));
                }
            }
            return tris;
        }

        /// <summary>A triangle as an order-independent key, so the two halves can be compared to the whole
        /// without caring which order the splitter emitted them in.</summary>
        static string Key((Vector3 a, Vector3 b, Vector3 c) t)
        {
            var s = new List<string> { $"{t.a.X:0.###},{t.a.Y:0.###},{t.a.Z:0.###}",
                                       $"{t.b.X:0.###},{t.b.Y:0.###},{t.b.Z:0.###}",
                                       $"{t.c.X:0.###},{t.c.Y:0.###},{t.c.Z:0.###}" };
            s.Sort(System.StringComparer.Ordinal);
            return string.Join("|", s);
        }

        /// <summary>A placed prop's mesh bounds in WORLD space -- the only way to ask whether two segments
        /// actually touch, as opposed to whether their origins are the right distance apart.</summary>
        static Aabb? WorldAabb(Node3D root)
        {
            foreach (var c in root.GetChildren())
                if (c is MeshInstance3D mi && mi.Mesh != null)
                    return root.GlobalTransform * mi.Mesh.GetAabb();
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            // ---- 1. THE SPLIT: posts + rail reassemble into the original, exactly.
            foreach (var baseName in new[] { EditorFenceRoad.Intact, EditorFenceRoad.Broken })
            {
                var whole = Tris(baseName);
                var posts = Tris(baseName + "_Posts");
                var rail = Tris(baseName + "_Rail");
                var bag = new List<string>();
                foreach (var t in posts) bag.Add(Key(t));
                foreach (var t in rail) bag.Add(Key(t));
                var want = new List<string>();
                foreach (var t in whole) want.Add(Key(t));
                bag.Sort(System.StringComparer.Ordinal); want.Sort(System.StringComparer.Ordinal);
                T.Check($"{baseName}: posts {posts.Count} + rail {rail.Count} = the original {whole.Count} tris, "
                      + "same triangles, none lost or duplicated",
                        posts.Count > 0 && rail.Count > 0 && string.Join(";", bag) == string.Join(";", want));

                // ⭐ AND THEY ARE DIFFERENT PARTS, not one empty. The rail is the beam: it lives in the upper
                // band and on the +X side, which is the measured fact the tool's facing logic depends on.
                float railMinZ = float.MaxValue, postMinZ = float.MaxValue;
                foreach (var t in rail) railMinZ = Mathf.Min(railMinZ, Mathf.Min(t.a.Z, Mathf.Min(t.b.Z, t.c.Z)));
                foreach (var t in posts) postMinZ = Mathf.Min(postMinZ, Mathf.Min(t.a.Z, Mathf.Min(t.b.Z, t.c.Z)));
                T.Check($"{baseName}: the rail sits ABOVE the posts' feet (rail z from {railMinZ:0.##}, posts from {postMinZ:0.##})",
                        railMinZ > postMinZ);
            }

            // ---- 2. THE TOOL, on the path a mapper takes: two clicks on the ground -> a run of segments.
            var ed = new Editor();
            World.AddChild(ed);
            var cam = new Camera3D();
            World.AddChild(cam);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            var tool = new EditorFenceRoad(ed, cam, objs, null);   // no Terrain in the rig: seating falls back to flat
            World.AddChild(tool);
            yield return Ticks(2);

            int before = objs.PlacedCount;
            const float Want = 20f;   // 4 m units
            int n = tool.LayRun(Vector3.Zero, new Vector3(EditorFenceRoad.PostSpacing * Want, 0f, 0f));
            T.Check($"a {EditorFenceRoad.PostSpacing * Want:0.#} m line lays {Want} units of "
                  + $"{EditorFenceRoad.PostSpacing:0.##} m (got {n})", n == (int)Want);
            int placed = objs.PlacedCount - before;
            // ⭐ N SPANS AND N+1 POSTS -- master: "no dupe posts". A post at both ends of every span is 2N,
            // which is the duplicate; one per boundary plus a closing one is the fence an actual fence has.
            T.Check($"...as {n} span(s) + {n + 1} posts, one per boundary and no duplicate ({placed} props)",
                    placed == n * 2 + 1);

            // ⭐ AND THEY BUTT, which is the whole claim of "a run" rather than "some props near a line". The
            // showcase render looked like it had gaps between sections, so this measures the pitch instead of
            // arguing with a screenshot: consecutive segments must sit exactly one segment-length apart along
            // the run, and the first must start half a length in so the run begins at the click.
            var centres = new List<Vector3>();
            foreach (var x in objs.PlacedOf(EditorFenceRoad.SpanUnit)) centres.Add(x.Origin);
            centres.Sort((p1, p2) => p1.X.CompareTo(p2.X));
            bool pitchOk = centres.Count == n;
            float worst = 0f;
            for (int i = 1; i < centres.Count; i++)
            {
                float d = centres[i].DistanceTo(centres[i - 1]);
                worst = Mathf.Max(worst, Mathf.Abs(d - EditorFenceRoad.PostSpacing));
                if (Mathf.Abs(d - EditorFenceRoad.PostSpacing) > 0.01f) pitchOk = false;
            }
            T.Check($"units butt end to end: every gap is {EditorFenceRoad.PostSpacing:0.##} m "
                  + $"(worst error {worst:0.###} m over {centres.Count} segments)", pitchOk);
            T.Check($"...and the first starts half a unit in, at the click ({centres[0].X:0.##} m)",
                    Mathf.Abs(centres[0].X - EditorFenceRoad.PostSpacing * 0.5f) < 0.01f);

            // ⭐ AND THE RAILS ACTUALLY TOUCH. Centre spacing being right is necessary and NOT sufficient: it
            // is also satisfied by segments whose MESH is shorter than the pitch, which would leave a hole at
            // every joint while every number above stayed perfect. The showcase render looked exactly like
            // that, so this measures the world-space bounds of consecutive rails instead of the origins.
            var rails = new List<Node3D>();
            foreach (var p in objs.PlacedOfNodes(EditorFenceRoad.SpanUnit)) rails.Add(p);
            rails.Sort((p1, p2) => p1.GlobalPosition.X.CompareTo(p2.GlobalPosition.X));
            float worstHole = 0f; int measured = 0;
            for (int i = 1; i < rails.Count; i++)
            {
                var a1 = WorldAabb(rails[i - 1]); var b1 = WorldAabb(rails[i]);
                if (a1 == null || b1 == null) continue;
                measured++;
                float hole = b1.Value.Position.X - a1.Value.End.X;   // >0 means a visible hole along the run
                worstHole = Mathf.Max(worstHole, hole);
            }
            T.Check($"consecutive rails meet: worst hole {worstHole:0.###} m over {measured} joint(s)",
                    measured > 0 && worstHole < 0.05f);

            // ---- 3. CONTROL: shorter than one segment lays NOTHING. Without this, "it lays segments" is
            // satisfied by a tool that stretches or overlaps a prop to reach whatever was clicked.
            int b2 = objs.PlacedCount;
            int none = tool.LayRun(Vector3.Zero, new Vector3(EditorFenceRoad.PostSpacing * 0.6f, 0f, 0f));
            T.Check($"a run shorter than one unit lays nothing ({none} units, {objs.PlacedCount - b2} props)",
                    none == 0 && objs.PlacedCount == b2);

            // ---- 3b. A CURVE: three or more points bend the run, and the pitch must survive the bend. That
            // is the whole reason it is walked by arc length -- a per-spline-segment placement stretches the
            // post rhythm on the long part of a curve and crowds it on the short part.
            //
            // ⚠ A GENTLE arc on purpose (~150 m radius): a rigid 16 m chord cannot follow a tighter one, and
            // the first version of this test used a 32 m bend and then blamed the tool for the 0.69 m the
            // chord-versus-arc difference accounts for. The tight case is checked separately, below.
            var arc = new List<Vector3> { new(0, 0, 400), new(120, 0, 460), new(240, 0, 400) };
            T.Check($"fixture: the test arc is within what a segment can follow "
                  + $"({EditorFenceRoad.TightestBendRadius(arc):0} m radius, limit {EditorFenceRoad.MinBendRadiusFor(EditorFenceRoad.PostSpacing):0.#})",
                    EditorFenceRoad.TightestBendRadius(arc) > EditorFenceRoad.MinBendRadiusFor(EditorFenceRoad.PostSpacing));
            int cn = tool.LayPathRun(arc);
            T.Check($"a 3-point path lays a curved run ({cn} units)", cn >= 30);
            var cc = new List<Vector3>();
            foreach (var x in objs.PlacedOf(EditorFenceRoad.SpanUnit))
                if (x.Origin.Z > 300f) cc.Add(x.Origin);
            cc.Sort((p1, p2) => p1.X.CompareTo(p2.X));
            float cWorst = 0f;
            for (int i = 1; i < cc.Count; i++)
                cWorst = Mathf.Max(cWorst, Mathf.Abs(cc[i].DistanceTo(cc[i - 1]) - EditorFenceRoad.PostSpacing));
            // ⚠ THE TOLERANCE IS RETAIL'S OWN, not a number picked to make this pass. Consecutive segment
            // centres are chord midpoints, so on a bend they sit very slightly under the arc pitch; the
            // question is whether that is worse than the game's own placement. PEI's 22 Fence_Road_0
            // placements span 15.948..16.15 m -- a 0.2 m spread by hand -- so 0.25 m is the bar, and this
            // comes in at a sixth of a metre on a 150 m bend.
            T.Check($"...with the SAME {EditorFenceRoad.PostSpacing:0.##} m pitch round the bend "
                  + $"(worst error {cWorst:0.###} m over {cc.Count} segments, retail's own spread is 0.2)",
                    cc.Count == cn && cWorst < 0.25f);

            // ⭐⭐ AND THE CURVED RUN'S SEGMENTS MEET TOO. An axis-aligned AABB is useless here -- rotated
            // segments have inflated, overlapping AABBs, so the straight test's measure would pass trivially.
            // This walks each segment's OWN length axis to its two ends instead: mesh local +Y is the prop's
            // length, so end = origin + basis.Y * half, and the hole is the distance from one segment's end to
            // the next one's start.
            var xf = new List<Transform3D>();
            foreach (var x in objs.PlacedOf(EditorFenceRoad.SpanUnit))
                if (x.Origin.Z > 300f) xf.Add(x);
            xf.Sort((p1, p2) => p1.Origin.X.CompareTo(p2.Origin.X));
            float curveHole = 0f;
            for (int i = 1; i < xf.Count; i++)
            {
                var endPrev = xf[i - 1].Origin + xf[i - 1].Basis.Y * (EditorFenceRoad.PostSpacing * 0.5f);
                var startNow = xf[i].Origin - xf[i].Basis.Y * (EditorFenceRoad.PostSpacing * 0.5f);
                curveHole = Mathf.Max(curveHole, endPrev.DistanceTo(startNow));
            }
            T.Check($"round the bend the segments still meet: worst joint {curveHole:0.###} m over "
                  + $"{Mathf.Max(0, xf.Count - 1)} joint(s)", xf.Count > 1 && curveHole < 0.25f);

            // ⭐ CONTROL: the run actually BENT. Every check above passes on a tool that quietly laid a straight
            // line through the first two points and ignored the third.
            float spread = 0f;
            foreach (var c in cc) spread = Mathf.Max(spread, Mathf.Abs(c.Z - cc[0].Z));
            T.Check($"control: the run really curves -- it departs {spread:0.#} m from its first segment "
                  + "(a straight line would be 0)", spread > 5f);

            // ⭐ AND THE LIMIT IS REAL, not a dead constant: a bend a rigid segment cannot follow must measure
            // tighter than it. Without this the fixture check above passes on a function that returns infinity.
            var hairpin = new List<Vector3> { new(0, 0, 600), new(9, 0, 609), new(18, 0, 600) };
            T.Check($"control: a hairpin measures tighter than the limit "
                  + $"({EditorFenceRoad.TightestBendRadius(hairpin):0.#} m < {EditorFenceRoad.MinBendRadiusFor(EditorFenceRoad.PostSpacing):0.#})",
                    EditorFenceRoad.TightestBendRadius(hairpin) < EditorFenceRoad.MinBendRadiusFor(EditorFenceRoad.PostSpacing));

            // ---- 3c. A WRECKED SECTION DROPS INTO AN INTACT RUN. Master: "how do broken pieces integrate?"
            // The wreck's own posts sit on the same 4 m rhythm with its end posts at exactly ±8, so it is four
            // units of run -- and it brings all five of its posts, so the run must place NONE across it.
            int bw = objs.PlacedCount;
            const int Units = 20, WreckStart = 8;
            int wn = EditorFenceRoad.LayPath(objs, null,
                new List<Vector3> { new(0, 0, 800), new(EditorFenceRoad.PostSpacing * Units, 0, 800) },
                false, false, null, null, new[] { WreckStart });
            T.Check($"the run still measures {Units} units with a wreck in it ({wn})", wn == Units);

            int spans = 0, postsIn = 0, wreckParts = 0;
            float x0 = EditorFenceRoad.PostSpacing * WreckStart;
            float x1 = EditorFenceRoad.PostSpacing * (WreckStart + EditorFenceRoad.BrokenUnits);
            foreach (var x in objs.PlacedOf(EditorFenceRoad.SpanUnit)) if (x.Origin.Z > 700f) spans++;
            foreach (var x in objs.PlacedOf(EditorFenceRoad.PostUnit))
                if (x.Origin.Z > 700f && x.Origin.X > x0 - 0.1f && x.Origin.X < x1 + 0.1f) postsIn++;
            foreach (var nmw in new[] { EditorFenceRoad.Broken + "_Posts", EditorFenceRoad.Broken + "_Rail" })
                foreach (var x in objs.PlacedOf(nmw)) if (x.Origin.Z > 700f) wreckParts++;

            T.Check($"the wreck replaced {EditorFenceRoad.BrokenUnits} spans "
                  + $"({spans} intact spans, expected {Units - EditorFenceRoad.BrokenUnits})",
                    spans == Units - EditorFenceRoad.BrokenUnits);
            T.Check($"...and the wrecked section itself went down ({wreckParts} part(s), posts + rail)",
                    wreckParts == 2);
            // ⭐⭐ THE CHECK THAT MATTERS: the wreck carries its own five posts, so a run post anywhere across
            // it is the doubling this rework removed -- just hidden inside a damaged section, where nobody
            // would look for it.
            T.Check($"...with NO run post inside it -- the wreck brings its own ({postsIn} found in "
                  + $"{x0:0.#}..{x1:0.#} m)", postsIn == 0);

            // ⭐ CONTROL: the same run with no marker DOES place posts there. Otherwise the check above passes
            // on a tool that stopped placing posts altogether.
            int bc = objs.PlacedCount;
            EditorFenceRoad.LayPath(objs, null,
                new List<Vector3> { new(0, 0, 900), new(EditorFenceRoad.PostSpacing * Units, 0, 900) },
                false, false, null, null, null);
            int postsThere = 0;
            foreach (var x in objs.PlacedOf(EditorFenceRoad.PostUnit))
                if (x.Origin.Z > 850f && x.Origin.X > x0 - 0.1f && x.Origin.X < x1 + 0.1f) postsThere++;
            T.Check($"control: with no wreck marker the run DOES post that stretch ({postsThere} posts)",
                    postsThere >= EditorFenceRoad.BrokenUnits);

            // ---- 3d. PARENTED TO A ROAD: the guardrail follows a road spline's OUTER edge on a turn.
            // Master: "next is parenting this to a road spline's outer edge on a turn".
            var field = new RoadField();
            World.AddChild(field);
            // ⚠ A bare rig has no road materials -- they come from the Unturned install -- so every road would
            // report a half-width of zero and the tool would (correctly) refuse. Give material 0 a width.
            field.DebugSetMaterialWidth(0, 9.2f);   // PEI's rendered half-width, the number ProcIslandSpawn uses
            yield return Ticks(1);
            // A quarter-circle sweeping LEFT (radius 90 m), which bends tighter than GuardBendRadius so it is
            // worth guarding, and gently enough that a 4 m unit follows it.
            var arcPts = new List<Vector3>();
            const float R = 90f;
            for (int d = 0; d <= 90; d += 10)
                arcPts.Add(new Vector3(R * Mathf.Sin(Mathf.DegToRad(d)), 0f, 1200f - R * (1f - Mathf.Cos(Mathf.DegToRad(d)))));
            int rd = field.AddRoadFromPolyline(arcPts);
            T.Check($"fixture: a road went down ({field.RoadCount} road(s), {field.RoadLength(rd):0.#} m, "
                  + $"half-width {field.RoadHalfWidth(rd):0.##} m)",
                    rd >= 0 && field.RoadLength(rd) > 50f && field.RoadHalfWidth(rd) > 0.5f);

            int br = objs.PlacedCount;
            int gn = EditorFenceRoad.LayAlongRoad(objs, null, field, rd, false, null, null);
            T.Check($"guardrail laid along the road's bend ({gn} units, {objs.PlacedCount - br} props)", gn > 0);

            // ⭐⭐ ON THE OUTER SIDE, AND THIS IS THE CLAIM. The arc curves LEFT, so its centre of curvature is
            // to the left and the OUTER edge is to the RIGHT -- i.e. every post must sit FARTHER from the
            // centre than the road's centreline does, not nearer. Measuring distance-from-centre is what makes
            // "outer" checkable at all; a check on which side a flag said would just restate the code.
            var centre = new Vector3(0f, 0f, 1200f - R);
            // ⚠ MinValue, not 0: every post being OUTSIDE makes (R - d) negative throughout, and a max
            // seeded at zero can never report that -- it just sits at 0 and the check fails on a
            // correct tool. Seed an extremum with an extremum.
            float worstInside = float.MinValue; int sampled = 0;
            foreach (var x in objs.PlacedOf(EditorFenceRoad.PostUnit))
            {
                if (x.Origin.Z < 1100f) continue;           // only this fixture's run
                sampled++;
                float d = new Vector2(x.Origin.X - centre.X, x.Origin.Z - centre.Z).Length();
                worstInside = Mathf.Max(worstInside, R - d);   // >0 means it landed INSIDE the curve
            }
            float wantOff = field.RoadHalfWidth(rd) + EditorFenceRoad.RoadClearance;
            T.Check($"...on the OUTER side of the turn: {sampled} post(s), worst one {worstInside:0.##} m inside "
                  + $"the centreline (expected all ~{wantOff:0.##} m OUTSIDE)",
                    sampled > 0 && worstInside < -wantOff * 0.5f);

            // ⭐⭐ AND THE RAIL FACES THE ROAD, which is a different question from which SIDE the run is on and
            // which the offset check above cannot see: the whole prop is 0.5 m wide, so both facings put every
            // post ~11 m outside the curve. Master, on the first render: "the posts are on the inside? should
            // be on the outside." A guardrail's beam takes the hit, so the beam belongs on the carriageway
            // side and the posts behind it.
            //
            // The beam is the prop's local +X half (measured: the Span mesh spans local X +0.258..+0.305 while
            // the Post is centred on 0), so basis.X is where the rail hangs -- and on the outer side of a bend
            // that must point back TOWARD the centre of curvature.
            float worstFacing = float.MinValue; int faced = 0;
            foreach (var x in objs.PlacedOf(EditorFenceRoad.SpanUnit))
            {
                if (x.Origin.Z < 1100f) continue;
                faced++;
                var toCentre = new Vector3(centre.X - x.Origin.X, 0f, centre.Z - x.Origin.Z).Normalized();
                // +1 = the beam points at the road, -1 = it points away into the field
                worstFacing = Mathf.Max(worstFacing, -x.Basis.X.Normalized().Dot(toCentre));
            }
            T.Check($"...with the RAIL facing the carriageway, posts behind it ({faced} span(s), worst "
                  + $"beam·road {-worstFacing:0.00}; +1 = at the road, -1 = away from it)",
                    faced > 0 && -worstFacing > 0.8f);

            // ⭐ CONTROL: a STRAIGHT road gets nothing. "The outer edge on a turn" means a guardrail down a
            // straight is wrong, and without this the check above passes on a tool that guards everything.
            var straight = new List<Vector3>();
            for (int k = 0; k <= 10; k++) straight.Add(new Vector3(k * 12f, 0f, 1400f));
            int sr = field.AddRoadFromPolyline(straight);
            int bs = objs.PlacedCount;
            int sn = EditorFenceRoad.LayAlongRoad(objs, null, field, sr, false, null, null);
            T.Check($"control: a straight road is not guarded ({sn} units, {objs.PlacedCount - bs} props)",
                    sn == 0 && objs.PlacedCount == bs);

            // ---- 4. UNDO takes the whole run, not one post at a time -- the reason RemovePlaced exists.
            int b3 = objs.PlacedCount;
            int rn = tool.LayRun(new Vector3(0f, 0f, 200f), new Vector3(EditorFenceRoad.PostSpacing * 3f, 0f, 200f));
            T.Check($"another run went down ({rn} units)", objs.PlacedCount == b3 + rn * 2 + 1);
            ed.Undo();
            yield return Ticks(1);
            T.Check($"one Ctrl+Z removes the WHOLE run ({objs.PlacedCount} props, back to {b3})",
                    objs.PlacedCount == b3);
        }
    }
}
