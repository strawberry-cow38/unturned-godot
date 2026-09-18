using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using SDG.Unturned;   // ProfileRules -- linked in, see the csproj

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
    // BUMP THIS WITH EVERY LAUNCHER CHANGE, and publish the release -- self-update only fires when the
    // published launcher.version is GREATER than this. I shipped the report-key field without bumping it,
    // so nobody's launcher updated and the field simply did not exist for them. The code change is only
    // half of a launcher change; the other half is this number plus the release.
    const int LauncherVersion = 16;   // v16: UI restyled to the in-game theme; Options disclosure (debug console / offline / report key / launcher log); "Check for update" button; debug-console toggle now works in BOTH directions; branch selection no longer dropped when made mid-operation
    // v15: sign-in goes through stmauth -- what is stored is a SIGNED token bound to a keypair this box holds (AuthClient), not a SteamID only this launcher believes. UG_AUTH_TOKEN + UG_AUTH_KEY (path, never the value)
    // ⚠ THE LINE ABOVE DESCRIBED v13 WHILE THE CONSTANT SAID 15, because I bumped it twice today and updated
    // neither. That is the same rot the NetProtocol "the live server is v45" comment had -- a fact stapled to
    // a number that moves without it. The number is the release; the note is what shipped in it. Move both.
    // v14: Offline mode toggle -> offline_mode.txt -> UG_OFFLINE (hides Multiplayer + Direct Connect in game)
    // v13: identity via Steam OpenID -- the typed name + picture picker are GONE; name/avatar/SteamID come from a verified sign-in -> UG_USERNAME / UG_PROFILE_PNG / UG_STEAMID
    // v11: Report key row (paste once) -> bugreport_key.txt -> UG_BUGREPORT_KEY for the game
    // v10: on branch-list refresh, prune local refs (remote-tracking + local branches) for branches deleted on the remote -- guarded so an unreachable remote never wipes refs
    const string VersionUrl = "https://github.com/strawberry-cow38/unturned-godot/releases/download/launcher/launcher.version";
    const string ExeUrl = "https://github.com/strawberry-cow38/unturned-godot/releases/download/launcher/UnturnedGodotLauncher-win-x64.exe";
    // Godot 4.6 mono (win64) — matches the project's Godot.NET.Sdk/4.6.2; auto-downloaded if Godot isn't found.
    const string GodotUrl = "https://downloads.godotengine.org/?version=4.6&flavor=stable&slug=mono_win64.zip&platform=windows.64";
    // Unturned install — the game reads its real map terrain live from here (via the UG_UNTURNED_DIR env var it honors).
    static readonly string DefaultUnturnedDir = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned";
    string _unturnedDir;   // resolved (env / default / saved / user-picked), passed to the game as UG_UNTURNED_DIR on launch
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    // ---- palette ------------------------------------------------------------------------------------
    // The in-game UI's colours, so the launcher and the game look like one product (strawberry 2026-09-17:
    // "more closely match the style of the inventory, etc ui"). Hex transcribed from game/UITheme.cs, which
    // stays the source of truth -- it is a Godot file (Godot.Color) and cannot be linked into Avalonia, so
    // the float values are written beside each one to make a drift review possible by eye rather than by
    // guess. UITheme.FontHeading is 16 and FontBody 13; those are used verbatim below.
    static SolidColorBrush B(string hex) => new(Color.Parse(hex));
    static readonly IBrush BgSolid    = B("#212124");   // UITheme.BgSolid   0.13 0.13 0.14
    static readonly IBrush BarSolid   = B("#303033");   // UITheme.BarSolid  0.19 0.19 0.20
    static readonly IBrush PanelEdge  = B("#3c3c41");   // the hairline on a raised card (UITheme.Border over BarSolid)
    static readonly IBrush TextMain   = B("#E0E0E8");   // UITheme.Text      0.88 0.88 0.91
    static readonly IBrush TextBody   = B("#C9C9C9");   // UITheme.TextBody  0.79 0.79 0.79
    static readonly IBrush TextDim    = B("#8C8F99");   // UITheme.TextDim   0.55 0.56 0.60
    static readonly IBrush Accent     = B("#9EC7F0");   // UITheme.Accent    0.62 0.78 0.94  (steel blue)
    static readonly IBrush Good       = B("#9ED199");   // UITheme.Good      0.62 0.82 0.60
    static readonly IBrush Bad        = B("#DB8575");   // UITheme.Bad       0.86 0.52 0.46
    const int FontHeading = 16, FontBody = 13;          // UITheme.FontHeading / FontBody

    enum Mode { Busy, Update, Play, Broken }

    readonly string _baseDir, _srcDir, _gameDir, _builtMarker;
    string _git, _dotnet, _godot;

    readonly TextBlock _currentLabel = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _latestLabel = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _status = new();   // Foreground set from the palette in BuildLayout
    readonly TextBox _log;
    readonly Button _steamButton = new() { Content = "Sign in through Steam", MinWidth = 170 };
    readonly TextBlock _nameStatus = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    readonly Avalonia.Controls.Image _pfpPreview = new() { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _keyBox = new() { Width = 260, Watermark = "paste key, then Save", FontSize = 13 };
    readonly TextBlock _keyStatus = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    readonly Button _action = new() { MinWidth = 150, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 16, IsEnabled = false };
    readonly CheckBox _consoleCheck = new() { Content = "Debug console window", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _offlineCheck = new() { Content = "Offline mode", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox _branchBox = new() { MinWidth = 220, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };   // branch selector (populated from the remote after clone)
    // (The old "Multiplayer test" checkbox was removed -- MP is now a top-level "Multiplayer" button on the
    // in-game main menu, which connects to claw.bitvox.me itself. Server browser later.)
    readonly Button _checkBtn = new() { Content = "Check for update", MinWidth = 130, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    // OPTIONS live behind a disclosure rather than on the front page (strawberry 2026-09-17: "hide the debug
    // console we have behind an options menu, which also holds the feedback key stuff"). The launcher's job
    // is branch + Play; everything you set once belongs one click away, not in the way every launch.
    readonly ToggleButton _optionsToggle = new() { Content = "Options", MinWidth = 90, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    Border _optionsPanel;          // the disclosure body -- built in BuildLayout, shown by the toggle
    StackPanel _logPanel;          // the launcher's own output; hidden unless asked for
    readonly CheckBox _showLogCheck = new() { Content = "Show launcher log", FontSize = FontBody, VerticalAlignment = VerticalAlignment.Center };
    // Serialises branch refreshes so a selection made mid-refresh is QUEUED, never dropped. See RefreshCoalescer.
    readonly RefreshCoalescer _refreshes = new();
    Mode _mode = Mode.Busy;

    public MainWindow()
    {
        _baseDir = AppContext.BaseDirectory;
        _srcDir = Path.Combine(_baseDir, "source");
        _gameDir = Path.Combine(_srcDir, "game");
        _builtMarker = Path.Combine(_srcDir, ".ugh_built");   // records the commit WE last built; untracked, survives reset --hard
        _branch = LoadBranch();   // the persisted branch selection (default main); the dropdown updates it

        Title = "Unturned Godot — Launcher";
        // Avalonia does NOT inherit the window icon from <ApplicationIcon> (that is the exe's shell icon only),
        // so it is set here as well. Embedded via avares:// rather than read from disk: the launcher updates by
        // replacing a single exe, so a loose icon file beside it is one the updater never refreshes.
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://UnturnedGodotLauncher/Assets/unturned.png"))); }
        catch { }   // a missing icon must never stop the launcher opening -- it is decoration, the Play button is not
        // ⚠ SIZES TO ITS CONTENT rather than to a fixed 680x520. With Options and the log both collapsed the
        // old fixed height left a large empty band between the build box and the Play button -- the window
        // looked like it had failed to finish drawing. Every row is Auto and the log has an explicit height,
        // so opening either disclosure grows the window and closing it takes the space back.
        Width = 700; MinWidth = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BgSolid;   // cards sit on this in BarSolid, the same panel-on-backdrop relationship the in-game UI uses

        _log = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12,
            Background = B("#17171A"), Foreground = TextBody,   // a well, darker than the backdrop -- it is output, not chrome
            BorderThickness = new Avalonia.Thickness(1), BorderBrush = PanelEdge,
        };
        _log.Height = 260;   // explicit, because the grid is all-Auto now -- see SizeToContent in the ctor
        ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Auto);

        _action.Click += async (_, _) => await OnActionAsync();

        Content = BuildLayout();
        _ = InitAsync();
    }

    Control BuildLayout()
    {
        // ⚠ NO TAGLINE. The old layout opened with "1:1 port launcher" under the title -- a line that told a
        // player nothing they could act on and cost a row on a 520px window (strawberry 2026-09-17: "remove
        // unecessary crap, '1:1 port launcher' text"). The title alone says what this is.
        var header = new TextBlock
        {
            Text = "UNTURNED · GODOT", FontSize = 22, FontWeight = FontWeight.Bold,
            Foreground = TextMain, Margin = new Avalonia.Thickness(0, 0, 0, 12),
        };

        TextBlock Dim(string t) => new() { Text = t, Foreground = TextDim, VerticalAlignment = VerticalAlignment.Center, FontSize = FontBody };
        Border Card(Control body) => new()
        {
            Background = BarSolid, CornerRadius = new Avalonia.CornerRadius(4),
            BorderThickness = new Avalonia.Thickness(1), BorderBrush = PanelEdge,
            Padding = new Avalonia.Thickness(12, 10), Child = body,
        };

        // ---- build state ---------------------------------------------------------------------------
        _currentLabel.Foreground = TextBody; _currentLabel.FontSize = FontBody;
        _latestLabel.Foreground = TextBody;  _latestLabel.FontSize = FontBody;
        var buildBox = Card(new StackPanel { Spacing = 6, Children = { _currentLabel, _latestLabel } });
        buildBox.Margin = new Avalonia.Thickness(0, 0, 0, 10);

        // ---- branch + the two things you do to it --------------------------------------------------
        // "Check for update" is explicit now (strawberry: "add a refresh 'check for update' button for the
        // currently selected branch"). It was only ever implicit -- a refresh happened on launch and on a
        // branch change, so a user watching for a commit that landed a minute ago had to restart the launcher.
        _checkBtn.Click += async (_, _) => await RequestRefreshAsync();
        var branchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        var bLabel = Dim("Branch:"); bLabel.Margin = new Avalonia.Thickness(0, 0, 8, 0);
        _checkBtn.Margin = new Avalonia.Thickness(8, 0, 0, 0);
        Grid.SetColumn(bLabel, 0); Grid.SetColumn(_branchBox, 1); Grid.SetColumn(_checkBtn, 2); Grid.SetColumn(_optionsToggle, 4);
        branchRow.Children.Add(bLabel); branchRow.Children.Add(_branchBox);
        branchRow.Children.Add(_checkBtn); branchRow.Children.Add(_optionsToggle);

        // ---- profile: the name and picture other players see --------------------------------------
        // IDENTITY COMES FROM STEAM, not from a box you type in (strawberry 2026-09-16: "just the steam
        // auth. one button on the launcher, opens in browser, sign in, get ID"). A self-asserted name is not
        // an identity, and bans used to key on ip+name, both of which a player can change at will.
        _nameStatus.Foreground = TextDim;
        _steamButton.Click += async (_, _) => await SignInWithSteamAsync();
        var signOut = new Button { Content = "Sign out", MinWidth = 80, FontSize = 12 };
        signOut.Click += (_, _) => SignOutOfSteam();
        var profileRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 10),
            Children = { Dim("Profile:"), _steamButton, signOut, _pfpPreview, _nameStatus },
        };
        RefreshProfileStatus();

        // ---- options (collapsed) -------------------------------------------------------------------
        // Godot mono ships BOTH godot.exe and godot_console.exe; the console build is a separate binary that
        // allocates a Windows console, it is not a flag. So the toggle picks the executable -- see Play(),
        // which routes through LauncherRules.GodotExeFor so the choice is SYMMETRIC (the old code could only
        // ever turn the console on, never off).
        // ⚠ DEFAULT ON, which is the behaviour every existing install already has. A launcher that quietly
        // stops showing you the log the day it updates is a worse surprise than an unticked box.
        _consoleCheck.IsChecked = LoadDebugConsole();
        _consoleCheck.IsCheckedChanged += (_, _) => SaveDebugConsole(_consoleCheck.IsChecked == true);
        ToolTip.SetTip(_consoleCheck, "Launches the game through Godot's console build, which opens a log window alongside it.");
        // ⚠ DEFAULT OFF, for the same reason the console defaults ON: a fresh install must behave like every
        // existing one, and every existing one has multiplayer. Nobody's launcher grows a new restriction.
        _offlineCheck.IsChecked = LoadOfflineMode();
        _offlineCheck.IsCheckedChanged += (_, _) => SaveOfflineMode(_offlineCheck.IsChecked == true);
        ToolTip.SetTip(_offlineCheck, "Hides Multiplayer and Direct Connect in game. Singleplayer is unaffected.");

        _keyBox.PasswordChar = '\u2022';   // not security (the file it writes is plaintext) -- a key you
                                            // paste is a key someone screen-sharing can otherwise read back
        _keyStatus.Foreground = TextDim;
        var saveKey = new Button { Content = "Save", MinWidth = 70, FontSize = 12 };
        saveKey.Click += (_, _) => SaveReportKey(_keyBox.Text ?? "");
        var keyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Dim("Report key:"), _keyBox, saveKey, _keyStatus },
        };
        RefreshKeyStatus();

        _showLogCheck.Foreground = TextBody;
        _showLogCheck.IsChecked = false;
        _showLogCheck.IsCheckedChanged += (_, _) => { if (_logPanel != null) _logPanel.IsVisible = _showLogCheck.IsChecked == true; };
        _consoleCheck.Foreground = TextBody; _offlineCheck.Foreground = TextBody;

        _optionsPanel = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _consoleCheck, _offlineCheck } },
                keyRow,
                _showLogCheck,
            },
        });
        _optionsPanel.Margin = new Avalonia.Thickness(0, 0, 0, 10);
        _optionsPanel.IsVisible = false;
        _optionsToggle.IsCheckedChanged += (_, _) => _optionsPanel.IsVisible = _optionsToggle.IsChecked == true;

        // ---- the launcher's own output, hidden by default ------------------------------------------
        // This is the "debug console we have" -- our git/build/godot transcript, not the game's. It is the
        // thing a player never needs and the thing I always need, so it collapses instead of going away.
        var logHeader = new TextBlock { Text = "Launcher log", FontSize = 11, Foreground = TextDim, Margin = new Avalonia.Thickness(2, 0, 0, 3) };
        _logPanel = new StackPanel { Spacing = 0, IsVisible = false, Children = { logHeader, _log } };

        // ---- footer --------------------------------------------------------------------------------
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.FontSize = FontBody;
        _status.Foreground = TextDim;
        Grid.SetColumn(_status, 0); Grid.SetColumn(_action, 1);
        footer.Children.Add(_status); footer.Children.Add(_action);

        // All Auto (see SizeToContent above): a * row would keep claiming the slack while its child is hidden,
        // which is exactly the empty band this replaces.
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"), Margin = new Avalonia.Thickness(18) };
        void Row(Control c, int r) { Grid.SetRow(c, r); grid.Children.Add(c); }
        Row(header, 0); Row(branchRow, 1); Row(profileRow, 2); Row(_optionsPanel, 3); Row(buildBox, 4); Row(_logPanel, 5); Row(footer, 6);
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
        await RequestRefreshAsync();     // through the lock like every other refresh -- a branch picked during startup queues
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
        if (_branchBox.SelectedItem is not string sel || sel == _branch) return;

        // ⚠ RECORD FIRST, ALWAYS. This used to open with `if (_mode == Mode.Busy) return;`, which threw the
        // selection away BEFORE _branch and branch.txt were written -- so the dropdown showed the new branch
        // while everything that acts on it still held the old one, and the next Update fetched the old one
        // (strawberry 2026-09-17: "sometimes when i switch branches from the dropdown too quickly, it doesnt
        // register the new branch i selected, so i download the other branch instead"). A user's selection is
        // not something an in-flight operation gets to discard; what can wait is the REFRESH, not the choice.
        _branch = sel;
        SaveBranch(sel);
        Log($"Branch -> {sel}");
        await RequestRefreshAsync();
    }

    /// <summary>Run one operation against the working copy at a time, and drain any refresh queued while it
    /// ran. EVERY entry point goes through here -- refresh, Check for update, Update, Play -- because they
    /// all drive git/dotnet in ONE clone: two at once is not slow, it is a corrupt tree. The queued refresh
    /// is what makes a branch change during a long update still land: the choice is recorded immediately,
    /// and the view catches up the moment the update finishes.</summary>
    async Task WithExclusiveAsync(Func<Task> op)
    {
        if (!_refreshes.Request()) { Log("(busy — queued; it will re-check when the current one finishes)"); return; }
        try { await op(); }
        finally { while (_refreshes.Done()) await RefreshAsync(); }
    }

    Task RequestRefreshAsync() => WithExclusiveAsync(RefreshAsync);

    async Task OnActionAsync()
    {
        if (_mode == Mode.Update) await WithExclusiveAsync(DoUpdateAsync);
        else if (_mode == Mode.Play) await WithExclusiveAsync(LaunchGame);
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

            // The game reads this from its CHILD PROCESS environment -- it lives for the life of the game
            // and does not persist into a shell someone later screenshots. Empty is a working state: the
            // report still files, unauthenticated.
            // Who the player is, for every server they join this session. Both are plain strings/paths, and
            // both are validated again by the game and then by the server -- the launcher is a convenience,
            // never the security boundary.
            string username = LoadUsername();
            Environment.SetEnvironmentVariable("UG_USERNAME", username.Length > 0 ? username : null);
            Environment.SetEnvironmentVariable("UG_PROFILE_PNG", File.Exists(ProfilePngConfig) ? ProfilePngConfig : null);
            string steamId = LoadSteamId();
            Environment.SetEnvironmentVariable("UG_STEAMID", steamId.Length > 0 ? steamId : null);   // display only -- an unsigned claim, and the game must never treat it as identity
            // The CREDENTIAL, and the key that makes it one. The token is what the server verifies; the
            // private key is what proves this machine is the one the token was issued to.
            // ⚠ THE KEY IS PASSED AS A PATH, NOT A VALUE. An env var is readable by every child process
            // and shows up in a crash dump; a path is a place to look that still needs the file's own
            // permissions. The token is fine inline -- it is public by design and useless without the key.
            string token = AuthClient.LoadToken(_baseDir);
            Environment.SetEnvironmentVariable("UG_AUTH_TOKEN", token.Length > 0 ? token : null);
            Environment.SetEnvironmentVariable("UG_AUTH_KEY", token.Length > 0 ? AuthClient.KeyPath(_baseDir) : null);
            if (token.Length == 0) Log("(not signed in -- singleplayer and local multiplayer only)");
            Log(username.Length > 0 ? $"Profile: {username}{(File.Exists(ProfilePngConfig) ? " (+picture)" : "")}"
                                    : "(no name set -- joining as " + ProfileRules.FallbackName + ")");

            // From DISK, not from _offlineCheck: Play() can run on a launcher whose window was never shown,
            // so the control may never have initialised. Same shape as LoadDebugConsole() above, and for the
            // same reason -- the file is also what survives a self-update.
            bool offline = LoadOfflineMode();
            Environment.SetEnvironmentVariable("UG_OFFLINE", offline ? "1" : null);
            if (offline) Log("Offline mode: multiplayer is hidden in game.");

            string reportKey = LoadReportKey();
            Environment.SetEnvironmentVariable("UG_BUGREPORT_KEY", reportKey.Length > 0 ? reportKey : null);
            if (reportKey.Length == 0) Log("(no report key set — bug reports will file anonymously)");

            // THE TOGGLE, BOTH WAYS. Godot mono ships godot.exe AND godot_console.exe; the console build is a
            // separate binary that allocates a Windows console, not a flag, so the toggle picks the executable.
            // The old code here only ever APPENDED "_console" and did nothing when the box was unticked --
            // which is a no-op on any machine whose resolved Godot already IS the console build (UNTURNED_GODOT_EXE
            // or a bare "godot" on PATH can both be). It then logged "launching the windowed build" while
            // launching the console one. LauncherRules.GodotExeFor strips as well as adds, and reports whether
            // it managed it so the line below states what happened instead of what was asked for.
            string exe = _godot;
            if (OperatingSystem.IsWindows())
            {
                bool wantConsole = LoadDebugConsole();
                var pick = LauncherRules.GodotExeFor(_godot, wantConsole, File.Exists);
                exe = pick.Exe;
                if (pick.Satisfied) Log(wantConsole ? "Debug console: on." : "Debug console: off.");
                else Log($"(wanted the {(wantConsole ? "console" : "windowed")} build but it isn't next to godot — launching {Path.GetFileName(exe)})");
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
            // UITheme's Accent and Good are TEXT colours (light steel blue / light green) and read as washed
            // out behind white text, so the fills are those same hues darkened rather than a different family.
            _action.Background = m == Mode.Play ? B("#3E6B45") : B("#3A5A78");
            _action.Foreground = TextMain;
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
        Dispatcher.UIThread.Post(() => { _mode = Mode.Broken; _action.IsEnabled = false; _action.Content = "—"; _status.Text = msg; _status.Foreground = Bad; });
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

    // ---- debug console toggle -----------------------------------------------------------------------
    // ⚠ Read from DISK at Play time, not from the checkbox: Play() can run on a launcher whose window was
    // never shown (and the control therefore never initialised), and the file is the thing that survives a
    // self-update anyway. The checkbox writes it; nothing else reads the checkbox.
    string DebugConsoleConfig => Path.Combine(_baseDir, "debug_console.txt");

    /// <summary>ON unless explicitly turned off -- a missing file is a fresh install, which must behave the
    /// way every existing one already does.</summary>
    bool LoadDebugConsole()
    {
        try { return !File.Exists(DebugConsoleConfig) || File.ReadAllText(DebugConsoleConfig).Trim() != "0"; }
        catch { return true; }
    }

    void SaveDebugConsole(bool on)
    {
        try { File.WriteAllText(DebugConsoleConfig, on ? "1" : "0"); Log(on ? "debug console: on" : "debug console: off (takes effect next launch)"); }
        catch (Exception ex) { Log("!! could not save the debug console setting: " + ex.Message); }
    }

    // ---- offline mode -------------------------------------------------------------------------------
    // A PREFERENCE, not a restriction the game enforces against its owner -- it exists so somebody who will
    // not be signing in gets a menu without dead ends, instead of a Multiplayer button that walks them to a
    // rejection. Read from disk at Play time for the same reason as the debug console above.
    string OfflineModeConfig => Path.Combine(_baseDir, "offline_mode.txt");

    /// <summary>OFF unless explicitly turned on -- a missing file is a fresh install, and no install should
    /// quietly acquire a restriction it did not have yesterday.</summary>
    bool LoadOfflineMode()
    {
        try { return File.Exists(OfflineModeConfig) && File.ReadAllText(OfflineModeConfig).Trim() == "1"; }
        catch { return false; }
    }

    void SaveOfflineMode(bool on)
    {
        try { File.WriteAllText(OfflineModeConfig, on ? "1" : "0"); Log(on ? "offline mode: on (takes effect next launch)" : "offline mode: off"); }
        catch (Exception ex) { Log("!! could not save the offline mode setting: " + ex.Message); }
    }

    // ---- report key ---------------------------------------------------------------------------------
    string ReportKeyConfig => Path.Combine(_baseDir, "bugreport_key.txt");

    string LoadReportKey()
    {
        try { return File.Exists(ReportKeyConfig) ? File.ReadAllText(ReportKeyConfig).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>Show the key's SHAPE, never the key. Enough to tell "I pasted something" from "I pasted
    /// the wrong thing" without putting the secret back on screen, where the whole point of the masked box
    /// was to keep it off.</summary>
    void RefreshKeyStatus()
    {
        string k = LoadReportKey();
        _keyStatus.Text = k.Length == 0
            ? "no key — reports file anonymously"
            : $"key set (…{k[^Math.Min(6, k.Length)..]})";
    }

    void SaveReportKey(string key)
    {
        key = key.Trim();
        try
        {
            if (key.Length == 0) { if (File.Exists(ReportKeyConfig)) File.Delete(ReportKeyConfig); Log("report key cleared"); }
            else
            {
                File.WriteAllText(ReportKeyConfig, key);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(ReportKeyConfig, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Log("report key saved");
            }
            _keyBox.Text = "";   // never leave it on screen after a save
            RefreshKeyStatus();
        }
        catch (Exception ex) { Log("!! couldn't save the report key: " + ex.Message); }
    }
    string LoadBranch()
    {
        try { if (File.Exists(BranchConfig)) { var b = File.ReadAllText(BranchConfig).Trim(); if (!string.IsNullOrWhiteSpace(b)) return b; } } catch { }
        return DefaultBranch;
    }
    void SaveBranch(string b) { try { File.WriteAllText(BranchConfig, b); } catch (Exception ex) { Log("(couldn't save branch: " + ex.Message + ")"); } }

    // ---- Unturned install resolution (the game reads its real map terrain from here via UG_UNTURNED_DIR) ----
    // ---- profile: username + picture ---------------------------------------------------------------
    // Two files beside the launcher, handed to the game as environment variables on launch -- the same route
    // UG_UNTURNED_DIR and UG_BUGREPORT_KEY take. The game reads them and nothing else, so a build launched
    // without the launcher still runs; it just has no name, and ProfileRules supplies the fallback.

    string SteamIdConfig => Path.Combine(_baseDir, "steamid.txt");
    string LoadSteamId()
    {
        try { return File.Exists(SteamIdConfig) ? File.ReadAllText(SteamIdConfig).Trim() : ""; }
        catch { return ""; }
    }

    void SignOutOfSteam()
    {
        // The token goes with the identity it belongs to. Leaving a signed credential behind after a
        // sign-out is the one thing a sign-out must not do.
        foreach (var f in new[] { SteamIdConfig, UsernameConfig, ProfilePngConfig,
                                  AuthClient.TokenPath(_baseDir), AuthClient.KeyPath(_baseDir) })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        Log("Signed out -- name, picture and SteamID cleared. You'll join as " + ProfileRules.FallbackName + ".");
        RefreshProfileStatus();
    }

    /// <summary>One button: OpenID in the browser, a VERIFIED SteamID back, then name + avatar from the
    /// key-free public profile document. Nothing is registered with Steam and no API key exists to leak.</summary>
    async Task SignInWithSteamAsync()
    {
        _steamButton.IsEnabled = false;
        try
        {
            // Through stmauth, not to Steam directly. The old flow verified a SteamID against Steam
            // correctly and it still convinced nobody but this launcher -- a server receiving "I am 7656..."
            // from a client cannot check it, because the claim and the machine making it are the same
            // machine. What comes back now is SIGNED, and bound to a key only this box holds.
            var res = await AuthClient.SignInAsync(_baseDir, Log);
            if (res.Token == null)
            {
                Log(res.Cancelled ? "(Steam sign-in cancelled: " + res.Error + ")" : "!! Steam sign-in failed: " + res.Error);
                return;
            }
            if (!string.IsNullOrEmpty(res.SteamId64)) File.WriteAllText(SteamIdConfig, res.SteamId64);
            Log("Signed in as SteamID " + (res.SteamId64 ?? "(unreadable)") + " -- token stored");
            if (!string.IsNullOrEmpty(res.SteamId64)) await PullSteamProfileAsync(res.SteamId64);
        }
        catch (Exception ex) { Log("!! Steam sign-in failed: " + ex.Message); }
        finally { _steamButton.IsEnabled = true; RefreshProfileStatus(); }
    }

    /// <summary>steamcommunity.com/profiles/&lt;id&gt;/?xml=1 -- public, no key, no App ID. Gives the persona
    /// name and a full-size avatar URL. A private profile still returns the name and avatar (those are public
    /// even when the inventory and details are not), so this does not depend on privacy settings the way an
    /// inventory read does.</summary>
    async Task PullSteamProfileAsync(string steamId)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            string xml = await http.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}/?xml=1");

            string persona = Between(xml, "<steamID><![CDATA[", "]]></steamID>");
            if (!string.IsNullOrWhiteSpace(persona)) SaveUsername(persona);
            else Log("(Steam returned no persona name -- keeping whatever was set)");

            string avatarUrl = Between(xml, "<avatarFull><![CDATA[", "]]></avatarFull>");
            if (string.IsNullOrWhiteSpace(avatarUrl)) { Log("(no avatar on that profile)"); return; }

            byte[] raw = await http.GetByteArrayAsync(avatarUrl);
            // Same pipeline the file picker used: squish to exactly 128x128, then check our own output with
            // the validator the SERVER runs, so a picture that would be refused in game is refused here.
            using (var ms = new MemoryStream(raw))
            using (var src = new Avalonia.Media.Imaging.Bitmap(ms))
            using (var scaled = src.CreateScaledBitmap(new Avalonia.PixelSize(128, 128),
                                                       Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality))
            using (var outFile = File.Create(ProfilePngConfig))
                scaled.Save(outFile);

            var verdict = ProfileRules.CheckAvatarPng(File.ReadAllBytes(ProfilePngConfig));
            if (verdict != ProfileRules.AvatarVerdict.Ok)
            {
                Log($"!! the Steam avatar came out unusable ({ProfileRules.Explain(verdict)}) -- not saved");
                try { File.Delete(ProfilePngConfig); } catch { }
            }
            else Log($"Avatar pulled from Steam ({new FileInfo(ProfilePngConfig).Length / 1024f:0.0} KB)");
        }
        catch (Exception ex) { Log("(couldn't read the Steam profile: " + ex.Message + ")"); }
    }

    static string Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal); if (i < 0) return null;
        i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.Ordinal); if (j < 0) return null;
        return s.Substring(i, j - i);
    }

    string UsernameConfig => Path.Combine(_baseDir, "username.txt");
    string ProfilePngConfig => Path.Combine(_baseDir, "profile.png");

    string LoadUsername()
    {
        try { return File.Exists(UsernameConfig) ? File.ReadAllText(UsernameConfig).Trim() : ""; }
        catch { return ""; }
    }

    void SaveUsername(string raw)
    {
        // Sanitised HERE with the same ProfileRules the server runs, and the box is REWRITTEN to the result.
        // Showing the player the name they will actually get is the whole reason the launcher links that file
        // instead of keeping its own idea of what a name is -- otherwise someone types a name, sees it
        // accepted, and is quietly renamed the first time they join.
        string clean = ProfileRules.SanitizeName(raw, out bool changed);
        try
        {
            File.WriteAllText(UsernameConfig, clean);
            Log(changed ? $"Name saved as \"{clean}\" (adjusted -- brackets, invisible characters and control codes are not allowed in a name)"
                        : $"Name saved as \"{clean}\"");
        }
        catch (Exception ex) { Log("(couldn't save the name: " + ex.Message + ")"); }
        RefreshProfileStatus();
    }

    async Task PickProfilePictureAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose a profile picture",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" },
                    },
                },
            });
            if (files == null || files.Count == 0) return;
            string src = files[0].Path?.LocalPath;
            if (string.IsNullOrEmpty(src)) { Log("(that file isn't on the local disk -- copy it locally first)"); return; }

            // SQUISHED, not letterboxed or cropped: 128x128 exactly, whatever shape went in. That is what the
            // game and the server both require, and doing it here means the wire never carries anything else.
            using (var src128 = new Avalonia.Media.Imaging.Bitmap(src))
            using (var scaled = src128.CreateScaledBitmap(new Avalonia.PixelSize(128, 128),
                                                          Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality))
            using (var outFile = File.Create(ProfilePngConfig))
                scaled.Save(outFile);

            // Then check our own output with the same validator the server will use. If this ever fails, the
            // resize wrote something the game would refuse, and the player should hear it now rather than
            // discover a checkerboard over their head in game.
            var verdict = ProfileRules.CheckAvatarPng(File.ReadAllBytes(ProfilePngConfig));
            if (verdict != ProfileRules.AvatarVerdict.Ok)
            {
                Log($"!! the resized picture came out unusable ({ProfileRules.Explain(verdict)}) -- not saved");
                try { File.Delete(ProfilePngConfig); } catch { }
            }
            else Log($"Profile picture set (squished to 128x128, {new FileInfo(ProfilePngConfig).Length / 1024f:0.0} KB)");
        }
        catch (Exception ex) { Log("(couldn't use that picture: " + ex.Message + ")"); }
        RefreshProfileStatus();
    }

    void RefreshProfileStatus()
    {
        string name = LoadUsername();
        bool hasPfp = File.Exists(ProfilePngConfig);
        string sid = LoadSteamId();
        _steamButton.Content = sid.Length > 0 ? "Re-sync from Steam" : "Sign in through Steam";
        _nameStatus.Text = sid.Length == 0
            ? "not signed in -- you'll join as " + ProfileRules.FallbackName
            : $"{name}  ·  {sid}" + (hasPfp ? "" : "  (no picture)");
        try
        {
            _pfpPreview.Source = hasPfp ? new Avalonia.Media.Imaging.Bitmap(ProfilePngConfig) : null;
        }
        catch { _pfpPreview.Source = null; }
    }

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
