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
    //
    // Three things decide what a triangle IS, and it took getting each of them wrong to find that out:
    // its ORIENTATION (which way it faces, and the loader reverses winding so the raw cross product points
    // the wrong way), its COLOUR (retail buildings are painted from a 4x2 palette, and on House_00 60% of
    // the triangles are window trim that orientation alone cannot tell from wall), and its EXTENT (a plane
    // holds every coplanar thing in the building, so one plane is many walls).
    //
    // KNOWN LIMITATIONS, all of which show up as "the import is close but wrong somewhere":
    //   - floors and ceilings are still not read, so an import has walls, roofs and foundations only
    //   - a flat roof is only found on a building that ALSO has a slope to calibrate the roof colour from
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
            public Vector3 Normal;                 // outward
            public float Dist;                     // plane offset along the normal
            public Vector3 Right, Up;              // the plane's own 2D basis
            public bool Sloped;                    // roof rather than wall
            public int Texel;                      // the palette colour this whole plane is painted in
            public float MinY = float.MaxValue;    // world extent, for finding the eave
            public readonly List<WallImport.Tri2> Tris = new();
        }

        /// <summary>One triangle of the source mesh, already in the upright frame and already classified.</summary>
        struct SrcTri
        {
            public Vector3 A, B, C, N;
            public float Area;
            public int Texel;
            public bool Vertical, Up;
        }

        /// <summary>Import a retail building .obj as wall plans, in the prop's own local space (Y up, origin at
        /// the building's centre on the ground). Returns an empty list if the mesh will not load.</summary>
        public static List<WallPlan> FromObj(string globalObjPath, int materialId = 0, bool readRoofs = true)
        {
            var plans = new List<WallPlan>();
            var mesh = ObjMesh.Load(globalObjPath);
            if (mesh == null || mesh.GetSurfaceCount() == 0) return plans;

            var arr = mesh.SurfaceGetArrays(0);
            var v = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
            var uv = (Vector2[])arr[(int)Mesh.ArrayType.TexUV];
            if (v == null || v.Length < 3) return plans;

            // Into the frame the prop is actually SEEN in. The mesh is authored lying down and stood up at
            // placement, so importing off the raw vertices recovers a building on its side.
            var upright = EditorObjects.Upright(0f);
            var pts = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) pts[i] = upright * v[i];

            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = -lo;
            foreach (var pw in pts) { lo = lo.Min(pw); hi = hi.Max(pw); }
            var centre = new Vector3((lo.X + hi.X) * 0.5f, 0f, (lo.Z + hi.Z) * 0.5f);

            var src = ReadTriangles(pts, uv);
            var roofTexels = RoofTexels(src);
            int roofTexel = -1;                        // paint the roof in its own colour, not the wall's
            foreach (var t in roofTexels) { roofTexel = t; break; }
            int revealTexel = WallMaterials.At(materialId).RevealTexel;

            // group into planes, gating on PALETTE as well as orientation
            var faces = new Dictionary<(int, int, int), Face>();
            foreach (var t in src)
            {
                // Roof-coloured VERTICAL planes are kept now, not skipped as fascia. They are the 0.50 m
                // band of roof-slab edge that runs round the top of every retail wall, and dropping it was
                // the difference strawberry_cow called out: the port had cream where the original has a grey
                // stripe under the eaves. It reads as a missing overhang even though nothing overhangs --
                // measured, the roof's outer edge and the wall face are both at 8.25.
                bool wantWall = t.Vertical && t.Texel != revealTexel;
                bool wantRoof = readRoofs && !t.Vertical && t.Up && roofTexels.Contains(t.Texel);
                if (!wantWall && !wantRoof) continue;
                bool sloped = wantRoof;

                var n = t.N;
                float d = n.Dot(t.A);
                // Quantised so the two triangles of one quad, and the many quads of one wall, land in the same
                // bucket. 5 degrees and 5 cm: tight enough to keep two walls 0.5 m apart separate, loose
                // enough to survive a ripped mesh's noise.
                // COLOUR is part of the key. A wall and the grey band above it are the same plane facing the
                // same way, so bucketing on geometry alone merges them into one surface -- and a surface has
                // one colour, so one of the two has to come out wrong. The whole file is this lesson.
                var key = (Mathf.RoundToInt(n.X * 20f) * 131 + Mathf.RoundToInt(n.Y * 20f),
                           Mathf.RoundToInt(n.Z * 20f), Mathf.RoundToInt(d * 20f) * 17 + t.Texel);
                if (!faces.TryGetValue(key, out var f))
                {
                    // The plane's own 2D frame. For a wall that is (horizontal along the run, world up); for a
                    // slope it is (horizontal along the eave, up the slope) -- the same basis the surface will
                    // be reconstructed in, so the recovered rectangle needs no second transform.
                    var right = new Vector3(-n.Z, 0f, n.X);
                    if (right.LengthSquared() < 1e-8f) right = Vector3.Right;
                    right = right.Normalized();
                    var up = sloped ? n.Cross(right).Normalized() : Vector3.Up;
                    if (sloped && up.Y < 0f) up = -up;
                    f = new Face { Normal = n, Dist = d, Right = right, Up = up, Sloped = sloped, Texel = t.Texel };
                    faces[key] = f;
                }
                f.MinY = Mathf.Min(f.MinY, Mathf.Min(t.A.Y, Mathf.Min(t.B.Y, t.C.Y)));

                // The TRIANGLE, not its bounding box. Treating each triangle's bbox as a solid panel assumed
                // every one was half of an axis-aligned rectangle; measured on House_00 that holds for 49% of
                // the vertical-plane triangles, so the other half fabricated solid that is not in the mesh and
                // the leftovers came back as windows in the wrong places.
                var (au, av) = Project(f, t.A);
                var (bu, bv) = Project(f, t.B);
                var (cu, cv) = Project(f, t.C);
                f.Tris.Add(new WallImport.Tri2(au, av, bu, bv, cu, cv));
            }

            // One wall per CONNECTED REGION of covered area, not one per plane.
            //
            // Taking a whole plane's bounding box as one wall is what made the first version a mess: a plane
            // holding two separate wall segments came back as one enormous wall with the space between them
            // recovered as an "opening". That is where both the merged openings and the walls standing proud
            // of the building came from -- it described "everything on this side of the house, minus the
            // gaps" instead of "a wall with a window in it".
            var candidates = new List<Cand>();
            var roofs = new List<(Face F, WallImport.Recovered R)>();
            float eave = float.MaxValue;
            foreach (var f in faces.Values)
            {
                if (f.Tris.Count == 0) continue;
                if (f.Sloped)
                {
                    var rr = WallImport.FromTriangles(f.Tris);
                    if (rr.Width < 2f || rr.Height < 1f) continue;
                    roofs.Add((f, rr));
                    eave = Mathf.Min(eave, f.MinY);
                    continue;
                }
                // A roof-coloured band is 0.50 m tall by construction, so it cannot share the wall's minimum
                // or it is filtered out as clutter -- which is exactly how it went missing.
                float minH = roofTexels.Contains(f.Texel) ? 0.35f : 1.5f;
                foreach (var r in WallImport.FromTrianglesSplit(f.Tris))
                {
                    if (r.Width < 2f || r.Height < minH) continue;       // clutter, not a wall
                    candidates.Add(new Cand { F = f, Rec = r });
                }
            }

            // Pair each candidate with the one looking back at it: those are the two sides of one wall, and
            // the gap between them is its thickness. Emitting both faces would double every wall in the
            // building and leave each one paper-thin.
            var bands = new List<WallPlan>();     // the strip between the wall and the roof
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
                    // The two faces of one wall start at the same height and cover the same run -- but NOT
                    // necessarily the same LENGTH. Requiring their widths to match within a metre was wrong:
                    // an exterior facade is one clean 16 m face outside and several shorter ones inside,
                    // chopped up where cross-walls land on it. Every one of those failed to pair, so both
                    // sides emitted as separate walls. That was always happening; it only became visible when
                    // surfaces started wearing their real colour and the interior's tan surfaced through the
                    // cream. Overlap against the SHORTER of the two is the test that actually means "the same
                    // run of wall".
                    if (Mathf.Abs(d.Rec.V0 - c.Rec.V0) > 0.6f) continue;
                    float overlap = Mathf.Min(c.Rec.U0 + c.Rec.Width, d.Rec.U0 + d.Rec.Width)
                                    - Mathf.Max(c.Rec.U0, d.Rec.U0);
                    if (overlap < Mathf.Min(c.Rec.Width, d.Rec.Width) * 0.5f) continue;
                    if (gap < bestGap) { bestGap = gap; pair = d; }
                }

                // The per-model measured thickness, not the global 0.70. House_00's walls are 0.533, so
                // every wall that failed to pair was built 0.17 too thick AND -- because the surface sits on
                // its centreline, half a thickness in from the facade plane -- parked 0.08 too far inside the
                // building. Two walls each off by that much do not line up where they meet, which is
                // "the wall corners are off" with no corner solver involved.
                float thickness = pair != null ? bestGap : WallMaterials.At(materialId).Thickness;
                used.Add(c);
                if (pair != null) used.Add(pair);

                // Take the colour off the OUTWARD face of the pair. Both sides of a wall are in the list and
                // the loop reaches whichever comes first, so half the exterior walls came back painted in the
                // interior's tan. Outward is decided by geometry, not by iteration order: a face is outward
                // when its own plane sits on the far side of the building's centre from it.
                var skin = c.F;
                if (pair != null && (pair.F.Dist - pair.F.Normal.Dot(centre)) > (c.F.Dist - c.F.Normal.Dot(centre)))
                    skin = pair.F;

                    int before = plans.Count;
                    EmitWall(plans, c, thickness, materialId, eave,
                             roofTexels.Contains(c.F.Texel) ? 0.35f : 1.5f,
// Everything vertical takes the palette's WALL colour, including the eave band.
                             // The band reads as texel 1 in the mesh -- the same dark grey as the roof -- and
                             // I emitted it that way on the measurement. strawberry_cow, who has the game in
                             // front of him: "the roof parts themselves are the right colors but the eaves
                             // are supposed to be wall color." The texel is what the model stores; it is not
                             // what the eave is meant to look like, and only the sloped planes are roof.
                             //
                             // Painting each wall its own measured texel was tried and reverted for a
                             // separate reason: it turned exteriors tan, because a facade's inner face emits
                             // as its own wall whenever pairing misses and the interior colour then wins the
                             // depth fight.
                             -1);
                    if (roofTexels.Contains(c.F.Texel))
                        for (int k = before; k < plans.Count; k++) bands.Add(plans[k]);
            }
            // Roof planes, emitted as pitched surfaces. Each sloped plane becomes one Roof surface in its own
            // frame, so a HIPPED roof comes back as its four slopes rather than being forced into the gable
            // the roof tool builds -- retail hips are common and a gable is not a substitute.
            foreach (var (f, r) in roofs)
            {
                float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(f.Up.Y, -1f, 1f)));
                float yaw = Mathf.RadToDeg(Mathf.Atan2(f.Right.X, f.Right.Z)) - 90f;
                var origin = f.Normal * f.Dist + f.Right * r.U0 + f.Up * r.V0;
                var plan = new WallPlan
                {
                    X = origin.X, Y = origin.Y, Z = origin.Z,
                    // 90 - pitch, NOT pitch - 90, and the difference is a mirror rather than an offset.
                    // Both put the surface at the same angle to the horizontal, so the plane looks right from
                    // any single camera; they differ in WHICH WAY it leans, so pitch - 90 gave four roof
                    // planes that each rose toward their own eave and met nothing. The tool's own AddGableRoof
                    // uses pitch - 90 correctly, because it picks the yaw to suit -- it spawns the south slope
                    // facing north. Here the yaw is dictated by the plane we read, so the sign has to absorb
                    // it. Checked by rebuilding the surface and testing its plane against the source mesh's,
                    // which is the only thing that can see a mirror.
                    Yaw = yaw, Pitch = 90f - pitch, Kind = SurfaceKind.Roof,
                    Length = r.Width, Height = r.Height, Texel = roofTexel,
                    InsetL0 = r.InsetL0, InsetL1 = r.InsetL1, InsetR0 = r.InsetR0, InsetR1 = r.InsetR1,
                    Thickness = EditorBuildings.SlabThickness, Material = materialId,
                };
                foreach (var o in r.Openings) plan.Openings.Add(o);   // chimney/skylight holes, unsnapped
                plans.Add(plan);
            }
            ClipBandsToRoofs(plans, bands);
            return plans;
        }

        /// <summary>Flatten the mesh to classified triangles.
        ///
        /// The normal is the cross product NEGATED, and that one sign is worth a paragraph. ObjMesh reverses
        /// every triangle's winding on load (Unity and Godot disagree on front-face order), so the naive
        /// cross product of the loaded vertices points INTO the solid. Checked rather than reasoned about:
        /// computing it both ways on House_00 and comparing against the file's own vn records gives 0 of 732
        /// agreeing and 732 of 732 anti-parallel. Every wall then sat a full thickness outside the facade,
        /// because the origin is offset along this normal, and the roof pass selected ceilings.</summary>
        static List<SrcTri> ReadTriangles(Vector3[] pts, Vector2[] uv)
        {
            var outp = new List<SrcTri>();
            for (int i = 0; i + 2 < pts.Length; i += 3)
            {
                Vector3 a = pts[i], b = pts[i + 1], c = pts[i + 2];
                var n = (b - a).Cross(c - a);
                float len = n.Length();
                if (len < 1e-6f) continue;
                n = -n / len;                                   // see above: the loader reversed the winding
                outp.Add(new SrcTri
                {
                    A = a, B = b, C = c, N = n, Area = len * 0.5f,
                    Texel = TexelOf(uv, i),
                    Vertical = Mathf.Abs(n.Y) <= 0.25f,
                    Up = n.Y > 0.25f,
                });
            }
            return outp;
        }

        /// <summary>Which palette texels the ROOF is painted in, measured off this model rather than assumed.
        ///
        /// Orientation alone cannot tell a roof from a ceiling -- both are horizontal and face up, which is
        /// how the first roof attempt swept 174 of House_00's floor and ceiling triangles in as building-sized
        /// slabs. Colour can: retail paints roofs in their own texel. So calibrate on the unambiguous case,
        /// the planes that are sloped AND upward (nothing but a roof is), and take the texels holding real
        /// area there. On House_00 that is exactly texel 1, covering all 20 sloped-up triangles and none of
        /// anything else -- and it also identifies the 34 vertical dark-grey triangles as fascia boards.
        ///
        /// A building with no sloped plane at all calibrates to nothing, so its flat roof is not recovered;
        /// that is the honest failure, not a guess.</summary>
        static HashSet<int> RoofTexels(List<SrcTri> src)
        {
            var area = new Dictionary<int, float>();
            float total = 0f;
            foreach (var t in src)
            {
                if (t.Vertical || !t.Up || t.N.Y > 0.98f) continue;    // sloped and upward: only a roof is
                area.TryGetValue(t.Texel, out float had);
                area[t.Texel] = had + t.Area;
                total += t.Area;
            }
            var set = new HashSet<int>();
            if (total <= 0f) return set;
            foreach (var kv in area)
                if (kv.Value >= total * 0.1f) set.Add(kv.Key);
            return set;
        }

        /// <summary>The palette texel a triangle is painted in. Building textures are 4x2 -- eight flat
        /// colours -- so the UV picks a cell, not a pixel. ObjMesh already flipped V on load, so this reads
        /// in Godot texture space (v=0 is the top row); getting that backwards lands one row low and reports
        /// the walls as trim.</summary>
        static int TexelOf(Vector2[] uv, int i)
        {
            if (uv == null || i + 2 >= uv.Length) return -1;
            var t = (uv[i] + uv[i + 1] + uv[i + 2]) / 3f;          // the centroid, so a seam does not decide it
            int col = Mathf.Clamp(Mathf.FloorToInt(t.X * 4f), 0, 3);
            int row = Mathf.Clamp(Mathf.FloorToInt(t.Y * 2f), 0, 1);
            return row * 4 + col;
        }

        /// <summary>Emit one recovered face as a wall, splitting off whatever falls outside [ground, eave].
        ///
        /// A retail facade is modelled as one continuous sheet from the bottom of the foundation to the ridge.
        /// Imported whole it is a single 9 m wall that starts underground and pokes through the roof. The two
        /// ends are different things: below ground is the foundation skirt, above the eave is the gable
        /// triangle. Only a gable END reaches above the eave, so the clip selects them on its own.</summary>
        static void EmitWall(List<WallPlan> plans, Cand c, float thickness, int materialId, float eave,
                             float minHeight = 1.5f, int texel = -1)
        {
            var rec = c.Rec;
            var right = Right(c.F);
            float yaw = Mathf.RadToDeg(Mathf.Atan2(right.X, right.Z)) - 90f;
            float inset = thickness * 0.5f;
            float baseY = rec.V0, top = rec.V0 + rec.Height;

            float below = Mathf.Max(0f, -baseY);
            if (below > 0.25f)
            {
                var fo = c.F.Normal * (c.F.Dist - inset) + right * rec.U0 + Vector3.Up * baseY;
                plans.Add(new WallPlan
                {
                    X = fo.X, Y = fo.Y, Z = fo.Z, Yaw = yaw, Kind = SurfaceKind.Foundation,
                    Length = rec.Width, Height = below,
                    Thickness = Mathf.Clamp(thickness, 0.2f, 1.2f), Material = materialId,
                });
                baseY = 0f;
            }

            float rise = 0f;
            if (eave < float.MaxValue && top > eave + 0.25f && baseY < eave - 0.25f)
            {
                rise = top - eave;                 // the gable triangle, rebuilt by the surface as a peak
                top = eave;
            }
            float height = top - baseY;
            // The SAME floor the candidate passed, not a second hard-coded one. A 0.50 m band of roof-slab
            // edge cleared the 0.35 m filter at selection and was then silently dropped here by a 1.5 that
            // only ever meant "a wall this short is clutter".
            if (height < minHeight) return;

            var origin = c.F.Normal * (c.F.Dist - inset) + right * rec.U0 + Vector3.Up * baseY;
            var plan = new WallPlan
            {
                X = origin.X, Y = origin.Y, Z = origin.Z,
                Yaw = yaw, Length = rec.Width, Height = height, GableRise = rise,
                Thickness = Mathf.Clamp(thickness, 0.2f, 1.2f), Material = materialId,
                // The measured colour of THIS plane, not the palette's idea of a wall. A retail building is
                // one palette and several colours: cream outside, tan inside, grey for the band under the
                // eaves. Painting all of it the wall colour is why the port read as flat.
                Texel = texel,
                // Walls get the trapezoid too, not just roofs -- a gable end is a triangle, and it is the
                // same primitive with both top insets meeting.
                InsetL0 = rec.InsetL0, InsetL1 = rec.InsetL1, InsetR0 = rec.InsetR0, InsetR1 = rec.InsetR1,
            };
            float shift = baseY - rec.V0;
            foreach (var o in rec.Openings)
            {
                var s = WallImport.SnapToRetail(o);
                s.V -= shift;                                        // openings are wall-relative; the base moved
                if (s.V + s.Height <= WallOpenings.MinOpening) continue;
                if (s.V >= height - WallOpenings.MinOpening) continue;
                if (s.V < 0f) { s.Height += s.V; s.V = 0f; }
                if (s.V + s.Height > height) s.Height = height - s.V;
                if (s.Width < WallOpenings.MinOpening || s.Height < WallOpenings.MinOpening) continue;
                // Archetype is presentation, but MoveOpening branches on its FloorPinned flag -- so importing
                // everything as archetype 0 (door, floor-pinned) meant grabbing an imported window to slide it
                // slammed it down to sill 0. Pick by what the opening actually is.
                s.Archetype = s.V <= WallOpenings.MinOpening ? 0 : 1;   // door : window
                plan.Openings.Add(s);
            }
            plans.Add(plan);
        }

        /// <summary>Trim the wall-to-roof strip so it runs only as far as the roof above it does.
        ///
        /// strawberry_cow: "scale those sections to match the roof width". The strip is recovered from its own
        /// plane, so it inherits the length of the WALL -- on House_00 that is a 16.5 m band across a side
        /// where the roof above it is 11.1 m, and the surplus carries on past the roof into open air. Where a
        /// band has no roof above it at all it is dropped: that is a gable end, and a gable end has no strip.
        ///
        /// Matching is by the roof's EAVE line -- its bottom edge in world space -- because that is the edge
        /// the strip sits under. A band keeps only the part of its run that lies beneath some eave.</summary>
        static void ClipBandsToRoofs(List<WallPlan> plans, List<WallPlan> bands)
        {
            if (bands.Count == 0) return;
            var eaves = new List<(Vector3 A, Vector3 B)>();
            foreach (var p in plans)
            {
                if (p.Kind != SurfaceKind.Roof) continue;
                var dir = new Vector3(Mathf.Cos(Mathf.DegToRad(p.Yaw)), 0f, -Mathf.Sin(Mathf.DegToRad(p.Yaw)));
                var a = new Vector3(p.X, p.Y, p.Z);
                eaves.Add((a, a + dir * p.Length));
            }
            foreach (var b in bands)
            {
                var dir = new Vector3(Mathf.Cos(Mathf.DegToRad(b.Yaw)), 0f, -Mathf.Sin(Mathf.DegToRad(b.Yaw)));
                var o = new Vector3(b.X, b.Y, b.Z);
                float keep0 = float.MaxValue, keep1 = float.MinValue;
                foreach (var (ea, eb) in eaves)
                {
                    var ed = eb - ea;
                    if (ed.LengthSquared() < 1e-6f) continue;
                    // the strip runs under an eave only if they are PARALLEL in plan and close by
                    var edn = new Vector3(ed.X, 0f, ed.Z).Normalized();
                    if (Mathf.Abs(edn.Dot(dir)) < 0.94f) continue;
                    // both ends of the eave, measured along the band's own run
                    float t0 = (ea - o).Dot(dir), t1 = (eb - o).Dot(dir);
                    if (t0 > t1) (t0, t1) = (t1, t0);
                    // and it has to be the eave over THIS wall, not the one on the far side
                    var mid = (ea + eb) * 0.5f;
                    var toMid = mid - (o + dir * Mathf.Clamp((mid - o).Dot(dir), 0f, b.Length));
                    if (new Vector2(toMid.X, toMid.Z).Length() > 1.5f) continue;
                    keep0 = Mathf.Min(keep0, Mathf.Max(0f, t0));
                    keep1 = Mathf.Max(keep1, Mathf.Min(b.Length, t1));
                }
                if (keep1 - keep0 < 0.5f) { b.Length = 0f; continue; }   // no roof over it: a gable end
                var start = o + dir * keep0;
                b.X = start.X; b.Y = start.Y; b.Z = start.Z;
                b.Length = keep1 - keep0;
            }
            plans.RemoveAll(p => p.Length <= 0.01f);
        }

        sealed class Cand { public Face F; public WallImport.Recovered Rec; }

        static Vector3 Right(Face f) => new Vector3(-f.Normal.Z, 0f, f.Normal.X).Normalized();

        static (float U, float V) Project(Face f, Vector3 p) => (p.Dot(f.Right), p.Dot(f.Up));
    }
}
