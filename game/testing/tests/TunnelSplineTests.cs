using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>"Tunnel": the shipped bore run along a road spline, portalled at both ends.
    ///
    /// Master 2026-10-09, straight after the bridge landed: "now work on tunnel splines. same method."
    /// Same method, three real differences -- no cut asset, retail's own end-closer, and the road stays
    /// drawn -- and each of them is a thing that fails quietly if it is wrong.</summary>
    public sealed class TunnelSplineTests : GameTest
    {
        public override string Name => "editor.tunnel_spline";
        public override double TimeoutSimSeconds => 30;

        static (float gap, float overlap) WorstJoint(List<Node3D> bores)
        {
            float g = 0f, o = 0f;
            const float half = EditorTunnelSpline.Pitch * 0.5f, hw = EditorTunnelSpline.HalfWidth;
            for (int i = 1; i < bores.Count; i++)
            {
                var a = bores[i - 1]; var b = bores[i];
                var af = a.GlobalTransform.Basis.Y.Normalized();
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    // ⚠ the section is CENTRED on its length, and SCALED, so its ends are half a pitch out
                    // along the already-shortened Y axis -- expressed in world units, not local ones.
                    var endC = a.GlobalPosition + af * half + a.GlobalTransform.Basis.X.Normalized() * sgn * hw;
                    var startC = b.GlobalPosition - b.GlobalTransform.Basis.Y.Normalized() * half
                               + b.GlobalTransform.Basis.X.Normalized() * sgn * hw;
                    float along = af.Dot(startC - endC);
                    g = Mathf.Max(g, along); o = Mathf.Max(o, -along);
                }
            }
            return (g, o);
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
            field.DebugSetMaterialWidth(0, 8.5f);
            field.DebugSetMaterialDepth(0, 0.3f);
            var terr = Terrain.CreateFlat(1, 1, withCollider: false);
            World.AddChild(terr);
            field.Terr = terr;
            yield return Ticks(2);
            float g0 = terr.SampleHeight(50f, 0f);

            // ---- 0. THE ASSET CONTRACT, because every constant in the tool is read off these two meshes.
            var bore = ContentProvider.ParseObj($"res://content/objects/{EditorTunnelSpline.BoreUnit}.obj");
            var portal = ContentProvider.ParseObj($"res://content/objects/{EditorTunnelSpline.PortalUnit}.obj");
            T.Check("the bore and portal meshes load", bore != null && portal != null);
            if (bore == null || portal == null) yield break;
            var bb = bore.GetAabb();
            T.Check($"the bore section is {EditorTunnelSpline.SourceLength:0.#} m long and "
                  + $"{EditorTunnelSpline.HalfWidth * 2f:0.#} m wide (Y {bb.Position.Y:0.#}..{bb.End.Y:0.#}, "
                  + $"X {bb.Position.X:0.#}..{bb.End.X:0.#})",
                    Mathf.Abs((bb.End.Y - bb.Position.Y) - EditorTunnelSpline.SourceLength) < 0.05f
                 && Mathf.Abs(bb.Position.X + EditorTunnelSpline.HalfWidth) < 0.05f);

            // ⭐ THE PITCH IS A WHOLE FRACTION OF THE SHIPPED SECTION, so N units reproduce retail's piece.
            float ratio = EditorTunnelSpline.SourceLength / EditorTunnelSpline.Pitch;
            T.Check($"...and the pitch divides it exactly ({ratio:0.###} units per shipped section)",
                    Mathf.Abs(ratio - Mathf.Round(ratio)) < 1e-4f);

            // ⚠ NO FLOOR. The tool keeps the painted road for exactly this reason; if the bore ever gained a
            // floor face the road would z-fight it and the right answer would flip.
            var ba = bore.SurfaceGetArrays(0);
            var BV = ba[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            float zlo = float.MaxValue;
            foreach (var v in BV) zlo = Mathf.Min(zlo, v.Z);
            int floor = 0;
            for (int i = 0; i + 2 < BV.Length; i += 3)
                if (Mathf.Abs(BV[i].Z - zlo) < 1e-3f && Mathf.Abs(BV[i + 1].Z - zlo) < 1e-3f
                 && Mathf.Abs(BV[i + 2].Z - zlo) < 1e-3f) floor++;
            T.Check($"the bore has NO floor ({floor} face(s) in its lowest plane) -- so the road must stay drawn",
                    floor == 0);

            // ---- 1. A RUN, STRAIGHT.
            var pts = new List<Vector3>();
            for (int k = 0; k <= 10; k++) pts.Add(new Vector3(k * 20f, g0, 0f));
            int road = field.AddRoadFromPolyline(pts, 0, false, true);
            yield return Ticks(1);
            T.Check("fixture: a road with its ribbon drawn", field.RoadRibbonVisible(road));

            int n = EditorTunnelSpline.LayAlong(objs, terr, field, road, null);
            yield return Ticks(1);
            var bores = new List<Node3D>();
            foreach (var b in objs.PlacedOfNodes(EditorTunnelSpline.BoreUnit)) bores.Add(b);
            var ports = new List<Node3D>();
            foreach (var p in objs.PlacedOfNodes(EditorTunnelSpline.PortalUnit)) ports.Add(p);

            float len = field.RoadLength(road);
            float wantBore = len - EditorTunnelSpline.PortalLength * 2f;
            T.Check($"bored the span BETWEEN the portals ({n} sections over {wantBore:0.#} m of a {len:0.#} m road)",
                    n == Mathf.FloorToInt(wantBore / EditorTunnelSpline.Pitch + 1e-3f));
            T.Check($"...with a portal at each end ({ports.Count})", ports.Count == 2);

            // ⭐ THE SECTIONS ARE SHORTENED, which is the whole no-cut-asset idea. A tool that forgot the
            // scale would place full 24 m shells every 4 m and look like a solid block.
            bool scaled = true;
            foreach (var b in bores)
                if (Mathf.Abs(b.GlobalTransform.Basis.Y.Length() - EditorTunnelSpline.Pitch / EditorTunnelSpline.SourceLength) > 0.01f)
                    scaled = false;
            T.Check($"each section is scaled to the pitch on its OWN axis "
                  + $"({(bores.Count > 0 ? bores[0].GlobalTransform.Basis.Y.Length() : -1f):0.000}, want "
                  + $"{EditorTunnelSpline.Pitch / EditorTunnelSpline.SourceLength:0.000})", bores.Count > 0 && scaled);
            // ...and ONLY on that axis: a parent-frame scale would shrink the width and height too.
            bool crossOk = true;
            foreach (var b in bores)
                if (Mathf.Abs(b.GlobalTransform.Basis.X.Length() - 1f) > 0.01f
                 || Mathf.Abs(b.GlobalTransform.Basis.Z.Length() - 1f) > 0.01f) crossOk = false;
            T.Check("...leaving its width and height untouched", crossOk);

            // ⚠ THE PORTALS FACE OUT. Each one's facade is its local +Y; reversed, you would be looking at
            // the back of a wall from inside the tunnel and at an open hole from outside.
            if (ports.Count == 2)
            {
                var centre = Vector3.Zero;
                foreach (var p in ports) centre += p.GlobalPosition;
                centre *= 0.5f;
                bool outward = true;
                foreach (var p in ports)
                    if (p.GlobalTransform.Basis.Y.Normalized().Dot((p.GlobalPosition - centre).Normalized()) < 0.9f)
                        outward = false;
                T.Check("each portal's facade faces out of the tunnel", outward);
            }

            // ⚠ AND THE ROAD IS STILL THERE -- the opposite of the bridge, which hides it.
            T.Check("the painted road is KEPT (a tunnel has no floor of its own)", field.RoadRibbonVisible(road));

            // ---- 2. THE SEAM, at the widest section the port tiles.
            var arc = new List<Vector3>();
            const float ArcR = 301f;
            for (int k = 0; k <= 30; k++)
            {
                float ang = k * 0.012f;
                arc.Add(new Vector3(ArcR * Mathf.Sin(ang), g0, 900f + ArcR * (1f - Mathf.Cos(ang))));
            }
            int ctlRoad = field.AddRoadFromPolyline(arc, 0, false, true);
            var before = new List<Node3D>(); foreach (var b in objs.PlacedOfNodes(EditorTunnelSpline.BoreUnit)) before.Add(b);
            EditorTunnelSpline.DebugNoJointOverlap = true;
            EditorTunnelSpline.LayAlong(objs, terr, field, ctlRoad, null);
            EditorTunnelSpline.DebugNoJointOverlap = false;
            var ctlBores = new List<Node3D>();
            foreach (var b in objs.PlacedOfNodes(EditorTunnelSpline.BoreUnit)) if (!before.Contains(b)) ctlBores.Add(b);
            var ctl = WorstJoint(ctlBores);
            T.Check($"control: WITHOUT the overlap a {ArcR:0} m bend opens {ctl.gap * 100f:0.0} cm at the "
                  + $"outer wall of a {EditorTunnelSpline.HalfWidth * 2f:0.#} m section ({ctlBores.Count} sections)",
                    ctlBores.Count > 5 && ctl.gap > 0.05f);

            int fixRoad = field.AddRoadFromPolyline(arc, 0, false, true);
            var before2 = new List<Node3D>(); foreach (var b in objs.PlacedOfNodes(EditorTunnelSpline.BoreUnit)) before2.Add(b);
            EditorTunnelSpline.LayAlong(objs, terr, field, fixRoad, null);
            var fixBores = new List<Node3D>();
            foreach (var b in objs.PlacedOfNodes(EditorTunnelSpline.BoreUnit)) if (!before2.Contains(b)) fixBores.Add(b);
            var fx = WorstJoint(fixBores);
            T.Check($"...and WITH it the same bend closes to {fx.gap * 1000f:0.#} mm ({fixBores.Count} sections)",
                    fixBores.Count > 5 && fx.gap < 0.01f);
            T.Check($"...paid for on the inside, buried not gapped ({fx.overlap * 100f:0.0} cm vs "
                  + $"{ctl.overlap * 100f:0.0} cm)", fx.overlap > ctl.overlap + 0.01f);

            // ---- 3. TOO SHORT TO PORTAL is reported, not silently half-done.
            var tiny = new List<Vector3> { new Vector3(0f, g0, 2000f), new Vector3(30f, g0, 2000f) };
            int shortRoad = field.AddRoadFromPolyline(tiny, 0, false, true);
            int pBefore = 0; foreach (var _ in objs.PlacedOfNodes(EditorTunnelSpline.PortalUnit)) pBefore++;
            EditorTunnelSpline.LayAlong(objs, terr, field, shortRoad, null);
            int pAfter = 0; foreach (var _ in objs.PlacedOfNodes(EditorTunnelSpline.PortalUnit)) pAfter++;
            T.Check($"a road under {EditorTunnelSpline.PortalLength * 2f:0.#} m gets no portals rather than "
                  + $"two overlapping ones ({pAfter - pBefore} added)", pAfter == pBefore);
        }
    }
}
