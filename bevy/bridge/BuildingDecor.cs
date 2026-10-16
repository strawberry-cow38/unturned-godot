using Godot;

namespace UnturnedBevy;

/// <summary>Raw original RuntimeDecorGenerators output, before Godot's packed
/// normal/colour storage. Geometry remains clockwise in the source Y-up frame.</summary>
public sealed record BuildingDecorMesh(Vector3[] Vertices, Vector3[] Normals,
    Vector2[] Uv, Color[] Colors, BuildingDecorMaterial Material);

public sealed class BuildingDecorMaterial
{
    public bool VertexColorUseAsAlbedo;
    public float Roughness;
    /// <summary>Source CullMode.Disabled: curtain and rug cloth is double-sided.</summary>
    public bool DoubleSided;
    /// <summary>Source TextureFilter.Nearest on the generated rug texture.</summary>
    public bool NearestFilter;
    public BuildingDecorPixels AlbedoTexture;
}

public sealed class BuildingDecorPixels(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bytes { get; } = new byte[checked(width * height * 4)];
    public void SetPixel(int x, int y, Color color)
    {
        if ((uint)x >= Width || (uint)y >= Height) throw new ArgumentOutOfRangeException();
        int at = checked((y * Width + x) * 4);
        Bytes[at] = ChartPixels.Channel(color.R);
        Bytes[at + 1] = ChartPixels.Channel(color.G);
        Bytes[at + 2] = ChartPixels.Channel(color.B);
        Bytes[at + 3] = ChartPixels.Channel(color.A);
    }
}
