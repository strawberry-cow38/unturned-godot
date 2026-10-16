using Godot;
using UnturnedSim;

namespace UnturnedBevy;

// Source EditorBuildings data assembly only. Does not create a map, authority,
// or render nodes. Generated stage coordinates are retained, including Y=2000;
// the original bake path, separately, subtracts StageOrigin for a placed prop.
public sealed partial class BuildingScene(string contentRoot)
{
    readonly BuildingPalettes WallMaterials = new(contentRoot);
    readonly List<BuildingSurface> _walls = new();
    readonly List<BuildingRamp> _stairRamps = new();
    public IReadOnlyList<BuildingSurface> Walls => _walls;
    public IReadOnlyList<BuildingRamp> Ramps => _stairRamps;
    public int ActiveMaterial;
    public BuildingKind? DesignatedKind;
    public readonly List<RoomDesignation> Designations = new();
    public readonly List<DoorDesignation> DoorMarks = new();
    public readonly List<PropDesignation> Furniture = new();
    public IReadOnlyList<WallPlan> Plans() => _walls.Select(PlanOf).ToArray();
    void RemoveWall(BuildingSurface wall) => _walls.Remove(wall);
    // Values have no engine lifetime; the source iteration lists contain only
    // live surfaces, and RemoveWall removes that same identity from the list.
    static bool IsInstanceValid(BuildingSurface wall) => wall != null;
}

// Source stair ramp: world rotation in radians, box size, no rendered surface.
// WallSave stores visible treads, not these transient ramps. Loading a saved
// layout into a fresh scene therefore does not reconstruct ramps implicitly.
public sealed record BuildingRamp(Vector3 Position, Vector3 Rotation, Vector3 Size)
{
    public Basis Basis => Basis.FromEuler(Rotation);
}
