using System.Globalization;
using Godot;
using UnturnedGodot;
using UnturnedSim;

namespace UnturnedBevy;

/// <summary>Original opening construction as values. No door/pane authority is
/// created here; wall/opening indices remain the caller's identity.</summary>
public sealed partial class BuildingOpenings(string contentRoot)
{
    Dictionary<string, List<WorldBuilder.DoorCatalogEntry>> _cat;
    Dictionary<string, List<(Vector3 Pivot, Vector3 Axis, float Angle, float Dur)>> _wood;
    readonly Dictionary<string, BuildingDoorMesh> meshes = new(StringComparer.Ordinal);
    BuildingDoorMesh Mesh(string path)
    {
        if (!meshes.TryGetValue(path, out var mesh))
            meshes[path] = mesh = File.Exists(path) ? BuildingDoorMesh.Load(path) : null;
        return mesh;
    }
    static BuildingDoorMesh Scaled(BuildingDoorMesh mesh, Vector3 scale) => mesh.Scaled(scale);
    public BuildingDoorHost Door(BuildingSurface wall, WallOpening opening)
    {
        if (!opening.HasDoor) return null;
        var host = SpawnProp(opening.DoorProp, BuildingSurface.ClearOpeningSize(opening),
            opening.DoorHingeAtStart != opening.DoorOpensFront);
        if (host == null) return null;
        foreach (var leaf in host.Leaves)
        {
            leaf.Action = opening.DoorAction;
            if (opening.DoorAction == DoorAction.Pocket)
            {
                var box = leaf.Mesh.GetAabb();
                float direction = leaf.Pivot.X >= box.GetCenter().X ? 1f : -1f;
                leaf.Slide = new(direction * box.Size.X, 0, 0);
                leaf.ColliderSize = new(leaf.ColliderSize.X, Mathf.Max(leaf.ColliderSize.Y, wall.Thickness + .02f), leaf.ColliderSize.Z);
            }
        }
        PlaceDoor(wall, host, opening);
        // Source WallSurface.RebuildDoors does not read DoorOpen. New leaves
        // always start closed; that persisted token must not invent a new pose.
        return host;
    }
    // WallSurface.RebuildGlass's source values, kept separate from door leaves.
    // Original panes are bit6/mask0, use the RAW opening rather than trim clear size.
    public static BuildingPane Pane(BuildingSurface wall, WallOpening opening)
    {
        if (!opening.HasGlass) return null;
        int tint = opening.GlassTint;
        var color = tint == 0 ? new Color(.62f,.73f,.78f,.26f)
            : new Color(((tint >> 16) & 255)/255f,((tint >> 8) & 255)/255f,(tint & 255)/255f,.26f);
        // Godot BoxMesh emits the Z faces first, then X, then Y. The source
        // DominantFaceAxis area vote therefore retains Z on equal areas.
        // Mesh.GetFaces passes through TriangleMesh's 0.0001 coordinate
        // snap. A .03999-wide pane therefore ties the .04 thickness in the
        // actual source vote; raw dimensions would incorrectly select X.
        static float FaceSpan(float size) {
            double step=(double).0001f;float half=size*.5f;
            float hi=(float)(Math.Floor(half/step+.5)*step);
            float lo=(float)(Math.Floor(-half/step+.5)*step);
            return hi-lo;
        }
        float x=FaceSpan(opening.Width),y=FaceSpan(opening.Height),z=FaceSpan(.04f);
        float area=x*y;var axis=Vector3.Back;
        if (z*y>area) {area=z*y;axis=Vector3.Right;}
        if (z*x>area) {area=z*x;axis=Vector3.Up;}
        if(area<1e-7f)axis=Vector3.Zero;
        return new(new(opening.U+opening.Width*.5f,opening.V+opening.Height*.5f,0),
            new(opening.Width,opening.Height,.04f),axis,color,
            Mathf.Max(1f,opening.GlassHp>0?opening.GlassHp:1f),opening.GlassIndestructible,
            wall.Thickness < (WallOpenings.DefaultThickness+WallOpenings.InteriorThickness)*.5f);
    }

}

public sealed class BuildingDoorHost(string name, BuildingDoorLeaf[] leaves)
{
    public string Name { get; } = name;
    public BuildingDoorLeaf[] Leaves { get; } = leaves;
    public BuildingTransform Transform { get; set; } = new(Basis.Identity, Vector3.Zero);
    public Vector3 Position { get => Transform.Position; set => Transform = Transform with { Position = value }; }
}

public sealed class BuildingDoorLeaf
{
    public string MeshFile, Sound;
    public BuildingDoorMesh Mesh;
    public Vector3 Scale, Pivot, Axis, ColliderCenter, ColliderSize, Slide;
    public BuildingTransform Body;
    public float Angle, Duration;
    public List<Vector2> OpenCurve, CloseCurve;
    public DoorAction Action;
    public static BuildingDoorLeaf Create(string meshFile, BuildingDoorMesh mesh, Vector3 pivot, Vector3 axis,
        float angle, float duration, BuildingTransform body, List<Vector2> open, List<Vector2> close, string sound)
    {
        var bounds = mesh.GetAabb();
        var size = bounds.Size;
        return new() { MeshFile=meshFile,Mesh=mesh,Scale=mesh.Scale,Pivot=pivot,
            Axis=axis.LengthSquared()>1e-6f?axis.Normalized():Vector3.Back,
            Angle=angle,Duration=duration,Body=body,OpenCurve=open,CloseCurve=close,Sound=sound,
            ColliderCenter=bounds.GetCenter(),
            ColliderSize=new(Mathf.Max(size.X,.15f),Mathf.Max(size.Y,.15f),Mathf.Max(size.Z,.15f)) };
    }
    public BuildingTransform ClosedMesh(BuildingTransform parent) =>
        ((parent * Body) * new BuildingTransform(Basis.Identity, Pivot)) * new BuildingTransform(Basis.Identity, -Pivot);
}

/// <summary>Original ObjMesh referenced triangle vertices, before engine storage.
/// This differs deliberately from MapServices' raw-v bounds for arbitrary props:
/// fitted leaf bounds must exclude unreferenced vertices and preserve degenerate
/// face vertices exactly as ArrayMesh does.</summary>
public sealed partial class BuildingDoorMesh(Vector3[] vertices, Vector3 scale)
{
    public Vector3[] Vertices { get; } = vertices;
    public Vector3 Scale { get; } = scale;
    public BuildingAabb GetAabb()
    {
        var lo = Vertices[0]; var hi = lo;
        foreach (var p in Vertices)
        {
            lo = new(Mathf.Min(lo.X,p.X),Mathf.Min(lo.Y,p.Y),Mathf.Min(lo.Z,p.Z));
            hi = new(Mathf.Max(hi.X,p.X),Mathf.Max(hi.Y,p.Y),Mathf.Max(hi.Z,p.Z));
        }
        return new(lo,hi-lo);
    }
    public BuildingDoorMesh Scaled(Vector3 scale) => new(Vertices.Select(v =>
        new Vector3(v.X*scale.X,v.Y*scale.Y,v.Z*scale.Z)).ToArray(),scale);
    const int CONV = 1; // original shipping ObjMesh default; diagnostic override is not authored content
    static float F(string text) => float.Parse(text,CultureInfo.InvariantCulture);
}

public sealed record BuildingPane(Vector3 Position,Vector3 Size,Vector3 Axis,Color Color,float Health,bool Indestructible,bool Covered);
