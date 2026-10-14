using SDG.Unturned;
using UnityEngine;

namespace UnturnedBevy;

/// <summary>Immutable host geometry for authoritative combat. Clients submit aim,
/// never hits. The bridge receives this table from the local world loader at boot.</summary>
public sealed class WorldGeometry
{
    readonly List<Triangle> _triangles = new();
    Node _root;
    readonly List<Triangle> _terrainTriangles = new();
    Node _terrainRoot;
    public int Count => _triangles.Count;
    readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Destructible);
    sealed class Node
    {
        public Vector3 Min, Max;
        public Node Left, Right;
        public Triangle[] Leaf;
    }
    public void Clear() { _triangles.Clear(); _terrainTriangles.Clear(); _root = null; _terrainRoot = null; }
    public void AddMesh(Vector3[] vertices, int[] indices, int destructible = -1, bool terrain = false)
    {
        if (indices.Length % 3 != 0) throw new ArgumentException("Triangle indices must be triples");
        if (indices.Any(i => i < 0 || i >= vertices.Length)) throw new ArgumentException("Triangle index exceeds vertices");
        for (int n = 0; n < indices.Length; n += 3)
        {
            var triangle = new Triangle(vertices[indices[n]], vertices[indices[n + 1]], vertices[indices[n + 2]], destructible);
            _triangles.Add(triangle);
            if (terrain) _terrainTriangles.Add(triangle);
        }
    }
    public void AddTerrain(RegionData data) => AddHeightfield((float)data.Coord.MinX, (float)data.Coord.MinZ, data.Cells, data.Spacing, data.Heights, data.Holes);
    public void AddHeightfield(float originX, float originZ, int cells, float spacing, float[] heights, bool[] holes = null)
    {
        if (cells < 1 || cells > 2048 || !float.IsFinite(spacing) || spacing <= 0 || heights.Length != (cells + 1) * (cells + 1)
            || heights.Any(h => !float.IsFinite(h)) || (holes != null && holes.Length != cells * cells))
            throw new ArgumentException("Invalid heightfield dimensions or values");
        int side = cells + 1;
        Vector3 At(int x, int z) => new(originX + x * spacing, heights[z * side + x], originZ + z * spacing);
        for (int z = 0; z < cells; z++) for (int x = 0; x < cells; x++)
        {
            if (holes != null && holes[z * cells + x]) continue;
            var a = At(x, z); var b = At(x + 1, z); var c = At(x, z + 1); var d = At(x + 1, z + 1);
            var t0 = new Triangle(a, c, b, -1); var t1 = new Triangle(b, c, d, -1);
            _triangles.Add(t0); _triangles.Add(t1); _terrainTriangles.Add(t0); _terrainTriangles.Add(t1);
        }
    }
    public void Build() { _root = Build(_triangles.ToArray(), 0); _terrainRoot = Build(_terrainTriangles.ToArray(), 0); }
    static Node Build(Triangle[] triangles, int depth)
    {
        if (triangles.Length == 0) return null;
        var node = new Node { Min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), Max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity) };
        foreach (var t in triangles) foreach (var p in new[] { t.A, t.B, t.C })
        {
            node.Min = new(Math.Min(node.Min.x, p.x), Math.Min(node.Min.y, p.y), Math.Min(node.Min.z, p.z));
            node.Max = new(Math.Max(node.Max.x, p.x), Math.Max(node.Max.y, p.y), Math.Max(node.Max.z, p.z));
        }
        if (triangles.Length <= 12) { node.Leaf = triangles; return node; }
        var size = node.Max - node.Min;
        int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        Array.Sort(triangles, (a, b) => Coordinate(a.A + a.B + a.C, axis).CompareTo(Coordinate(b.A + b.B + b.C, axis)));
        int mid = triangles.Length / 2;
        node.Left = Build(triangles[..mid], depth + 1); node.Right = Build(triangles[mid..], depth + 1);
        return node;
    }
    public bool Ray(Vector3 from, Vector3 to, out Vector3 hit, out int destructible)
    {
        float nearest = 1f; int index = -1; bool found = false;
        Visit(_root, from, to - from, ref nearest, ref index, ref found);
        hit = from + (to - from) * nearest; destructible = index;
        return found;
    }
    public float GroundHeight(float x, float z)
    {
        float t = 1f; int index = -1; bool hit = false;
        Visit(_terrainRoot, new(x, 10000, z), new(0, -20000, 0), ref t, ref index, ref hit);
        return hit ? 10000 - 20000 * t : 0;
    }
    static void Visit(Node node, Vector3 origin, Vector3 dir, ref float nearest, ref int index, ref bool found)
    {
        if (node == null || !Box(node.Min, node.Max, origin, dir, nearest)) return;
        if (node.Leaf == null)
        {
            Visit(node.Left, origin, dir, ref nearest, ref index, ref found);
            Visit(node.Right, origin, dir, ref nearest, ref index, ref found);
            return;
        }
        foreach (var triangle in node.Leaf)
        {
            var edge1 = triangle.B - triangle.A; var edge2 = triangle.C - triangle.A;
            var p = Vector3.Cross(dir, edge2); float determinant = Vector3.Dot(edge1, p);
            if (MathF.Abs(determinant) < 1e-8f) continue;
            float inverse = 1f / determinant; var s = origin - triangle.A;
            float u = inverse * Vector3.Dot(s, p); if (u < -1e-6f || u > 1.000001f) continue;
            var q = Vector3.Cross(s, edge1); float v = inverse * Vector3.Dot(dir, q);
            if (v < -1e-6f || u + v > 1.000001f) continue;
            float t = inverse * Vector3.Dot(edge2, q);
            if (t < 0 || t > nearest) continue;
            nearest = t; index = triangle.Destructible; found = true;
        }
    }
    static bool Box(Vector3 min, Vector3 max, Vector3 origin, Vector3 dir, float end)
    {
        float start = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = Coordinate(origin, axis), d = Coordinate(dir, axis), lo = Coordinate(min, axis), hi = Coordinate(max, axis);
            if (MathF.Abs(d) < 1e-9f) { if (o < lo || o > hi) return false; continue; }
            float a = (lo - o) / d, b = (hi - o) / d;
            if (a > b) (a, b) = (b, a);
            start = MathF.Max(start, a); end = MathF.Min(end, b); if (start > end) return false;
        }
        return true;
    }
    static float Coordinate(Vector3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;
}
