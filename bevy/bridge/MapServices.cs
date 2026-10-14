using System.Globalization;
using System.Numerics;
using System.Text.Json;
using UnturnedGodot;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

/// <summary>Engine-independent world registration from the original placement
/// manifest. Index reservation follows WorldBuilder; resource indexing follows
/// ResourceField. The original data declarations are extracted at build time.</summary>
public static class MapServices
{
    public sealed record Parsed(JsonElement Spec, ObjectMapping[] Objects, object[] Resources);
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
    }
    public static Parsed Parse(JsonElement request)
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
        string active = Main.ActiveHolidayNow();
        var rubble = DestructibleField.LoadCatalog();
        var mappings = new List<ObjectMapping>();
        var fixtures = new List<object>(); var containers = new List<object>(); var destructibles = new List<object>(); var taps = new List<object>();
        int index = 0, sourceIndex = -1;
        var bounds = new Dictionary<string, (float[] lo, float[] hi)>();
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
            if (WorldBuilder.ContainerShelf.TryGetValue(guid, out var cfg))
            {
                containerOrdinal = containers.Count;
                containers.Add(new { cfg.mesh, cfg.table, cfg.display, cfg.label, position = pos, yaw });
            }
            else
            {
                if (name is "Gas_Pump_0" or "Circuit_0")
                {
                    fixtureOrdinal = fixtures.Count;
                    fixtures.Add(new { defId = name == "Gas_Pump_0" ? DeployableDef.GasPump.Id : DeployableDef.GridSource.Id, position = pos, yaw });
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
                    if (b.lo != null) taps.Add(new { position = pos, rotation = new[] { F(p[4]), yaw, -F(p[6]) }, scale = new[] { F(p[7]), F(p[8]), F(p[9]) }, min = b.lo, max = b.hi });
                }
            }
            if (fixtureOrdinal != null || containerOrdinal != null || actualDestructible != null)
                mappings.Add(new() { SourceIndex = sourceIndex, Name = name, Position = pos, DestructibleIndex = actualDestructible,
                    EffectId = actualDestructible.HasValue ? scalar.EffectId : null, FixtureOrdinal = fixtureOrdinal, ContainerOrdinal = containerOrdinal });
        }
        var resources = new List<object>(); var resourceMetadata = new List<object>(); int resourceIndex = 0;
        string resDir = Path.Combine(root, "resources" + suffix.ToLowerInvariant()), resManifest = Path.Combine(resDir, "resources.txt");
        if (File.Exists(resManifest)) foreach (string line in File.ReadLines(resManifest))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || !int.TryParse(p[1], out _)) continue;
            if (p.Length >= 3 && p[2] != "NONE" && p[2] != active) continue;
            string name = p[0], bin = Path.Combine(resDir, name + ".bin");
            if (!File.Exists(bin)) continue;
            using var reader = new BinaryReader(File.OpenRead(bin));
            int count = reader.ReadInt32();
            if (count < 0 || count > 65535 || reader.BaseStream.Length != 4L + count * 36L) throw new InvalidDataException("Invalid resource instance table: " + bin);
            for (int n = 0; n < count; n++)
            {
                var v = Enumerable.Range(0, 9).Select(_ => reader.ReadSingle()).ToArray();
                if (v.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Non-finite resource placement");
                var position = new[] { v[0], v[1], -v[2] };
                if (ResourceField.IsTree(name))
                {
                    var matrix = PlacementMatrix(new[] { v[3], 180 - v[4], -v[5] }, v[6..9]);
                    float sy = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
                    position[1] -= ResourceField.TreeSink * (sy < .001f ? 1 : sy);
                }
                ushort reward = ResourceField.ForageReward(name);
                resources.Add(new { index = resourceIndex, name, instance = n, position, rewardItemId = reward });
                if (reward != 0) resourceMetadata.Add(new { index = resourceIndex, position, rewardItemId = reward, resetSeconds = ResourceField.ForageResetSeconds });
                resourceIndex++;
            }
        }
        var zones = DeadzoneMap.Load("deadzones" + suffix.ToLowerInvariant() + ".tsv").Select(z => new {
            center = new[] { z.Center.X, z.Center.Y, z.Center.Z }, halfExtent = new[] { z.HalfExtent.X, z.HalfExtent.Y, z.HalfExtent.Z },
            kind = z.Kind.ToString(), shape = z.Shape.ToString() });
        var spec = JsonSerializer.SerializeToElement(new { mapRoot = Text(request, "mapRoot"), resourceCount = resourceIndex, resources = resourceMetadata,
            destructibleCount = index, destructibles, containers, fixtures, taps, deadzones = zones });
        return new(spec, mappings.ToArray(), resources.ToArray());
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
}
