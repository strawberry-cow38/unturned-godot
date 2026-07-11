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

        public void LoadFromEnvironment(string envDir)
        {
            var mats = ParseRoadsDat(Path.Combine(envDir, "Roads.dat"));
            var roads = ParsePathsDat(Path.Combine(envDir, "Paths.dat"));
            int built = 0;
            foreach (var r in roads)
            {
                if (r.Joints.Count < 2 || r.Material < 0 || r.Material >= mats.Count) continue;
                float texH = r.Material < TexHeight.Length ? TexHeight[r.Material] : 256f;
                var mesh = BuildRoadMesh(r, mats[r.Material], texH);
                if (mesh == null) continue;
                var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = RoadMaterial3D(r.Material, mats[r.Material].Concrete) };
                AddChild(mi);
                mi.CreateTrimeshCollision();   // solid road surface -> walk/drive on it (StaticBody + concave shape)
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

        ArrayMesh BuildRoadMesh(RoadData r, RoadMat mat, float texHeight)
        {
            // RoadMaterial.cs: the 'width' field is MISLEADINGLY named -- it's already the HALF-width of the flat
            // section (HalfWidth=width). Likewise 'depth' IS the half-vertical-size (HalfVerticalSize=depth). Do NOT halve.
            float halfWidth = mat.Width;
            float halfVerticalSize = mat.Depth;
            float verticalSize = halfVerticalSize * 2f;
            float offset = mat.Offset;
            int segCount = r.IsLoop ? r.Joints.Count : r.Joints.Count - 1;

            var rowV = new List<Vector3[]>();   // per cross-section: 4 verts
            var rowUV = new List<float>();       // per cross-section: V coord (distance)
            float invRepeat = mat.Height != 0f ? mat.Height / texHeight : 1f / texHeight;  // src: UV repeats every texture.height/mat.height world units
            float distance = 0f;
            Vector3 prev = Vector3.Zero;
            bool first = true;

            for (int seg = 0; seg < segCount; seg++)
            {
                var a = r.Joints[seg].Vertex;
                var b = r.Joints[seg == r.Joints.Count - 1 ? 0 : seg + 1].Vertex;
                int steps = Mathf.Max(2, (int)(a.DistanceTo(b) / 2f));   // ~1 sample / 2 world units
                for (int st = 0; st <= steps; st++)
                {
                    if (seg > 0 && st == 0) continue;   // skip dup at shared joints
                    float t = (float)st / steps;
                    Vector3 pos = SplinePos(r, seg, t);
                    if (Terr != null && !r.Joints[seg].IgnoreTerrain) pos.Y = Terr.SampleHeight(pos.X, pos.Z);

                    Vector3 dir = (SplinePos(r, seg, Mathf.Min(t + 0.02f, 1f)) - SplinePos(r, seg, Mathf.Max(t - 0.02f, 0f)));
                    dir.Y = 0f;
                    dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Forward;
                    Vector3 normal = Vector3.Up;
                    Vector3 side = dir.Cross(normal).Normalized();
                    // src buildMesh: raise pos.y so BOTH edges clear the terrain -> road sits PROUD on slopes (doesn't sink into hills).
                    if (Terr != null && !r.Joints[seg].IgnoreTerrain)
                    {
                        Vector3 lft = pos + side * halfWidth; float lo = Terr.SampleHeight(lft.X, lft.Z) - pos.Y; if (lo > 0f) pos.Y += lo;
                        Vector3 rgt = pos - side * halfWidth; float ro = Terr.SampleHeight(rgt.X, rgt.Z) - pos.Y; if (ro > 0f) pos.Y += ro;
                    }
                    // + halfVerticalSize: lift the road so its beveled outer edge sits AT the terrain and the full
                    // vertical thickness is above ground (visible), instead of the bevel sinking into the terrain.
                    pos.Y += offset + halfVerticalSize;

                    var cs = new Vector3[4];
                    cs[0] = pos + side * (halfWidth + verticalSize) - normal * halfVerticalSize;
                    cs[1] = pos + side * halfWidth + normal * halfVerticalSize;
                    cs[2] = pos - side * halfWidth + normal * halfVerticalSize;
                    cs[3] = pos - side * (halfWidth + verticalSize) - normal * halfVerticalSize;

                    if (!first) distance += pos.DistanceTo(prev);
                    prev = pos; first = false;
                    rowV.Add(cs);
                    rowUV.Add(distance * invRepeat);
                }
            }

            if (rowV.Count < 2) return null;

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var idx = new List<int>();
            float[] uCoord = { 0f, 0.05f, 0.95f, 1f };   // across the strip (taper edges ~ road surface 0-1)
            for (int i = 0; i < rowV.Count; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    verts.Add(rowV[i][k]);
                    norms.Add(Vector3.Up);
                    uvs.Add(new Vector2(uCoord[k], rowUV[i]));
                }
            }
            for (int i = 0; i + 1 < rowV.Count; i++)
            {
                int a = i * 4, b = (i + 1) * 4;
                for (int q = 0; q < 3; q++)   // 3 quads: left-taper, road, right-taper
                {
                    int a0 = a + q, a1 = a + q + 1, b0 = b + q, b1 = b + q + 1;
                    idx.Add(a0); idx.Add(a1); idx.Add(b1);
                    idx.Add(a0); idx.Add(b1); idx.Add(b0);
                }
            }

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
    }
}
