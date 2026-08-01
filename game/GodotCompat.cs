// SDG.Compat <-> Godot vector adapter. The ported core uses UnityEngine.Vector2/3 (SDG.Compat); Godot uses
// Godot.Vector2/3. This converts between them, component for component.
//
// -------------------------------------------------------------------------------------------------------
// HISTORY, because the previous version of this file was actively dangerous (DUPLICATE_AUDIT 2.21).
//
// It negated Z ("Unity is left-handed Y-up / Godot right-handed Y-up - Z flips") and announced itself as
// "the ONLY boundary crossing ... Centralised here so the whole port shares one handedness convention",
// with a note to "revisit once the first ripped mesh lands to confirm the sign".
//
// Nobody revisited. The meshes landed and the port settled on the OPPOSITE convention: every real
// conversion in the codebase is a straight component copy with NO flip - five separate private `ToU`
// helpers (ClientWorldSession, ContainerNetSync, VehicleNetSync, AnimalNetSync, MpLoopback) plus dozens of
// inline `new Vector3(e.Pos.x, e.Pos.y, e.Pos.z)`. The flipping version's only remaining caller was a
// Phase 0c boot smoke-print that converts (1,2,3) and prints the result.
//
// So the flip was never exercised, and it is not what the rest of the port does. Worth resolving rather
// than leaving open: a file calling itself the one true boundary crossing is one a contributor will
// reasonably reach for, and every position converted through it would have come out mirrored - silently,
// because a mirrored Z is still a plausible-looking position.
//
// The Quaternion and Color overloads are REMOVED rather than "corrected". They had zero callers, so there
// was nothing to check a guess against, and inventing a handedness convention with no consumer is exactly
// how this file became wrong the first time. Add one back when something needs it, against a real case
// that can be verified.
// -------------------------------------------------------------------------------------------------------
namespace UnturnedGodot
{
    public static class GodotCompat
    {
        public static Godot.Vector3 ToGodot(this UnityEngine.Vector3 v) => new Godot.Vector3(v.x, v.y, v.z);
        public static UnityEngine.Vector3 ToSdg(this Godot.Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
        public static Godot.Vector2 ToGodot(this UnityEngine.Vector2 v) => new Godot.Vector2(v.x, v.y);
        public static UnityEngine.Vector2 ToSdg(this Godot.Vector2 v) => new UnityEngine.Vector2(v.X, v.Y);
    }
}
