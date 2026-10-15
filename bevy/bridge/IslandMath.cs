// Value-only Godot 4.6 math needed by the unchanged finite island generator.
namespace Godot;

public static partial class Mathf
{
    public static float Pow(float x, float y) => MathF.Pow(x, y);
    public static float Atan(float x) => MathF.Atan(x);
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

public partial struct Vector3
{
    public readonly Vector3 Lerp(Vector3 to, float weight) =>
        new(Mathf.Lerp(X, to.X, weight), Mathf.Lerp(Y, to.Y, weight), Mathf.Lerp(Z, to.Z, weight));
    public static Vector3 operator *(float scale, Vector3 value) => value * scale;
}

// Godot Color.Lerp: retained component arithmetic for the original chart bake.
public partial struct Color
{
    public readonly Color Lerp(Color to, float weight) => new(
        Mathf.Lerp(R,to.R,weight), Mathf.Lerp(G,to.G,weight),
        Mathf.Lerp(B,to.B,weight), Mathf.Lerp(A,to.A,weight));
}
