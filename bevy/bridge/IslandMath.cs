// Value-only Godot 4.6 math needed by the unchanged finite island generator.
namespace Godot;

public static partial class Mathf
{
    public static float Pow(float x, float y) => MathF.Pow(x, y);
    public static float Acos(float x) => MathF.Acos(x);
    public static int Sign(float x) => Math.Sign(x);
    public static bool IsEqualApprox(float a, float b)
    {
        if (a == b) return true;
        return Math.Abs(a - b) < Math.Max(1e-6f, 1e-6f * Math.Abs(a));
    }
    public static float SmoothStep(float from, float to, float x)
    {
        if (IsEqualApprox(from, to)) return from;
        float t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

public partial struct Vector2
{
    public readonly Vector2 Lerp(Vector2 to, float weight) =>
        new(Mathf.Lerp(X, to.X, weight), Mathf.Lerp(Y, to.Y, weight));
    public static Vector2 operator /(Vector2 value, float scale) =>
        new(value.X / scale, value.Y / scale);
    public static Vector2 operator *(float scale, Vector2 value) => value * scale;
}
