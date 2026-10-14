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
    public struct Vector3(float x, float y, float z) { public float X = x, Y = y, Z = z; }
    public struct Color(float r, float g, float b, float a = 1) { public float R = r, G = g, B = b, A = a; }
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
