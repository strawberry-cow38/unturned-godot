// SDG.Compat ⇄ Godot adapter (Phase 0c). The ported core uses UnityEngine.Vector3/Quaternion/Color
// (SDG.Compat); Godot uses Godot.Vector3/... . These extension methods are the ONLY boundary crossing.
// NB: Unity is left-handed Y-up / Godot right-handed Y-up — Z flips. Centralised here so the whole port
// shares one handedness convention (revisit once the first ripped mesh lands to confirm the sign).
namespace UnturnedGodot
{
    public static class GodotCompat
    {
        public static Godot.Vector3 ToGodot(this UnityEngine.Vector3 v) => new Godot.Vector3(v.x, v.y, -v.z);
        public static UnityEngine.Vector3 ToSdg(this Godot.Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, -v.Z);
        public static Godot.Vector2 ToGodot(this UnityEngine.Vector2 v) => new Godot.Vector2(v.x, v.y);
        public static UnityEngine.Vector2 ToSdg(this Godot.Vector2 v) => new UnityEngine.Vector2(v.X, v.Y);
        public static Godot.Quaternion ToGodot(this UnityEngine.Quaternion q) => new Godot.Quaternion(q.x, q.y, -q.z, -q.w);
        public static UnityEngine.Quaternion ToSdg(this Godot.Quaternion q) => new UnityEngine.Quaternion(q.X, q.Y, -q.Z, -q.W);
        public static Godot.Color ToGodot(this UnityEngine.Color c) => new Godot.Color(c.r, c.g, c.b, c.a);
        public static UnityEngine.Color ToSdg(this Godot.Color c) => new UnityEngine.Color(c.R, c.G, c.B, c.A);
    }
}
