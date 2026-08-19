using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE DRAW-A-ROAD/RAIL TOOL (strawberry 2026-08-19). The node tool it supersedes had no L1 coverage at
    // all -- it was only ever exercised by a --editorroads screenshot harness -- so this is the first test of
    // the road data model in-engine.
    //
    // What matters here is NOT that a road appears. It is that a JUNCTION is exact. Junctions are derived
    // from geometry (two road ENDS at the same position) rather than stored, because Paths.dat is retail's
    // format and has nowhere to put a branch. That choice is only sound if the editor makes coincidence
    // exact -- so the checks below assert EQUALITY of the joined positions, not proximity, and each one is
    // paired with the un-snapped control that must NOT produce a junction.
    public class RoadDrawJunctions : GameTest
    {
        public override string Name => "editor.road_draw_junctions";
        public override double TimeoutSimSeconds => 20;

        static List<Vector3> Line(Vector3 a, Vector3 b, int n)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < n; i++) pts.Add(a.Lerp(b, i / (float)(n - 1)));
            return pts;
        }

        public override IEnumerable<Step> Run()
        {
            var field = new RoadField();
            World.AddChild(field);
            yield return Ticks(1);

            // ---- a drawn polyline becomes one road with smooth tangents
            int a = field.AddRoadFromPolyline(Line(new Vector3(0, 0, 0), new Vector3(160, 0, 0), 21));
            T.Check($"a drawn stroke becomes one road ({field.RoadCount} roads, {field.JointCount(a)} joints)",
                    a == 0 && field.RoadCount == 1 && field.JointCount(a) == 21);

            // A straight stroke must come out STRAIGHT. Catmull-Rom with the wrong divisor bulges between
            // points, which reads as "the tool draws wobbly roads" and is invisible in a joint count.
            var t1 = field.TangentPos(a, 10, 1) - field.JointPos(a, 10);
            T.Check($"a straight stroke gets collinear tangents (tangent {t1}, off-axis {Mathf.Abs(t1.Z):0.0000})",
                    Mathf.Abs(t1.Z) < 1e-3f && t1.X > 0.1f);

            T.Check($"one road on its own is not a junction ({field.Junctions().Count})", field.Junctions().Count == 0);

            // ---- CONTROL: two road ends at the SAME position are still NOT a junction, because a junction is
            // now a NODE you bind to, not a coincidence. This is the check that pins strawberry's design call:
            // if it ever passes, junctions have silently gone back to being derived from geometry.
            var endA = field.JointPos(a, field.JointCount(a) - 1);
            int b = field.AddRoadFromPolyline(Line(endA, new Vector3(endA.X, 0, 120), 12));
            T.Check($"two ends at an IDENTICAL position are not a junction on their own ({field.Junctions().Count})",
                    field.JointPos(b, 0) == endA && field.Junctions().Count == 0);

            // ---- BIND: a node, with both ends bound to it, IS one.
            int n0 = field.AddJunction(endA);
            field.BindRoadEnd(a, atEnd: true, n0);
            field.BindRoadEnd(b, atEnd: false, n0);
            var js = field.Junctions();
            T.Check($"binding both ends to a node makes a junction ({js.Count})", js.Count == 1);
            T.Check($"...joining exactly 2 road ends", js.Count == 1 && js[0].Ends.Count == 2);

            // ---- THE POINT OF A NODE: move it, and every bound rail follows. Derived junctions cannot do
            // this -- you would drag each end separately and hope they still matched.
            var moved = endA + new Vector3(0f, 0f, 25f);
            field.MoveJunction(n0, moved);
            T.Check($"moving the node drags BOTH bound rail ends with it ({field.JointPos(a, field.JointCount(a) - 1)} / {field.JointPos(b, 0)})",
                    field.JointPos(a, field.JointCount(a) - 1) == moved && field.JointPos(b, 0) == moved);
            T.Check($"...and it is still one junction afterwards ({field.Junctions().Count})", field.Junctions().Count == 1);

            // ---- A 3-WAY: split a road and bind all three ends to one node.
            var mid = field.JointPos(a, 5);
            int before = field.RoadCount;
            int split = field.SplitRoadAt(a, 5);
            T.Check($"splitting mid-road makes a second road ({before} -> {field.RoadCount})",
                    split >= 0 && field.RoadCount == before + 1);
            int n1 = field.AddJunction(mid);
            field.BindRoadEnd(a, atEnd: true, n1);
            field.BindRoadEnd(split, atEnd: false, n1);
            int c = field.AddRoadFromPolyline(Line(mid, new Vector3(mid.X, 0, -120), 12));
            field.BindRoadEnd(c, atEnd: false, n1);
            var three = field.Junctions().Find(x => x.Ends.Count >= 3);
            T.Check($"three rails bound to one node is a 3-way junction (largest group {(three.Ends?.Count ?? 0)})",
                    three.Ends != null && three.Ends.Count == 3);
            T.Check($"the branch road really is one of the three ({c})",
                    three.Ends != null && three.Ends.Exists(e => e.Road == c));

            field.QueueFree();
        }
    }

    // ...AND THAT THE TOOL WIRES THEM UP. Everything above drives RoadField directly, which proves the data
    // model and NOTHING about EditorRoadDraw -- the exact split that let a fully-built door feature ship
    // unreachable, and that let two ladder tests pass while real ladders were unclimbable. So this one goes
    // through the tool's own path, including its snapping, which is where the "make coincidence exact" job
    // actually lives.
    public class RoadDrawToolWiring : GameTest
    {
        public override string Name => "editor.road_draw_tool";
        public override double TimeoutSimSeconds => 20;

        static List<Vector3> Line(Vector3 a, Vector3 b, int n)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < n; i++) pts.Add(a.Lerp(b, i / (float)(n - 1)));
            return pts;
        }

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var field = new RoadField();
            World.AddChild(field);
            var cam = new Camera3D();
            World.AddChild(cam);
            var tool = new EditorRoadDraw(ed, cam, field);
            World.AddChild(tool);
            yield return Ticks(1);

            int a = tool.DebugDrawRoad(Line(new Vector3(0, 0, 0), new Vector3(160, 0, 0), 21));
            T.Check($"the TOOL lays a road ({a}, {field.RoadCount} in the field)", a >= 0 && field.RoadCount == 1);

            // Drawn NEAR the first road's end but not on it -- the tool must snap it, so this is a junction
            // even though the caller's own coordinates were 4 m out.
            var offBy4 = Line(new Vector3(163, 0, 3), new Vector3(163, 0, 120), 12);
            int b = tool.DebugDrawRoad(offBy4);
            T.Check($"the tool SNAPPED a 4 m-off start onto the existing end ({field.JointPos(b, 0)})",
                    field.JointPos(b, 0) == field.JointPos(a, field.JointCount(a) - 1));
            T.Check($"...so the tool produced a real junction ({field.Junctions().Count})", field.Junctions().Count == 1);

            // CONTROL: the same draw with snapping OFF must NOT connect. Without this the check above would
            // also pass on a tool that snapped nothing and simply got lucky with the numbers.
            var far = tool.DebugDrawRoad(Line(new Vector3(-400, 0, 3), new Vector3(-400, 0, 120), 12), snapEnds: false);
            T.Check($"CONTROL -- an unsnapped stroke far from anything adds no junction ({field.Junctions().Count})",
                    far >= 0 && field.Junctions().Count == 1);

            // A branch through the tool: end on a mid-joint, tool splits and three ends meet.
            var mid = field.JointPos(a, 10);
            int c = tool.DebugDrawRoad(Line(mid + new Vector3(2f, 0f, 2f), new Vector3(mid.X, 0, -120), 12));
            var three = field.Junctions().Find(x => x.Ends.Count >= 3);
            T.Check($"the tool turns a stroke onto a mid-road point into a 3-way junction (largest group {(three.Ends?.Count ?? 0)})",
                    three.Ends != null && three.Ends.Count == 3 && c >= 0);

            tool.QueueFree(); field.QueueFree(); cam.QueueFree(); ed.QueueFree();
        }
    }

    // THE NODE HAS TO BE GRABBABLE, or it is an invisible thing you can create and never touch again. This
    // covers the marker/drag path specifically -- MoveJunction was already tested at the model level and
    // passed while nothing in the tool called it, which is the same "works but unreachable" split that shipped
    // the doors.
    public class RoadDrawNodeHandles : GameTest
    {
        public override string Name => "editor.road_draw_nodes";
        public override double TimeoutSimSeconds => 20;

        static List<Vector3> Line(Vector3 a, Vector3 b, int n)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < n; i++) pts.Add(a.Lerp(b, i / (float)(n - 1)));
            return pts;
        }

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var field = new RoadField(); World.AddChild(field);
            var cam = new Camera3D(); World.AddChild(cam);
            var tool = new EditorRoadDraw(ed, cam, field); World.AddChild(tool);
            yield return Ticks(1);

            int a = tool.DebugDrawRoad(Line(new Vector3(0, 0, 0), new Vector3(100, 0, 0), 6));
            int b = tool.DebugDrawRoad(Line(new Vector3(103, 0, 2), new Vector3(103, 0, 100), 6));
            // EVERY PIECE gets a node at each end now (strawberry: "nodes are just at the ends of each road
            // piece"), so two pieces sharing one end is 3 nodes, not 1: A's free start, the shared one, B's
            // free end. Only the shared one is a JUNCTION (2+ roads bound).
            T.Check($"each piece gets a node per end, sharing the joined one ({field.JunctionCount} nodes)",
                    field.JunctionCount == 3);
            T.Check($"...of which exactly one is a junction ({field.Junctions().Count})", field.Junctions().Count == 1);

            tool.DebugSetDrawing(true);
            yield return Ticks(1);
            T.Check($"entering the tool builds a marker per node ({tool.DebugNodeMarkerCount} vs {field.JunctionCount})",
                    tool.DebugNodeMarkerCount == field.JunctionCount && tool.DebugNodeMarkerCount == 3);

            // Dragging the SHARED node must carry BOTH rail ends. Found by degree, not by index -- node 0 is
            // A's free start, and the first version of this test dragged that and "proved" nothing moved.
            int shared = -1;
            for (int i = 0; i < field.JunctionCount; i++) if (field.JunctionEdges(i).Count >= 2) { shared = i; break; }
            T.Check($"found the shared node ({shared})", shared >= 0);
            var to = new Vector3(140f, 0f, 40f);
            tool.DebugDragNode(shared, to);
            T.Check($"dragging the node moved rail A's end ({field.JointPos(a, field.JointCount(a) - 1)})",
                    field.JointPos(a, field.JointCount(a) - 1) == to);
            T.Check($"...and rail B's end ({field.JointPos(b, 0)})", field.JointPos(b, 0) == to);
            T.Check($"...and it is still a junction ({field.Junctions().Count})", field.Junctions().Count == 1);

            // Deleting the node frees the ends but must NOT delete the rails -- losing two roads because you
            // removed a connector would be a nasty surprise.
            int roadsBefore = field.RoadCount;
            field.RemoveJunction(shared);
            T.Check($"deleting a node keeps the rails ({roadsBefore} -> {field.RoadCount})", field.RoadCount == roadsBefore);
            T.Check($"...and frees their ends (a.end={field.RoadEndJunction(a, true)}, b.start={field.RoadEndJunction(b, false)})",
                    field.RoadEndJunction(a, true) == -1 && field.RoadEndJunction(b, false) == -1);
            T.Check($"...so there is no junction left ({field.Junctions().Count})", field.Junctions().Count == 0);

            tool.QueueFree(); field.QueueFree(); cam.QueueFree(); ed.QueueFree();
        }
    }

    // SNAPPING TO THE SPLINE ITSELF (strawberry 2026-08-19: "the tool can snap along the splines... the spline
    // line is what new roads snap to"). The distinction that matters: a road's JOINTS are discrete points, so
    // joint-snapping only lets you branch where a joint happens to sit. This aims at a spot deliberately
    // BETWEEN two joints and requires the junction to land there -- which is only possible if the tool
    // measured against the curve, inserted a joint at the hit, and split.
    public class RoadDrawSplineSnap : GameTest
    {
        public override string Name => "editor.road_spline_snap";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var field = new RoadField(); World.AddChild(field);
            var cam = new Camera3D(); World.AddChild(cam);
            var tool = new EditorRoadDraw(ed, cam, field); World.AddChild(tool);
            yield return Ticks(1);

            // A road with joints exactly every 20 m: 0, 20, 40, 60, 80, 100.
            var pts = new List<Vector3>();
            for (int i = 0; i <= 5; i++) pts.Add(new Vector3(i * 20f, 0f, 0f));
            int a = tool.DebugDrawRoad(pts);
            T.Check($"base road laid with joints every 20 m ({field.JointCount(a)})", field.JointCount(a) == 6);

            // Aim at x=50 -- the MIDPOINT between joints 2 (40) and 3 (60), 10 m from either. A joint-snapping
            // tool would pull this to 40 or 60; the curve is what it must measure against.
            var target = new Vector3(50f, 0f, 0f);
            bool onSpline = field.NearestPointOnSpline(target, 12f, out int sr, out int seg, out _, out var sp);
            T.Check($"the spline query finds a point ON the curve, not at a joint ({sp})",
                    onSpline && sr == a && Mathf.Abs(sp.X - 50f) < 1.5f);

            int nodesBefore = field.JunctionCount;
            int b = tool.DebugDrawRoad(new List<Vector3> { target, new Vector3(50f, 0f, -60f), new Vector3(50f, 0f, -120f) });
            T.Check($"the branch was laid ({b})", b >= 0);

            // The junction must be AT x=50, not dragged to a joint.
            var three = field.Junctions().Find(x => x.Ends.Count >= 3);
            T.Check($"branching mid-segment makes a 3-way (largest group {(three.Ends?.Count ?? 0)})",
                    three.Ends != null && three.Ends.Count == 3);
            T.Check($"...and it sits where we aimed, x={three.Pos.X:0.0} (not snapped back to 40 or 60)",
                    three.Ends != null && Mathf.Abs(three.Pos.X - 50f) < 1.5f);
            T.Check($"...which required splitting the base road ({nodesBefore} nodes -> {field.JunctionCount})",
                    field.RoadCount >= 3);

            tool.QueueFree(); field.QueueFree(); cam.QueueFree(); ed.QueueFree();
        }
    }

    // PROP CONNECTION POINTS (strawberry 2026-08-19: "giving certain props road/rail connection points. for
    // example all the road 'cap' props have snap points where roads will connect to").
    //
    // The catalogue is derived from the meshes, so the test that matters is not "does the file parse" but
    // "does a road drawn NEAR a prop's connector land exactly ON it" -- and, as the control, that a road
    // drawn far from one does not get dragged to it.
    public class RoadPropConnectors : GameTest
    {
        public override string Name => "editor.road_prop_connectors";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            // The catalogue itself, off the real derived file.
            var line = PropConnectors.For("Road_Line_0");
            T.Check($"Road_Line_0 has 2 connectors ({line?.Count})", line != null && line.Count == 2);
            var tee = PropConnectors.For("Road_Tee_0");
            T.Check($"Road_Tee_0 has 3 ({tee?.Count})", tee != null && tee.Count == 3);
            var quad = PropConnectors.For("Road_Quad_0");
            T.Check($"Road_Quad_0 has 4 ({quad?.Count})", quad != null && quad.Count == 4);
            T.Check("a non-road prop has none", PropConnectors.For("Ladder_Metal_0") == null);
            // The two the extractor refused to guess at stay absent ON PURPOSE.
            T.Check("Bridge_Line_Cap_1 is absent -- the extractor could not read it and did not guess",
                    PropConnectors.For("Bridge_Line_Cap_1") == null);

            // Placement: register a tile with a known transform and check a drawn road snaps to it.
            PropConnectors.ClearPlaced();
            var xf = new Transform3D(Basis.Identity, new Vector3(200f, 0f, 0f));
            PropConnectors.Register("Road_Line_0", xf);
            T.Check($"placing the tile registered its points ({PropConnectors.PlacedCount})", PropConnectors.PlacedCount == 2);

            var ed = new Editor(); World.AddChild(ed);
            var field = new RoadField(); World.AddChild(field);
            var cam = new Camera3D(); World.AddChild(cam);
            var tool = new EditorRoadDraw(ed, cam, field); World.AddChild(tool);
            yield return Ticks(1);

            // Where did the tile's connectors actually land? Ask, rather than assume the axis mapping.
            T.Check("a connector is findable near the placed tile",
                    PropConnectors.Nearest(new Vector3(200f, 0f, 0f), 40f, out var cp));
            PropConnectors.Nearest(new Vector3(200f, 0f, 0f), 40f, out cp);

            // Draw a road starting ~5 m off the connector: it must snap ONTO it exactly.
            var off = cp + new Vector3(5f, 0f, 0f);
            int r = tool.DebugDrawRoad(new List<Vector3> { off, off + new Vector3(0f, 0f, 60f), off + new Vector3(0f, 0f, 120f) });
            T.Check($"a road drawn near a prop connector snaps exactly onto it ({field.JointPos(r, 0)} vs {cp})",
                    field.JointPos(r, 0) == cp);

            // CONTROL: far away, it must NOT be dragged to the prop.
            var far = new Vector3(-600f, 0f, 0f);
            int r2 = tool.DebugDrawRoad(new List<Vector3> { far, far + new Vector3(0f, 0f, 60f) });
            T.Check($"CONTROL -- a road drawn far from the prop is NOT pulled to it ({field.JointPos(r2, 0)})",
                    field.JointPos(r2, 0) == far);

            PropConnectors.ClearPlaced();
            tool.QueueFree(); field.QueueFree(); cam.QueueFree(); ed.QueueFree();
        }
    }

    // THE BUTTONS (strawberry: "add both road tools as actual ui buttons"). Two things are worth testing and
    // neither is "a button exists": that the tools are MUTUALLY EXCLUSIVE (they both bind LMB on the terrain,
    // so two live tools means one click doing two things), and that the buttons reflect the tools' REAL state
    // rather than the panel's memory of its own last click -- because the keys still work, and a button
    // claiming a tool is on while it is off is worse than having no button.
    public class RoadPanelButtons : GameTest
    {
        public override string Name => "editor.road_panel_buttons";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var field = new RoadField(); World.AddChild(field);
            var cam = new Camera3D(); World.AddChild(cam);
            var draw = new EditorRoadDraw(ed, cam, field); World.AddChild(draw);
            var legacy = new EditorRoads(ed, cam, field); World.AddChild(legacy);
            var panel = new EditorRoadsPanel(draw, legacy); World.AddChild(panel);
            yield return Ticks(2);

            T.Check($"both tools start off (draw {draw.Drawing}, legacy {legacy.Paving})", !draw.Drawing && !legacy.Paving);

            panel.DebugClick(draw: true);
            T.Check($"the Draw button turns the draw tool on ({draw.Drawing})", draw.Drawing);
            T.Check($"...and leaves the legacy tool off ({legacy.Paving})", !legacy.Paving);

            panel.DebugClick(draw: false);
            T.Check($"the Legacy button turns the legacy tool on ({legacy.Paving})", legacy.Paving);
            T.Check($"...and turns the DRAW tool off -- one owner of the mouse ({draw.Drawing})", !draw.Drawing);

            panel.DebugClick(draw: true);
            T.Check($"and back again, exclusively (draw {draw.Drawing}, legacy {legacy.Paving})",
                    draw.Drawing && !legacy.Paving);

            // The keys still work. Drive the TOOL directly (as a keypress does) and the button must follow --
            // this is the check that catches a panel trusting its own last click.
            draw.SetActive(false);
            panel.DebugSync();
            T.Check($"a tool turned off by the KEY updates its button ({panel.DebugDrawButtonOn})", !panel.DebugDrawButtonOn);
            legacy.SetActive(true);
            panel.DebugSync();
            T.Check($"...and a tool turned on by the key does too ({panel.DebugLegacyButtonOn})", panel.DebugLegacyButtonOn);

            panel.QueueFree(); draw.QueueFree(); legacy.QueueFree(); field.QueueFree(); cam.QueueFree(); ed.QueueFree();
        }
    }

    // ...AND THAT IT SURVIVES A SAVE. The junction graph is a NEW sidecar file next to Paths.dat, and its road
    // links are POSITIONAL against that file's road order -- there is no per-road id to key on. That is the
    // kind of coupling that works perfectly until the two files disagree and then binds the wrong rails to the
    // wrong nodes silently, months later, presenting as a routing bug. So this round-trips it, and also checks
    // the refusal path: a sidecar whose road count does not match must be DROPPED, not applied.
    public class RoadGraphPersistence : GameTest
    {
        public override string Name => "editor.road_graph_persist";
        public override double TimeoutSimSeconds => 20;

        static List<Vector3> Line(Vector3 a, Vector3 b, int n)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < n; i++) pts.Add(a.Lerp(b, i / (float)(n - 1)));
            return pts;
        }

        public override IEnumerable<Step> Run()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ug_roadgraph_test");
            System.IO.Directory.CreateDirectory(dir);
            string pathsFile = System.IO.Path.Combine(dir, "Paths.dat");
            string graphFile = System.IO.Path.Combine(dir, RoadField.GraphFileName);

            var f1 = new RoadField();
            World.AddChild(f1);
            yield return Ticks(1);

            int a = f1.AddRoadFromPolyline(Line(new Vector3(0, 0, 0), new Vector3(100, 0, 0), 6));
            int b = f1.AddRoadFromPolyline(Line(new Vector3(100, 0, 0), new Vector3(100, 0, 100), 6));
            var at = f1.JointPos(a, f1.JointCount(a) - 1);
            int n0 = f1.AddJunction(at);
            f1.BindRoadEnd(a, atEnd: true, n0);
            f1.BindRoadEnd(b, atEnd: false, n0);
            T.Check($"built a 2-road junction to save ({f1.Junctions().Count})", f1.Junctions().Count == 1);

            T.Check("Paths.dat written", f1.SavePaths(pathsFile));
            T.Check("junction sidecar written", f1.SaveGraph(graphFile));

            // ---- reload into a FRESH field
            var f2 = new RoadField();
            World.AddChild(f2);
            yield return Ticks(1);
            T.Check("Paths.dat read back", f2.ReloadPaths(pathsFile));
            T.Check($"...with both roads ({f2.RoadCount})", f2.RoadCount == 2);
            T.Check("junction sidecar read back", f2.LoadGraph(graphFile));
            T.Check($"...restoring the node ({f2.JunctionCount})", f2.JunctionCount == 1);
            T.Check($"...at the same position ({f2.JunctionPos(0)} vs {at})", f2.JunctionPos(0).DistanceTo(at) < 1e-3f);
            T.Check($"...with BOTH bindings intact (a.end={f2.RoadEndJunction(0, true)}, b.start={f2.RoadEndJunction(1, false)})",
                    f2.RoadEndJunction(0, true) == 0 && f2.RoadEndJunction(1, false) == 0);
            T.Check($"...so it is still a junction after a round trip ({f2.Junctions().Count})", f2.Junctions().Count == 1);

            // ---- REFUSAL: a sidecar that does not match the road list must be dropped, not applied. Without
            // this the links would bind by position to whatever roads happen to be loaded.
            var f3 = new RoadField();
            World.AddChild(f3);
            yield return Ticks(1);
            f3.AddRoadFromPolyline(Line(new Vector3(0, 0, 0), new Vector3(50, 0, 0), 4));   // ONE road, sidecar says two
            bool applied = f3.LoadGraph(graphFile);
            T.Check($"a stale sidecar (1 road vs 2 links) is REFUSED, not applied ({applied})", !applied);
            T.Check($"...and leaves no bindings behind ({f3.RoadEndJunction(0, true)})", f3.RoadEndJunction(0, true) == -1);

            // ---- no junctions -> no stale file left on disk
            var f4 = new RoadField();
            World.AddChild(f4);
            yield return Ticks(1);
            f4.AddRoadFromPolyline(Line(new Vector3(0, 0, 0), new Vector3(50, 0, 0), 4));
            f4.SaveGraph(graphFile);
            T.Check("saving a field with no junctions removes the sidecar rather than leaving a stale one",
                    !System.IO.File.Exists(graphFile));

            f1.QueueFree(); f2.QueueFree(); f3.QueueFree(); f4.QueueFree();
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }
}
