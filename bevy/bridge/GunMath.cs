// Value-only adapters let original parsers and pure harvesting methods compile unchanged without a Godot SDK, native library or runtime.
namespace Godot;
public static class Mathf
{
    public static int CeilToInt(float v) => (int)MathF.Ceiling(v);
    public static int FloorToInt(float v) => (int)MathF.Floor(v);
    public static int RoundToInt(float v) => (int)MathF.Round(v);
    public static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
    public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
    public static int Max(int a, int b) => Math.Max(a, b);
    public static float Max(float a, float b) => Math.Max(a, b);
    public static float Cos(float v) => MathF.Cos(v);
    public static float Sin(float v) => MathF.Sin(v);
    public static float Min(float a, float b) => Math.Min(a, b);
    public static float Sqrt(float a) => MathF.Sqrt(a);
}
public struct Vector2(float x, float y)
{
    public float X = x, Y = y;
    public static Vector2 Right => new(1, 0);
    public readonly Vector2 Normalized()
    {
        float length = MathF.Sqrt(X * X + Y * Y);
        return length > 0 ? new(X / length, Y / length) : default;
    }
}
