using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Launch WARMUP, STAGED by asset TYPE (master: "the zombie/vehicle stages"): the TOP bar names the current
    // category (Terrain / Vehicles / Zombies / Animals / Foliage / Clothing / Resources / Items / Objects) with its
    // within-stage count; the BOTTOM bar shows the current asset + overall progress. Preloads the vanilla core
    // meshes into the ObjMesh cache = front-loads them (curated maps load their extra assets on-demand later). A few
    // per frame so the bars animate, then hands off to the menu. Vanilla only; this is master's two-tier design.
    public partial class Warmup : Node
    {
        struct Entry { public string Stage; public int X, N; public string Path; }
        readonly List<Entry> _entries = new();
        int _i, _perFrame;
        LoadingScreen _ls;
        System.Action _onDone;

        static string[] Files(string sub, string glob)
        {
            var dir = ProjectSettings.GlobalizePath(sub.Length == 0 ? "res://content" : $"res://content/{sub}");
            return System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, glob) : System.Array.Empty<string>();
        }

        // classify an objects/*.obj by name -> Vehicles / Animals / Objects (the bulk).
        static readonly string[] VehicleKeys = { "Firetruck", "Roadster", "Truck", "Veh_", "Jeep", "Quad", "Ambulance", "Snowmobile", "Forklift", "Tractor", "APC" };
        static readonly string[] AnimalKeys = { "Animal_", "Cow", "Deer", "Pig", "Bear", "Wolf", "Chicken", "Reindeer" };
        static bool Match(string name, string[] keys)
        {
            foreach (var k in keys) if (name.Contains(k, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static Warmup Begin(Node parent, LoadingScreen ls, System.Action onDone)
        {
            var w = new Warmup { _ls = ls, _onDone = onDone };

            // split content/objects by name into Objects (bulk) / Vehicles / Animals
            var objs = new List<string>(); var vehs = new List<string>(); var anim = new List<string>();
            foreach (var p in Files("objects", "*.obj"))
            {
                var n = System.IO.Path.GetFileNameWithoutExtension(p);
                if (Match(n, VehicleKeys)) vehs.Add(p);
                else if (Match(n, AnimalKeys)) anim.Add(p);
                else objs.Add(p);
            }
            // zombies: the character mesh + the zombie atlases
            var zomb = new List<string>();
            var ch = ProjectSettings.GlobalizePath("res://content/character.txt");
            if (System.IO.File.Exists(ch)) zomb.Add(ch);
            foreach (var p in Files("", "zombie_atlas*.png")) zomb.Add(p);

            // stage ORDER (named small stages first so they read; the two big ones -- Items 402, Objects ~380 -- last)
            var stages = new (string name, string[] files)[]
            {
                ("Terrain",   Files("terrain", "*.png")),
                ("Vehicles",  vehs.ToArray()),
                ("Zombies",   zomb.ToArray()),
                ("Animals",   anim.ToArray()),
                ("Foliage",   Files("foliage", "*.obj")),
                ("Clothing",  Files("clothing", "*.obj")),
                ("Resources", Files("resources", "*.obj")),
                ("Items",     Files("items", "*.txt")),
                ("Objects",   objs.ToArray()),
            };
            foreach (var (name, files) in stages)
                for (int i = 0; i < files.Length; i++)
                    w._entries.Add(new Entry { Stage = name, X = i + 1, N = files.Length, Path = files[i] });

            w._perFrame = Mathf.Max(1, w._entries.Count / 120);   // ~120 frames (~4s @30fps) for the whole set
            ls?.SetTotal(Mathf.Max(1, w._entries.Count));
            parent.AddChild(w);
            return w;
        }

        public override void _Process(double delta)
        {
            for (int b = 0; b < _perFrame && _i < _entries.Count; b++, _i++)
            {
                var e = _entries[_i];
                _ls?.SetStage(e.Stage, e.X, e.N);
                _ls?.SetStatus(System.IO.Path.GetFileNameWithoutExtension(e.Path));
                if (e.Path.EndsWith(".obj")) ObjMesh.Load(e.Path);   // parse + cache (the real front-load)
                else { try { using var f = System.IO.File.OpenRead(e.Path); } catch { } }   // touch non-.obj (items/terrain) so the OS caches it
                _ls?.Advance();
            }
            if (_i >= _entries.Count)
            {
                RubbleFx.Warm(); RubbleSnd.Warm();   // front-load the retail break VFX/SFX so the FIRST prop smash doesn't (master: hard stutter). Re-run on map entry re-warms after ResourceCaches.ClearAll.
                GD.Print($"[warmup] preloaded {ObjMesh.CachedCount} meshes across {_entries.Count} assets");
                _onDone?.Invoke();
                QueueFree();
            }
        }
    }
}
