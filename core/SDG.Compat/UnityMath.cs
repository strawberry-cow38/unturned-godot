// SDG.Compat — UnityEngine math/color structs (Godot port Phase 0c harness).
// Unity-API-compatible Vector2/3/4, Quaternion, Color, Color32 so engine-agnostic U3-SDK code that
// `using UnityEngine;` compiles + behaves identically outside Unity. Semantics match UnityEngine exactly
// (verified against the game's own UnityDatEx/UnityNetPak tests). Namespace deliberately UnityEngine.
// At the Godot boundary these convert to Godot.Vector3/Quaternion/Color (a thin adapter, added later).
using System;

namespace UnityEngine
{
    [Serializable]
    public struct Vector2 : IEquatable<Vector2>
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
        public static Vector2 up => new Vector2(0f, 1f);
        public static Vector2 right => new Vector2(1f, 0f);
        public float this[int i] { get => i == 0 ? x : y; set { if (i == 0) x = value; else y = value; } }
        public float magnitude => Mathf.Sqrt(x * x + y * y);
        public float sqrMagnitude => x * x + y * y;
        public Vector2 normalized { get { float m = magnitude; return m > 1E-05f ? this / m : zero; } }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        public static bool operator ==(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < 9.99999944E-11f;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { t = Mathf.Clamp01(t); return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t); }
        public bool Equals(Vector2 o) => x == o.x && y == o.y;
        public override bool Equals(object o) => o is Vector2 v && Equals(v);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);
        public override string ToString() => $"({x:F1}, {y:F1})";
    }

    [Serializable]
    public struct Vector3 : IEquatable<Vector3>
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; this.z = 0f; }
        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public float this[int i] { get => i == 0 ? x : (i == 1 ? y : z); set { if (i == 0) x = value; else if (i == 1) y = value; else z = value; } }
        public float magnitude => Mathf.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized { get { float m = magnitude; return m > 1E-05f ? this / m : zero; } }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 9.99999944E-11f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        public void Normalize() { this = normalized; }
        public bool Equals(Vector3 o) => x == o.x && y == o.y && z == o.z;
        public override bool Equals(object o) => o is Vector3 v && Equals(v);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        public override string ToString() => $"({x:F1}, {y:F1}, {z:F1})";
    }

    [Serializable]
    public struct Vector4 : IEquatable<Vector4>
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vector4 zero => new Vector4(0f, 0f, 0f, 0f);
        public static Vector4 one => new Vector4(1f, 1f, 1f, 1f);
        public float this[int i] { get => i == 0 ? x : (i == 1 ? y : (i == 2 ? z : w)); set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else w = value; } }
        public static Vector4 operator +(Vector4 a, Vector4 b) => new Vector4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
        public static Vector4 operator -(Vector4 a, Vector4 b) => new Vector4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
        public static Vector4 operator *(Vector4 a, float d) => new Vector4(a.x * d, a.y * d, a.z * d, a.w * d);
        public static bool operator ==(Vector4 a, Vector4 b) => (a - b).x * (a - b).x + (a - b).y * (a - b).y + (a - b).z * (a - b).z + (a - b).w * (a - b).w < 9.99999944E-11f;
        public static bool operator !=(Vector4 a, Vector4 b) => !(a == b);
        public bool Equals(Vector4 o) => x == o.x && y == o.y && z == o.z && w == o.w;
        public override bool Equals(object o) => o is Vector4 v && Equals(v);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1);
        public override string ToString() => $"({x:F1}, {y:F1}, {z:F1}, {w:F1})";
    }

    [Serializable]
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);
        public float this[int i] { get => i == 0 ? x : (i == 1 ? y : (i == 2 ? z : w)); set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else w = value; } }
        public static Quaternion Euler(float px, float py, float pz)
        {
            float cx = Mathf.Cos(px * Mathf.Deg2Rad * 0.5f), sx = Mathf.Sin(px * Mathf.Deg2Rad * 0.5f);
            float cy = Mathf.Cos(py * Mathf.Deg2Rad * 0.5f), sy = Mathf.Sin(py * Mathf.Deg2Rad * 0.5f);
            float cz = Mathf.Cos(pz * Mathf.Deg2Rad * 0.5f), sz = Mathf.Sin(pz * Mathf.Deg2Rad * 0.5f);
            // Unity uses ZXY intrinsic order.
            return new Quaternion(
                sx * cy * cz + cx * sy * sz,
                cx * sy * cz - sx * cy * sz,
                cx * cy * sz - sx * sy * cz,
                cx * cy * cz + sx * sy * sz);
        }
        public static Quaternion Euler(Vector3 e) => Euler(e.x, e.y, e.z);
        public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        public static bool operator ==(Quaternion a, Quaternion b) => Dot(a, b) > 0.999999f;
        public static bool operator !=(Quaternion a, Quaternion b) => !(a == b);
        public static float Dot(Quaternion a, Quaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        public bool Equals(Quaternion o) => x == o.x && y == o.y && z == o.z && w == o.w;
        public override bool Equals(object o) => o is Quaternion q && Equals(q);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1);
        public override string ToString() => $"({x:F1}, {y:F1}, {z:F1}, {w:F1})";
    }

    [Serializable]
    public struct Color : IEquatable<Color>
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
        public static Color clear => new Color(0f, 0f, 0f, 0f);
        public float this[int i] { get => i == 0 ? r : (i == 1 ? g : (i == 2 ? b : a)); set { if (i == 0) r = value; else if (i == 1) g = value; else if (i == 2) b = value; else a = value; } }
        public static Color operator +(Color x, Color y) => new Color(x.r + y.r, x.g + y.g, x.b + y.b, x.a + y.a);
        public static Color operator *(Color x, float d) => new Color(x.r * d, x.g * d, x.b * d, x.a * d);
        public static bool operator ==(Color x, Color y) => (Vector4)x == (Vector4)y;
        public static bool operator !=(Color x, Color y) => !(x == y);
        public static Color Lerp(Color a, Color c, float t) { t = Mathf.Clamp01(t); return new Color(a.r + (c.r - a.r) * t, a.g + (c.g - a.g) * t, a.b + (c.b - a.b) * t, a.a + (c.a - a.a) * t); }
        public static implicit operator Vector4(Color c) => new Vector4(c.r, c.g, c.b, c.a);
        public static implicit operator Color(Vector4 v) => new Color(v.x, v.y, v.z, v.w);
        // NB: Color32<->Color conversions live on Color32 (matches Unity); defining here too = ambiguous.
        public bool Equals(Color o) => r == o.r && g == o.g && b == o.b && a == o.a;
        public override bool Equals(object o) => o is Color c && Equals(c);
        public override int GetHashCode() => ((Vector4)this).GetHashCode();
        public override string ToString() => $"RGBA({r:F3}, {g:F3}, {b:F3}, {a:F3})";
    }

    [Serializable]
    public struct Color32 : IEquatable<Color32>
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public byte this[int i] { get => i == 0 ? r : (i == 1 ? g : (i == 2 ? b : a)); set { if (i == 0) r = value; else if (i == 1) g = value; else if (i == 2) b = value; else a = value; } }
        public static implicit operator Color32(Color c) => new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255));
        public static implicit operator Color(Color32 c) => new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
        public static Color32 Lerp(Color32 a, Color32 b, float t) { t = Mathf.Clamp01(t); return new Color32((byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t), (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t)); }
        public bool Equals(Color32 o) => r == o.r && g == o.g && b == o.b && a == o.a;
        public override bool Equals(object o) => o is Color32 c && Equals(c);
        public override int GetHashCode() => (r) | (g << 8) | (b << 16) | (a << 24);
        public override string ToString() => $"RGBA({r}, {g}, {b}, {a})";
    }
}
