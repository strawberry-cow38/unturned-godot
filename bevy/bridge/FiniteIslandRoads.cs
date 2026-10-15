using Godot;

namespace UnturnedBevy;

/// <summary>Original RoadField data and curve operations, with scene creation
/// omitted. SavePaths retains the source file format consumed by native roads.</summary>
public sealed partial class FiniteIslandRoads
{
    public FiniteIslandSurface Terr { get; }
    readonly List<RoadData> _roads = new();
    List<RoadMat> _mats;
    byte _pathsVersion = 6;
    public int RoadCount => _roads.Count;
    public int JointCount(int road) => road >= 0 && road < _roads.Count ? _roads[road].Joints.Count : 0;
    public Vector3 JointPos(int road, int joint) => _roads[road].Joints[joint].Vertex;

    public FiniteIslandRoads(FiniteIslandSurface surface, string environmentDirectory)
    {
        Terr = surface;
        _mats = ParseRoadsDat(Path.Combine(environmentDirectory, "Roads.dat"));
        if (_mats.Count <= 5)
            throw new InvalidDataException("Finite island roads require the original PEI road materials, including Trail row 5");
    }
}
