using Godot;

namespace UnturnedBevy;

public sealed partial class FiniteIslandSurface
{
    /// <summary>Exact source chunk arrays, including untouched edge normals,
    /// one-quad holes and one-metre river subdivision. Engine material and
    /// physics owners consume these arrays; no second terrain body is created.</summary>
    public sealed record ChunkData(int X, int Z, Vector3[] Positions, Vector3[] Normals,
        Vector2[] Uvs, Color[] Colors, int[] Indices);
    public int ChunkCountX => (_gw - 2) / CHUNK + 1;
    public int ChunkCountZ => (_gh - 2) / CHUNK + 1;
}
