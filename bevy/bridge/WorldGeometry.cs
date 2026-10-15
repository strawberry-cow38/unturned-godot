using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevy;

/// <summary>Host geometry for authoritative combat. Clients submit aim, never
/// hits. The local world loader supplies static meshes and keyed moving bodies.</summary>
public sealed partial class WorldGeometry
{
    readonly List<Triangle> _triangles = new();
    readonly Dictionary<string, Node> _dynamic = new();
    readonly List<Capsule> _capsules = new();
    readonly Dictionary<string, Capsule[]> _dynamicCapsules = new();
    readonly record struct Capsule(Vector3 Foot, float Radius, float Height);
    Node _root;
    readonly List<Triangle> _terrainTriangles = new();
    Node _terrainRoot;
    public int Count => _triangles.Count;
    public Func<int, bool> DestructibleAlive { get; set; }
    public Func<int, bool> ResourceAlive { get; set; }
    public Func<uint, bool> VehicleExists { get; set; }
    public Func<uint, string, bool> VehiclePartSolid { get; set; }
    public Func<uint, bool> DeployableExists { get; set; }
    public Func<string, bool> DynamicAlive { get; set; }
    readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Destructible, int Resource = -1, uint Vehicle = 0, uint Deployable = 0, string Part = null, bool Thin = false, bool Water = false);
    sealed class Node
    {
        public Vector3 Min, Max;
        public Node Left, Right;
        public Triangle[] Leaf;
    }
    public void Clear() { _triangles.Clear(); _terrainTriangles.Clear(); _capsules.Clear(); _root = null; _terrainRoot = null; ClearDynamic(); _layerPatches.Clear(); }
    public void ClearDynamic() { _dynamic.Clear(); _dynamicCapsules.Clear(); }
    public void RemoveDynamic(string key) { _dynamic.Remove(key); _dynamicCapsules.Remove(key); }
    public void TranslateWorld(Vector3 delta)
    {
        Triangle Shift(Triangle t) => t with { A = t.A + delta, B = t.B + delta, C = t.C + delta };
        void Tree(Node n)
        {
            if (n == null) return;
            n.Min += delta; n.Max += delta;
            if (n.Leaf != null) for (int i = 0; i < n.Leaf.Length; i++) n.Leaf[i] = Shift(n.Leaf[i]);
            Tree(n.Left); Tree(n.Right);
        }
        for (int i = 0; i < _triangles.Count; i++) _triangles[i] = Shift(_triangles[i]);
        for (int i = 0; i < _terrainTriangles.Count; i++) _terrainTriangles[i] = Shift(_terrainTriangles[i]);
        Tree(_root); Tree(_terrainRoot); foreach (var tree in _dynamic.Values) Tree(tree);
        for (int i = 0; i < _capsules.Count; i++) _capsules[i] = _capsules[i] with { Foot = _capsules[i].Foot + delta };
        foreach (var capsules in _dynamicCapsules.Values)
            for (int i = 0; i < capsules.Length; i++) capsules[i] = capsules[i] with { Foot = capsules[i].Foot + delta };
        foreach (var p in _layerPatches) { p.X += delta.x; p.Z += delta.z; }
    }

    public void SetDynamic(string key, WorldGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Dynamic geometry requires a stable key");
        if (DynamicAlive?.Invoke(key) == false) { RemoveDynamic(key); return; }
        if (geometry._root == null) _dynamic.Remove(key); else _dynamic[key] = geometry._root;
        if (geometry._capsules.Count == 0) _dynamicCapsules.Remove(key); else _dynamicCapsules[key] = geometry._capsules.ToArray();
    }
    /// <summary>Opaque world-layer body, such as NpcCharacter's original standing
    /// capsule. This carries no damageable entity identity or reward semantics.</summary>
    public void AddCapsule(Vector3 foot, float radius, float height)
    {
        if (!float.IsFinite(foot.x) || !float.IsFinite(foot.y) || !float.IsFinite(foot.z)
            || !float.IsFinite(radius) || radius <= 0 || !float.IsFinite(height) || height < radius * 2)
            throw new ArgumentException("Invalid world capsule dimensions or position");
        _capsules.Add(new(foot, radius, height));
    }
    public void AddMesh(Vector3[] vertices, int[] indices, int destructible = -1, bool terrain = false, int resource = -1, uint vehicle = 0, uint deployable = 0, string bodyPart = null, bool thin = false, bool water = false)
    {
        if (indices.Length % 3 != 0) throw new ArgumentException("Triangle indices must be triples");
        if (indices.Any(i => i < 0 || i >= vertices.Length)) throw new ArgumentException("Triangle index exceeds vertices");
        for (int n = 0; n < indices.Length; n += 3)
        {
            var triangle = new Triangle(vertices[indices[n]], vertices[indices[n + 1]], vertices[indices[n + 2]], destructible, resource, vehicle, deployable, bodyPart, thin, water);
            _triangles.Add(triangle);
            if (terrain) _terrainTriangles.Add(triangle);
        }
    }
    public void AddTerrain(RegionData data, double originX = 0, double originZ = 0) => AddHeightfield((float)(data.Coord.MinX - originX), (float)(data.Coord.MinZ - originZ), data.Cells, data.Spacing, data.Heights, data.Holes);
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
        bool found = RayDetailed(from, to, out var result); hit = result.Point; destructible = result.DestructibleIndex; return found;
    }
    bool RayContact(Vector3 from,Vector3 to,out Vector3 point,out Triangle triangle,out Vector3 normal,out string dynamicKey,bool worldOnly=false,uint excludedVehicle=0)
    {
        float nearest=1f;triangle=default;bool found=false;dynamicKey=null;
        Visit(_root,from,to-from,ref nearest,ref triangle,ref found,worldOnly,excludedVehicle);
        foreach(var pair in _dynamic)
            if(Visit(pair.Value,from,to-from,ref nearest,ref triangle,ref found,worldOnly,excludedVehicle))dynamicKey=pair.Key;
        normal=Vector3.Cross(triangle.B-triangle.A,triangle.C-triangle.A).normalized;
        if(Vector3.Dot(normal,to-from)>0)normal=-normal;
        float length=(to-from).magnitude;
        if(length>0)
        {
            var direction=(to-from)/length;
            if(VisitCapsules(_capsules,from,direction,length,ref nearest,ref normal))
            { found=true;dynamicKey=null;triangle=new(default,default,default,-1); }
            foreach(var group in _dynamicCapsules)
                if(excludedVehicle==0||group.Key!="vehicle:"+excludedVehicle)
                if(VisitCapsules(group.Value,from,direction,length,ref nearest,ref normal))
                { found=true;dynamicKey=group.Key;triangle=new(default,default,default,-1); }
        }
        point=found?from+(to-from)*nearest:to;return found;
    }
    static bool VisitCapsules(IReadOnlyList<Capsule> capsules,Vector3 from,Vector3 direction,float length,ref float nearest,ref Vector3 normal)
    {
        bool changed=false;
        for(int i=0;i<capsules.Count;i++)
        {
            var capsule=capsules[i];
            float distance=ZombieCombat.RayCapsule(from,direction,capsule.Foot,capsule.Radius,capsule.Height);
            if(distance<0||distance>nearest*length)continue;
            nearest=distance/length;changed=true;
            normal=ZombieCombat.CapsuleNormal(from+direction*distance,capsule.Foot,capsule.Radius,capsule.Height);
        }
        return changed;
    }
    /// <summary>Identity of the nearest host-owned moving collider. A static wall
    /// returns a null key; callers must not ray-test their fixture in isolation.</summary>
    public bool RayDynamicKey(Vector3 from,Vector3 to,out string key,uint excludedVehicle=0)
        => RayContact(from,to,out _,out _,out _,out key,excludedVehicle:excludedVehicle);
    public bool RayDetailed(Vector3 from, Vector3 to, out CombatWorldHit hit)
        => RayExcludingVehicle(0,from,to,out hit);
    public bool RayExcludingVehicle(uint excludedVehicle,Vector3 from,Vector3 to,out CombatWorldHit hit)
    {
        bool found=RayContact(from,to,out var point,out var triangle,out var normal,out _,excludedVehicle:excludedVehicle);
        hit=found?new(point,triangle.Destructible,triangle.Resource,triangle.Vehicle,triangle.Deployable,triangle.Part,
            normal:normal,thin:triangle.Thin,water:triangle.Water):new(to);
        return found;
    }
    public bool RayCosmetic(Vector3 from,Vector3 to,out Vector3 point,out bool thin,out bool water)
    {
        bool found=RayContact(from,to,out point,out var triangle,out _,out _);thin=triangle.Thin;water=triangle.Water;return found;
    }
    public bool BlastBlocked(Vector3 from, Vector3 to)
    {
        var up = new Vector3(0, .8f, 0); from += up; to += up;
        return RayContact(from,to,out _,out _,out _,out _,true);
    }
    public float GroundHeight(float x, float z)
    {
        float t = 1f; Triangle triangle = default; bool hit = false;
        Visit(_terrainRoot, new(x, 10000, z), new(0, -20000, 0), ref t, ref triangle, ref hit);
        return hit ? 10000 - 20000 * t : 0;
    }
    bool Visit(Node node, Vector3 origin, Vector3 dir, ref float nearest, ref Triangle nearestTriangle, ref bool found, bool worldOnly = false,uint excludedVehicle=0)
    {
        if (node == null || !Box(node.Min, node.Max, origin, dir, nearest)) return false;
        if (node.Leaf == null)
        {
            bool left = Visit(node.Left, origin, dir, ref nearest, ref nearestTriangle, ref found, worldOnly,excludedVehicle);
            bool right = Visit(node.Right, origin, dir, ref nearest, ref nearestTriangle, ref found, worldOnly,excludedVehicle);
            return left || right;
        }
        bool changed = false;
        foreach (var triangle in node.Leaf)
        {
            if(excludedVehicle!=0&&triangle.Vehicle==excludedVehicle)continue;
            if (worldOnly && (triangle.Vehicle != 0 || triangle.Deployable != 0)) continue;
            if (triangle.Destructible >= 0 && DestructibleAlive?.Invoke(triangle.Destructible) == false) continue;
            if (triangle.Resource >= 0 && ResourceAlive?.Invoke(triangle.Resource) == false) continue;
            if (triangle.Vehicle != 0 && (VehicleExists?.Invoke(triangle.Vehicle) == false || VehiclePartSolid?.Invoke(triangle.Vehicle, triangle.Part) == false)) continue;
            if (triangle.Deployable != 0 && DeployableExists?.Invoke(triangle.Deployable) == false) continue;
            var edge1 = triangle.B - triangle.A; var edge2 = triangle.C - triangle.A;
            var p = Vector3.Cross(dir, edge2); float determinant = Vector3.Dot(edge1, p);
            if (MathF.Abs(determinant) < 1e-8f) continue;
            float inverse = 1f / determinant; var s = origin - triangle.A;
            float u = inverse * Vector3.Dot(s, p); if (u < -1e-6f || u > 1.000001f) continue;
            var q = Vector3.Cross(s, edge1); float v = inverse * Vector3.Dot(dir, q);
            if (v < -1e-6f || u + v > 1.000001f) continue;
            float t = inverse * Vector3.Dot(edge2, q);
            if (t < 0 || t > nearest) continue;
            nearest = t; nearestTriangle = triangle; found = true; changed = true;
        }
        return changed;
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
