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
                    if (_byName.ContainsKey(key)) GD.PushWarning($"[assetcatalog] duplicate bundle name '{key}' ({f}) -- overwriting the earlier one");
                    _byName[key] = b; _pathByName[key] = path;
                }
                GD.Print($"[assetcatalog] registered {_byName.Count} factory bundle(s)");
            }
            catch (System.Exception e) { GD.PushWarning($"[assetcatalog] scan failed: {e.Message}"); _scanned = false; }   // a transient failure (dir missing on a fresh install) must not permanently silence the catalog
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
            var used = new HashSet<ushort>();
            var names = new List<string>(_byName.Keys);
            names.Sort(System.StringComparer.Ordinal);   // deterministic order ONLY for the rare hash-collision probe
            foreach (var name in names)
            {
                var b = _byName[name];
                // id is a STABLE hash of the NAME (not a running index): adding/removing OTHER bundles never renumbers
                // an existing item, so an SP save that stored id 6xxxx still resolves to the same item.
                ushort id = FactoryItemId(name, used);
                if (id == 0) { GD.PushError($"[assetcatalog] no free id for factory item '{name}'"); continue; }
                used.Add(id);
                if (b.Type == "gun")
                {
                    SDG.Unturned.Assets.add(new SDG.Unturned.ItemAsset
                    {
                        id = id, itemName = PrettyName(name), type = SDG.Unturned.EItemType.GUN,
                        rarity = SDG.Unturned.EItemRarity.RARE, size_x = 4, size_y = 2,
                        gunName = name, description = "An Asset Factory creation.",
                    });
                }
                else if (b.Type == "deployable")
                {
                    // placement is keyed on DeployableDef.ById(id) (NOT the item type), so a plain GENERIC item + a
                    // factory DeployableDef under the SAME id -> holding the item auto-routes to the placement ghost
                    // (PlayerController:965 EquipHeldDeployable), LMB plants the composed body + consumes one.
                    SDG.Unturned.Assets.add(new SDG.Unturned.ItemAsset
                    {
                        id = id, itemName = PrettyName(name), type = SDG.Unturned.EItemType.GENERIC,
                        rarity = SDG.Unturned.EItemRarity.RARE, size_x = 2, size_y = 2,
                        description = "An Asset Factory deployable.",
                    });
                    DeployableDef.RegisterFactory(BuildDeployableDef(id, name, b));
                }
                else continue;   // props/vehicles aren't inventory items (props = map-editor palette; vehicles = spawn by name)
                _factoryItemName[id] = name;
            }
            if (_factoryItemName.Count > 0) GD.Print($"[assetcatalog] {_factoryItemName.Count} factory item(s) registered as real items (id {FactoryItemIdBase}+)");
        }

        // Stable per-NAME item id in [FactoryItemIdBase, +Span) via FNV-1a, so an item's id depends ONLY on its own
        // name -- adding/removing OTHER bundles can never renumber it (which would corrupt SP saves that stored the id).
        // Linear-probe on the rare hash collision; callers iterate names in sorted order so that probe is deterministic.
        const ushort FactoryItemIdSpan = 5000;   // 60000..65000, clear of the 65531+ overflow guard
        static ushort FactoryItemId(string name, HashSet<ushort> used)
        {
            uint h = 2166136261u;
            foreach (char c in name) { h ^= c; h *= 16777619u; }
            ushort start = (ushort)(h % FactoryItemIdSpan);
            for (ushort i = 0; i < FactoryItemIdSpan; i++)
            {
                ushort id = (ushort)(FactoryItemIdBase + ((start + i) % FactoryItemIdSpan));
                if (!used.Contains(id)) return id;
            }
            return 0;   // span full (would need 5000 factory items) -- caller logs + skips
        }

        // A DeployableDef for a factory deployable bundle: SAME id as its item (so holding the item -> EquipHeldDeployable),
        // body built from the composed bundle (FactoryBundle -> Deployable.BuildMesh), authored UPRIGHT. Size is a fallback
        // footprint only -- the placed collider hugs the real composed-mesh AABB. HoldMesh null = ghost-only carry for now.
        static DeployableDef BuildDeployableDef(ushort id, string name, AssetBundle b)
        {
            return new DeployableDef
            {
                Id = id, Name = PrettyName(name), FactoryBundle = name,
                Size = new Vector3(1f, 2f, 1f), Health = 200f, Offset = 0.75f, Radius = 0.5f, Range = 6f, Upright = true,   // Offset lifts the placer's clearance sphere off the ground (else it overlaps the ground -> always invalid/red)
            };
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

        public static void Refresh() { _scanned = false; _byName.Clear(); _pathByName.Clear(); EnsureScanned(); Viewmodel.InvalidateVisuals(); }   // rebuild the viewmodel's cached gun-visual table too, else a newly-authored gun mounts as eaglefire
    }
}
