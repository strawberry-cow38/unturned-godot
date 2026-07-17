using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

// Avalonia GUI launcher for Unturned Godot. Replaces the old console launcher: it clones/force-pulls the source
// repo, shows the current vs latest build, updates on demand (git reset --hard -- src is gospel, no merge conflicts),
// builds + imports, then launches the game with a debug console. Code-only Avalonia (no XAML) so it stays one small
// project. Runs on Windows (WinExe) and, since it's plain net8.0 Avalonia, builds/runs on Linux/Mac too.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // This is a WinExe (no console) -> an unhandled startup/async exception dies SILENTLY with no window and no
        // trace. Log every fatal path to a file next to the exe so a "launcher just closes" report is diagnosable.
        // The self-update runs fire-and-forget (async), so an AppDomain-level + unobserved-task hook is required --
        // a plain try/catch around Main would miss it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("UnobservedTaskException", e.Exception); e.SetObserved(); };
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { LogCrash("Main", ex); throw; }
    }

    static void LogCrash(string where, Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "launcher-crash.log");   // exe's own dir (self-update writes here, so it's writable)
            File.AppendAllText(path, $"[{DateTime.Now:u}] {where}: {ex?.ToString() ?? "(null)"}\n\n");
        }
        catch { /* logging must never itself crash the crash handler */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
