using Godot;

namespace UnturnedBevy;

public static partial class FiniteIslandChart
{
    /// <summary>Original RGB8 pixels and labels. The native image encoder writes
    /// ImageName once and reuses that baked file on reopen, as ProcIslandMap.Rebind
    /// requires. Rebuilding without the original route/town lists loses overlays.</summary>
    public sealed record ChartData(int Width, int Height, byte[] Rgb, string Nodes,
        Vector2 Centre, float Size, string ImageName, string NodesName);
    /// <summary>Existing baked art and its original projection. Null NodesName
    /// preserves the caller's previous nodes when the original sidecar is absent.</summary>
    public sealed record ChartBinding(Vector2 Centre, float Size, string ImageName, string NodesName);
}

// Image.SetPixel's RGB8 branch in Godot4.6 core/io/image.cpp. The color component
// is float, but 255.0 promotes the multiply to double before truncation. Keeping
// that distinction matters at exact byte boundaries. No gamma conversion occurs.
internal sealed class ChartPixels(int width, int height)
{
    public byte[] Bytes { get; } = new byte[checked(width * height * 3)];
    public void SetPixel(int x, int y, Color color)
    {
        if ((uint)x >= width || (uint)y >= height) throw new ArgumentOutOfRangeException();
        int at = checked((y * width + x) * 3);
        Bytes[at] = Channel(color.R);
        Bytes[at+1] = Channel(color.G);
        Bytes[at+2] = Channel(color.B);
    }
    static byte Channel(float value) => (byte)Math.Clamp(value * 255.0, 0.0, 255.0);
}
