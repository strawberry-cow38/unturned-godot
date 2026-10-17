using System.Globalization;
using System.Numerics;
using System.Text.Json;
using UnturnedGodot;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Engine-independent world registration from the original placement
/// manifest. Index reservation follows WorldBuilder; resource indexing follows
/// ResourceField. The original data declarations are extracted at build time.</summary>
public static partial class MapServices
{
    public sealed record Parsed(JsonElement Spec, ObjectMapping[] Objects, object[] Resources,
        IReadOnlyDictionary<int, VendMachine> Vendors = null);
    /// <summary>A placed <c>VendingMachine</c>: its tray (<c>DispensePoint</c>) in the absolute
    /// placement frame, and the one drink it sells.</summary>
    public sealed record VendMachine(float[] Tray, ushort Drink);
    /// <summary><c>VendingMachine.DispensePoint</c>: centred across the mesh's +Y front, clear of it by
    /// 0.35 m, 0.30 m up the mesh's Z, through the placement.</summary>
    internal static float[] VendTray((float[] lo, float[] hi) b, Matrix4x4 transform)
    {
        var at = Vector3.Transform(new(b.lo[0] + (b.hi[0] - b.lo[0]) * .5f, b.hi[1] + .35f, b.lo[2] + .30f), transform);
        return [at.X, at.Y, at.Z];
    }
    public sealed class ObjectMapping
    {
        public int SourceIndex { get; init; }
        public string Name { get; init; }
        public float[] Position { get; init; }
        public int? DestructibleIndex { get; init; }
        public int? EffectId { get; init; }
        public int? FixtureOrdinal { get; init; }
        public int? ContainerOrdinal { get; init; }
        public uint? FixtureNetId { get; set; }
        public uint? ContainerNetId { get; set; }
        public uint? ObjectDoorNetId { get; set; }
        public uint[] SeatNetIds { get; set; }
    }
    public static Parsed Parse(JsonElement request, string activeHoliday = null)
    {
        string root = Path.GetFullPath(Text(request, "contentRoot", Godot.ProjectSettings.ContentRoot));
        if (root != Path.GetFullPath(Godot.ProjectSettings.ContentRoot))
            throw new ArgumentException("Map contentRoot must match the catalog root passed to init");
        string map = Text(request, "mapName", "PEI");
        if (map.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) throw new ArgumentException("Invalid mapName");
        string suffix = map.Equals("PEI", StringComparison.OrdinalIgnoreCase) ? "" : "_" + map;
        string objectDir = Path.Combine(root, "objects"), manifest = Path.Combine(objectDir, "placements" + suffix + ".txt");
        if (!File.Exists(manifest)) throw new FileNotFoundException("Map placement manifest is missing", manifest);
        var names = ReadPairs(Path.Combine(objectDir, "guid_mesh.txt"));
        var holiday = ReadPairs(Path.Combine(objectDir, "holiday_props.txt"));
        string active = activeHoliday ?? Main.ActiveHolidayNow();
        var rubble = DestructibleField.LoadCatalog();
        var mappings = new List<ObjectMapping>();
        var fixtures = new List<object>(); var containers = new List<object>(); var destructibles = new List<object>(); var taps = new List<object>();
        var objectDoors = new List<object>(); var containerDoors = new List<object>(); var seats = new List<object>(); var npcs = new List<object>();
        var doorCatalog = WorldBuilder.LoadDoorCatalog(objectDir + Path.DirectorySeparatorChar);
        var seatCatalog = WorldBuilder.LoadSeatCatalog(objectDir + Path.DirectorySeparatorChar);
        int index = 0, sourceIndex = -1;
        var bounds = new Dictionary<string, (float[] lo, float[] hi)>();
        var vendors = new Dictionary<int, VendMachine>();
        foreach (string line in File.ReadLines(manifest))
        {
            sourceIndex++;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 10 || !names.TryGetValue(p[0], out string name)) continue;
            string guid = p[0].ToLowerInvariant();
            bool destructible = rubble.TryGetValue(guid, out var scalar);
            int reserved = destructible ? index++ : -1; // before the holiday/container branches, as in WorldBuilder
            if (holiday.TryGetValue(guid, out string restricted) && restricted != active) continue;
            var pos = new[] { F(p[1]), F(p[2]), -F(p[3]) };
            float yaw = 180 - F(p[5]);
            int? fixtureOrdinal = null, containerOrdinal = null, actualDestructible = null;
            uint? objectDoorId = null; var seatIds = new List<uint>();
            if (WorldBuilder.ContainerShelf.TryGetValue(guid, out var cfg))
            {
                containerOrdinal = containers.Count;
                containers.Add(new { cfg.mesh, cfg.table, cfg.display, cfg.label, position = pos, yaw });
                if (doorCatalog.TryGetValue(cfg.mesh, out var containerLeaves))
                {
                    var models = DoorModels(objectDir, cfg.mesh, containerLeaves);
                    if (models.Length > 0) containerDoors.Add(new { containerOrdinal = containerOrdinal.Value, sourceIndex,
                        name = cfg.mesh, position = pos, rotation = new[] { F(p[4]), yaw, -F(p[6]) }, scale = new[] { F(p[7]), F(p[8]), F(p[9]) },
                        open = false, lidRisesWhenLit = ContainerNetSync.IsCooker(cfg.mesh, out var cookKind) && cookKind == SDG.Unturned.ECookerKind.Barbecue, leaves = models });
                }
            }
            else
            {
                if (name is "Gas_Pump_0" or "Circuit_0")
                {
                    fixtureOrdinal = fixtures.Count;
                    fixtures.Add(new { defId = name == "Gas_Pump_0" ? DeployableDef.GasPump.Id : DeployableDef.GridSource.Id, position = pos, yaw });
                }
                if (File.Exists(Path.Combine(objectDir, name + ".obj")))
                {
                    var rotation = new[] { F(p[4]), yaw, -F(p[6]) }; var scale = new[] { F(p[7]), F(p[8]), F(p[9]) };
                    var transform = PlacementMatrix(rotation, scale); transform.Translation = new(pos[0], pos[1], pos[2]);
                    if (name is "Vendor_0" or "Vendor_1")
                    {
                        if (!bounds.TryGetValue(name, out var vb)) bounds[name] = vb = MeshBounds(Path.Combine(objectDir, name + ".obj"));
                        if (vb.lo != null) vendors[sourceIndex] = new(VendTray(vb, transform), name == "Vendor_1" ? VendSoda : VendCola);
                    }
                    if (doorCatalog.TryGetValue(name, out var leaves))
                    {
                        var models = DoorModels(objectDir, name, leaves);
                        if (models.Length > 0)
                        {
                            objectDoorId = checked((uint)objectDoors.Count + 1);
                            // Match ObjectDoor.ClosedLeafCentreWorld: a long prop's
                            // root can be far behind the leaf the player touches.
                            var lead = leaves.First(d => File.Exists(Path.Combine(objectDir, d.MeshFile)));
                            var leafBounds = MeshBounds(Path.Combine(objectDir, lead.MeshFile));
                            var centre = Vector3.Transform(new((leafBounds.lo[0] + leafBounds.hi[0]) * .5f,
                                (leafBounds.lo[1] + leafBounds.hi[1]) * .5f, (leafBounds.lo[2] + leafBounds.hi[2]) * .5f), transform);
                            objectDoors.Add(new { netId = objectDoorId.Value, sourceIndex, name, position = pos, rotation, scale,
                                interactionPosition = new[] { centre.X, centre.Y, centre.Z },
                                open = leaves.First(d => File.Exists(Path.Combine(objectDir, d.MeshFile))).DefaultOpen, leaves = models });
                        }
                    }
                    if (seatCatalog.TryGetValue(name, out var anchors)) foreach (var seat in anchors)
                    {
                        var at = Vector3.Transform(new(seat.Pos.X, seat.Pos.Y, seat.Pos.Z), transform);
                        var forward = Vector3.TransformNormal(new(seat.Face.X, seat.Face.Y, seat.Face.Z), transform); forward.Y = 0;
                        if (forward.LengthSquared() < .0001f)
                        {
                            forward = new(-transform.M31, 0, -transform.M33);
                            if (forward.LengthSquared() < .0001f) forward = new(0, 0, -1);
                        }
                        forward = Vector3.Normalize(forward);
                        uint seatId = checked((uint)seats.Count + 1); seatIds.Add(seatId);
                        seats.Add(new { netId = seatId, sourceIndex, name, position = new[] { at.X, at.Y, at.Z },
                            face = new[] { forward.X, forward.Y, forward.Z }, groundY = pos[1] });
                    }
                }
                // A missing source mesh never acquired a body in WorldBuilder.
                if (destructible && File.Exists(Path.Combine(objectDir, name + ".obj")))
                {
                    actualDestructible = reserved;
                    DestructibleField.Drops.TryGetValue(guid, out var drop);
                    destructibles.Add(new { index = reserved, health = scalar.Health, resetSeconds = scalar.ResetTicks * .02,
                        position = pos, dropItemId = drop.Item, dropMin = drop.Min, dropMax = drop.Max });
                }
                if (name is "Counter_1" or "Counter_3" or "Tub_0")
                {
                    if (!bounds.TryGetValue(name, out var b)) bounds[name] = b = MeshBounds(Path.Combine(objectDir, name + ".obj"));
                    // WaterTap.LabelFor: a bathtub says so, every counter sink is a "Sink".
                    if (b.lo != null) taps.Add(new { position = pos, rotation = new[] { F(p[4]), yaw, -F(p[6]) }, scale = new[] { F(p[7]), F(p[8]), F(p[9]) }, min = b.lo, max = b.hi, label = name == "Tub_0" ? "Bathtub" : "Sink" });
                }
            }
            if (fixtureOrdinal != null || containerOrdinal != null || actualDestructible != null || objectDoorId != null || seatIds.Count > 0)
                mappings.Add(new() { SourceIndex = sourceIndex, Name = name, Position = pos, DestructibleIndex = actualDestructible,
                    EffectId = actualDestructible.HasValue ? scalar.EffectId : null, FixtureOrdinal = fixtureOrdinal, ContainerOrdinal = containerOrdinal,
                    ObjectDoorNetId = objectDoorId, SeatNetIds = seatIds.Count > 0 ? seatIds.ToArray() : null });
        }
        var resourceData = ParseResources(Path.Combine(root, "resources" + suffix.ToLowerInvariant()), active);
        var resources = resourceData.Mappings;
        var resourceMetadata = resourceData.Metadata;
        int resourceIndex = resourceData.Count;
        // Editor-authored NPC coordinates are already in the engine frame (no Z flip).
        string npcFile = NpcCatalog.PlacementPath(map);
        if (File.Exists(npcFile)) foreach (string line in File.ReadLines(npcFile))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || NpcCatalog.CharacterByKey(parts[0]) is not { } character) continue;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;
            float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw);
            npcs.Add(new { key = parts[0], position = new[] { x, y, z }, yaw, character = NpcMetadata.Character(character) });
        }
        var zones = DeadzoneMap.Load("deadzones" + suffix.ToLowerInvariant() + ".tsv").Select(z => new {
            center = new[] { z.Center.X, z.Center.Y, z.Center.Z }, halfExtent = new[] { z.HalfExtent.X, z.HalfExtent.Y, z.HalfExtent.Z },
            kind = z.Kind.ToString(), shape = z.Shape.ToString() });
        var spec = JsonSerializer.SerializeToElement(new { mapRoot = Text(request, "mapRoot"), resourceCount = resourceIndex, resources = resourceMetadata,
            destructibleCount = index, destructibles, containers, fixtures, taps, objectDoors, containerDoors, seats, npcs, deadzones = zones });
        return new(spec, mappings.ToArray(), resources.ToArray(), vendors);
    }
    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    static Dictionary<string, string> ReadPairs(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path)) foreach (string line in File.ReadLines(path))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (p.Length >= 2) d[p[0]] = p[1];
        }
        return d;
    }
    static (float[] lo, float[] hi) MeshBounds(string path)
    {
        if (!File.Exists(path)) return default;
        var lo = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var hi = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        foreach (string line in File.ReadLines(path)) if (line.StartsWith("v "))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) continue;
            for (int n = 0; n < 3; n++) { float f = F(p[n + 1]); lo[n] = Math.Min(lo[n], f); hi[n] = Math.Max(hi[n], f); }
        }
        return float.IsFinite(lo[0]) ? (lo, hi) : default;
    }
    // System.Numerics uses row vectors; transpose the original Godot Y*X*Z basis
    // and apply Basis.Scaled's world-axis scale, preserving non-uniform instances.
    internal static Matrix4x4 PlacementMatrix(float[] rotation, float[] scale) =>
        Matrix4x4.CreateRotationZ(rotation[2] * MathF.PI / 180) * Matrix4x4.CreateRotationX(rotation[0] * MathF.PI / 180)
        * Matrix4x4.CreateRotationY(rotation[1] * MathF.PI / 180) * Matrix4x4.CreateScale(scale[0], scale[1], scale[2]);
    static object[] DoorModels(string objectDir, string name, IEnumerable<WorldBuilder.DoorCatalogEntry> leaves)
    {
        return leaves.Where(d => File.Exists(Path.Combine(objectDir, d.MeshFile))).Select(d => {
                            string curveBase = d.MeshFile.EndsWith("_door.obj") ? d.MeshFile[..^"_door.obj".Length] : name;
                            return new { mesh = "objects/" + d.MeshFile, pivot = new[] { d.Pivot.X, d.Pivot.Y, d.Pivot.Z },
                                axis = new[] { d.Axis.X, d.Axis.Y, d.Axis.Z }, angleDegrees = d.AngleDeg,
                                durationSeconds = d.DurationSec, defaultOpen = d.DefaultOpen, sound = d.Sound,
                                openCurve = WorldBuilder.LoadDoorCurve(objectDir + Path.DirectorySeparatorChar, curveBase, "open")?.Select(k => new[] { k.X, k.Y }).ToArray(),
                                closeCurve = WorldBuilder.LoadDoorCurve(objectDir + Path.DirectorySeparatorChar, curveBase, "close")?.Select(k => new[] { k.X, k.Y }).ToArray() };
                        }).ToArray();
    }

}
