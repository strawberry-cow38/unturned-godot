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
    readonly Dictionary<string,string> nameToGuid = new(StringComparer.Ordinal);
    readonly Dictionary<string,string> guidToName = new(StringComparer.Ordinal);
    public FiniteIslandObjects(string contentDirectory)
    {
        objectsDirectory = Path.Combine(contentDirectory, "objects");
        string catalog = Path.Combine(objectsDirectory, "guid_mesh.txt");
        if (!File.Exists(catalog)) return;
        foreach (var line in File.ReadLines(catalog))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            // EditorObjects.LoadCatalog keeps the FIRST alias in both directions.
            nameToGuid.TryAdd(p[1], p[0]);
            guidToName.TryAdd(p[0], p[1]);
        }
    }

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
