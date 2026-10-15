using System.Numerics;
using UnturnedGodot;

namespace UnturnedBevy;

public static partial class MapServices
{
    internal sealed record ResourceData(int Count, object[] Mappings, object[] Metadata);

    // Shared original ResourceField registration order and metadata. Finite
    // generated bins use the same format; only their source directory differs.
    internal static ResourceData ParseResources(string resDir, string active)
    {
        var bounds = new Dictionary<string, (float[] lo, float[] hi)>();
        var resources = new List<object>(); var resourceMetadata = new List<object>(); int resourceIndex = 0;
        string resManifest = Path.Combine(resDir, "resources.txt");
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
                float treeScaleY = 1, canopyScaleXZ = 1;
                bool tree = ResourceField.IsTree(name);
                if (tree)
                {
                    var matrix = PlacementMatrix(new[] { v[3], 180 - v[4], -v[5] }, v[6..9]);
                    float sy = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(); treeScaleY = sy;
                    canopyScaleXZ = Math.Max(new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(),new Vector3(matrix.M31, matrix.M32, matrix.M33).Length());
                    position[1] -= ResourceField.TreeSink * (sy < .001f ? 1 : sy);
                }
                ushort reward = ResourceField.ForageReward(name);
                resources.Add(new { index = resourceIndex, name, instance = n, position, rewardItemId = reward });
                if (tree || name.StartsWith("Metal"))
                {
                    float trunkLength = 8;
                    if (tree)
                    {
                        string cacheKey = resDir + "/" + name + "_debris";
                        if (!bounds.TryGetValue(cacheKey, out var debris))
                        {
                            for (int part = 0; part < 12; part++)
                            {
                                var next = MeshBounds(Path.Combine(resDir, name + "_debris_" + part + ".obj"));
                                if (next.lo == null) break;
                                if (debris.lo == null) debris = next;
                                else for (int axis = 0; axis < 3; axis++)
                                { debris.lo[axis] = Math.Min(debris.lo[axis], next.lo[axis]); debris.hi[axis] = Math.Max(debris.hi[axis], next.hi[axis]); }
                            }
                            bounds[cacheKey] = debris;
                        }
                        if (debris.lo != null) trunkLength = Math.Max(1, (debris.hi[1] - debris.lo[1]) * Math.Max(.01f, treeScaleY));
                    }
                    resourceMetadata.Add(new { index = resourceIndex, kind = tree ? "tree" : "ore", name, position, trunkLength, canopyScale = tree ? new[]{canopyScaleXZ,treeScaleY} : null });
                }
                if (reward != 0) resourceMetadata.Add(new { index = resourceIndex, position, rewardItemId = reward, resetSeconds = ResourceField.ForageResetSeconds });
                resourceIndex++;
            }
        }
        return new(resourceIndex, resources.ToArray(), resourceMetadata.ToArray());
    }
}
