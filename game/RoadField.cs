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
        class RoadData { public int Material; public bool IsLoop; public List<Joint> Joints = new(); public byte[] GuidBytes; public MeshInstance3D Mi; public StaticBody3D Body; }

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

        Material RoadMaterial3D(int index, bool concrete)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/roads/road_{index}.png");
            if (System.IO.File.Exists(p))
            {
                var img = new Image();
                if (img.Load(p) == Error.Ok)
                    return new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img), TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            }
            return new StandardMaterial3D { AlbedoColor = concrete ? new Color(0.34f, 0.34f, 0.35f) : new Color(0.45f, 0.37f, 0.28f), Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
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

        public void RemoveRoad(int road)
        {
            if (road < 0 || road >= _roads.Count) return;
            var r = _roads[road];
            r.Mi?.QueueFree(); r.Body?.QueueFree();
            _roads.RemoveAt(road);
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
            float halfWidth = mat.Width;
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
            var soup = new Vector3[idx.Count];
            for (int i = 0; i < idx.Count; i++) soup[i] = verts[idx[i]];
            collision = idx.Count >= 3 ? new ConcavePolygonShape3D { Data = soup, BackfaceCollision = true } : null;

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
