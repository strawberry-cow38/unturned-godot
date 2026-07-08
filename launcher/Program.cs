using System.Diagnostics;
using System.IO.Compression;

// Auto-update launcher for the Unturned Godot port. Checks version.txt on the tunnel; if the hosted build
// is newer than what's installed (or nothing's installed), downloads + extracts it, then runs the game.
const string Base = "https://catboy.cowtools.uk/unturned/";
string dir = AppContext.BaseDirectory;
string gameDir = Path.Combine(dir, "game");
string localVer = Path.Combine(dir, "installed.txt");
string exe = Path.Combine(gameDir, "UnturnedGodot.exe");

Console.WriteLine("=== Unturned Godot Launcher ===");
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    string remote = (await http.GetStringAsync(Base + "version.txt")).Trim();
    string local = File.Exists(localVer) ? File.ReadAllText(localVer).Trim() : "";
    if (remote != local || !File.Exists(exe))
    {
        Console.WriteLine($"Downloading build {remote} ...");
        byte[] zip = await http.GetByteArrayAsync(Base + "UnturnedGodot-win64.zip");
        string tmp = Path.Combine(dir, "_build.zip");
        await File.WriteAllBytesAsync(tmp, zip);
        if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true);
        Directory.CreateDirectory(gameDir);
        ZipFile.ExtractToDirectory(tmp, gameDir, true);
        File.Delete(tmp);
        File.WriteAllText(localVer, remote);
        Console.WriteLine("Installed " + remote);
    }
    else Console.WriteLine("Up to date (" + local + ")");
}
catch (Exception e) { Console.WriteLine("Update check failed: " + e.Message + "\nLaunching installed build if present..."); }

if (File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = gameDir }); }
else { Console.WriteLine("No build installed and download failed. Check your connection."); Console.ReadKey(); }
