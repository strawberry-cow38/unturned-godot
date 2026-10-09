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
            const float Want = 5f;   // segments
            int n = tool.LayRun(Vector3.Zero, new Vector3(EditorFenceRoad.SegmentLength * Want, 0f, 0f));
            T.Check($"a {EditorFenceRoad.SegmentLength * Want:0.#} m line lays {Want} segments (got {n})", n == (int)Want);
            int placed = objs.PlacedCount - before;
            T.Check($"...as TWO props each -- posts and rail separately ({placed} props for {n} segments)",
                    placed == n * 2);

            // ⭐ AND THEY BUTT, which is the whole claim of "a run" rather than "some props near a line". The
            // showcase render looked like it had gaps between sections, so this measures the pitch instead of
            // arguing with a screenshot: consecutive segments must sit exactly one segment-length apart along
            // the run, and the first must start half a length in so the run begins at the click.
            var centres = new List<Vector3>();
            foreach (var x in objs.PlacedOf(EditorFenceRoad.Intact + "_Rail")) centres.Add(x.Origin);
            centres.Sort((p1, p2) => p1.X.CompareTo(p2.X));
            bool pitchOk = centres.Count == n;
            float worst = 0f;
            for (int i = 1; i < centres.Count; i++)
            {
                float d = centres[i].DistanceTo(centres[i - 1]);
                worst = Mathf.Max(worst, Mathf.Abs(d - EditorFenceRoad.SegmentLength));
                if (Mathf.Abs(d - EditorFenceRoad.SegmentLength) > 0.01f) pitchOk = false;
            }
            T.Check($"segments butt end to end: every gap is {EditorFenceRoad.SegmentLength:0.##} m "
                  + $"(worst error {worst:0.###} m over {centres.Count} segments)", pitchOk);
            T.Check($"...and the first starts half a segment in, at the click ({centres[0].X:0.##} m)",
                    Mathf.Abs(centres[0].X - EditorFenceRoad.SegmentLength * 0.5f) < 0.01f);

            // ---- 3. CONTROL: shorter than one segment lays NOTHING. Without this, "it lays segments" is
            // satisfied by a tool that stretches or overlaps a prop to reach whatever was clicked.
            int b2 = objs.PlacedCount;
            int none = tool.LayRun(Vector3.Zero, new Vector3(EditorFenceRoad.SegmentLength * 0.6f, 0f, 0f));
            T.Check($"a run shorter than one segment lays nothing ({none} segments, {objs.PlacedCount - b2} props)",
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
                  + $"({EditorFenceRoad.TightestBendRadius(arc):0} m radius, limit {EditorFenceRoad.MinBendRadius:0})",
                    EditorFenceRoad.TightestBendRadius(arc) > EditorFenceRoad.MinBendRadius);
            int cn = tool.LayPathRun(arc);
            T.Check($"a 3-point path lays a curved run ({cn} segments)", cn >= 8);
            var cc = new List<Vector3>();
            foreach (var x in objs.PlacedOf(EditorFenceRoad.Intact + "_Rail"))
                if (x.Origin.Z > 300f) cc.Add(x.Origin);
            cc.Sort((p1, p2) => p1.X.CompareTo(p2.X));
            float cWorst = 0f;
            for (int i = 1; i < cc.Count; i++)
                cWorst = Mathf.Max(cWorst, Mathf.Abs(cc[i].DistanceTo(cc[i - 1]) - EditorFenceRoad.SegmentLength));
            // ⚠ THE TOLERANCE IS RETAIL'S OWN, not a number picked to make this pass. Consecutive segment
            // centres are chord midpoints, so on a bend they sit very slightly under the arc pitch; the
            // question is whether that is worse than the game's own placement. PEI's 22 Fence_Road_0
            // placements span 15.948..16.15 m -- a 0.2 m spread by hand -- so 0.25 m is the bar, and this
            // comes in at a sixth of a metre on a 150 m bend.
            T.Check($"...with the SAME {EditorFenceRoad.SegmentLength:0.##} m pitch round the bend "
                  + $"(worst error {cWorst:0.###} m over {cc.Count} segments, retail's own spread is 0.2)",
                    cc.Count == cn && cWorst < 0.25f);

            // ⭐ CONTROL: the run actually BENT. Every check above passes on a tool that quietly laid a straight
            // line through the first two points and ignored the third.
            float spread = 0f;
            foreach (var c in cc) spread = Mathf.Max(spread, Mathf.Abs(c.Z - cc[0].Z));
            T.Check($"control: the run really curves -- it departs {spread:0.#} m from its first segment "
                  + "(a straight line would be 0)", spread > 5f);

            // ⭐ AND THE LIMIT IS REAL, not a dead constant: a bend a rigid segment cannot follow must measure
            // tighter than it. Without this the fixture check above passes on a function that returns infinity.
            var hairpin = new List<Vector3> { new(0, 0, 600), new(40, 0, 630), new(80, 0, 600) };
            T.Check($"control: a hairpin measures tighter than the limit "
                  + $"({EditorFenceRoad.TightestBendRadius(hairpin):0} m < {EditorFenceRoad.MinBendRadius:0})",
                    EditorFenceRoad.TightestBendRadius(hairpin) < EditorFenceRoad.MinBendRadius);

            // ---- 4. UNDO takes the whole run, not one post at a time -- the reason RemovePlaced exists.
            int b3 = objs.PlacedCount;
            tool.LayRun(new Vector3(0f, 0f, 200f), new Vector3(EditorFenceRoad.SegmentLength * 3f, 0f, 200f));
            T.Check("another run went down", objs.PlacedCount == b3 + 6);
            ed.Undo();
            yield return Ticks(1);
            T.Check($"one Ctrl+Z removes the WHOLE run ({objs.PlacedCount} props, back to {b3})",
                    objs.PlacedCount == b3);
        }
    }
}
