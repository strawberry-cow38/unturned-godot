using Godot;

namespace UnturnedBevy;

/// <summary>Original Main.BuildEditorNew data-stage ordering, with source scene
/// consumers writing value records. Foliage, chart, map persistence and native
/// materialization follow this stage; this alone does not start a play session.</summary>
public sealed class FiniteIslandGeneration
{
    // ProcIsland's town pads and ProcIslandSpawn's diagnostics are source static
    // scratch. Keep the complete dependent sequence together, including any
    // future generated-vehicle chooser that also reads those pads.
    static readonly object generationGate = new();
    public FiniteIslandLayout Layout { get; private init; }
    public FiniteIslandSurface Surface { get; private init; }
    public FiniteIslandRoads Roads { get; private init; }
    public FiniteIslandObjects Objects { get; private init; }
    public (int roads, int buildings, int missing) Spawned { get; private init; }
    public IReadOnlyList<(Vector3 Pos, float Yaw)> Players { get; private init; }
    public (List<Vector3> Zombies, List<(Vector3 Pos, byte Table)> Loot,
        List<Vector3> Animals) Spawns { get; private init; }
    public IReadOnlyList<FiniteIslandVehicle> Vehicles { get; private init; }

    public static FiniteIslandGeneration Generate(string contentDirectory,
        string environmentDirectory, int tilesX, int tilesZ, int seed, bool lakes = false)
    {
        lock (generationGate)
        {
            var layout = new FiniteIslandLayout(tilesX, tilesZ);
            layout.GenerateIsland(seed, lakes);
            var surface = new FiniteIslandSurface(layout);
            var objects = new FiniteIslandObjects(contentDirectory);
            var spawned = FiniteIslandPaths.Spawn(surface, objects);
            surface.PaintBelowHeight(surface.SeaLevelY + 4f, 5);
            surface.PaintSteeperThan(FiniteIslandPaths.SteepRise, 0);
            FiniteIslandPaths.PaintGroundwork(surface);
            var players = FiniteIslandPaths.ChoosePlayerSpawns(surface, seed);
            FiniteIslandPaths.SpawnTownFurniture(surface, objects);
            var roads = new FiniteIslandRoads(surface, environmentDirectory);
            FiniteIslandPaths.SpawnRoutes(surface, roads);
            FiniteIslandPaths.SpawnRoadside(surface, objects);
            FiniteIslandPaths.SpawnTrails(surface, roads);
            FiniteIslandPaths.SpawnCamps(surface, objects);
            FiniteIslandPaths.SpawnLandmarks(surface, objects);
            var spawns = FiniteIslandPaths.GenerateSpawnTables(surface, seed);
            var vehicles = new List<FiniteIslandVehicle>();
            FiniteIslandPaths.SpawnVehicles(surface, vehicles, seed);
            return new FiniteIslandGeneration { Layout = layout, Surface = surface,
                Roads = roads, Objects = objects, Spawned = spawned, Players = players,
                Spawns = spawns, Vehicles = vehicles };
        }
    }
}
