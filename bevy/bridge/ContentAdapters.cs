// Only filesystem/value adapters for linked catalog sources. No Godot engine types
// or rendering methods are loaded by this process.
namespace Godot
{
    public static class ProjectSettings
    {
        public static string ContentRoot { get; set; }
        public static string GlobalizePath(string path)
        {
            if (!path.StartsWith("res://content/")) return path;
            string resolved = System.IO.Path.Combine(ContentRoot, path[14..]);
            // Godot preserves the directory separator; original loaders append
            // filenames directly to GlobalizePath("res://content/").
            return path.EndsWith('/') && !resolved.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? resolved + System.IO.Path.DirectorySeparatorChar : resolved;
        }
    }
    public sealed class FileAccess : IDisposable
    {
        readonly StreamReader _reader;
        FileAccess(string path) => _reader = new StreamReader(path);
        public enum ModeFlags { Read }
        public static bool FileExists(string path) => File.Exists(ProjectSettings.GlobalizePath(path));
        public static string GetFileAsString(string path) => File.ReadAllText(ProjectSettings.GlobalizePath(path));
        public static FileAccess Open(string path, ModeFlags _) => new(ProjectSettings.GlobalizePath(path));
        public bool EofReached() => _reader.EndOfStream;
        public string GetLine() => _reader.ReadLine();
        public void Dispose() => _reader.Dispose();
    }
    public static class GD { public static void PushWarning(string message) => Console.Error.WriteLine(message); }
    public partial struct Vector3(float x, float y, float z)
    {
        public float X = x, Y = y, Z = z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.X * b, a.Y * b, a.Z * b);
    }
    public partial struct Color(float r, float g, float b, float a = 1)
    {
        public float R = r, G = g, B = b, A = a;
        public Color(string hex) : this(0, 0, 0)
        {
            uint rgb = uint.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber);
            R = (rgb >> 16 & 255) / 255f; G = (rgb >> 8 & 255) / 255f; B = (rgb & 255) / 255f;
        }
    }
    // These source callers intentionally use unseeded random streams. Preserve
    // their probability/range contract; portable saves capture the rolled items.
    public sealed class RandomNumberGenerator
    {
        readonly Random _random = new();
        public float Randf() => _random.NextSingle();
        public float RandfRange(float from,float to) => from + (to-from)*_random.NextSingle();
        public int RandiRange(int from, int to) => _random.Next(from, checked(to + 1));
    }
}
namespace UnturnedGodot
{
    public static class Log
    {
        public static void Print(string message) => Console.Error.WriteLine(message);
        public static void Err(string message) => Console.Error.WriteLine(message);
    }
    public static class Terrain { public static float SeaLevelY = SDG.Unturned.InfiniteTerrain.SeaLevel; }
    public static class Viewmodel
    {
        public static bool IsKnownGun(string name)
        {
            var path = Godot.ProjectSettings.GlobalizePath("res://content/guns_visual.tsv");
            return File.Exists(path) && File.ReadLines(path).Any(l => l.Split('\t')[0] == name);
        }
    }
}
