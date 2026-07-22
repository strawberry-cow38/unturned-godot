using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
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
    const string DefaultBranch = "main";
    string _branch = DefaultBranch;   // the tracked branch -- the dropdown switches it; persisted to branch.txt
    const string Solution = "game/UnturnedGodot.sln";
    const string BuildConfig = "Debug";

    // Self-update: this launcher's own version. Bump on every launcher change + upload the matching launcher.version
    // (a bare integer) + the new exe to the GitHub release. On startup we fetch launcher.version; if it's higher, we
    // download the new exe, hand off to a swap-helper, and relaunch -- so the launcher updates itself, no manual grab.
    const int LauncherVersion = 10;   // v10: on branch-list refresh, prune local refs (remote-tracking + local branches) for branches deleted on the remote -- guarded so an unreachable remote never wipes refs
    const string VersionUrl = "https://github.com/strawberry-cow38/unturned-godot/releases/download/launcher/launcher.version";
    const string ExeUrl = "https://github.com/strawberry-cow38/unturned-godot/releases/download/launcher/UnturnedGodotLauncher-win-x64.exe";
    // Godot 4.6 mono (win64) — matches the project's Godot.NET.Sdk/4.6.2; auto-downloaded if Godot isn't found.
    const string GodotUrl = "https://downloads.godotengine.org/?version=4.6&flavor=stable&slug=mono_win64.zip&platform=windows.64";
    // Unturned install — the game reads its real map terrain live from here (via the UG_UNTURNED_DIR env var it honors).
    static readonly string DefaultUnturnedDir = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned";
    string _unturnedDir;   // resolved (env / default / saved / user-picked), passed to the game as UG_UNTURNED_DIR on launch
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    enum Mode { Busy, Update, Play, Broken }

    readonly string _baseDir, _srcDir, _gameDir, _builtMarker;
    string _git, _dotnet, _godot;

    readonly TextBlock _currentLabel = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _latestLabel = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _status = new() { Foreground = Brushes.Gray };
    readonly TextBox _log;
    readonly Button _action = new() { MinWidth = 150, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 16, IsEnabled = false };
    readonly ComboBox _branchBox = new() { MinWidth = 220, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };   // branch selector (populated from the remote after clone)
    // (The old "Multiplayer test" checkbox was removed -- MP is now a top-level "Multiplayer" button on the
    // in-game main menu, which connects to claw.bitvox.me itself. Server browser later.)
    Mode _mode = Mode.Busy;

    public MainWindow()
    {
        _baseDir = AppContext.BaseDirectory;
        _srcDir = Path.Combine(_baseDir, "source");
        _gameDir = Path.Combine(_srcDir, "game");
        _builtMarker = Path.Combine(_srcDir, ".ugh_built");   // records the commit WE last built; untracked, survives reset --hard
        _branch = LoadBranch();   // the persisted branch selection (default main); the dropdown updates it

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
        // right side: just the Play button (the MP-test checkbox moved in-game).
        var rightSide = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Children = { _action } };
        Grid.SetColumn(_status, 0); Grid.SetColumn(rightSide, 1);
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status); footer.Children.Add(rightSide);

        var branchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 10),
            Children =
            {
                new TextBlock { Text = "Branch:", Foreground = new SolidColorBrush(Color.Parse("#7a828c")), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 },
                _branchBox,
            },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto"), Margin = new Avalonia.Thickness(16) };
        void Row(Control c, int r) { Grid.SetRow(c, r); grid.Children.Add(c); }
        Row(header, 0); Row(sub, 1); Row(branchRow, 2); Row(buildBox, 3); Row(logHeader, 4); Row(_log, 5); Row(footer, 6);
        return grid;
    }

    // ---- flow ----

    async Task InitAsync()
    {
        if (await CheckSelfUpdateAsync()) return;   // a newer launcher exists -> we downloaded it, spawned the swap-helper, and are closing
        _git = ResolveOnPath("git");
        _dotnet = ResolveOnPath("dotnet");
        _godot = await EnsureGodotAsync();   // UNTURNED_GODOT_EXE / PATH / prior auto-download, else fetch Godot 4.6 mono
        _unturnedDir = ResolveUnturnedDirSilent();   // env / saved pick / default Steam path (no prompt here; the picker fires at Play time)
        if (_git == null) { Fail("git not found on PATH."); return; }
        if (_dotnet == null) { Fail("dotnet SDK not found on PATH."); return; }
        if (_godot == null) { Fail("Godot not found and the auto-download failed. Set UNTURNED_GODOT_EXE, put godot on PATH, or check your connection."); return; }

        if (!Directory.Exists(Path.Combine(_srcDir, ".git")))
        {
            SetBusy("Cloning source…");
            Log($"$ git clone --depth 1 --branch {_branch} {RepoUrl} source");
            // shallow + single-branch: grab ONLY the latest snapshot, not 90+ MiB of history (the launcher always
            // force-resets to latest anyway, so history is dead weight -- this is the "turbo download" fix).
            if (await RunAsync(_git, new[] { "clone", "--depth", "1", "--single-branch", "--branch", _branch, RepoUrl, _srcDir }, _baseDir) != 0) { Fail($"git clone failed (branch '{_branch}' exists + auth set up?)."); return; }
        }
        await PopulateBranchesAsync();   // fill the dropdown from the remote (origin exists now)
        await RefreshAsync();
    }

    // Self-update the launcher exe. Returns true if an update is underway (caller must stop -- we're closing). Windows
    // only: a running .exe can't overwrite itself, so we download the new exe beside the current one, then hand off to a
    // tiny .bat that waits for THIS process to exit, swaps the file, relaunches, and deletes itself.
    async Task<bool> CheckSelfUpdateAsync()
    {
        if (!OperatingSystem.IsWindows()) return false;
        string exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;
        try
        {
            SetBusy("Checking launcher version…");
            int remote = int.TryParse((await Http.GetStringAsync(VersionUrl)).Trim(), out var r) ? r : 0;
            if (remote <= LauncherVersion) return false;
            Log($"Launcher update v{LauncherVersion} -> v{remote}. Downloading…");
            SetBusy("Updating launcher…");
            var bytes = await Http.GetByteArrayAsync(ExeUrl);
            // VERIFY before swapping: a truncated download, a redirect/HTML error body, or a partial write must NEVER
            // overwrite a working launcher (that bricks it -- exactly the failure this guards). A real self-contained
            // exe is ~75 MB and starts with the PE "MZ" magic. Anything else -> abort, keep running the current one.
            if (bytes.Length < 10_000_000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                Log($"(launcher self-update ABORTED: got {bytes.Length} bytes, not a valid exe — keeping the current launcher)");
                return false;
            }
            string newExe = exePath + ".new";
            await File.WriteAllBytesAsync(newExe, bytes);
            int pid = Environment.ProcessId;
            string bat = Path.Combine(Path.GetTempPath(), "ugh_selfupdate.bat");
            const string q = "\"";
            await File.WriteAllTextAsync(bat, string.Join("\r\n", new[]
            {
                "@echo off",
                ":wait",
                $"tasklist /FI {q}PID eq {pid}{q} | find {q}{pid}{q} >nul && (ping -n 2 127.0.0.1 >nul & goto wait)",
                $"move /y {q}{newExe}{q} {q}{exePath}{q} >nul",
                $"start {q}{q} {q}{exePath}{q}",
                $"del {q}%~f0{q}",
            }) + "\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {q}{bat}{q}") { UseShellExecute = false, CreateNoWindow = true });
            Log("Launcher downloaded — restarting into the new version…");
            Dispatcher.UIThread.Post(() => Close());
            return true;
        }
        catch (Exception ex) { Log("(launcher self-update skipped: " + ex.Message + ")"); return false; }
    }

    // Fetch the SELECTED branch with an EXPLICIT refspec so origin/<branch> always exists locally. The initial clone is
    // --single-branch (only main's tracking ref + fetch config), so a plain `git fetch origin <other>` updates FETCH_HEAD
    // but may not create origin/<other> -- which the rev-parse/reset below need. The +<branch>:refs/remotes/origin/<branch>
    // form force-creates the remote-tracking ref for ANY branch, so branch-switching Just Works on the shallow clone.
    async Task<int> FetchBranchAsync() =>
        await RunAsync(_git, new[] { "fetch", "--depth", "1", "origin", $"+{_branch}:refs/remotes/origin/{_branch}" }, _srcDir);

    async Task RefreshAsync()
    {
        SetBusy("Checking for updates…");
        Log($"$ git fetch --depth 1 origin {_branch}:refs/remotes/origin/{_branch}");
        int fetchRc = await FetchBranchAsync();

        string localHash = await Capture(_git, new[] { "rev-parse", "--short", "HEAD" });
        string localDate = await Capture(_git, new[] { "show", "-s", "--format=%cd", "--date=format:%Y-%m-%d %H:%M", "HEAD" });
        string localMsg = await Capture(_git, new[] { "show", "-s", "--format=%s", "HEAD" });
        string remoteHash = await Capture(_git, new[] { "rev-parse", "--short", $"origin/{_branch}" });
        string remoteDate = await Capture(_git, new[] { "show", "-s", "--format=%cd", "--date=format:%Y-%m-%d %H:%M", $"origin/{_branch}" });
        string remoteMsg = await Capture(_git, new[] { "show", "-s", "--format=%s", $"origin/{_branch}" });

        // fetch failed / the branch doesn't exist on the remote -> never misreport "up to date". Let the user retry or
        // reselect; if there's a local tree we can still rebuild/switch, otherwise it's broken.
        if (fetchRc != 0 || string.IsNullOrEmpty(remoteHash))
        {
            bool haveLocal = !string.IsNullOrEmpty(localHash);
            Dispatcher.UIThread.Post(() =>
            {
                _currentLabel.Text = $"Current build:   {Or(localHash, "—")}   ·   {Or(localDate, "unknown")}\n   {Or(localMsg, "")}";
                _latestLabel.Text = $"Latest build:    (couldn't reach origin/{_branch})";
            });
            if (haveLocal) SetMode(Mode.Update, "Retry", $"Couldn't reach branch '{_branch}' — retry, or pick another.");
            else Fail($"Couldn't reach branch '{_branch}' on the remote.");
            return;
        }

        // shallow clones have no history to rev-list, so compare tips: differ = update available (src is gospel). After a
        // branch SWITCH, HEAD is still the old branch's commit (working tree not reset yet) -> differs -> "Update", which
        // performs the switch on click. Same commit across branches = identical tree = genuinely up to date.
        bool behind = remoteHash != localHash && !string.IsNullOrEmpty(localHash);
        // "built" = WE built this exact commit. Do NOT trust game/.godot existing -- the repo may ship a committed
        // (stale, machine-specific) .godot with pre-built assemblies, which would let a fresh clone "Play" a mismatched
        // dll and crash with "Cannot instantiate C# script res://Main.cs". Only our own build marker counts. On a branch
        // switch the marker holds the OLD commit != the new HEAD -> not "built" -> a rebuild is forced. Correct.
        bool built = false;
        try { built = File.Exists(_builtMarker) && File.ReadAllText(_builtMarker).Trim() == localHash && !string.IsNullOrEmpty(localHash); } catch { }

        Dispatcher.UIThread.Post(() =>
        {
            _currentLabel.Text = $"Current build:   {Or(localHash, "—")}   ·   {Or(localDate, "unknown")}\n   {Or(localMsg, "")}";
            _latestLabel.Text = $"Latest build:    {Or(remoteHash, "—")}   ·   {Or(remoteDate, "unknown")}\n   {Or(remoteMsg, "")}";
        });

        if (!built) SetMode(Mode.Update, behind ? "Switch & build" : "Install & Play", behind ? $"On '{_branch}' — build needed." : "First run — build needed.");
        else if (behind) SetMode(Mode.Update, "Update", "Update available.");
        else SetMode(Mode.Play, "Play", "Up to date.");
    }

    // Fill the branch dropdown from the remote. `git ls-remote --heads origin` hits the remote directly, so it lists ALL
    // branches even though our working clone is --single-branch. Falls back to just the current + default branch if the
    // remote can't be reached. The change handler is (re)wired AFTER setting the initial selection so restoring the saved
    // branch never fires a spurious switch.
    async Task PopulateBranchesAsync()
    {
        var branches = new List<string>();
        string outp = await Capture(_git, new[] { "ls-remote", "--heads", "origin" });
        bool remoteReached = !string.IsNullOrWhiteSpace(outp);
        if (remoteReached)
            foreach (var line in outp.Split('\n'))
            {
                const string mark = "refs/heads/";
                int i = line.IndexOf(mark);
                if (i >= 0) branches.Add(line.Substring(i + mark.Length).Trim());
            }
        // prune local refs for branches deleted on the remote. GUARD: only when ls-remote actually reached origin --
        // an unreachable remote returns an EMPTY list, and pruning against that would wipe every local ref on a blip.
        if (remoteReached)
            await PruneDeletedBranchesAsync(new HashSet<string>(branches.Where(b => !string.IsNullOrWhiteSpace(b)), StringComparer.Ordinal));
        if (!branches.Contains(DefaultBranch)) branches.Add(DefaultBranch);   // always offer main
        if (!branches.Contains(_branch)) branches.Add(_branch);               // keep a saved (maybe deleted) branch selectable
        branches = branches.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct()
                           .OrderBy(b => b == DefaultBranch ? "\0" : b, StringComparer.OrdinalIgnoreCase).ToList();   // main first, then alpha
        Dispatcher.UIThread.Post(() =>
        {
            _branchBox.SelectionChanged -= OnBranchChanged;   // don't fire while (re)binding
            _branchBox.ItemsSource = branches;
            _branchBox.SelectedItem = branches.Contains(_branch) ? _branch : DefaultBranch;
            _branchBox.SelectionChanged += OnBranchChanged;
        });
    }

    // Clean out local refs for branches that no longer exist on the remote (e.g. a merged PR's branch was deleted).
    // `live` = the branch names ls-remote just returned. Deletes stale remote-tracking refs (origin/<x>) and any local
    // branch whose remote is gone -- but NEVER the currently checked-out branch or main, and only when the remote was
    // actually reached (the caller guards on that, so a network failure can't nuke everything). Best-effort + logged.
    async Task PruneDeletedBranchesAsync(HashSet<string> live)
    {
        string cur = (await Capture(_git, new[] { "rev-parse", "--abbrev-ref", "HEAD" }))?.Trim() ?? "";

        // stale remote-tracking refs: refs/remotes/origin/<x> where <x> isn't a live remote branch
        string rt = await Capture(_git, new[] { "for-each-ref", "--format=%(refname:short)", "refs/remotes/origin" });
        foreach (var raw in (rt ?? "").Split('\n'))
        {
            string r = raw.Trim();                                  // e.g. "origin/feature-x"
            if (r.Length == 0 || r == "origin" || r.EndsWith("/HEAD")) continue;
            string name = r.StartsWith("origin/") ? r.Substring("origin/".Length) : r;
            if (name.Length == 0 || live.Contains(name)) continue;
            if (await RunAsync(_git, new[] { "branch", "-rd", r }, _srcDir) == 0) Log($"pruned deleted remote branch {r}");
        }

        // local branches whose upstream is gone -- never the current branch or main (safety)
        string lb = await Capture(_git, new[] { "for-each-ref", "--format=%(refname:short)", "refs/heads" });
        foreach (var raw in (lb ?? "").Split('\n'))
        {
            string b = raw.Trim();
            if (b.Length == 0 || b == cur || b == DefaultBranch || live.Contains(b)) continue;
            if (await RunAsync(_git, new[] { "branch", "-D", b }, _srcDir) == 0) Log($"pruned local branch {b} (deleted on remote)");
        }
    }

    async void OnBranchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mode == Mode.Busy) return;   // ignore selection churn mid-operation
        if (_branchBox.SelectedItem is not string sel || sel == _branch) return;
        _branch = sel;
        SaveBranch(sel);
        Log($"Branch -> {sel}");
        await RefreshAsync();   // re-fetch the new branch + show if a switch/build is needed (the switch itself happens on Update)
    }

    async Task OnActionAsync()
    {
        if (_mode == Mode.Update) await DoUpdateAsync();
        else if (_mode == Mode.Play) await LaunchGame();
    }

    async Task DoUpdateAsync()
    {
        SetBusy("Updating…");
        Log($"$ git fetch --depth 1 origin {_branch} && git reset --hard origin/{_branch}   (force — src is gospel)");
        if (await FetchBranchAsync() != 0) { Log($"!! fetch failed for branch '{_branch}'."); await RefreshAsync(); return; }
        if (await RunAsync(_git, new[] { "reset", "--hard", $"origin/{_branch}" }, _srcDir) != 0) { Log("!! git reset failed."); await RefreshAsync(); return; }

        SetBusy("Building…");
        Log($"$ dotnet build {Solution} -c {BuildConfig}");
        if (await RunAsync(_dotnet, new[] { "build", Solution, "-c", BuildConfig, "-v", "q", "-nologo" }, _srcDir) != 0)
        { Log("!! build failed — see output above."); SetMode(Mode.Update, "Retry update", "Build failed."); return; }

        SetBusy("Importing resources…");
        Log("$ godot --headless --import");
        await RunAsync(_godot, new[] { "--path", _gameDir, "--headless", "--import" }, _gameDir);

        string headNow = await Capture(_git, new[] { "rev-parse", "--short", "HEAD" });   // stamp the marker with the commit we just built
        try { File.WriteAllText(_builtMarker, headNow); } catch (Exception ex) { Log("(couldn't write build marker: " + ex.Message + ")"); }

        Log("Update complete — launching.");
        await LaunchGame();   // strawberry: after an update/install finishes, go straight into the game (no second click)
    }

    async Task LaunchGame()
    {
        try
        {
            // The game reads the real map terrain live from a local Unturned install. Resolve it (env/saved/default was
            // tried silently at startup); if still unknown, prompt for the folder now and remember it for next time.
            if (_unturnedDir == null) _unturnedDir = await PickAndSaveUnturnedDirAsync();
            if (_unturnedDir != null)
            {
                Environment.SetEnvironmentVariable("UG_UNTURNED_DIR", _unturnedDir);   // the godot child inherits the launcher's env (UseShellExecute)
                Log("Unturned: " + _unturnedDir);
            }
            else Log("(no Unturned install selected — the real map won't load; install Unturned or pick its folder next launch)");

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
            // (MP test connect moved in-game: the main menu's "Multiplayer" button connects to claw.bitvox.me.)
            Process.Start(psi);
            Log($"Launched: {Path.GetFileName(exe)} --path game — handing off, closing launcher.");
            SetBusy("Handing off to game…");
            // hand off to the game process (it's detached), then close the launcher window -> quits the app (strawberry)
            Dispatcher.UIThread.Post(async () => { await Task.Delay(600); Close(); });
        }
        catch (Exception ex) { Log("!! launch failed: " + ex.Message); await RefreshAsync(); }   // recover to a clickable state
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

    // ---- Godot auto-download (4.6 mono win64, matches the project's Godot.NET.Sdk) ----
    async Task<string> EnsureGodotAsync()
    {
        var found = ResolveGodot();
        if (found != null) return found;
        string dir = Path.Combine(_baseDir, "godot");
        var have = FindGodotExe(dir);
        if (have != null) { Log("Godot (auto-downloaded): " + have); return have; }
        try
        {
            Log("Godot not found — downloading Godot 4.6 mono (win64), ~104 MB…");
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "godot46_mono_win64.zip");
            var bytes = await Http.GetByteArrayAsync(GodotUrl);
            await File.WriteAllBytesAsync(zip, bytes);
            Log("Extracting Godot…");
            ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
            try { File.Delete(zip); } catch { }
            var exe = FindGodotExe(dir);
            Log(exe != null ? "Godot 4.6 ready: " + exe : "!! Godot extracted but the editor exe wasn't found.");
            return exe;
        }
        catch (Exception ex) { Log("!! Godot download failed: " + ex.Message); return null; }
    }

    // the mono win64 zip extracts a Godot_v4.6-stable_mono_win64/ folder; take the editor exe, skip the *_console.exe.
    static string FindGodotExe(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "Godot_v*_win64.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains("console", StringComparison.OrdinalIgnoreCase))
            : null;

    // ---- branch selection persistence (remembers the dropdown choice across launches) ----
    string BranchConfig => Path.Combine(_baseDir, "branch.txt");
    string LoadBranch()
    {
        try { if (File.Exists(BranchConfig)) { var b = File.ReadAllText(BranchConfig).Trim(); if (!string.IsNullOrWhiteSpace(b)) return b; } } catch { }
        return DefaultBranch;
    }
    void SaveBranch(string b) { try { File.WriteAllText(BranchConfig, b); } catch (Exception ex) { Log("(couldn't save branch: " + ex.Message + ")"); } }

    // ---- Unturned install resolution (the game reads its real map terrain from here via UG_UNTURNED_DIR) ----
    string UnturnedDirConfig => Path.Combine(_baseDir, "unturned_dir.txt");

    // a usable Unturned install = it has the PEI map terrain the default play mode loads.
    static bool IsUnturnedDir(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(Path.Combine(dir, "Maps", "PEI", "Landscape"));

    // env var -> saved pick -> default Steam path. No prompt here (the picker fires at Play time if this returns null).
    string ResolveUnturnedDirSilent()
    {
        var env = Environment.GetEnvironmentVariable("UG_UNTURNED_DIR");
        if (IsUnturnedDir(env)) return env;
        string saved = null;
        try { if (File.Exists(UnturnedDirConfig)) saved = File.ReadAllText(UnturnedDirConfig).Trim(); } catch { }
        if (IsUnturnedDir(saved)) return saved;
        if (IsUnturnedDir(DefaultUnturnedDir)) return DefaultUnturnedDir;
        return null;
    }

    // prompt for the Unturned folder, validate + persist it. Returns the chosen dir (or null if cancelled).
    async Task<string> PickAndSaveUnturnedDirAsync()
    {
        Log("Unturned not found automatically — pick your Unturned install folder (the one containing 'Maps').");
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        { Title = "Select your Unturned install folder", AllowMultiple = false });
        if (picked.Count == 0) return null;
        string dir = picked[0].Path.LocalPath;
        if (!IsUnturnedDir(dir))
            Log("(that folder has no Maps\\PEI — saving it anyway, but the map may not load; pick the folder that contains 'Maps'.)");
        try { File.WriteAllText(UnturnedDirConfig, dir); } catch (Exception ex) { Log("(couldn't save Unturned path: " + ex.Message + ")"); }
        return dir;
    }
}
