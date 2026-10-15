using System.Numerics;
using System.Text.Json;
using UnturnedGodot;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public static partial class MapServices
{
    // EditorObjects.Place calls SmartProps.AttachEditor, not the retail
    // WorldBuilder switch. In particular containers are keyed by mesh name,
    // doors are independent children, and no prop seat/rubble is attached.
    internal static Parsed ParseFinite(JsonElement request, string activeHoliday)
    {
        string content = Path.GetFullPath(Text(request, "contentRoot", Godot.ProjectSettings.ContentRoot));
        if (content != Path.GetFullPath(Godot.ProjectSettings.ContentRoot))
            throw new ArgumentException("Map contentRoot must match the catalog root passed to init");
        var files = FiniteIslandFiles.Open(content, Text(request, "directory"));
        if (Text(request, "mapName", "Island_" + files.Info.Name) != "Island_" + files.Info.Name)
            throw new ArgumentException("Finite services must use the selected source map");
        string objects = Path.Combine(content, "objects");
        var doors = WorldBuilder.LoadDoorCatalog(objects + Path.DirectorySeparatorChar);
        var mappings = new List<ObjectMapping>();
        var containers = new List<object>();
        var objectDoors = new List<object>();
        // Consume the same source-parser handoff as native rendering. Fresh
        // generation retains full live bases; reopen uses quantized saved ones.
        using var placements = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(files.Root, ".native", "props.json")));
        var seen = new HashSet<int>();
        foreach (var p in placements.RootElement.EnumerateArray())
        {
            int sourceIndex = Int(p, "sourceIndex", -1);
            string name = Text(p, "name");
            if (sourceIndex < 0 || !seen.Add(sourceIndex) || string.IsNullOrEmpty(name)
                || name.IndexOfAny(['/', '\\']) >= 0)
                throw new InvalidDataException("Invalid finite source placement identity");
            var position = Vec(p, "position");
            var columns = p.GetProperty("basis").EnumerateArray().Select(v => Vec(v)).ToArray();
            if (columns.Length != 3)
                throw new InvalidDataException("Invalid finite source placement basis");
            var x = columns[0]; var y = columns[1]; var z = columns[2];
            // Godot basis columns become System.Numerics row-vector rows.
            var transform = new Matrix4x4(x.x,x.y,x.z,0, y.x,y.y,y.z,0,
                z.x,z.y,z.z,0, position.x,position.y,position.z,1);
            float yaw = Godot.Mathf.RadToDeg(Godot.Mathf.Atan2(-x.z, x.x));
            var pos = Array(position);
            int? containerOrdinal = null;
            uint? objectDoorId = null;
            if (WorldBuilder.ContainerForMesh(name, out int table, out bool display, out string label))
            {
                containerOrdinal = containers.Count;
                containers.Add(new { mesh = name, table, display, label = label ?? "Container", position = pos, yaw });
            }
            if (doors.TryGetValue(name, out var leaves))
            {
                var models = DoorModels(objects, name, leaves);
                if (models.Length > 0)
                {
                    var lead = leaves.First(d => File.Exists(Path.Combine(objects, d.MeshFile)));
                    var bounds = MeshBounds(Path.Combine(objects, lead.MeshFile));
                    var centre = Vector3.Transform(new Vector3((bounds.lo[0]+bounds.hi[0])*.5f,
                        (bounds.lo[1]+bounds.hi[1])*.5f, (bounds.lo[2]+bounds.hi[2])*.5f), transform);
                    objectDoorId = checked((uint)objectDoors.Count + 1);
                    objectDoors.Add(new { netId = objectDoorId.Value, sourceIndex, name, position = pos,
                        interactionPosition = new[] { centre.X, centre.Y, centre.Z },
                        open = lead.DefaultOpen, leaves = models });
                }
            }
            if (containerOrdinal.HasValue || objectDoorId.HasValue)
                mappings.Add(new() { SourceIndex = sourceIndex, Name = name, Position = pos,
                    ContainerOrdinal = containerOrdinal, ObjectDoorNetId = objectDoorId });
        }
        var resources = files.Info.Resources == null ? new ResourceData(0, [], [])
            : ParseResources(Path.Combine(files.Root, files.Info.Resources), activeHoliday);
        var spec = JsonSerializer.SerializeToElement(new {
            mapRoot = Text(request, "mapRoot"), resourceCount = resources.Count, resources = resources.Metadata,
            containers, objectDoors, fixtures = System.Array.Empty<object>(),
            containerDoors = System.Array.Empty<object>(), seats = System.Array.Empty<object>(),
            npcs = System.Array.Empty<object>(), deadzones = System.Array.Empty<object>()
        });
        return new(spec, mappings.ToArray(), resources.Mappings);
    }
}
