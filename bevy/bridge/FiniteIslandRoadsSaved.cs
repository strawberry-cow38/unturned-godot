namespace UnturnedBevy;

public sealed partial class FiniteIslandRoads
{
    public void LoadPaths(string path)
    {
        // Parse first so a truncated file cannot replace the current road list
        // or its format version. The source parser updates both while reading.
        byte previousVersion = _pathsVersion;
        List<RoadData> roads;
        try { roads = ParsePathsDat(path); }
        catch { _pathsVersion = previousVersion; throw; }
        _roads.Clear();
        _roads.AddRange(roads);
    }
}
