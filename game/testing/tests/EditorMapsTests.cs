using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Map names become FILE PATHS. Every sub-editor builds its save path by splicing MapName into a
    /// filename -- nine call sites, none of them escaping anything -- and the workshop menu now lets a user
    /// type that name, which nothing did before. So "../../foo" is no longer hypothetical.
    ///
    /// Sanitise is an allow-list on purpose. A deny-list that strips "/" and ".." is the version that gets
    /// walked around: backslash on the other platform, a bare "..", a trailing dot, a name that is nothing but
    /// separators. These check the OUTCOME (can the result escape, can it be empty) rather than that specific
    /// characters were removed, because the second kind passes while the bypass still works.</summary>
    public class EditorMapNamesAreSafePaths : GameTest
    {
        public override string Name => "editor.map_names_are_safe_paths";

        static readonly string[] Nasty =
        {
            "../../etc/passwd", "..", ".", "../sibling", "a/b", "a\\b", "  ", "", "...",
            "/absolute", "C:\\win", "name\u0000null", "con", "..\\..\\up",
        };

        public override IEnumerable<Step> Run()
        {
            foreach (var raw in Nasty)
            {
                var s = EditorMaps.Sanitise(raw);
                if (s == null) { T.Check($"'{Show(raw)}' -> refused", true); continue; }
                // If anything survived it must be inert as a path fragment: no separators, no traversal, and
                // combining it must stay inside the directory it was combined with.
                string dir = ProjectSettings.GlobalizePath("res://content/objects/");
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "editor_" + s + ".txt"));
                bool inside = full.StartsWith(System.IO.Path.GetFullPath(dir), System.StringComparison.Ordinal);
                bool clean = s.IndexOf('/') < 0 && s.IndexOf('\\') < 0 && s != ".." && s != "."
                             && s.IndexOf('\0') < 0;
                T.Check($"'{Show(raw)}' -> '{s}' stays inside content/objects", inside);
                T.Check($"'{Show(raw)}' -> '{s}' has no path syntax", clean);
            }

            // Ordinary names must SURVIVE -- a sanitiser that eats everything is "safe" and useless.
            foreach (var ok in new[] { "My Map", "pei_2", "Test-Map 3" })
                T.Check($"'{ok}' survives unchanged", EditorMaps.Sanitise(ok) == ok);

            T.Check($"length is capped ({EditorMaps.Sanitise(new string('a', 200))?.Length})",
                    (EditorMaps.Sanitise(new string('a', 200)) ?? "").Length <= EditorMaps.MaxNameLength);

            // Round-trip: a saved filename must parse back to the name that produced it, for every tail the
            // sub-editors actually write. Listing depends on this and nothing else checks it.
            foreach (var (file, want) in new[]
            {
                ("editor_My Map.txt", "My Map"),
                ("editor_My Map_Walls.dat", "My Map"),
                ("editor_My Map_heightmap.bin", "My Map"),
                ("editor_My Map_Paths.dat", "My Map"),
                ("editor_My Map_gridpower.txt", "My Map"),
                ("editor_My Map_crates.txt", "My Map"),
                ("editor_My Map_environment.txt", "My Map"),
            })
                T.Check($"'{file}' -> '{EditorMaps.NameFromFile(file)}'", EditorMaps.NameFromFile(file) == want);

            T.Check("a non-map file is not a map", EditorMaps.NameFromFile("Items.dat") == null);
            yield break;
        }

        static string Show(string s) => s.Replace("\0", "\\0").Replace("\\", "\\\\");
    }
}
