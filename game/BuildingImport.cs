using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot
{
    // Port an existing retail building mesh into the editable wall/opening format.
    //
    // The hard half is not here: recovering "a wall with a door and two windows" from a pile of panels is
    // WallImport's job, and it does it by running the generator's own partition backwards. This file only has
    // to find the panels -- which planes of the mesh are walls, which face pairs are the two sides of one
    // wall, and where each wall sits in the world.
    // KNOWN LIMITATIONS, all of which show up as "the import is close but wrong somewhere":
    //   - only near-vertical planes are read, so an import has NO floors, roofs or foundations -- walls only
    //   - if face pairing fails, BOTH faces emit as separate default-thickness walls: a silent double wall
    //   - a recovered face under 2 m in either dimension is dropped, so a short jog in a facade vanishes
    //   - the plane bucket is plain rounding with no hysteresis, so a wall straddling a quantum splits in two
    //   - openings come from one face of the pair; the other face's holes are discarded
    // It is a porting aid to be cleaned up by hand, not a conversion to be trusted unattended.
    public static class BuildingImport
    {
        /// <summary>A recovered wall face: one plane of the mesh, flattened.</summary>
        sealed class Face
        {
            public Vector3 Normal;                 // outward, horizontal
            public float Dist;                     // plane offset along the normal
            public readonly List<WallSolid> Panels = new();
        }

        /// <summary>Import a retail building .obj as wall plans, in the prop's own local space (Y up, origin at
        /// the building's centre on the ground). Returns an empty list if the mesh will not load.</summary>
        public static List<WallPlan> FromObj(string globalObjPath, int materialId = 0)
        {
            var plans = new List<WallPlan>();
            var mesh = ObjMesh.Load(globalObjPath);
            if (mesh == null || mesh.GetSurfaceCount() == 0) return plans;

            var arr = mesh.SurfaceGetArrays(0);
            var v = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
            if (v == null || v.Length < 3) return plans;

            // Into the frame the prop is actually SEEN in. The mesh is authored lying down and stood up at
            // placement, so importing off the raw vertices recovers a building on its side.
            var upright = EditorObjects.Upright(0f);
            var pts = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) pts[i] = upright * v[i];

            // group the vertical triangles into planes
            var faces = new Dictionary<(int, int, int), Face>();
            for (int i = 0; i + 2 < pts.Length; i += 3)
            {
                Vector3 a = pts[i], b = pts[i + 1], c = pts[i + 2];
                var n = (b - a).Cross(c - a);
                if (n.LengthSquared() < 1e-9f) continue;
                n = n.Normalized();
                if (Mathf.Abs(n.Y) > 0.25f) continue;                 // not a wall: floor, roof or a slope

                float d = n.Dot(a);
                // Quantised so the two triangles of one quad, and the many quads of one wall, land in the same
                // bucket. 5 degrees and 5 cm: tight enough to keep two walls 0.5 m apart separate, loose
                // enough to survive a ripped mesh's noise.
                var key = (Mathf.RoundToInt(n.X * 20f), Mathf.RoundToInt(n.Z * 20f), Mathf.RoundToInt(d * 20f));
                if (!faces.TryGetValue(key, out var f))
                {
                    f = new Face { Normal = n, Dist = d };
                    faces[key] = f;
                }

                // Its 2D bounding box IS the quad it half-covers: these buildings are box-modelled, so every
                // triangle is half of an axis-aligned rectangle and the two halves give the same box. That is
                // what lets the panels be recovered without reassembling triangle pairs.
                var (u0, v0) = Project(f, a);
                float uMin = u0, uMax = u0, vMin = v0, vMax = v0;
                foreach (var p in new[] { b, c })
                {
                    var (pu, pv) = Project(f, p);
                    uMin = Mathf.Min(uMin, pu); uMax = Mathf.Max(uMax, pu);
                    vMin = Mathf.Min(vMin, pv); vMax = Mathf.Max(vMax, pv);
                }
                if (uMax - uMin > WallOpenings.Eps && vMax - vMin > WallOpenings.Eps)
                    f.Panels.Add(new WallSolid(uMin, vMin, uMax, vMax));
            }

            // One wall per CONNECTED CLUSTER of panels, not one per plane.
            //
            // Taking a whole plane's bounding box as one wall is what made the first version a mess: a plane
            // holding two separate wall segments came back as one enormous wall with the space between them
            // recovered as an "opening". That is where both the merged openings and the walls standing proud
            // of the building came from -- it described "everything on this side of the house, minus the
            // gaps" instead of "a wall with a window in it".
            var candidates = new List<Cand>();
            foreach (var f in faces.Values)
                foreach (var cluster in Cluster(f.Panels))
                {
                    var r = WallImport.FromPanels(cluster);
                    if (r.Width < 2f || r.Height < 1.5f) continue;       // clutter, not a wall
                    candidates.Add(new Cand { F = f, Rec = r });
                }

            // Pair each candidate with the one looking back at it: those are the two sides of one wall, and
            // the gap between them is its thickness. Emitting both faces would double every wall in the
            // building and leave each one paper-thin.
            var used = new HashSet<Cand>();
            foreach (var c in candidates)
            {
                if (used.Contains(c)) continue;
                Cand pair = null;
                float bestGap = float.MaxValue;
                foreach (var d in candidates)
                {
                    if (d == c || used.Contains(d)) continue;
                    if (c.F.Normal.Dot(d.F.Normal) > -0.94f) continue;   // must face back at it
                    float gap = Mathf.Abs(c.F.Dist + d.F.Dist);          // opposite normals: sum = -separation
                    if (gap > 1.6f || gap < 0.05f) continue;             // a wall, not the far side of the building
                    // The two faces of ONE wall cover the same run at the same height, so check both. Width
                    // alone pairs a wall with any similar-length wall elsewhere in the building.
                    if (Mathf.Abs(d.Rec.Width - c.Rec.Width) > 1.0f) continue;
                    if (Mathf.Abs(d.Rec.V0 - c.Rec.V0) > 0.6f) continue;
                    float overlap = Mathf.Min(c.Rec.U0 + c.Rec.Width, d.Rec.U0 + d.Rec.Width)
                                    - Mathf.Max(c.Rec.U0, d.Rec.U0);
                    if (overlap < c.Rec.Width * 0.5f) continue;          // not the same run along the wall
                    if (gap < bestGap) { bestGap = gap; pair = d; }
                }

                float thickness = pair != null ? bestGap : WallOpenings.DefaultThickness;
                used.Add(c);
                if (pair != null) used.Add(pair);

                var rec = c.Rec;
                var right = Right(c.F);
                var origin = c.F.Normal * (c.F.Dist - (pair != null ? thickness * 0.5f : 0f))
                             + right * rec.U0 + Vector3.Up * rec.V0;
                float yaw = Mathf.RadToDeg(Mathf.Atan2(right.X, right.Z)) - 90f;

                var plan = new WallPlan
                {
                    X = origin.X, Y = origin.Y, Z = origin.Z,
                    Yaw = yaw, Length = rec.Width, Height = rec.Height,
                    Thickness = Mathf.Clamp(thickness, 0.2f, 1.2f), Material = materialId,
                };
                foreach (var o in rec.Openings)
                {
                    var snapped = WallImport.SnapToRetail(o);
                    // Archetype is presentation, but MoveOpening branches on its FloorPinned flag -- so
                    // importing everything as archetype 0 (door, floor-pinned) meant grabbing an imported
                    // window to slide it slammed it down to sill 0. Pick by what the opening actually is.
                    snapped.Archetype = snapped.V <= WallOpenings.MinOpening ? 0 : 1;   // door : window
                    plan.Openings.Add(snapped);
                }
                plans.Add(plan);
            }
            return plans;
        }

        sealed class Cand { public Face F; public WallImport.Recovered Rec; }

        /// <summary>Split a plane's panels into groups that actually touch. Two panels belong to the same wall
        /// only if you can walk between them across panels -- the space between two separate segments of one
        /// plane is a gap in the BUILDING, not a window in a wall.</summary>
        static List<List<WallSolid>> Cluster(List<WallSolid> panels)
        {
            const float Touch = 0.06f;      // ripped panel edges do not meet exactly
            int n = panels.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            void Union(int i, int j) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; }

            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var p = panels[i];
                    var q = panels[j];
                    bool uOverlap = p.U0 <= q.U1 + Touch && q.U0 <= p.U1 + Touch;
                    bool vOverlap = p.V0 <= q.V1 + Touch && q.V0 <= p.V1 + Touch;
                    if (uOverlap && vOverlap) Union(i, j);
                }

            var groups = new Dictionary<int, List<WallSolid>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<WallSolid>();
                g.Add(panels[i]);
            }
            return new List<List<WallSolid>>(groups.Values);
        }

        static Vector3 Right(Face f) => new Vector3(-f.Normal.Z, 0f, f.Normal.X).Normalized();

        static (float U, float V) Project(Face f, Vector3 p)
        {
            var r = Right(f);
            return (p.Dot(r), p.Y);
        }
    }
}
