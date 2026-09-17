using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // The set of saved editor maps, and the rules for what a map may be CALLED.
    //
    // A map is not one file. Saving fans out across five directories -- objects, spawns, terrain, roads,
    // buildings -- and every one of those paths is built by interpolating MapName into a filename
    // (`editor_{MapName}_Walls.dat` and eight siblings). That has two consequences this class exists to own:
    //
    //   1. LISTING a map means unioning the names implied by all of those patterns, because a map with no
    //      props still has walls, and a map with only terrain still exists. Scanning one directory would
    //      quietly hide maps rather than fail.
    //   2. The NAME IS A PATH FRAGMENT. Nine call sites splice it in unescaped, so "../../foo" walks out of
    //      the content tree and a name with a slash writes somewhere nobody will find again. The menu now
    //      lets a user type that name, which it never could before, so sanitising stops being theoretical.
    public static class EditorMaps
    {
        public const int MaxNameLength = 40;

        // dir -> the filename patterns that live in it. `*` is where the map name goes.
        static readonly (string Dir, string Pattern)[] Sources =
        {
            ("res://content/objects/",   "editor_*.txt"),
            ("res://content/spawns/",    "editor_*.txt"),
            ("res://content/terrain/",   "editor_*_heightmap.bin"),
            ("res://content/roads/",     "editor_*_Paths.dat"),
            ("res://content/buildings/", "editor_*_Walls.dat"),
        };

        // Suffixes a map name never includes -- they are the per-feature tails the sub-editors append. Longest
        // first, so "_gridpower" is stripped before a shorter suffix could match inside it.
        static readonly string[] Tails =
        {
            "_heightmap.bin", "_environment.txt", "_gridpower.txt", "_gaspump.txt", "_shelves.txt",
            "_crates.txt", "_Walls.dat", "_Paths.dat", "_animals.txt", "_vehicles.txt", "_items.txt",
            "_players.txt", "_zombies.txt",
        };

        /// <summary>Every map that has at least one file on disk, sorted, no duplicates.</summary>
        public static List<string> List()
        {
            var found = new SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, pattern) in Sources)
            {
                string d = ProjectSettings.GlobalizePath(dir);
                if (!System.IO.Directory.Exists(d)) continue;
                foreach (var path in System.IO.Directory.GetFiles(d, pattern))
                {
                    var name = NameFromFile(System.IO.Path.GetFileName(path));
                    if (name != null) found.Add(name);
                }
            }
            return new List<string>(found);
        }

        /// <summary>A generated map's own marker: the seed it came from. ⚠ A FILE, not a name convention.
        /// "Island 12345 8" looks like a proc map and so does a hand-made map somebody called that, and the
        /// difference matters -- the proc tab lists these, the delete button erases them, and player saves are
        /// keyed by them. Inferring it from a prefix would put a hand-built map one rename away from being
        /// deleted by a button labelled for something else.</summary>
        public static string ProcPath(string map) =>
            ProjectSettings.GlobalizePath($"res://content/objects/editor_{map}_proc.txt");

        public static void MarkProc(string map, int seed)
        {
            try { System.IO.File.WriteAllText(ProcPath(map), seed.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (System.Exception e) { Log.Err($"[editor-maps] could not mark '{map}' as generated: {e.Message}"); }
        }

        /// <summary>The seed a map was generated from, or null if it was not generated.</summary>
        public static int? ProcSeed(string map)
        {
            try
            {
                string p = ProcPath(map);
                if (!System.IO.File.Exists(p)) return null;
                return int.TryParse(System.IO.File.ReadAllText(p).Trim(),
                                    System.Globalization.NumberStyles.Integer,
                                    System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : null;
            }
            catch { return null; }
        }

        /// <summary>Every generated map, newest first by write time -- the order a list of things you made wants
        /// to be in.</summary>
        public static List<(string Name, int Seed)> ListProc()
        {
            var outp = new List<(string, int, System.DateTime)>();
            foreach (var m in List())
            {
                var sd = ProcSeed(m);
                if (sd == null) continue;
                System.DateTime when;
                try { when = System.IO.File.GetLastWriteTimeUtc(ProcPath(m)); } catch { when = System.DateTime.MinValue; }
                outp.Add((m, sd.Value, when));
            }
            outp.Sort((a, b) => b.Item3.CompareTo(a.Item3));
            var res = new List<(string, int)>(outp.Count);
            foreach (var e in outp) res.Add((e.Item1, e.Item2));
            return res;
        }

        /// <summary>Erase every file belonging to a map (strawberry 2026-09-17: "here they can be deleted via a
        /// button").
        /// ⚠ DERIVED FROM Sources AND Tails, the same two tables List() reads, rather than a hand-written list
        /// of filenames. A delete that knows about fewer files than the lister does leaves a map that still
        /// appears in the menu with half its contents gone -- which is worse than not deleting it.</summary>
        public static int Delete(string map)
        {
            int n = 0;
            foreach (var (dir, pattern) in Sources)
            {
                string d = ProjectSettings.GlobalizePath(dir);
                if (!System.IO.Directory.Exists(d)) continue;
                foreach (var path in System.IO.Directory.GetFiles(d, pattern.Replace("*", map)))
                    try { System.IO.File.Delete(path); n++; } catch { }
                // ...and every per-feature tail for this map, which Sources' patterns do not all cover.
                foreach (var tail in Tails)
                    foreach (var path in System.IO.Directory.GetFiles(d, $"editor_{map}{tail}"))
                        try { System.IO.File.Delete(path); n++; } catch { }
                foreach (var path in System.IO.Directory.GetFiles(d, $"editor_{map}.txt"))
                    try { System.IO.File.Delete(path); n++; } catch { }
            }
            try { if (System.IO.File.Exists(ProcPath(map))) { System.IO.File.Delete(ProcPath(map)); n++; } } catch { }
            Log.Print($"[editor-maps] deleted '{map}' ({n} file(s))");
            return n;
        }

        /// <summary>"editor_Foo_Walls.dat" -> "Foo". Null if it is not a map file.</summary>
        public static string NameFromFile(string file)
        {
            const string pre = "editor_";
            if (file == null || !file.StartsWith(pre)) return null;
            string rest = file.Substring(pre.Length);
            foreach (var tail in Tails)
                if (rest.EndsWith(tail, System.StringComparison.OrdinalIgnoreCase))
                    return Trim(rest.Substring(0, rest.Length - tail.Length));
            int dot = rest.LastIndexOf('.');
            return dot > 0 ? Trim(rest.Substring(0, dot)) : null;   // the plain `editor_<name>.txt` props file
        }

        static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        /// <summary>What a typed name is allowed to become. Returns null when nothing usable survives, so the
        /// caller refuses rather than saving to a name the user did not ask for.
        ///
        /// Deliberately an ALLOW-list: letters, digits, space, dash, underscore. A deny-list of "/" and ".."
        /// is the version that gets bypassed -- backslashes on the other platform, a trailing dot, a
        /// reserved device name. Anything outside the allow-list simply does not survive.</summary>
        public static string Sanitise(string raw)
        {
            if (raw == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (char c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ') sb.Append(c);
                if (sb.Length >= MaxNameLength) break;
            }
            var s = sb.ToString().Trim();
            // A name made only of separators collapses to nothing; so does one made only of dots, which is the
            // traversal case -- "." and ".." both have every character stripped by the loop above.
            return s.Length == 0 ? null : s;
        }

        public static bool Exists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var n in List()) if (string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A free name based on `want`: "Map", "Map 2", "Map 3"... so Create never silently overwrites
        /// somebody's map because they reused a name.</summary>
        public static string Unique(string want)
        {
            string baseName = Sanitise(want) ?? "New Map";
            if (!Exists(baseName)) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                string cand = $"{baseName} {i}";
                if (!Exists(cand)) return cand;
            }
            return baseName;
        }
    }
}
