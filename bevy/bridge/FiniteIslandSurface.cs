using Godot;
using UnturnedGodot;

namespace UnturnedBevy;

/// <summary>Data backing for the original finite Terrain surface operations.
/// Saved authored river recipes affect the same sampled surface as generation.
/// Mesh/water rebuilding is left to the native renderer.</summary>
public sealed partial class FiniteIslandSurface
{
    readonly float[,] _grid;
    readonly byte[,] _dom;
    readonly int _gw, _gh, _dw, _dh;
    const float _bx = 0, _bz = 0;
    const float TILE_HEIGHT = 2048f, UNIT = 4f;
    public float[,] Grid => _grid;
    public byte[,] Layers => _dom;
    public int Width => _gw;
    public int Height => _gh;
    public int IslandSeed { get; }
    public bool HasWater { get; }
    public float SeaLevelY { get; }
    public IReadOnlyList<ProcIsland.Link> IslandLinks { get; }
    public IReadOnlyList<ProcIsland.MonumentTile> IslandTiles { get; }
    public IReadOnlyList<ProcIsland.MonumentBuilding> IslandBuildings { get; }
    public List<ProcIsland.Route> IslandRoutes { get; }
    public IReadOnlyList<ProcIsland.Route> IslandTrails { get; }
    public IReadOnlyList<ProcIsland.Camp> IslandCamps { get; }
    public IReadOnlyList<ProcIsland.Landmark> IslandLandmarks { get; }

    public FiniteIslandSurface(FiniteIslandLayout layout, bool water = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _grid = (float[,])layout.Grid.Clone();
        _gw = layout.Width; _gh = layout.Height;
        _dw = _gw - 1; _dh = _gh - 1;
        _dom = new byte[_dw, _dh];
        for (int x=0; x<_dw; x++) for (int z=0; z<_dh; z++) _dom[x,z] = 2;
        IslandSeed = layout.Seed;
        HasWater = water; SeaLevelY = 25.6f;
        IslandLinks = layout.Links; IslandTiles = layout.Tiles;
        IslandBuildings = layout.Buildings;
        // SpawnRoutes retires only rejected optional junction routes. Keep the
        // initial source layout inspectable rather than mutating its list.
        IslandRoutes = layout.Routes.ToList(); IslandTrails = layout.Trails;
        IslandCamps = layout.Camps; IslandLandmarks = layout.Landmarks;
    }

    public (float MinX, float MaxX, float MinZ, float MaxZ) WorldBoundsXZ() =>
        (_bx, _bx + (_gw - 1) * UNIT, -(_bz + (_gh - 1) * UNIT), -_bz);
}
