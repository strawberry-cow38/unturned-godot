using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // Unturned's road SPLINE network (Environment/Paths.dat = bezier roads, Roads.dat = per-material configs),
    // SEPARATE from the placed road props. Each road = a cubic-bezier spline through joints (vertex + 2
    // tangents); we extrude the source's tapered road strip along it (Road.buildMesh: 4-vert trapezoid
    // cross-section per sample, terrain-following). Src: getPosition = BezierTool(P0=v, P1=v+tan1, P2=e+tan0,
    // P3=e.v); verts = pos ± side*(halfWidth[+depth]) ± normal*halfDepth + normal*offset.
    public partial class RoadField : Node3D
    {
        public Terrain Terr;

        struct RoadMat { public float Width, Height, Depth, Offset; public bool Concrete; }
        const float WidthScale = 1.15f;   // master 2026-08-24: roads slightly thicker (fills the bald patch next to Fernwood Farm); the collider shares this width
        class Joint   // class so the editor can move a vertex/tangent in place
        {
            public Vector3 Vertex, Tan0, Tan1; public float Offset; public bool IgnoreTerrain; public byte Mode;
            public void SetTangent(int i, Vector3 t)   // source RoadJoint.setTangent: MIRROR mirrors, ALIGNED matches length, FREE independent
            {
                if (i == 0) Tan0 = t; else Tan1 = t;
                if (Mode == 0) { if (i == 0) Tan1 = -t; else Tan0 = -t; }                                                  // MIRROR
                else if (Mode == 1) { float m = (i == 0 ? Tan1 : Tan0).Length(); var a = -t.Normalized() * m; if (i == 0) Tan1 = a; else Tan0 = a; }   // ALIGNED
            }
        }
        class RoadData { public int Material; public bool IsLoop; public List<Joint> Joints = new(); public byte[] GuidBytes; public MeshInstance3D Mi; public StaticBody3D Body;
            public int StartJunction = -1, EndJunction = -1; }   // junction NODE this road's first/last joint is bound to (-1 = free end)

        // JUNCTION NODES (strawberry 2026-08-19: "we should invent a junction node. the existing maps are
        // considered 'legacy' and simply use the old tool"). A junction OWNS its position; road ends reference
        // it. That is the whole point of making it a node rather than deriving it from coincident endpoints:
        // drag the junction and every rail bound to it follows, which cannot go out of sync. With the derived
        // version you had to drag N ends to the same spot and hope they still matched to the millimetre.
        //
        // Stored in a SIDECAR file, never in Paths.dat -- that stays exactly retail-shaped, so legacy maps and
        // the game's existing road system are untouched. A map with no sidecar simply has no junctions.
        class Junction { public Vector3 Pos; }
        readonly List<Junction> _junctions = new();

        // editor state: the parsed roads + materials kept live so a joint move can rebuild one road + save Paths.dat back
        readonly List<RoadData> _roads = new();
        List<RoadMat> _mats = new();
        byte _pathsVersion = 6;
        public int RoadCount => _roads.Count;
        public int JointCount(int road) => road >= 0 && road < _roads.Count ? _roads[road].Joints.Count : 0;
        public Vector3 JointPos(int road, int joint) => _roads[road].Joints[joint].Vertex;
        public void SetJointPos(int road, int joint, Vector3 p) { _roads[road].Joints[joint].Vertex = p; RebuildRoad(road); }

        // Unity (x,y,z) -> Godot (x,y,-z), the port's negate-Z layout (matches props/terrain).
        static Vector3 G(float x, float y, float z) => new Vector3(x, y, -z);

        // road_N.png heights (Roads.unity3d container order: Highway_0/1, Racetrack, Road, Tracks, Trail, White/Yellow) for UV repeat.
        static readonly float[] TexHeight = { 128, 128, 256, 2, 256, 64, 256, 256, 256, 256 };

        static Shader _wetShader;
        // Roads wear the wet-surface shader (strawberry 2026-09-04 "im not seeing the ripple impacts on the road"): the same
        // material the storm demo's ground uses, fed the road's own texture -- soaks dark + glossy and shows the raindrop
        // impact rings; dry weather looks exactly like the old StandardMaterial (both globals sit at 0 and the block skips).
        Material RoadMaterial3D(int index, bool concrete)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/roads/road_{index}.png");
            Image img = null;
            if (System.IO.File.Exists(p)) { img = new Image(); if (!ContentProvider.LoadOk(img, p)) img = null; }
            if (!concrete)   // dirt / gravel TRAILS stay plain: no wet sheen, no raindrop rings (strawberry: "just the solid concrete roads")
            {
                if (img != null)
                    return new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img), TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                return new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.37f, 0.28f), Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            }
            // CONCRETE (asphalt) roads: the wet-surface shader -- rain rings + sheen, roofed spans stay dry via the roof map.
            RainSystem3D.EnsureGlobals();   // the shader links the rain globals -- they must exist first (the GrassDisplacers lesson)
            _wetShader ??= GD.Load<Shader>("res://content/wet_surface.gdshader");
            var m = new ShaderMaterial { Shader = _wetShader };
            m.SetShaderParameter("dry_roughness", 1.0f);
            m.SetShaderParameter("impact_amount", 1.0f);
            m.SetShaderParameter("splash_scale", 1.0f);
            if (img != null) { m.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(img)); m.SetShaderParameter("use_tex", true); return m; }
            m.SetShaderParameter("dry_albedo", new Vector3(0.34f, 0.34f, 0.35f));
            return m;
        }

        // Terrain normal from the height gradient (smoothed over e units) so the road banks WITH the slope
        // instead of staying flat -> even edges on cross-slopes (src uses LevelGround.getNormal).
        Vector3 SampleNormal(float x, float z)
        {
            const float e = 4f;
            float hL = Terr.SampleHeight(x - e, z), hR = Terr.SampleHeight(x + e, z);
            float hD = Terr.SampleHeight(x, z - e), hU = Terr.SampleHeight(x, z + e);
            return new Vector3(hL - hR, 2f * e, hD - hU).Normalized();
        }

        public void LoadFromEnvironment(string envDir)
        {
            _mats = ParseRoadsDat(Path.Combine(envDir, "Roads.dat"));
            var roads = ParsePathsDat(Path.Combine(envDir, "Paths.dat"));
            _roads.Clear();
            int built = 0;
            foreach (var r in roads)
            {
                _roads.Add(r);   // keep EVERY road (even degenerate) so editor indices match + SavePaths round-trips all
                if (r.Joints.Count < 2 || r.Material < 0 || r.Material >= _mats.Count) continue;
                BuildRoadNode(r);
                built++;
            }
            GD.Print($"[roads] built {built} spline roads ({roads.Count} in Paths.dat, {_mats.Count} materials)");
        }

        // NEW MAP: load only the road MATERIALS (from a shared Roads.dat) so roads can be ADDED, with no roads to start.
        public void LoadMaterialsOnly(string envDir)
        {
            _mats = ParseRoadsDat(Path.Combine(envDir, "Roads.dat"));
            _roads.Clear();
            GD.Print($"[roads] new-map materials loaded ({_mats.Count})");
        }

        // build (or rebuild) the MeshInstance + collider for one road, stashing them on the RoadData (flat top-ribbon collider)
        void BuildRoadNode(RoadData r)
        {
            float texH = r.Material < TexHeight.Length ? TexHeight[r.Material] : 256f;
            var mesh = BuildRoadMesh(r, _mats[r.Material], texH, out var collShape);
            if (mesh == null) return;
            if (r.Mi == null) { r.Mi = new MeshInstance3D(); AddChild(r.Mi); }
            r.Mi.Mesh = mesh;
            r.Mi.VisibilityRangeEnd = LodTable.ImposterMaxDistance;   // master: roads render out to the landmark distance (~1400m), not uncapped
            r.Mi.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
            r.Mi.MaterialOverride = RoadMaterial3D(r.Material, _mats[r.Material].Concrete);
            if (collShape != null)
            {
                if (r.Body == null) { r.Body = new StaticBody3D(); r.Body.AddChild(new CollisionShape3D()); AddChild(r.Body); }
                foreach (var c in r.Body.GetChildren()) if (c is CollisionShape3D cs) cs.Shape = collShape;
            }
        }

        // editor: re-extrude one road's spline after a joint moved
        public void RebuildRoad(int i)
        {
            if (i >= 0 && i < _roads.Count && _roads[i].Joints.Count >= 2 && _roads[i].Material >= 0 && _roads[i].Material < _mats.Count)
                BuildRoadNode(_roads[i]);
        }

        // source EditorRoads.primary: with a road selected, LMB on ground adds a vertex; before/after chosen by which
        // tangent the new point projects onto (Vector3.Dot). Returns the inserted joint index (or -1).
        public int AddVertexNearSelected(int road, int selJoint, Vector3 point)
        {
            if (road < 0 || road >= _roads.Count) return -1;
            var r = _roads[road];
            int insertIndex;
            if (selJoint < 0 || selJoint >= r.Joints.Count) insertIndex = r.Joints.Count;
            else
            {
                var jt = r.Joints[selJoint];
                insertIndex = (point - jt.Vertex).Dot(jt.Tan0) > (point - jt.Vertex).Dot(jt.Tan1) ? selJoint : selJoint + 1;
            }
            AddVertex(r, insertIndex, point);
            RebuildRoad(road);
            return insertIndex;
        }

        void AddVertex(RoadData r, int vertexIndex, Vector3 point)   // source Road.addVertex: default tangents (2.5f, pointing at neighbours)
        {
            var joint = new Joint { Vertex = point };
            if (r.Joints.Count == 1)   // the 2nd joint: both tangents point at each other
            {
                r.Joints[0].SetTangent(1, (point - r.Joints[0].Vertex).Normalized() * 2.5f);
                joint.SetTangent(0, (r.Joints[0].Vertex - point).Normalized() * 2.5f);
            }
            else if (r.Joints.Count > 1)
            {
                if (vertexIndex == 0)
                    joint.SetTangent(1, (r.IsLoop ? r.Joints[0].Vertex - r.Joints[^1].Vertex : r.Joints[0].Vertex - point).Normalized() * 2.5f);
                else if (vertexIndex == r.Joints.Count)
                {
                    if (r.IsLoop) joint.SetTangent(1, (r.Joints[0].Vertex - r.Joints[^1].Vertex).Normalized() * 2.5f);
                    else joint.SetTangent(0, (r.Joints[^1].Vertex - point).Normalized() * 2.5f);
                }
                else joint.SetTangent(1, (r.Joints[vertexIndex].Vertex - r.Joints[vertexIndex - 1].Vertex).Normalized() * 2.5f);
            }
            r.Joints.Insert(vertexIndex, joint);
        }

        // source removeVertex: fewer than 2 joints left -> remove the whole road. Returns true if the road itself was removed.
        public bool RemoveVertex(int road, int joint)
        {
            if (road < 0 || road >= _roads.Count) return false;
            var r = _roads[road];
            if (r.Joints.Count < 2 || joint < 0 || joint >= r.Joints.Count) { RemoveRoad(road); return true; }
            r.Joints.RemoveAt(joint);
            if (r.Joints.Count < 2) { RemoveRoad(road); return true; }
            RebuildRoad(road);
            return false;
        }

        // source LevelRoads.addRoad: new road, material 0, ONE joint (renders once a 2nd vertex is added). Returns the road index.
        public int AddRoad(Vector3 point)
        {
            var r = new RoadData { Material = 0, GuidBytes = System.Array.Empty<byte>() };
            r.Joints.Add(new Joint { Vertex = point });
            _roads.Add(r);
            return _roads.Count - 1;
        }

        // ---- DRAW-TOOL SUPPORT (strawberry 2026-08-19: a draw-a-road/rail tool with branches + junctions,
        // with the node tool kept as legacy). Everything below is additive: Paths.dat is retail's format and
        // has no field for a branch, so junctions are NOT stored -- they are a GEOMETRIC fact, two road ends
        // at the same point. Keeping it that way means the saved file stays exactly retail-shaped, the game's
        // road system is untouched, and the train's spline query keeps working with no changes at all. The
        // editor's job is therefore to make coincidence EXACT rather than approximate, which is what the
        // snapping in EditorRoadDraw is for -- a junction you can see but that is 3 cm apart is not one.

        /// <summary>Build a whole road from a drawn polyline, with Catmull-Rom tangents so the result is
        /// smooth without the user placing a single handle. Joints are MIRROR mode (0) so the two tangents
        /// stay opposite and the curve is C1 -- which is what makes a drawn rail look drawn rather than
        /// hand-jointed. Returns the new road index, or -1 if there are too few points to be a road.</summary>
        public int AddRoadFromPolyline(System.Collections.Generic.IReadOnlyList<Vector3> pts, int material = 0, bool loop = false)
        {
            if (pts == null || pts.Count < 2) return -1;
            var r = new RoadData { Material = Mathf.Clamp(material, 0, Mathf.Max(0, _mats.Count - 1)), IsLoop = loop, GuidBytes = System.Array.Empty<byte>() };
            for (int i = 0; i < pts.Count; i++) r.Joints.Add(new Joint { Vertex = pts[i], Mode = 0 });
            RetangentRoad(r);
            _roads.Add(r);
            int idx = _roads.Count - 1;
            if (r.Joints.Count >= 2 && r.Material >= 0 && r.Material < _mats.Count) BuildRoadNode(r);
            return idx;
        }

        /// <summary>Catmull-Rom tangents across a whole road. The 1/6 is the standard Catmull-Rom to cubic-
        /// Bezier conversion (the control point sits a third of the way along the neighbour chord, and the
        /// chord here spans TWO segments), so a straight run of evenly spaced points comes out actually
        /// straight instead of subtly wavy.</summary>
        void RetangentRoad(RoadData r)
        {
            int n = r.Joints.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 prev = i > 0 ? r.Joints[i - 1].Vertex : (r.IsLoop ? r.Joints[n - 1].Vertex : r.Joints[i].Vertex);
                Vector3 next = i < n - 1 ? r.Joints[i + 1].Vertex : (r.IsLoop ? r.Joints[0].Vertex : r.Joints[i].Vertex);
                var t = (next - prev) / 6f;
                if (t.LengthSquared() < 1e-8f) t = new Vector3(0f, 0f, 2.5f);   // degenerate (duplicate points): keep a sane handle
                r.Joints[i].Mode = 0;          // MIRROR, so SetTangent keeps Tan0 = -Tan1
                r.Joints[i].SetTangent(1, t);
            }
        }

        /// <summary>Nearest joint on any road to a world point, for snap-to-junction. Returns false if none is
        /// within maxDist. <paramref name="skipRoad"/> excludes the road currently being drawn.</summary>
        public bool NearestJoint(Vector3 p, float maxDist, out int road, out int joint, int skipRoad = -1)
        {
            road = -1; joint = -1;
            float best = maxDist * maxDist;
            for (int ri = 0; ri < _roads.Count; ri++)
            {
                if (ri == skipRoad) continue;
                var js = _roads[ri].Joints;
                for (int ji = 0; ji < js.Count; ji++)
                {
                    float d = (js[ji].Vertex - p).LengthSquared();
                    if (d < best) { best = d; road = ri; joint = ji; }
                }
            }
            return road >= 0;
        }

        /// <summary>Split a road at one of its joints into two roads meeting exactly at that point -- the
        /// operation that turns "I drew across an existing road" into a real T-junction. The joint is
        /// DUPLICATED rather than shared: both halves keep a copy at the identical position, so the junction
        /// reads the same way as two independently drawn ends that snapped together. Returns the new road's
        /// index, or -1 if the joint is an endpoint (nothing to split) or the road is a loop.</summary>
        public int SplitRoadAt(int road, int joint)
        {
            if (road < 0 || road >= _roads.Count) return -1;
            var r = _roads[road];
            if (r.IsLoop || joint <= 0 || joint >= r.Joints.Count - 1) return -1;
            var tail = new RoadData { Material = r.Material, IsLoop = false, GuidBytes = System.Array.Empty<byte>() };
            for (int i = joint; i < r.Joints.Count; i++)
                tail.Joints.Add(new Joint { Vertex = r.Joints[i].Vertex, Offset = r.Joints[i].Offset, IgnoreTerrain = r.Joints[i].IgnoreTerrain, Mode = r.Joints[i].Mode, Tan0 = r.Joints[i].Tan0, Tan1 = r.Joints[i].Tan1 });
            r.Joints.RemoveRange(joint + 1, r.Joints.Count - joint - 1);
            _roads.Add(tail);
            RebuildRoad(road);
            int ti = _roads.Count - 1;
            if (tail.Joints.Count >= 2 && tail.Material >= 0 && tail.Material < _mats.Count) BuildRoadNode(tail);
            return ti;
        }

        // ---- SNAP TO THE SPLINE ITSELF, not to the joints on it ------------------------------------------
        // strawberry 2026-08-19: "the tool can snap along the splines... the spline line is what new roads
        // snap to". Snapping to JOINTS only lets you branch where a joint happens to sit -- roughly every 8 m
        // on a drawn road, and wherever the retail data put them on an imported one. Snapping to the CURVE
        // means the junction lands where you actually aimed, which is the difference between a road tool and
        // a road tool you fight.

        /// <summary>Closest point on any road's spline to a world point. Returns false if nothing is within
        /// maxDist. <paramref name="seg"/> is the joint index the segment starts at and <paramref name="t"/>
        /// its bezier parameter, which together locate the point precisely enough to insert a joint there.</summary>
        public bool NearestPointOnSpline(Vector3 p, float maxDist, out int road, out int seg, out float t, out Vector3 pos, int skipRoad = -1)
        {
            road = -1; seg = -1; t = 0f; pos = Vector3.Zero;
            float best = maxDist * maxDist;
            const int Steps = 12;   // per segment; segments are short, and a joint is inserted at the hit anyway
            for (int ri = 0; ri < _roads.Count; ri++)
            {
                if (ri == skipRoad) continue;
                var r = _roads[ri];
                int last = r.IsLoop ? r.Joints.Count - 1 : r.Joints.Count - 2;
                for (int si = 0; si <= last; si++)
                {
                    for (int k = 0; k <= Steps; k++)
                    {
                        float tt = k / (float)Steps;
                        var q = SplinePos(r, si, tt);
                        float d = (q - p).LengthSquared();
                        if (d < best) { best = d; road = ri; seg = si; t = tt; pos = q; }
                    }
                }
            }
            return road >= 0;
        }

        /// <summary>Insert a joint at a point on a segment, so the curve can be split exactly there. Returns
        /// the new joint's index. Tangents are re-fitted across the road afterwards, which keeps the shape
        /// close to what it was -- an inserted joint should not visibly kink the road you branched off.</summary>
        public int InsertJointOnSegment(int road, int seg, Vector3 pos)
        {
            if (road < 0 || road >= _roads.Count) return -1;
            var r = _roads[road];
            if (seg < 0 || seg >= r.Joints.Count) return -1;
            int at = seg + 1;
            r.Joints.Insert(at, new Joint { Vertex = pos, Mode = 0 });
            RetangentRoad(r);
            RebuildRoad(road);
            return at;
        }

        public int JunctionCount => _junctions.Count;
        public Vector3 JunctionPos(int j) => j >= 0 && j < _junctions.Count ? _junctions[j].Pos : Vector3.Zero;

        /// <summary>Create a junction node at a world position. Returns its index.</summary>
        public int AddJunction(Vector3 pos) { _junctions.Add(new Junction { Pos = pos }); return _junctions.Count - 1; }

        /// <summary>Nearest junction node to a point, for snapping. -1 if none within maxDist.</summary>
        public int JunctionAt(Vector3 p, float maxDist)
        {
            int best = -1; float bd = maxDist * maxDist;
            for (int i = 0; i < _junctions.Count; i++)
            {
                float d = (_junctions[i].Pos - p).LengthSquared();
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Bind a road END to a junction node, moving that end's joint onto the node. atEnd=false is
        /// the road's first joint, true is its last. Binding is what makes the connection real -- the joint is
        /// snapped to the NODE's coordinates, so the two can never disagree.</summary>
        public void BindRoadEnd(int road, bool atEnd, int junction)
        {
            if (road < 0 || road >= _roads.Count || junction < 0 || junction >= _junctions.Count) return;
            var r = _roads[road];
            if (r.Joints.Count == 0) return;
            if (atEnd) r.EndJunction = junction; else r.StartJunction = junction;
            r.Joints[atEnd ? r.Joints.Count - 1 : 0].Vertex = _junctions[junction].Pos;
            RetangentRoad(r);
            RebuildRoad(road);
        }

        public void UnbindRoadEnd(int road, bool atEnd)
        {
            if (road < 0 || road >= _roads.Count) return;
            if (atEnd) _roads[road].EndJunction = -1; else _roads[road].StartJunction = -1;
        }

        public int RoadEndJunction(int road, bool atEnd) =>
            road >= 0 && road < _roads.Count ? (atEnd ? _roads[road].EndJunction : _roads[road].StartJunction) : -1;

        /// <summary>Every road end bound to this junction. This is the routing query -- "arriving here, where
        /// can I go" -- and it reads the BINDINGS, not the geometry.</summary>
        public System.Collections.Generic.List<(int Road, bool AtEnd)> JunctionEdges(int junction)
        {
            var outp = new System.Collections.Generic.List<(int, bool)>();
            for (int i = 0; i < _roads.Count; i++)
            {
                if (_roads[i].StartJunction == junction) outp.Add((i, false));
                if (_roads[i].EndJunction == junction) outp.Add((i, true));
            }
            return outp;
        }

        /// <summary>Move a junction node -- and every road end bound to it. The reason the node exists.</summary>
        public void MoveJunction(int junction, Vector3 to)
        {
            if (junction < 0 || junction >= _junctions.Count) return;
            _junctions[junction].Pos = to;
            for (int i = 0; i < _roads.Count; i++)
            {
                var r = _roads[i];
                bool touched = false;
                if (r.StartJunction == junction && r.Joints.Count > 0) { r.Joints[0].Vertex = to; touched = true; }
                if (r.EndJunction == junction && r.Joints.Count > 0) { r.Joints[^1].Vertex = to; touched = true; }
                if (touched) { RetangentRoad(r); RebuildRoad(i); }
            }
        }

        /// <summary>Delete a junction node, freeing every end bound to it. Indices above it shift down, so the
        /// bindings are renumbered here rather than left dangling.</summary>
        public void RemoveJunction(int junction)
        {
            if (junction < 0 || junction >= _junctions.Count) return;
            _junctions.RemoveAt(junction);
            foreach (var r in _roads)
            {
                if (r.StartJunction == junction) r.StartJunction = -1; else if (r.StartJunction > junction) r.StartJunction--;
                if (r.EndJunction == junction) r.EndJunction = -1; else if (r.EndJunction > junction) r.EndJunction--;
            }
        }

        /// <summary>Junction nodes with 2+ roads bound -- what a router treats as a real decision point. A node
        /// with one road is a loose end you have not connected yet, not a junction.</summary>
        public System.Collections.Generic.List<(Vector3 Pos, System.Collections.Generic.List<(int Road, bool AtEnd)> Ends)> Junctions()
        {
            var outp = new System.Collections.Generic.List<(Vector3, System.Collections.Generic.List<(int, bool)>)>();
            for (int i = 0; i < _junctions.Count; i++)
            {
                var e = JunctionEdges(i);
                if (e.Count >= 2) outp.Add((_junctions[i].Pos, e));
            }
            return outp;
        }

        public void RemoveRoad(int road)
        {
            if (road < 0 || road >= _roads.Count) return;
            var r = _roads[road];
            r.Mi?.QueueFree(); r.Body?.QueueFree();
            _roads.RemoveAt(road);
            // Junction BINDINGS live on the road, so removing the road removes them with it -- nothing to fix
            // up here. Junction nodes deliberately SURVIVE losing their last road: a node you placed is a
            // thing you meant, and silently deleting it because you re-drew one of its rails would be worse
            // than leaving a visible loose node behind.
        }

        // --- inc3: bezier tangent handles + per-road material ---
        bool Valid(int road, int joint) => road >= 0 && road < _roads.Count && joint >= 0 && joint < _roads[road].Joints.Count;
        public Vector3 TangentPos(int road, int joint, int ti)   // world handle position = vertex + tangent
        {
            if (!Valid(road, joint)) return Vector3.Zero;
            var jt = _roads[road].Joints[joint];
            return jt.Vertex + (ti == 0 ? jt.Tan0 : jt.Tan1);
        }
        public void SetTangent(int road, int joint, int ti, Vector3 handleWorld)   // source moveTangent: setTangent(ti, handle - vertex), mode-aware, then re-extrude
        {
            if (!Valid(road, joint)) return;
            var jt = _roads[road].Joints[joint];
            jt.SetTangent(ti, handleWorld - jt.Vertex);
            RebuildRoad(road);
        }
        public byte JointMode(int road, int joint) => Valid(road, joint) ? _roads[road].Joints[joint].Mode : (byte)0;
        public void SetJointMode(int road, int joint, byte m) { if (Valid(road, joint)) _roads[road].Joints[joint].Mode = m; }   // affects the NEXT setTangent; no geometry change now
        public int RoadMaterial(int road) => road >= 0 && road < _roads.Count ? _roads[road].Material : 0;
        public void SetRoadMaterial(int road, int m) { if (road >= 0 && road < _roads.Count) { _roads[road].Material = m; RebuildRoad(road); } }
        public int MaterialCount => _mats.Count;
        // ===== TRAIN spline API: ride the rails. Tracks = Roads.unity3d material index 4. A train advances a
        // DISTANCE-along parameter; EvaluateAlong gives the terrain-snapped point + unit tangent to sit a bogie on. =====
        public const int TracksMaterial = 4;
        public int RoadMaterialOf(int road) => (road >= 0 && road < _roads.Count) ? _roads[road].Material : -1;
        public bool RoadLoops(int road) => road >= 0 && road < _roads.Count && _roads[road].IsLoop;
        public float RoadLength(int road)
        {
            if (road < 0 || road >= _roads.Count) return 0f;
            var r = _roads[road]; int segs = r.IsLoop ? r.Joints.Count : r.Joints.Count - 1; if (r.Joints.Count < 2) return 0f;
            float total = 0f; for (int i = 0; i < segs; i++) total += SegLength(r, i); return total;
        }
        /// <summary>World point (terrain-snapped) + unit tangent at a DISTANCE along a road. Loops wrap; open roads clamp.</summary>
        public bool EvaluateAlong(int road, float distance, out Vector3 pos, out Vector3 tangent, bool snapTerrain = true)
        {
            pos = Vector3.Zero; tangent = Vector3.Forward;
            if (road < 0 || road >= _roads.Count) return false;
            var r = _roads[road]; int jc = r.Joints.Count; if (jc < 2) return false;
            int segs = r.IsLoop ? jc : jc - 1; float total = RoadLength(road);
            pos = PosAlong(r, segs, total, distance, snapTerrain);
            // Tangent from a JOINT-CONTINUOUS arc-length finite diff. The old fixed bezier-t delta clamped at each
            // segment boundary -> a one-sided, discontinuous tangent, so a bogie crossing every joint snapped its
            // heading -> the wheels "jitter at high speed / on turns" (master 2026-08-19). Sampling the position a
            // metre either side (which walks across joints) gives a smooth heading everywhere.
            const float dd = 1.0f;
            Vector3 tg = PosAlong(r, segs, total, distance + dd, snapTerrain) - PosAlong(r, segs, total, distance - dd, snapTerrain);
            tangent = tg.LengthSquared() > 1e-6f ? tg.Normalized() : Vector3.Forward;
            return true;
        }

        // Position at an arc-length `distance` along a road (arc-length reparam of the bezier + terrain snap).
        // Split out so EvaluateAlong can sample it a metre either side for a joint-continuous tangent.
        Vector3 PosAlong(RoadData r, int segs, float total, float distance, bool snapTerrain = true)
        {
            if (r.IsLoop && total > 0.001f) distance = Mathf.PosMod(distance, total); else distance = Mathf.Clamp(distance, 0f, total);
            for (int i = 0; i < segs; i++)
            {
                float L = Mathf.Max(SegLength(r, i), 0.001f);
                if (distance <= L || i == segs - 1)
                {
                    // arc-length reparam: find the bezier t whose arc length from 0..t == distance (bezier t is
                    // NOT uniform in arc length -> feeding distance/L straight in slowed the train through curves).
                    const int SUB = 24;
                    Vector3 sp = SplinePos(r, i, 0f); float acc = 0f, t = 1f;
                    for (int k = 1; k <= SUB; k++)
                    {
                        Vector3 p = SplinePos(r, i, (float)k / SUB);
                        float seg = p.DistanceTo(sp);
                        if (acc + seg >= distance || k == SUB)
                        {
                            float f = seg > 1e-6f ? (distance - acc) / seg : 0f;
                            t = Mathf.Clamp(((k - 1) + Mathf.Clamp(f, 0f, 1f)) / SUB, 0f, 1f);
                            break;
                        }
                        acc += seg; sp = p;
                    }
                    Vector3 pos = SplinePos(r, i, t);
                    if (snapTerrain && Terr != null && !r.Joints[i].IgnoreTerrain) pos.Y = Terr.SampleHeight(pos.X, pos.Z);   // train passes snapTerrain:false -> ride the track's own smooth spline Y, not the bumpy heightmap (master: ignore terrain, follow the track)
                    return pos;
                }
                distance -= L;
            }
            return SplinePos(r, segs - 1, 1f);
        }
        /// <summary>Nearest TRACK road (material 4) to a world point, + the distance-along of the closest sampled point.</summary>
        public bool NearestTrack(Vector3 world, out int road, out float distanceAlong)
        {
            road = -1; distanceAlong = 0f; float best = float.MaxValue;
            for (int ri = 0; ri < _roads.Count; ri++)
            {
                var r = _roads[ri]; if (r.Material != TracksMaterial || r.Joints.Count < 2) continue;
                int segs = r.IsLoop ? r.Joints.Count : r.Joints.Count - 1; float acc = 0f;
                for (int i = 0; i < segs; i++)
                {
                    float L = Mathf.Max(SegLength(r, i), 0.001f); int n = Mathf.Max(2, (int)(L / 4f));
                    for (int k = 0; k <= n; k++)
                    {
                        float t = (float)k / n; Vector3 pp = SplinePos(r, i, t); float dsq = pp.DistanceSquaredTo(world);
                        if (dsq < best) { best = dsq; road = ri; distanceAlong = acc + t * L; }
                    }
                    acc += L;
                }
            }
            return road >= 0;
        }

        // Roads.unity3d container order (same as TexHeight) -> friendly names for the picker; concrete/dirt as a fallback tag
        static readonly string[] MatNames = { "Highway_0", "Highway_1", "Racetrack", "Road", "Tracks", "Trail", "White", "Yellow", "Road_8", "Road_9" };
        public string RoadMaterialName(int road)
        {
            int m = RoadMaterial(road);
            string name = m >= 0 && m < MatNames.Length ? MatNames[m] : $"mat{m}";
            return $"{m}:{name}";
        }

        // per-road loop + per-joint height offset / ignore-terrain (the rest of the source RoadJoint data model)
        public bool RoadIsLoop(int road) => road >= 0 && road < _roads.Count && _roads[road].IsLoop;
        public void SetRoadLoop(int road, bool loop) { if (road >= 0 && road < _roads.Count) { _roads[road].IsLoop = loop; RebuildRoad(road); } }
        public float JointOffset(int road, int joint) => Valid(road, joint) ? _roads[road].Joints[joint].Offset : 0f;
        public void SetJointOffset(int road, int joint, float o) { if (Valid(road, joint)) { _roads[road].Joints[joint].Offset = o; RebuildRoad(road); } }
        public bool JointIgnoreTerrain(int road, int joint) => Valid(road, joint) && _roads[road].Joints[joint].IgnoreTerrain;
        public void SetJointIgnoreTerrain(int road, int joint, bool ig) { if (Valid(road, joint)) { _roads[road].Joints[joint].IgnoreTerrain = ig; RebuildRoad(road); } }

        // undo: deep-copy the roads DATA (not the mesh nodes) as an opaque snapshot; Restore swaps it back + rebuilds.
        RoadData CloneData(RoadData r)
        {
            var c = new RoadData { Material = r.Material, IsLoop = r.IsLoop, GuidBytes = r.GuidBytes };
            foreach (var j in r.Joints) c.Joints.Add(new Joint { Vertex = j.Vertex, Tan0 = j.Tan0, Tan1 = j.Tan1, Offset = j.Offset, IgnoreTerrain = j.IgnoreTerrain, Mode = j.Mode });
            return c;
        }
        public object Snapshot()
        {
            var snap = new List<RoadData>();
            foreach (var r in _roads) snap.Add(CloneData(r));
            return snap;
        }
        public void Restore(object snapshot)
        {
            if (snapshot is not List<RoadData> snap) return;
            foreach (var r in _roads) { r.Mi?.QueueFree(); r.Body?.QueueFree(); }
            _roads.Clear();
            foreach (var r in snap) { var c = CloneData(r); _roads.Add(c); if (c.Joints.Count >= 2 && c.Material >= 0 && c.Material < _mats.Count) BuildRoadNode(c); }
        }

        // editor reopen: replace the map's roads with the SAVED edits (same Paths.dat format), so edits round-trip
        public bool ReloadPaths(string pathsFile)
        {
            if (!File.Exists(pathsFile)) return false;
            foreach (var r in _roads) { r.Mi?.QueueFree(); r.Body?.QueueFree(); }
            _roads.Clear();
            foreach (var r in ParsePathsDat(pathsFile))
            {
                _roads.Add(r);
                if (r.Joints.Count >= 2 && r.Material >= 0 && r.Material < _mats.Count) BuildRoadNode(r);
            }
            GD.Print($"[roads] reloaded {_roads.Count} roads from saved edits ({pathsFile})");
            return true;
        }

        // editor Save(): write Paths.dat back (exact reverse of ParsePathsDat, same version/guids/modes). G() negates Z on
        // read (Unity z -> Godot -z), so undo it here: unityZ = -godotZ. Saved to an editor path, NOT the retail install.
        public bool SavePaths(string path)
        {
            byte version = _pathsVersion;
            if (version <= 1 || _roads.Count == 0) return false;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(version);
            bw.Write((ushort)_roads.Count);
            foreach (var r in _roads)
            {
                bw.Write((ushort)r.Joints.Count);
                bw.Write((byte)r.Material);
                if (version > 2) bw.Write(r.IsLoop);
                if (version >= 6) { var g = r.GuidBytes ?? System.Array.Empty<byte>(); bw.Write((ushort)g.Length); bw.Write(g); }
                foreach (var jt in r.Joints)
                {
                    bw.Write(jt.Vertex.X); bw.Write(jt.Vertex.Y); bw.Write(-jt.Vertex.Z);
                    if (version > 2)
                    {
                        bw.Write(jt.Tan0.X); bw.Write(jt.Tan0.Y); bw.Write(-jt.Tan0.Z);
                        bw.Write(jt.Tan1.X); bw.Write(jt.Tan1.Y); bw.Write(-jt.Tan1.Z);
                        bw.Write(jt.Mode);
                    }
                    if (version > 4) bw.Write(jt.Offset);
                    if (version > 3) bw.Write(jt.IgnoreTerrain);
                }
            }
            return true;
        }

        // ---- THE JUNCTION GRAPH SIDECAR ----------------------------------------------------------------
        // Written NEXT TO Paths.dat, never inside it. Paths.dat is retail's format; a legacy map has no
        // sidecar and therefore no junctions, which is exactly the "existing maps use the old tool" split.
        //
        // Road links are stored POSITIONALLY, in the same order Paths.dat writes its roads, because that file
        // has no per-road identity to key on (the GUID is optional and not unique across hand-drawn roads).
        // So the two files must be written together and read together -- SaveGraph is called from the same
        // Save() that writes Paths.dat, and a count mismatch on load is treated as a stale sidecar and
        // discarded rather than applied to the wrong roads.
        public const string GraphFileName = "Junctions.dat";

        public bool SaveGraph(string path)
        {
            if (_junctions.Count == 0 && _roads.TrueForAll(r => r.StartJunction < 0 && r.EndJunction < 0))
            {
                if (File.Exists(path)) File.Delete(path);   // nothing to say: leave no stale sidecar behind
                return false;
            }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write((byte)1);
            bw.Write((ushort)_junctions.Count);
            foreach (var j in _junctions) { bw.Write(j.Pos.X); bw.Write(j.Pos.Y); bw.Write(-j.Pos.Z); }   // same Unity-Z convention as Paths.dat
            bw.Write((ushort)_roads.Count);
            foreach (var r in _roads) { bw.Write((short)r.StartJunction); bw.Write((short)r.EndJunction); }
            return true;
        }

        public bool LoadGraph(string path)
        {
            _junctions.Clear();
            foreach (var r in _roads) { r.StartJunction = -1; r.EndJunction = -1; }
            if (!File.Exists(path)) return false;
            using var br = new BinaryReader(File.OpenRead(path));
            byte version = br.ReadByte();
            if (version != 1) { GD.PrintErr($"[roads] junction graph version {version} not understood -- ignoring"); return false; }
            int jn = br.ReadUInt16();
            for (int i = 0; i < jn; i++) _junctions.Add(new Junction { Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), -br.ReadSingle()) });
            int rn = br.ReadUInt16();
            if (rn != _roads.Count)
            {
                // Positional links against a different road list would bind the wrong rails to the wrong
                // nodes -- silently, and in a way that looks like a routing bug much later. Refuse instead.
                GD.PrintErr($"[roads] junction sidecar lists {rn} roads but the field has {_roads.Count} -- stale, dropping the links (nodes kept)");
                return false;
            }
            for (int i = 0; i < rn; i++) { _roads[i].StartJunction = br.ReadInt16(); _roads[i].EndJunction = br.ReadInt16(); }
            GD.Print($"[roads] loaded {_junctions.Count} junction nodes, {Junctions().Count} of them connecting 2+ roads");
            return true;
        }

        List<RoadMat> ParseRoadsDat(string path)
        {
            var list = new List<RoadMat>();
            if (!File.Exists(path)) return list;
            using var br = new BinaryReader(File.OpenRead(path));
            byte version = br.ReadByte();
            byte count = br.ReadByte();
            for (int i = 0; i < count; i++)
            {
                var m = new RoadMat { Width = br.ReadSingle(), Height = br.ReadSingle(), Depth = br.ReadSingle() };
                if (version > 1) m.Offset = br.ReadSingle();
                m.Concrete = br.ReadBoolean();
                list.Add(m);
            }
            return list;
        }

        List<RoadData> ParsePathsDat(string path)
        {
            var list = new List<RoadData>();
            if (!File.Exists(path)) return list;
            using var br = new BinaryReader(File.OpenRead(path));
            byte version = br.ReadByte();
            _pathsVersion = version;   // remembered so SavePaths writes the exact same layout back
            if (version <= 1) return list;
            ushort count = br.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                var road = new RoadData();
                ushort length = br.ReadUInt16();
                road.Material = br.ReadByte();
                if (version > 2) road.IsLoop = br.ReadBoolean();
                if (version >= 6) { ushort gl = br.ReadUInt16(); road.GuidBytes = br.ReadBytes(gl); }   // roadAssetRef: length-prefixed byte array (readGUID)
                for (int j = 0; j < length; j++)
                {
                    var jt = new Joint { Vertex = G(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()) };
                    if (version > 2)
                    {
                        jt.Tan0 = G(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        jt.Tan1 = G(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        jt.Mode = br.ReadByte();   // ERoadMode (MIRROR/ALIGNED/FREE) -- round-tripped
                    }
                    if (version > 4) jt.Offset = br.ReadSingle();
                    if (version > 3) jt.IgnoreTerrain = br.ReadBoolean();
                    road.Joints.Add(jt);
                }
                list.Add(road);
            }
            return list;
        }

        static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        // spline position for segment starting at joint[index], param t in [0,1]
        Vector3 SplinePos(RoadData r, int index, float t)
        {
            var s = r.Joints[index];
            var e = r.Joints[index == r.Joints.Count - 1 ? 0 : index + 1];
            return Bezier(s.Vertex, s.Vertex + s.Tan1, e.Vertex + e.Tan0, e.Vertex, t);
        }

        ArrayMesh BuildRoadMesh(RoadData r, RoadMat mat, float texHeight, out ConcavePolygonShape3D collision)
        {
            collision = null;
            // src Road.buildMesh: HalfWidth=width (field IS the half-width), HalfVerticalSize=depth, verticalSize=2*depth,
            // VerticalOffset=offset. Keep position.y AT terrain height (+ per-joint offset); the SURFACE verts go UP by
            // halfVerticalSize while the outer TAPER verts go DOWN by halfVerticalSize -> the taper sinks BELOW the
            // ground so there's never a gap to see under. verticalOffset is applied per-vert along the normal, NOT as a lift.
            float halfWidth = mat.Width * WidthScale;   // master: slightly thicker (fills bald patches)
            float halfVerticalSize = mat.Depth;
            float verticalSize = halfVerticalSize * 2f;
            float verticalOffset = mat.Offset;
            bool loop = r.IsLoop;
            int jc = r.Joints.Count;
            int segs = loop ? jc : jc - 1;

            // src updateSamples: arc-length step every 5 world units, carried continuously across joints, + a final sample.
            var samples = new List<(int idx, float t)>();
            float carry = 0f;
            for (int index = 0; index < segs; index++)
            {
                float length = Mathf.Max(SegLength(r, index), 0.001f);
                float step;
                for (step = carry; step < length; step += 5f) samples.Add((index, step / length));   // sample every 5u (src value) -- the 2.5u tighter sampling left BALD road patches, reverted (master)
                carry = step - length;
            }
            if (loop) samples.Add((0, 0f)); else samples.Add((jc - 2, 1f));
            if (samples.Count < 2) return null;

            float invRepeat = mat.Height != 0f ? mat.Height / texHeight : 1f / texHeight;   // src: UV repeats every texture.height/mat.height world units

            var ringV = new List<Vector3[]>();
            var ringN = new List<Vector3>();
            var ringVd = new List<float>();   // UV v = accumulated distance * invRepeat
            float distance = 0f;
            Vector3 prevC = Vector3.Zero;
            Vector3 fC = Vector3.Zero, fS = Vector3.Right, fN = Vector3.Up, fD = Vector3.Forward;   // first sample frame (start cap)
            Vector3 lC = Vector3.Zero, lS = Vector3.Right, lN = Vector3.Up, lD = Vector3.Forward;   // last sample frame (end cap)

            for (int s = 0; s < samples.Count; s++)
            {
                int index = samples[s].idx; float t = samples[s].t;
                bool ign = r.Joints[index].IgnoreTerrain;
                Vector3 pos = SplinePos(r, index, t);
                if (Terr != null && !ign) pos.Y = Terr.SampleHeight(pos.X, pos.Z);
                Vector3 dir = SplinePos(r, index, Mathf.Min(t + 0.02f, 1f)) - SplinePos(r, index, Mathf.Max(t - 0.02f, 0f));
                dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Forward;
                Vector3 normal = (Terr != null && !ign) ? SampleNormal(pos.X, pos.Z) : Vector3.Up;
                Vector3 side = dir.Cross(normal).Normalized();
                // per-joint offset lerped along the segment (added to y)
                float jo = index < jc - 1 ? Mathf.Lerp(r.Joints[index].Offset, r.Joints[index + 1].Offset, t)
                         : loop ? Mathf.Lerp(r.Joints[index].Offset, r.Joints[0].Offset, t) : r.Joints[index].Offset;
                pos.Y += jo;   // keep the centre on the terrain (UV distance + end caps use it)

                // BANK the surface to the terrain at EACH edge so it hugs the cross-slope, instead of lifting the whole strip to the
                // highest edge (that floated the downhill side on a cross-slope) -- master "horizontal banking issue".
                Vector3 lp = pos + side * halfWidth, rp = pos - side * halfWidth;   // left/right surface X-Z
                float lY = ((Terr != null && !ign) ? Terr.SampleHeight(lp.X, lp.Z) : pos.Y - jo) + jo;
                float rY = ((Terr != null && !ign) ? Terr.SampleHeight(rp.X, rp.Z) : pos.Y - jo) + jo;
                Vector3 lSurf = new Vector3(lp.X, lY, lp.Z) + normal * (halfVerticalSize + verticalOffset);
                Vector3 rSurf = new Vector3(rp.X, rY, rp.Z) + normal * (halfVerticalSize + verticalOffset);
                var cs = new Vector3[4];
                cs[1] = lSurf;                                                 // road surface left (at the terrain edge)
                cs[2] = rSurf;                                                 // road surface right (at the terrain edge)
                cs[0] = lSurf + side * verticalSize - normal * verticalSize;   // outer-left taper (out + down)
                cs[3] = rSurf - side * verticalSize - normal * verticalSize;   // outer-right taper (out + down)

                if (s > 0) distance += pos.DistanceTo(prevC);
                prevC = pos;
                ringV.Add(cs); ringN.Add(normal); ringVd.Add(distance * invRepeat);
                if (s == 0) { fC = pos; fS = side; fN = normal; fD = dir; }
                lC = pos; lS = side; lN = normal; lD = dir;
            }

            // assemble rings with src end caps: [startCap] s0..sN [endCap] (loop = just the sample rings, last==first closes it)
            var rings = new List<Vector3[]>();
            var rn = new List<Vector3>();
            var rv = new List<float>();
            if (!loop) { rings.Add(Cap(fC, fS, fN, fD, -1f, halfWidth, verticalSize, halfVerticalSize, verticalOffset)); rn.Add(fN); rv.Add(ringVd[0]); }
            for (int i = 0; i < ringV.Count; i++) { rings.Add(ringV[i]); rn.Add(ringN[i]); rv.Add(ringVd[i]); }
            if (!loop) { rings.Add(Cap(lC, lS, lN, lD, 1f, halfWidth, verticalSize, halfVerticalSize, verticalOffset)); rn.Add(lN); rv.Add(ringVd[ringVd.Count - 1]); }
            if (rings.Count < 2) return null;

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var idx = new List<int>();
            float[] uC = { 0f, 0f, 1f, 1f };   // src: outer/inner-left share u=0, inner/outer-right share u=1
            for (int i = 0; i < rings.Count; i++)
                for (int k = 0; k < 4; k++) { verts.Add(rings[i][k]); norms.Add(rn[i]); uvs.Add(new Vector2(uC[k], rv[i])); }
            // stitch 6 tris per ring pair = 3 quads (left-taper, road, right-taper). winding that lights the top from
            // ABOVE and makes the trimesh collider face up (the src winding, flipped by our negate-Z verts, did neither).
            for (int i = 0; i + 1 < rings.Count; i++)
            {
                int a = i * 4, b = (i + 1) * 4;
                for (int q = 0; q < 3; q++)
                {
                    int a0 = a + q, a1 = a + q + 1, b0 = b + q, b1 = b + q + 1;
                    idx.Add(a0); idx.Add(a1); idx.Add(b1);
                    idx.Add(a0); idx.Add(b1); idx.Add(b0);
                }
            }

            // collision = the FULL road shell (top + side bevels + end ramps), double-sided so the player never falls
            // through or gets pushed the wrong way. matches src (MeshCollider of the whole road mesh). the earlier
            // "stuck" was the INVERTED winding facing collision downward, not the geometry -> fixed by the winding above.
            // COMPLETELY SOLID collider (master): the visual mesh is an open-bottom shell (top + two side tapers), so a
            // fast/edge case can slip UNDER it. The collider adds a bottom quad per ring pair joining the two taper-bottom
            // verts (indices 0 and 3) -> a CLOSED solid tube under BackfaceCollision. Visual mesh stays untouched.
            var cidx = new List<int>(idx);
            for (int i = 0; i + 1 < rings.Count; i++)
            {
                int a = i * 4, b = (i + 1) * 4;
                cidx.Add(a + 0); cidx.Add(a + 3); cidx.Add(b + 3);   // taper-bottom quad, sealing the underside
                cidx.Add(a + 0); cidx.Add(b + 3); cidx.Add(b + 0);
            }
            var soup = new Vector3[cidx.Count];
            for (int i = 0; i < cidx.Count; i++) soup[i] = verts[cidx[i]];
            collision = cidx.Count >= 3 ? new ConcavePolygonShape3D { Data = soup, BackfaceCollision = true } : null;

            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = norms.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
            var m = new ArrayMesh();
            m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return m;
        }

        // src end cap: 4 verts all at the LOW taper level (-normal*halfVerticalSize), shoved fore/aft by
        // direction*verticalSize*2 -> stitching it to the first/last ring makes the ramp-down at each road end.
        static Vector3[] Cap(Vector3 p, Vector3 side, Vector3 normal, Vector3 dir, float sign,
                             float halfWidth, float verticalSize, float halfVerticalSize, float verticalOffset)
        {
            Vector3 lo = -normal * halfVerticalSize + normal * verticalOffset + dir * (verticalSize * 2f * sign);
            return new[]
            {
                p + side * (halfWidth + verticalSize) + lo,
                p + side * halfWidth + lo,
                p - side * halfWidth + lo,
                p - side * (halfWidth + verticalSize) + lo,
            };
        }

        // bezier arc-length estimate for a segment (matches src getLengthEstimate closely enough for sample stepping)
        float SegLength(RoadData r, int index)
        {
            Vector3 prev = SplinePos(r, index, 0f); float len = 0f;
            for (int i = 1; i <= 16; i++) { Vector3 p = SplinePos(r, index, i / 16f); len += p.DistanceTo(prev); prev = p; }
            return len;
        }
    }
}
