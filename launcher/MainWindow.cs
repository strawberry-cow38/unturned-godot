using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

// The launcher window. Code-only Avalonia. Owns the whole flow: resolve tools -> clone/refresh -> show current vs
// latest build -> Update (force pull + build + import) or Play (launch game with a debug console). All git/dotnet/godot
// work runs off the UI thread; output streams into the log panel via the Dispatcher.
public class MainWindow : Window
{
    const string RepoUrl = "https://github.com/strawberry-cow38/unturned-godot.git";
    const string Branch = "main";
    const string Solution = "game/UnturnedGodot.sln";
    const string BuildConfig = "Debug";

    enum Mode { Busy, Update, Play, Broken }

    readonly string _baseDir, _srcDir, _gameDir, _builtMarker;
    string _git, _dotnet, _godot;

    readonly TextBlock _currentLabel = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _latestLabel = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _status = new() { Foreground = Brushes.Gray };
    readonly TextBox _log;
    readonly Button _action = new() { MinWidth = 150, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 16, IsEnabled = false };
    Mode _mode = Mode.Busy;

    public MainWindow()
    {
        _baseDir = AppContext.BaseDirectory;
        _srcDir = Path.Combine(_baseDir, "source");
        _gameDir = Path.Combine(_srcDir, "game");
        _builtMarker = Path.Combine(_srcDir, ".ugh_built");   // records the commit WE last built; untracked, survives reset --hard

        Title = "Unturned Godot — Launcher";
        Width = 680; Height = 520; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#16181d"));

        _log = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12,
            Background = new SolidColorBrush(Color.Parse("#0d0f13")), Foreground = new SolidColorBrush(Color.Parse("#c8d0d8")),
            BorderThickness = new Avalonia.Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#2a2e36")),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Auto);

        _action.Click += async (_, _) => await OnActionAsync();

        Content = BuildLayout();
        _ = InitAsync();
    }

    Control BuildLayout()
    {
        var header = new TextBlock { Text = "UNTURNED · GODOT", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        var sub = new TextBlock { Text = "1:1 port launcher", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#7a828c")), Margin = new Avalonia.Thickness(0, 0, 0, 8) };

        var buildBox = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1d2027")), CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(12, 10), Margin = new Avalonia.Thickness(0, 0, 0, 10),
            Child = new StackPanel { Spacing = 6, Children = { _currentLabel, _latestLabel } },
        };
        _currentLabel.Foreground = new SolidColorBrush(Color.Parse("#c8d0d8"));
        _latestLabel.Foreground = new SolidColorBrush(Color.Parse("#c8d0d8"));

        var logHeader = new TextBlock { Text = "Debug console", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#7a828c")), Margin = new Avalonia.Thickness(2, 0, 0, 3) };

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        Grid.SetColumn(_status, 0); Grid.SetColumn(_action, 1);
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status); footer.Children.Add(_action);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Avalonia.Thickness(16) };
        void Row(Control c, int r) { Grid.SetRow(c, r); grid.Children.Add(c); }
        Row(header, 0); Row(sub, 1); Row(buildBox, 2); Row(logHeader, 3); Row(_log, 4); Row(footer, 5);
        return grid;
    }

    // ---- flow ----

    async Task InitAsync()
    {
        _git = ResolveOnPath("git");
        _dotnet = ResolveOnPath("dotnet");
        _godot = ResolveGodot();
        if (_git == null) { Fail("git not found on PATH."); return; }
        if (_dotnet == null) { Fail("dotnet SDK not found on PATH."); return; }
        if (_godot == null) { Fail("Godot mono exe not found. Set UNTURNED_GODOT_EXE or put godot on PATH."); return; }

        if (!Directory.Exists(Path.Combine(_srcDir, ".git")))
        {
            SetBusy("Cloning source…");
            Log($"$ git clone --depth 1 {RepoUrl} source");
            // shallow + single-branch: grab ONLY the latest snapshot, not 90+ MiB of history (the launcher always
            // force-resets to latest anyway, so history is dead weight -- this is the "turbo download" fix).
            if (await RunAsync(_git, new[] { "clone", "--depth", "1", "--single-branch", "--branch", Branch, RepoUrl, _srcDir }, _baseDir) != 0) { Fail("git clone failed (auth set up for the repo?)."); return; }
        }
        await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        SetBusy("Checking for updates…");
        Log("$ git fetch --depth 1 origin " + Branch);
        await RunAsync(_git, new[] { "fetch", "--depth", "1", "origin", Branch }, _srcDir);

        string localHash = await Capture(_git, new[] { "rev-parse", "--short", "HEAD" });
        string localDate = await Capture(_git, new[] { "show", "-s", "--format=%cd", "--date=format:%Y-%m-%d %H:%M", "HEAD" });
        string localMsg = await Capture(_git, new[] { "show", "-s", "--format=%s", "HEAD" });
        string remoteHash = await Capture(_git, new[] { "rev-parse", "--short", $"origin/{Branch}" });
        string remoteDate = await Capture(_git, new[] { "show", "-s", "--format=%cd", "--date=format:%Y-%m-%d %H:%M", $"origin/{Branch}" });
        string remoteMsg = await Capture(_git, new[] { "show", "-s", "--format=%s", $"origin/{Branch}" });
        // shallow clones have no history to rev-list, so compare tips: differ = update available (src is gospel).
        bool behind = !string.IsNullOrEmpty(remoteHash) && !string.IsNullOrEmpty(localHash) && remoteHash != localHash;
        // "built" = WE built this exact commit. Do NOT trust game/.godot existing -- the repo may ship a committed
        // (stale, machine-specific) .godot with pre-built assemblies, which would let a fresh clone "Play" a mismatched
        // dll and crash with "Cannot instantiate C# script res://Main.cs". Only our own build marker counts.
        bool built = false;
        try { built = File.Exists(_builtMarker) && File.ReadAllText(_builtMarker).Trim() == localHash && !string.IsNullOrEmpty(localHash); } catch { }

        Dispatcher.UIThread.Post(() =>
        {
            _currentLabel.Text = $"Current build:   {Or(localHash, "—")}   ·   {Or(localDate, "unknown")}\n   {Or(localMsg, "")}";
            _latestLabel.Text = $"Latest build:    {Or(remoteHash, "—")}   ·   {Or(remoteDate, "unknown")}\n   {Or(remoteMsg, "")}";
        });

        if (!built) SetMode(Mode.Update, "Install & Play", "First run — build needed.");
        else if (behind) SetMode(Mode.Update, "Update", "Update available.");
        else SetMode(Mode.Play, "Play", "Up to date.");
    }

