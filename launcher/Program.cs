using System.Diagnostics;

// Source-pull launcher (same pattern as the Blockheads/struggle-game launchers). git clone or `git pull`
// the repo into ./source, dotnet build the game solution, import resources, then run Godot on the project.
// The "diff" is literally whatever `git pull` fetches -- only changed objects, no zip/export hosting.
//
// Requires on the host: git, dotnet 8 SDK, and Godot 4.6.x MONO (set UNTURNED_GODOT_EXE or put godot on PATH).
internal static class Program
{
    const string RepoUrl = "https://github.com/strawberry-cow38/unturned-godot.git";
    const string SourceDirName = "source";
    const string Solution = "game/UnturnedGodot.sln";
    const string BuildConfig = "Debug";

    static int Main()
    {
        Console.Title = "Unturned Godot - Launcher";
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string srcDir = Path.Combine(baseDir, SourceDirName);
            string gameDir = Path.Combine(srcDir, "game");

            string git = ResolveOnPath("git");
            if (git is null) return Bail("git not found on PATH.");
            string dotnet = ResolveOnPath("dotnet");
            if (dotnet is null) return Bail("dotnet 8 SDK not found on PATH.");
            string godot = ResolveGodot();
            if (godot is null) return Bail("Godot mono exe not found. Set UNTURNED_GODOT_EXE or put godot on PATH.");

            if (!Directory.Exists(Path.Combine(srcDir, ".git")))
            {
                Console.WriteLine($"Cloning {RepoUrl} ...");
                if (Run(git, new[] { "clone", RepoUrl, srcDir }, baseDir) != 0) return Bail("git clone failed (private repo -- is your GitHub auth set up?).");
            }
            else
            {
                Console.WriteLine("Updating (git pull -- only the diff downloads)...");
                if (Run(git, new[] { "pull", "--ff-only" }, srcDir) != 0) Console.WriteLine("git pull failed; running current checkout.");
            }

            Console.WriteLine($"Building {Solution} ...");
            if (Run(dotnet, new[] { "build", Solution, "-c", BuildConfig, "-v", "q", "-nologo" }, srcDir) != 0) return Bail("dotnet build failed.");

            Console.WriteLine("Importing resources...");
            Run(godot, new[] { "--path", gameDir, "--headless", "--import" }, gameDir);

            Console.WriteLine("Launching Unturned Godot...");
            var psi = new ProcessStartInfo(godot) { UseShellExecute = false, WorkingDirectory = gameDir };
            psi.ArgumentList.Add("--path");
            psi.ArgumentList.Add(gameDir);
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return Bail(null); }
    }

    static int Run(string exe, string[] args, string wd)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = wd };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi); p.WaitForExit(); return p.ExitCode;
    }

    static string ResolveOnPath(string name)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { string p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    static string ResolveGodot()
    {
        string env = Environment.GetEnvironmentVariable("UNTURNED_GODOT_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var n in new[] { "godot", "Godot", "godot_mono", "Godot_mono" })
        {
            var r = ResolveOnPath(n); if (r != null) return r;
        }
        return null;
    }

    static int Bail(string msg)
    {
        if (msg != null) Console.Error.WriteLine("ERROR: " + msg);
        Console.WriteLine("Press any key to exit.");
        try { Console.ReadKey(); } catch { }
        return 1;
    }
}
