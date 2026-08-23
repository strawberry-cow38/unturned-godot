using System;
using System.IO;

namespace UnturnedGodot.BugReport
{
    /// <summary>Which build a bug report came from.
    ///
    /// The one real report filed before this existed came back with game_commit null, and that is the field
    /// you need most when a report says "this broke" -- without it there is no way to tell whether the bug
    /// is still present, already fixed, or never existed on the branch you are reading from.
    ///
    /// HONEST LIMITS, because this resolves at runtime rather than at build time:
    ///   - In a dev run (editor or `godot --path game`) the repo is right there, so reading .git/HEAD gives
    ///     the true commit. That IS the deployment today -- the people filing reports run from the repo.
    ///   - In an exported game there is no .git, so this returns whatever `application/config/version`
    ///     was stamped into project.godot at export, or "unknown". Wiring an export step to stamp that is
    ///     the correct fix when there is an export; pretending the runtime read covers it would not be.
    ///   - A dirty working tree reports the commit it is based on, not the code actually running. Reports
    ///     from a dev mid-edit are approximate by nature and the suffix says so.
    /// Cached: this touches the filesystem and is read once per report, but a report is a rare event and a
    /// static field costs nothing.</summary>
    public static class BuildStamp
    {
        static string _cached;

        /// <summary>Set by the host before first use (Godot maps res:// itself; this core lib must not
        /// depend on the engine). Null = fall back to the process working directory.</summary>
        public static string ProjectDir;

        /// <summary>Set by the host from ProjectSettings application/config/version, if an export stamped one.</summary>
        public static string ConfiguredVersion;

        public static string Commit => _cached ??= Resolve();

        static string Resolve()
        {
            try
            {
                string root = FindGitRoot(ProjectDir ?? Directory.GetCurrentDirectory());
                if (root != null)
                {
                    string sha = ReadHead(root);
                    if (sha != null)
                        return sha.Substring(0, Math.Min(12, sha.Length)) + (IsDirty(root) ? "+dirty" : "");
                }
            }
            catch { /* a bug report must never fail because it could not name the build */ }
            return string.IsNullOrEmpty(ConfiguredVersion) ? "unknown" : ConfiguredVersion;
        }

        static string FindGitRoot(string start)
        {
            var d = new DirectoryInfo(start);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            return null;
        }

        static string ReadHead(string root)
        {
            string head = File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim();
            if (!head.StartsWith("ref:")) return head;                    // detached HEAD is the sha itself
            string refPath = head.Substring(4).Trim();
            string loose = Path.Combine(root, ".git", refPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return File.ReadAllText(loose).Trim();
            // Packed refs: a freshly-cloned checkout has no loose ref file for its branch.
            string packed = Path.Combine(root, ".git", "packed-refs");
            if (!File.Exists(packed)) return null;
            foreach (string line in File.ReadAllLines(packed))
            {
                if (line.Length == 0 || line[0] == '#' || line[0] == '^') continue;
                int sp = line.IndexOf(' ');
                if (sp > 0 && line.Substring(sp + 1).Trim() == refPath) return line.Substring(0, sp);
            }
            return null;
        }

        /// <summary>Cheap approximation: the index mtime moving after the last commit means someone has
        /// staged or touched something. Not `git status` -- we are not shelling out from a bug report.</summary>
        static bool IsDirty(string root)
        {
            try
            {
                string idx = Path.Combine(root, ".git", "index");
                string headFile = Path.Combine(root, ".git", "HEAD");
                return File.Exists(idx) && File.GetLastWriteTimeUtc(idx) > File.GetLastWriteTimeUtc(headFile);
            }
            catch { return false; }
        }
    }
}
