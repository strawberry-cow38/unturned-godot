using Godot;

namespace UnturnedBevy;

/// <summary>Data sink for ProcIslandSpawn's original EditorObjects.Place calls.
/// Keeps source bases (including tilted/scaled boulders), names and material
/// variants. Presence follows EditorObjects.MeshFor's original OBJ lookup.</summary>
public sealed partial class FiniteIslandObjects
{
    public sealed record Placement(string Name, Vector3 Position, Basis Basis, string Material);
    readonly string objectsDirectory;
    readonly Dictionary<string, bool> available = new(StringComparer.Ordinal);
    readonly List<Placement> placements = new();
    public IReadOnlyList<Placement> Placements => placements;
    public FiniteIslandObjects(string contentDirectory) =>
        objectsDirectory = Path.Combine(contentDirectory, "objects");

    public Placement Place(string name, Vector3 position, Basis basis, string material = null)
    {
        if (!available.TryGetValue(name, out bool exists))
            available[name] = exists = File.Exists(Path.Combine(objectsDirectory, name + ".obj"));
        if (!exists) return null;
        var placement = new Placement(name, position, basis, material);
        placements.Add(placement);
        return placement;
    }
}
