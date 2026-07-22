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

        // --- Asset Factory items: register gun/deployable bundles as REAL ItemAssets ---
        // Called at the END of ItemCatalog.RegisterAll (after the real items + Assets.clear()) so a factory asset
        // `give`s, sits in the inventory, and equips through the normal item path -- no console hack. Ids sit at
        // 60000+ (well above the retail range), assigned by SORTED bundle name = stable within a folder state.
        public const ushort FactoryItemIdBase = 60000;
        static readonly Dictionary<ushort, string> _factoryItemName = new();   // factory item id -> bundle name (icons are name-keyed)

        public static void RegisterFactoryItems()
        {
            EnsureScanned();
            _factoryItemName.Clear();
            var names = new List<string>(_byName.Keys);
            names.Sort(System.StringComparer.Ordinal);   // stable id assignment
            ushort id = FactoryItemIdBase;
            foreach (var name in names)
            {
                var b = _byName[name];
                if (b.Type != "gun") continue;   // guns first; deployables join here next
                SDG.Unturned.Assets.add(new SDG.Unturned.ItemAsset
                {
                    id = id, itemName = PrettyName(name), type = SDG.Unturned.EItemType.GUN,
                    rarity = SDG.Unturned.EItemRarity.RARE, size_x = 4, size_y = 2,
                    gunName = name, description = "An Asset Factory creation.",
                });
                _factoryItemName[id] = name;
                id++;
            }
            if (_factoryItemName.Count > 0) GD.Print($"[assetcatalog] {_factoryItemName.Count} factory item(s) registered as real items (id {FactoryItemIdBase}+)");
        }

        // Reverse map for the inventory icon loader: a factory item id -> its bundle name (icons are name-keyed
        // so re-indexing the ids never orphans a baked icon). null for a non-factory / unknown id.
        public static string FactoryItemName(ushort id) => _factoryItemName.TryGetValue(id, out var n) ? n : null;

        static string PrettyName(string key)
        {
            var parts = key.Replace('_', ' ').Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) parts[i] = char.ToUpper(parts[i][0]) + parts[i][1..];
            return parts.Length == 0 ? key : string.Join(' ', parts);
        }

        public static void Refresh() { _scanned = false; _byName.Clear(); _pathByName.Clear(); EnsureScanned(); }
    }
}
