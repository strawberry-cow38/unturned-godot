using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // Auto-registration for Asset Factory output: scans content/assets/*.assetbundle ONCE (lazily)
    // so the game can look up + spawn any factory-made asset by name. This is the "auto-register"
    // seam every per-type binder + spawner uses — drop a .assetbundle in the folder and it's
    // available, no code. Defensive: never throws, safe to call from any mode.
    public static class AssetCatalog
    {
        public const string Dir = "res://content/assets/";
        static readonly Dictionary<string, AssetBundle> _byName = new();
        static readonly Dictionary<string, string> _pathByName = new();
        static bool _scanned;

        public static void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                foreach (var f in DirAccess.GetFilesAt(Dir))
                {
                    if (!f.EndsWith(".assetbundle")) continue;
                    string path = Dir + f;
                    var b = AssetBundle.Load(path);
                    if (b == null) continue;
                    string key = string.IsNullOrEmpty(b.Name) ? System.IO.Path.GetFileNameWithoutExtension(f) : b.Name;
                    _byName[key] = b; _pathByName[key] = path;
                }
                GD.Print($"[assetcatalog] registered {_byName.Count} factory bundle(s)");
            }
            catch (System.Exception e) { GD.PushWarning($"[assetcatalog] scan failed: {e.Message}"); }
        }

        public static IReadOnlyCollection<string> All() { EnsureScanned(); return _byName.Keys; }
        public static AssetBundle Get(string name) { EnsureScanned(); return _byName.TryGetValue(name, out var b) ? b : null; }
        public static string PathOf(string name) { EnsureScanned(); return _pathByName.TryGetValue(name, out var p) ? p : null; }

        public static IEnumerable<string> OfType(string type)
        {
            EnsureScanned();
            foreach (var kv in _byName) if (kv.Value.Type == type) yield return kv.Key;
        }

        // Spawn a registered bundle into an in-game node tree (via AssetBundleLoader). null if unknown.
        public static Node3D Spawn(string name)
        {
            var b = Get(name);
            return b == null ? null : AssetBundleLoader.Build(b);
        }

        public static void Refresh() { _scanned = false; _byName.Clear(); _pathByName.Clear(); EnsureScanned(); }
    }
}
