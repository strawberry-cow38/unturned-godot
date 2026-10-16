using Godot;

namespace UnturnedBevy;

/// <summary>Value sink for the original SpawnVehicles scene boundary. Only the
/// constructor/parenting change; the source method sets these exact poses.</summary>
public sealed class FiniteIslandVehicle(string kind, int variant)
{
    public string Kind { get; } = kind;
    public int Variant { get; } = variant;
    public Vector3 GlobalPosition { get; set; }
    public Vector3 RotationDegrees { get; set; }
}

/// <summary>Ephemeral EditorPlayMode population handoff. Only fresh generation
/// has these source lists; opening saved terrain never reconstructs them.</summary>
public sealed record FiniteIslandPopulation(float[][] Zombies,
    FiniteIslandPopulation.LootPoint[] Loot, float[][] Animals,
    FiniteIslandPopulation.Vehicle[] Vehicles)
{
    public sealed record LootPoint(float[] Position, byte Table);
    public sealed record Vehicle(string Kind, int Variant, float[] Position, float Yaw);
    static float[] Position(Vector3 value) => [value.X,value.Y,value.Z];
    public static FiniteIslandPopulation From(FiniteIslandGeneration generated) => generated == null ? null :
        new(generated.Spawns.Zombies.Select(Position).ToArray(),
            generated.Spawns.Loot.Select(p => new LootPoint(Position(p.Pos),p.Table)).ToArray(),
            generated.Spawns.Animals.Select(Position).ToArray(),
            generated.Vehicles.Select(v => new Vehicle(v.Kind,v.Variant,Position(v.GlobalPosition),v.RotationDegrees.Y)).ToArray());
}
