// SDG.Compat — minimal UnityEngine.Mathf shim for the Godot port (Phase 0c harness).
// Lets engine-agnostic U3-SDK code that `using UnityEngine;` for Mathf compile + run outside Unity.
// Pure delegation to System.Math/MathF; values match Unity's Mathf exactly (Unity's Mathf IS a float
// wrapper over System.Math). Namespace deliberately UnityEngine so source `using` lines resolve unchanged.
namespace UnityEngine
{
    public static class Mathf
    {
        public const float PI = 3.14159265358979f;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public const float Deg2Rad = PI * 2f / 360f;
        public const float Rad2Deg = 360f / (PI * 2f);
        public const float Epsilon = 1.401298E-45f;

        public static float Sin(float f) => (float)System.Math.Sin(f);
        public static float Cos(float f) => (float)System.Math.Cos(f);
        public static float Tan(float f) => (float)System.Math.Tan(f);
        public static float Asin(float f) => (float)System.Math.Asin(f);
        public static float Acos(float f) => (float)System.Math.Acos(f);
        public static float Atan(float f) => (float)System.Math.Atan(f);
        public static float Atan2(float y, float x) => (float)System.Math.Atan2(y, x);
        public static float Sqrt(float f) => (float)System.Math.Sqrt(f);
        public static float Abs(float f) => System.Math.Abs(f);
        public static int Abs(int v) => System.Math.Abs(v);
        public static float Pow(float f, float p) => (float)System.Math.Pow(f, p);
        public static float Exp(float p) => (float)System.Math.Exp(p);
        public static float Log(float f) => (float)System.Math.Log(f);
        public static float Log(float f, float b) => (float)System.Math.Log(f, b);
        public static float Log10(float f) => (float)System.Math.Log10(f);
        public static float Ceil(float f) => (float)System.Math.Ceiling(f);
        public static float Floor(float f) => (float)System.Math.Floor(f);
        public static float Round(float f) => (float)System.Math.Round(f);
        public static int CeilToInt(float f) => (int)System.Math.Ceiling(f);
        public static int FloorToInt(float f) => (int)System.Math.Floor(f);
        public static int RoundToInt(float f) => (int)System.Math.Round(f);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
        public static float LerpAngle(float a, float b, float t) { float d = Repeat(b - a, 360f); if (d > 180f) d -= 360f; return a + d * Clamp01(t); }
        public static float Repeat(float t, float length) => Clamp(t - Floor(t / length) * length, 0f, length);
        public static float DeltaAngle(float current, float target) { float d = Repeat(target - current, 360f); if (d > 180f) d -= 360f; return d; }
        public static float MoveTowards(float current, float target, float maxDelta) => Abs(target - current) <= maxDelta ? target : current + Sign(target - current) * maxDelta;
        public static bool Approximately(float a, float b) => Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);
        public static float InverseLerp(float a, float b, float value) => a != b ? Clamp01((value - a) / (b - a)) : 0f;
    }
}
