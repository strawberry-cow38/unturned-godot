using Godot;
using UnturnedSim;

namespace UnturnedBevy;

// No nodes, colliders or authority are created by these source-value adapters.
public sealed partial class BuildingPalettes(string contentRoot) { }

public sealed class BuildingTriangles
{
    public readonly List<Vector3> Vertices = new();
    public void AddVertex(Vector3 vertex) => Vertices.Add(vertex);
}
public abstract class BuildingShape { }
public sealed class BuildingBox : BuildingShape { public Vector3 Size; }
public sealed class BuildingHull : BuildingShape { public Vector3[] Points; }
public sealed class BuildingCollision
{
    public Vector3 Position;
    public BuildingShape Shape;
}
public sealed record BuildingGeometry(Vector3[] Front, Vector3[] Back, Vector3[] Trim,
    BuildingCollision[] Solids, BuildingCollision[] TrimSolids);

public sealed partial class BuildingSurface
{
    readonly BuildingPalettes WallMaterials;
    public Basis Basis;
    public Vector3 Position;
    Vector3 _rotationDegrees;
    public Vector3 RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            _rotationDegrees = value;
            Basis = Basis.FromEuler(new(Mathf.DegToRad(value.X), Mathf.DegToRad(value.Y), Mathf.DegToRad(value.Z)));
        }
    }
    public BuildingTransform GlobalTransform => new(Basis, Position);
    public Vector3 ToGlobal(Vector3 local) => GlobalTransform * local;
    public Vector3 ToLocal(Vector3 world) => Basis.Inverse() * (world - Position);
    public BuildingSurface(BuildingPalettes palettes) { WallMaterials = palettes; RotationDegrees = Vector3.Zero; }
    public BuildingSurface(WallPlan plan, BuildingPalettes palettes)
    {
        WallMaterials = palettes;
        Length = plan.Length; Height = plan.Height; Thickness = plan.Thickness;
        MaterialId = plan.Material; Kind = plan.Kind; Texel = plan.Texel;
        GableRise = plan.GableRise;
        InsetL0 = plan.InsetL0; InsetL1 = plan.InsetL1;
        InsetR0 = plan.InsetR0; InsetR1 = plan.InsetR1;
        MaterialIdBack = plan.MaterialBack; TexelBack = plan.TexelBack;
        Openings.AddRange(plan.Openings);
        Position = new(plan.X, plan.Y, plan.Z);
        RotationDegrees = new(plan.Pitch, plan.Yaw, 0);
    }
}

public readonly record struct BuildingTransform(Basis Basis, Vector3 Position)
{
    public static Vector3 operator *(BuildingTransform t, Vector3 v) => t.Basis * v + t.Position;
}
public readonly record struct BuildingAabb(Vector3 Position, Vector3 Size)
{
    public Vector3 End => Position + Size;
}
