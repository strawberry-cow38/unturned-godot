using UnturnedGodot;

namespace UnturnedBevy;

/// <summary>
/// Original finite-island terrain/layout stage. This is deliberately separate
/// from InfiniteTerrain. ProcIslandSpawn subsequently conforms roads and paints
/// the final ground; this intermediate layout is not yet a playable-world export.
/// </summary>
public sealed partial class FiniteIslandLayout
{
    // Original Terrain.CreateFlat dimensions, grid axes and normalized heights.
    // Proc +Z maps to world -Z. Keeping the source storage here avoids changing
    // arithmetic/order before the native consumer converts the final heightfield.
    readonly float[,] _grid;
    readonly int _gw, _gh;
    int _islandSeed;
    List<ProcIsland.Link> _islandLinks = new();
    List<ProcIsland.Connector> _islandConnectors = new();
    readonly List<ProcIsland.MonumentTile> _islandTiles = new();
    readonly List<ProcIsland.MonumentBuilding> _islandBuildings = new();
    List<ProcIsland.Route> _islandRoutes = new();
    List<ProcIsland.Route> _islandTrails = new();
    List<ProcIsland.Camp> _islandCamps = new();
    List<ProcIsland.Landmark> _islandLandmarks = new();

    public float[,] Grid => _grid;
    public int Width => _gw;
    public int Height => _gh;
    public int Seed => _islandSeed;
    public IReadOnlyList<ProcIsland.Link> Links => _islandLinks;
    public IReadOnlyList<ProcIsland.Connector> Connectors => _islandConnectors;
    public IReadOnlyList<ProcIsland.MonumentTile> Tiles => _islandTiles;
    public IReadOnlyList<ProcIsland.MonumentBuilding> Buildings => _islandBuildings;
    public IReadOnlyList<ProcIsland.Route> Routes => _islandRoutes;
    public IReadOnlyList<ProcIsland.Route> Trails => _islandTrails;
    public IReadOnlyList<ProcIsland.Camp> Camps => _islandCamps;
    public IReadOnlyList<ProcIsland.Landmark> Landmarks => _islandLandmarks;

    public FiniteIslandLayout(int tilesX = 3, int tilesZ = 3)
    {
        if (tilesX < 1 || tilesX > 8) throw new ArgumentOutOfRangeException(nameof(tilesX));
        if (tilesZ < 1 || tilesZ > 8) throw new ArgumentOutOfRangeException(nameof(tilesZ));
        _gw = checked(tilesX * 256 + 1);
        _gh = checked(tilesZ * 256 + 1);
        _grid = new float[_gw, _gh];
    }
}
