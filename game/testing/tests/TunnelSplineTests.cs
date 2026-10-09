using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>"Tunnel": the shipped section's profile SWEPT along a road spline, portalled at both ends.
    ///
    /// Master, on the tiled first version: "theres gaps and probably overlaps inside the tunnel. you need
    /// cuts/blending." The checks below are about that correction, so they are about the thing tiling could
    /// not give: a bore wall with no joint in it at all.</summary>
    public sealed class TunnelSplineTests : GameTest
    {
        public override string Name => "editor.tunnel_spline";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var cam = new Camera3D();
            World.AddChild(cam);
            // ⚠ CLEAR THE SIDECAR BEFORE CONSTRUCTING, not only after. EditorObjects loads the custom
            // placeables in its own setup, so a file left behind by a previous run is already placed before
            // the first assertion -- which is exactly how this test came back reporting two tunnels for one.
            try { System.IO.File.Delete(ProjectSettings.GlobalizePath("res://content/objects/editor__tunnels.txt")); } catch { }
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

            // ---- 0. THE PROFILE IS READ OFF THE SHIPPED PROP, and comes out as TWO OPEN ARCHES: the bore
            // and the outer shell, both floorless. A first pass that assumed closed loops reported three,
            // splitting the shell in half -- so the count is asserted, not trusted.
            var prof = objs.TunnelProfile();
            T.Check($"the section profile reads as two open arches ({(prof == null ? -1 : prof.Count)})",
                    prof != null && prof.Count == 2);
            if (prof == null || prof.Count != 2) yield break;
            int profPts = 0;
            float widest = 0f, tallest = 0f, lowest = float.MaxValue;
            foreach (var ch in prof)
            {
                profPts += ch.Length;
                foreach (var pt in ch)
                {
                    widest = Mathf.Max(widest, Mathf.Abs(pt.X));
                    tallest = Mathf.Max(tallest, pt.Y);
                    lowest = Mathf.Min(lowest, pt.Y);
                }
            }
            T.Check($"...spanning the section's own measured extents (half-width {widest:0.##} m, "
                  + $"height {lowest:0.##}..{tallest:0.##})",
                    Mathf.Abs(widest - EditorTunnelSpline.HalfWidth) < 0.05f && tallest > 16f && lowest < -0.9f);
            // ⚠ NO FLOOR, which is why the tool keeps the painted road drawn.
            bool floored = false;
            foreach (var ch in prof)
                for (int k = 0; k + 1 < ch.Length; k++)
                    if (Mathf.Abs(ch[k].Y - lowest) < 1e-3f && Mathf.Abs(ch[k + 1].Y - lowest) < 1e-3f) floored = true;
            T.Check("the section has no floor span -- the road through it stays drawn", !floored);

            // ---- 1. A BORE ON THE TIGHTEST REAL BEND.
            var arc = new List<Vector3>();
            const float ArcR = 301f;
            for (int k = 0; k <= 30; k++)
            {
                float ang = k * 0.012f;
                arc.Add(new Vector3(ArcR * Mathf.Sin(ang), g0, ArcR * (1f - Mathf.Cos(ang))));
            }
            int road = field.AddRoadFromPolyline(arc, 0, false, true);
            yield return Ticks(1);
            T.Check("fixture: a curved road with its ribbon drawn", field.RoadRibbonVisible(road));

            int rings = EditorTunnelSpline.LayAlong(objs, terr, field, road, null);
            yield return Ticks(1);
            var tunnels = new List<Node3D>();
            foreach (var t in objs.PlacedOfNodes(EditorObjects.TunnelName)) tunnels.Add(t);
            T.Check($"the bore is ONE swept mesh, not a run of props ({tunnels.Count} node(s), {rings} rings)",
                    tunnels.Count == 1 && rings > 20);

            // ---- 2. ⭐⭐ THE THING MASTER REPORTED. A tiled run cannot avoid a seam at every joint: flat
            // parallel ends on a curve gap on one wall and overlap on the other, and the overlap that closes
            // the 12 m shell over-closes the 8 m bore. A sweep's consecutive rings SHARE their vertices, so
            // there is nothing to gap -- and that is checkable exactly: the number of DISTINCT vertex
            // positions must be rings x profile points. Duplicated rings would double it.
            if (tunnels.Count == 1)
            {
                var mi = tunnels[0].GetChild<MeshInstance3D>(0);
                var am = mi.Mesh as ArrayMesh;
                T.Check("fixture: the swept mesh exists and has geometry",
                        am != null && am.GetSurfaceCount() > 0);
                if (am != null && am.GetSurfaceCount() > 0)
                {
                    // ⚠⚠ VERTEX COLOURS, because MatFor sets VertexColorUseAsAlbedo and therefore
                    // MULTIPLIES by them. Every prop mesh gets a white COLOR array from ObjMesh.Load; a
                    // generated mesh without one is albedo x nothing and renders near-black, which is
                    // exactly what the first three sweeps did.
                    // one-shot: diff MY mesh against the one the PROP path produces, slot by slot. Comparing
                    // against ContentProvider.ParseObj earlier was the wrong artifact -- props load through
                    // ObjMesh.Load, which writes arrays ParseObj does not.
                    var refProp = objs.Place("Tunnel_Line_0", new Vector3(500f, 0f, 500f), EditorObjects.Upright(0f));
                    if (refProp != null)
                    {
                        var rm = refProp.GetChild<MeshInstance3D>(0).Mesh as ArrayMesh;
                        var ra = rm.SurfaceGetArrays(0);
                        var ma = am.SurfaceGetArrays(0);
                        string[] slot = { "Vertex", "Normal", "Tangent", "Color", "TexUV", "TexUV2" };
                        for (int q = 0; q < slot.Length; q++)
                            Log.Print($"[tundiag] {slot[q],-8} prop={(ra[q].VariantType == Variant.Type.Nil ? "nil" : "SET")}"
                                    + $"  swept={(ma[q].VariantType == Variant.Type.Nil ? "nil" : "SET")}");
                        Log.Print($"[tundiag] prop surface format = {rm.SurfaceGetFormat(0)}");
                        Log.Print($"[tundiag] swept surface format = {am.SurfaceGetFormat(0)}");
                        var rn = ra[(int)Mesh.ArrayType.Normal].AsVector3Array();
                        var mn = ma[(int)Mesh.ArrayType.Normal].AsVector3Array();
                        Log.Print($"[tundiag] prop n[0]={rn[0]} swept n[0]={mn[0]}");
                    }

                    var swC = am.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Color];
                    T.Check("the swept mesh carries vertex colours -- the shared material multiplies by them",
                            swC.VariantType != Variant.Type.Nil && swC.AsColorArray().Length > 0);
                    if (swC.VariantType != Variant.Type.Nil && swC.AsColorArray().Length > 0)
                    {
                        var cc = swC.AsColorArray();
                        bool white = true;
                        foreach (var col in cc) if (col.R < 0.99f || col.G < 0.99f || col.B < 0.99f) white = false;
                        T.Check($"...and they are white, so the material's own albedo shows through ({cc[0]})", white);
                    }

                    var V = am.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var uniq = new HashSet<Vector3>();
                    foreach (var v in V)
                        uniq.Add(new Vector3(Mathf.Round(v.X * 1000f) / 1000f, Mathf.Round(v.Y * 1000f) / 1000f,
                                             Mathf.Round(v.Z * 1000f) / 1000f));
                    int want = rings * profPts;
                    T.Check($"every ring SHARES its vertices with the next -- no joint to gap "
                          + $"({uniq.Count} distinct positions for {rings} rings x {profPts} profile points)",
                            uniq.Count == want);

                    // ⭐ AND THE BORE WALL HOLDS ITS RADIUS. This is the lip master saw, measured: project
                    // every vertex onto the nearest centreline station and compare its lateral offset with
                    // the profile's. A tiled run steps at each joint; a sweep must not move at all.
                    var centre = (Vector3[])tunnels[0].GetMeta("tunnel_centre");
                    float worst = 0f;
                    foreach (var v in uniq)
                    {
                        float best = float.MaxValue; int bi = 0;
                        for (int i = 0; i < centre.Length; i++)
                        {
                            float d = new Vector2(v.X - centre[i].X, v.Z - centre[i].Z).LengthSquared();
                            if (d < best) { best = d; bi = i; }
                        }
                        float lat = Mathf.Sqrt(best);
                        // the nearest station's lateral offset must be one the profile actually contains
                        // ⚠ against the profile AS WIDENED. The bore is scaled laterally to clear the road,
                        // so comparing with the authored offsets reports the widening itself as an error.
                        float latScale = (float)tunnels[0].GetMeta("tunnel_lateral");
                        float near = float.MaxValue;
                        foreach (var ch in prof)
                            foreach (var pt in ch) near = Mathf.Min(near, Mathf.Abs(Mathf.Abs(pt.X) * latScale - lat));
                        worst = Mathf.Max(worst, near);
                    }
                    T.Check($"...and every wall vertex sits at a lateral offset the profile declares "
                          + $"(worst {worst * 1000f:0.#} mm off)", worst < 0.06f);
                }
            }

            // ---- 2b. ⭐ THE BORE CLEARS THE ROAD IT CARRIES. Master: "make the tunnel wider to fit the
            // whole road spline + a small border." The shipped section's bore is a fixed 16 m, right for the
            // carriageway it was drawn for and far too narrow for a dual highway.
            if (tunnels.Count == 1)
            {
                float roadHalf = field.RoadHalfWidth(road);
                float authored = TunnelMesh.BoreHalfWidth(prof);
                var mi2 = tunnels[0].GetChild<MeshInstance3D>(0);
                var vv = (mi2.Mesh as ArrayMesh).SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var cc = (Vector3[])tunnels[0].GetMeta("tunnel_centre");
                // widest lateral reach of any wall vertex from its nearest station
                float reach = 0f;
                foreach (var v in vv)
                {
                    float best = float.MaxValue;
                    foreach (var c in cc) best = Mathf.Min(best, new Vector2(v.X - c.X, v.Z - c.Z).LengthSquared());
                    reach = Mathf.Max(reach, Mathf.Sqrt(best));
                }
                float wantBore = roadHalf + EditorTunnelSpline.BoreBorder;
                T.Check($"fixture: the road is {roadHalf * 2f:0.#} m wide and the section's authored bore "
                      + $"{authored * 2f:0.#} m", roadHalf > 1f && authored > 1f);
                T.Check($"the swept bore clears the road plus its border (reaches {reach:0.##} m, needs "
                      + $"{wantBore:0.##} m)", reach >= wantBore - 0.01f);
                // ⭐ CONTROL: it must not simply be huge. A scale that ignored the road would pass "clears"
                // trivially, so the OUTER wall has to stay in proportion to what was authored.
                float lat = (float)tunnels[0].GetMeta("tunnel_lateral");
                T.Check($"...by scaling laterally, not arbitrarily (x{lat:0.00}, never below the authored 1.00)",
                        lat >= 1f && lat < 4f);
                // ⚠ AND THE HEIGHT IS UNTOUCHED -- widening must not inflate the arch.
                float tall = 0f;
                foreach (var v in vv) tall = Mathf.Max(tall, v.Y - cc[0].Y);
                float wantTall = 0f;
                foreach (var ch in prof) foreach (var pt in ch) wantTall = Mathf.Max(wantTall, pt.Y);
                T.Check($"...with the arch's HEIGHT unchanged ({tall:0.##} m vs the profile's {wantTall:0.##} m)",
                        Mathf.Abs(tall - wantTall) < 0.3f);
            }

            // ---- 3. PORTALS AT BOTH ENDS, facing out, and the road still drawn.
            var ports = new List<Node3D>();
            foreach (var p in objs.PlacedOfNodes(EditorTunnelSpline.PortalUnit)) ports.Add(p);
            T.Check($"a portal at each end ({ports.Count})", ports.Count == 2);
            if (ports.Count == 2)
            {
                var mid = (ports[0].GlobalPosition + ports[1].GlobalPosition) * 0.5f;
                bool outward = true;
                foreach (var p in ports)
                    if (p.GlobalTransform.Basis.Y.Normalized().Dot((p.GlobalPosition - mid).Normalized()) < 0.9f)
                        outward = false;
                T.Check("...each facade facing out of the tunnel", outward);
                // ⭐ AND WIDENED WITH THE BORE. A fixed-width portal on a widened tube is a mouth that no
                // longer matches the tunnel behind it -- visible from the one place everybody looks.
                float lat2 = (float)tunnels[0].GetMeta("tunnel_lateral");
                float worstP = 0f;
                foreach (var p in ports) worstP = Mathf.Max(worstP, Mathf.Abs(p.GlobalTransform.Basis.X.Length() - lat2));
                T.Check($"...and scaled across to match the bore (worst {worstP:0.###} off x{lat2:0.00})",
                        worstP < 0.01f);
            }
            T.Check("the painted road is KEPT -- the opposite of the bridge, which hides it",
                    field.RoadRibbonVisible(road));

            // ---- 4. IT SURVIVES A SAVE. The mesh has no guid, so Save() skips it by design and the
            // CENTRELINE goes to a sidecar instead -- rebuilt on load rather than persisting the triangles.
            objs.DebugSaveTunnels();
            var removed = new List<Node3D>(tunnels);
            objs.RemovePlaced(removed);
            yield return Ticks(1);
            int after = 0;
            foreach (var _ in objs.PlacedOfNodes(EditorObjects.TunnelName)) after++;
            T.Check($"fixture: removed before reloading ({after})", after == 0);
            objs.DebugLoadTunnels();
            yield return Ticks(1);
            var back = new List<Node3D>();
            foreach (var t in objs.PlacedOfNodes(EditorObjects.TunnelName)) back.Add(t);
            T.Check($"the swept tunnel comes back from its centreline ({back.Count})", back.Count == 1);
            if (back.Count == 1)
            {
                var c2 = (Vector3[])back[0].GetMeta("tunnel_centre");
                T.Check($"...with the same station count, so the rebuild is the same tunnel "
                      + $"({c2.Length} vs {rings})", c2.Length == rings);
            }

            // ---- 5. TOO SHORT TO PORTAL is reported rather than half-done.
            var tiny = new List<Vector3> { new Vector3(0f, g0, 2000f), new Vector3(30f, g0, 2000f) };
            int shortRoad = field.AddRoadFromPolyline(tiny, 0, false, true);
            int pBefore = 0; foreach (var _ in objs.PlacedOfNodes(EditorTunnelSpline.PortalUnit)) pBefore++;
            EditorTunnelSpline.LayAlong(objs, terr, field, shortRoad, null);
            int pAfter = 0; foreach (var _ in objs.PlacedOfNodes(EditorTunnelSpline.PortalUnit)) pAfter++;
            T.Check($"a road under {EditorTunnelSpline.PortalLength * 2f:0.#} m gets no portals rather than "
                  + $"two overlapping ones ({pAfter - pBefore} added)", pAfter == pBefore);
            try { System.IO.File.Delete(objs.DebugTunnelPath); } catch { }
        }
    }
}