    async Task OnActionAsync()
    {
        if (_mode == Mode.Update) await DoUpdateAsync();
        else if (_mode == Mode.Play) LaunchGame();
    }

    async Task DoUpdateAsync()
    {
        SetBusy("Updating…");
        Log("$ git fetch --depth 1 origin " + Branch + " && git reset --hard origin/" + Branch + "   (force — src is gospel)");
        await RunAsync(_git, new[] { "fetch", "--depth", "1", "origin", Branch }, _srcDir);
        if (await RunAsync(_git, new[] { "reset", "--hard", $"origin/{Branch}" }, _srcDir) != 0) { Log("!! git reset failed."); await RefreshAsync(); return; }

        SetBusy("Building…");
        Log($"$ dotnet build {Solution} -c {BuildConfig}");
        if (await RunAsync(_dotnet, new[] { "build", Solution, "-c", BuildConfig, "-v", "q", "-nologo" }, _srcDir) != 0)
        { Log("!! build failed — see output above."); SetMode(Mode.Update, "Retry update", "Build failed."); return; }

        SetBusy("Importing resources…");
        Log("$ godot --headless --import");
        await RunAsync(_godot, new[] { "--path", _gameDir, "--headless", "--import" }, _gameDir);

        string headNow = await Capture(_git, new[] { "rev-parse", "--short", "HEAD" });   // stamp the marker with the commit we just built
        try { File.WriteAllText(_builtMarker, headNow); } catch (Exception ex) { Log("(couldn't write build marker: " + ex.Message + ")"); }

        Log("Update complete.");
        await RefreshAsync();
    }

    void LaunchGame()
    {
        try
        {
            string exe = _godot;
            if (OperatingSystem.IsWindows())   // Godot mono ships a *_console.exe that pops a debug console window
            {
                string con = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] + "_console.exe" : exe + "_console";
                if (File.Exists(con)) exe = con;
                else Log("(no *_console.exe next to godot — launching without a separate debug window)");
            }
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = _gameDir };
            psi.ArgumentList.Add("--path");
            psi.ArgumentList.Add(_gameDir);
            Process.Start(psi);
            Log($"Launched: {Path.GetFileName(exe)} --path game — handing off, closing launcher.");
            SetBusy("Handing off to game…");
            // hand off to the game process (it's detached), then close the launcher window -> quits the app (strawberry)
            Dispatcher.UIThread.Post(async () => { await Task.Delay(600); Close(); });
        }
        catch (Exception ex) { Log("!! launch failed: " + ex.Message); }
    }

    // ---- helpers ----

    void SetMode(Mode m, string label, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _mode = m;
            _action.Content = label;
            _action.IsEnabled = m is Mode.Update or Mode.Play;
            _action.Background = new SolidColorBrush(Color.Parse(m == Mode.Play ? "#2e7d32" : "#1565c0"));
            _action.Foreground = Brushes.White;
            _status.Text = status;
        });
    }

    void SetBusy(string status)
    {
        Dispatcher.UIThread.Post(() => { _mode = Mode.Busy; _action.IsEnabled = false; _action.Content = "…"; _status.Text = status; });
    }

    void Fail(string msg)
    {
        Log("ERROR: " + msg);
        Dispatcher.UIThread.Post(() => { _mode = Mode.Broken; _action.IsEnabled = false; _action.Content = "—"; _status.Text = msg; _status.Foreground = new SolidColorBrush(Color.Parse("#e57373")); });
    }

    void Log(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _log.Text = (_log.Text ?? "") + line + "\n";
            _log.CaretIndex = _log.Text.Length;   // scroll to the end
        });
    }

    static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    // Run a process, streaming stdout+stderr into the log. Returns the exit code.
    async Task<int> RunAsync(string exe, string[] args, string wd)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = wd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { Log($"!! could not start {exe}: {ex.Message}"); return -1; }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // Run a process quietly and return trimmed stdout (for `git rev-parse` etc.); "" on failure.
    async Task<string> Capture(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = _srcDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            string outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return outp.Trim();
        }
        catch { return ""; }
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
}
