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
        struct Joint { public Vector3 Vertex, Tan0, Tan1; public float Offset; public bool IgnoreTerrain; }
        class RoadData { public int Material; public bool IsLoop; public List<Joint> Joints = new(); }

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
            var mats = ParseRoadsDat(Path.Combine(envDir, "Roads.dat"));
            var roads = ParsePathsDat(Path.Combine(envDir, "Paths.dat"));
            int built = 0;
            foreach (var r in roads)
            {
                if (r.Joints.Count < 2 || r.Material < 0 || r.Material >= mats.Count) continue;
                float texH = r.Material < TexHeight.Length ? TexHeight[r.Material] : 256f;
                var mesh = BuildRoadMesh(r, mats[r.Material], texH, out var collShape);
                if (mesh == null) continue;
                var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = RoadMaterial3D(r.Material, mats[r.Material].Concrete) };
                AddChild(mi);
                if (collShape != null)   // flat top-ribbon collider (double-sided) -> clean walk/drive, nothing to snag on
                {
                    var body = new StaticBody3D();
                    body.AddChild(new CollisionShape3D { Shape = collShape });
                    AddChild(body);
                }
                built++;
            }
            GD.Print($"[roads] built {built} spline roads ({roads.Count} in Paths.dat, {mats.Count} materials)");
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
            if (version <= 1) return list;
            ushort count = br.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                var road = new RoadData();
                ushort length = br.ReadUInt16();
                road.Material = br.ReadByte();
                if (version > 2) road.IsLoop = br.ReadBoolean();
                if (version >= 6) { ushort gl = br.ReadUInt16(); br.ReadBytes(gl); }   // roadAssetRef: length-prefixed byte array (readGUID)
                for (int j = 0; j < length; j++)
                {
                    var jt = new Joint { Vertex = G(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()) };
                    if (version > 2)
                    {
                        jt.Tan0 = G(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        jt.Tan1 = G(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        br.ReadByte();   // mode
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
                for (step = carry; step < length; step += 2.5f) samples.Add((index, step / length));   // sample every 2.5u (src 5) so the road hugs the terrain tighter between joints (master)
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
