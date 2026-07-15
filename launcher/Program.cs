using Avalonia;

// Avalonia GUI launcher for Unturned Godot. Replaces the old console launcher: it clones/force-pulls the source
// repo, shows the current vs latest build, updates on demand (git reset --hard -- src is gospel, no merge conflicts),
// builds + imports, then launches the game with a debug console. Code-only Avalonia (no XAML) so it stays one small
// project. Runs on Windows (WinExe) and, since it's plain net8.0 Avalonia, builds/runs on Linux/Mac too.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
