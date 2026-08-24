using Godot;
using System.Collections.Generic;
using UnturnedGodot.BugReport;

namespace UnturnedGodot
{
    /// <summary>Push-to-report. Hold the key: a screenshot is taken at PRESS and the microphone records
    /// while it is held. On release the audio, both screenshots and a context blob are written to an outbox
    /// on disk and then uploaded; the service transcribes the audio with Gemini and files a report.
    ///
    /// Screenshot at PRESS, not release, because the anomaly is on screen at the instant you decide to
    /// report it -- across ten seconds of narration the ragdoll despawns, the particle finishes and the
    /// camera drifts. A second shot at release gives the model a before/after pair for free.
    ///
    /// DISK BEFORE NETWORK, always. The outbox directory is written before any upload is attempted, so a
    /// dropped connection, a quit mid-send or a service outage costs nothing: the next launch retries. The
    /// server dedupes on ClientReportId, so a retry after an ambiguous failure cannot double-file.</summary>
    public partial class BugReporter : Node
    {
        // ---- knobs -------------------------------------------------------------------------------------
        public const Key HoldKey = Key.Backslash;   // matched as a PHYSICAL key: see _Input
        const float BusBufferSeconds = 1.0f;        // -> next_pow2(44100) = 65536 frames = 1.49 s of slack
        const int OverlayLayer = 260;               // above EditorPlayMode(160) and Main(200): a recording
                                                    // indicator must never be occluded by another layer
        const double MaxRecordSeconds = ReportSession.MaxSeconds;
        const string BusName = "BugReportCapture";
        const string PlayerGroup = "players";       // PlayerController.cs AddToGroup("players")

        /// <summary>L1 must never write the developer's real user:// nor upload.
        ///
        /// THIS IS NOT A STYLE POINT, it is the worst defect the implementation review found. BugReporter is
        /// attached by WorldBuilder.AttachPlayerShell, which ~35 in-engine tests reach through
        /// ClientWorldSession.SpawnShell. Each one constructs a reporter whose _Ready fires RetryOutbox
        /// against user://bugreports/outbox -- the DEVELOPER'S REAL QUEUE -- and POSTs every directory in it
        /// to the live production endpoint, deleting each on success. On this box the directory does not
        /// exist so it is a silent no-op, which is precisely why it survived review: it only fires on a
        /// machine where someone has actually used the feature, i.e. the machine that will do the manual
        /// verification.
        ///
        /// So the gate is a single call TestHost makes at startup AND re-asserts between every test, rather
        /// than three fields each test has to remember to set and restore. A test that dies on a failed
        /// check or a watchdog timeout never runs its own restore.</summary>
        public static bool UploadEnabled = true;
        public static string OutboxRootOverride;

        /// <summary>Mic capture is gated on this rather than on "am I headless", because L1 IS headless and
        /// gating on headlessness would skip the one place the plumbing can actually be proven. Measured:
        /// the whole bus/capture/drain path works fine under the Dummy driver, it simply yields silence.</summary>
        public static bool MicEnabled = true;

        /// <summary>Put every reporter in this process into a state where it cannot touch the real outbox
        /// and cannot reach the network. Idempotent; call it as often as you like.</summary>
        public static void EnterTestMode()
        {
            UploadEnabled = false;
            MicEnabled = false;                       // bugreport.* opts back IN for its own duration
            OutboxRootOverride = "user://test_bugreports";
            Endpoint = "http://127.0.0.1:1/";         // belt-and-braces: if UploadEnabled were ever wrongly
                                                      // true, this refuses instantly instead of reaching prod
        }

        // ---- state -------------------------------------------------------------------------------------
        readonly ReportSession _session = new ReportSession();
        AudioBuffer _audio;
        AudioStreamPlayer _micPlayer;
        AudioEffectCapture _capture;
        int _busIdx = -1;
        AudioStatus _audioStatus = AudioStatus.Disabled;
        byte[] _shotAtPress;
        CanvasLayer _overlay;
        Label _label;
        double _toastLeft;
        // Rolling 5 s worst frame. The instantaneous frame_ms in the perf block is whatever the frame the
        // key was released on happened to cost, which is close to useless for a report about stuttering --
        // by the time you finish describing a hitch it is over. This keeps the worst frame from the window
        // the player was actually talking through.
        double _worstFrameMs, _worstWindowLeft;
        static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient
        {
            // Fully qualified on purpose: `using Godot;` puts Godot.HttpClient in scope and an unqualified
            // HttpClient is CS0104 ambiguous in every file in this project.
            Timeout = System.TimeSpan.FromSeconds(60),
        };
        /// <summary>STATIC, not per-instance. QueueFree is deferred, so during a world swap the old and new
        /// reporters briefly coexist and a per-instance gate serialises neither against the other: the new
        /// one's startup RetryOutbox walks the same directory the old one's UploadDir is mid-way through,
        /// and a DeleteDir can land between the context read and the file attachments -- producing a report
        /// that uploads WITH context and WITHOUT the screenshots or audio. Server-side dedupe then makes it
        /// a coin flip which one lands, so the truncated one can win. Sharing the gate removes the race
        /// rather than relying on the dedupe to paper over it.</summary>
        static readonly System.Threading.SemaphoreSlim _uploadGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>Static, so EnterTestMode closes the hole for every reporter INCLUDING the ones that do
        /// not exist yet -- an instance field would only protect reporters already constructed, and the ones
        /// that matter are built later by AttachPlayerShell.</summary>
        public static string Endpoint = "https://claw.bitvox.me/bugreport/report";

        /// <summary>Cleared in _ExitTree so a scene reload cannot leave the console's `report` command
        /// pointing at a freed node -- the classic stale-singleton crash in this codebase.</summary>
        public static BugReporter Instance { get; private set; }
        public AudioStatus LastAudioStatus => _audioStatus;
        public ReportState State => _session.State;

        public override void _Ready()
        {
            // A pause menu stops _Process, and the capture ring holds only ~1.5 s -- without this the drain
            // stalls and audio is discarded silently.
            ProcessMode = ProcessModeEnum.Always;
            Instance = this;
            // BuildStamp is engine-free, so the host has to hand it the paths Godot owns.
            // Env var read BEFORE LoadConfig so the file cannot override what the launcher just handed us.
            string envKey = System.Environment.GetEnvironmentVariable("UG_BUGREPORT_KEY");
            if (!string.IsNullOrWhiteSpace(envKey)) ReportKey = envKey.Trim();
            BuildStamp.ProjectDir ??= ProjectSettings.GlobalizePath("res://");
            BuildStamp.ConfiguredVersion ??= (string)ProjectSettings.GetSetting("application/config/version", "");
            BuildOverlay();
            LoadConfig();
            if (UploadEnabled) CallDeferred(nameof(StartRetry));
        }

        public override void _ExitTree()
        {
            if (Instance == this) Instance = null;
            TeardownMic();
        }

        void BuildOverlay()
        {
            _overlay = new CanvasLayer { Layer = OverlayLayer };
            _label = new Label
            {
                Text = "", Modulate = new Color(1f, 0.3f, 0.3f),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _label.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _label.Position = new Vector2(-120f, 18f);
            _label.CustomMinimumSize = new Vector2(240f, 24f);
            _overlay.AddChild(_label);
            AddChild(_overlay);
            _overlay.Visible = false;
        }

        /// <summary>The report key, or "" for none.
        ///
        /// Env var FIRST, config file second. The launcher passes UG_BUGREPORT_KEY in the game's CHILD
        /// PROCESS environment, which is the better home of the two: it exists for the life of this process,
        /// is not readable from another user's session, and does not persist into a shell someone later
        /// screenshots. The cfg file stays supported because the launcher is not the only way to start the
        /// game. "None" is a working state -- reports still file, unauthenticated, exactly as they did
        /// before keys existed.</summary>
        public static string ReportKey = "";

        void LoadConfig()
        {
            // Wrapped and type-checked: this runs inside _Ready, so a bugreport.cfg whose `endpoint` is not
            // a string would throw an InvalidCastException that kills the node outright -- leaving no bug
            // reporter at all, with nothing a player could connect to the cause.
            try
            {
                var cfg = new ConfigFile();
                if (cfg.Load("user://bugreport.cfg") != Error.Ok) return;
                var ep = cfg.GetValue("report", "endpoint", Endpoint);
                if (ep.VariantType == Variant.Type.String) Endpoint = (string)ep;
                var k = cfg.GetValue("report", "key", "");
                if (k.VariantType == Variant.Type.String && !string.IsNullOrWhiteSpace((string)k)
                    && string.IsNullOrEmpty(ReportKey)) ReportKey = ((string)k).Trim();
            }
            catch (System.Exception e) { GD.PushWarning($"[bugreport] bad config, using defaults: {e.Message}"); }
        }

        public override void _Input(InputEvent ev)
        {
            if (ev is InputEventKey { Echo: true }) return;              // auto-repeat must not restart it
            if (!Keybinds.Matches(GameAction.BugReport, ev)) return;     // the BOUND control (default `\`, PHYSICAL), key OR mouse -- this is the rebind the task existed for (e.g. Mouse 5)
            if (ev is InputEventKey mk && (mk.CtrlPressed || mk.AltPressed || mk.MetaPressed)) return;   // Ctrl+\ etc. are not a report
            bool down = Keybinds.IsDown(ev);
            // Repo precedent is `is LineEdit` rather than `!= null` (MapUI.cs:118) -- a plain null check over-suppresses.
            if (down && GetViewport().GuiGetFocusOwner() is LineEdit) return;

            bool acted;
            if (down) { acted = _session.Press(); if (acted) StartRecording(); }
            else { acted = _session.Release(); if (acted) FinishRecording(); }
            // Only when we ACTED. Marking an event handled that we ignored swallows it from everything
            // downstream -- including the release half of a `\` typed into the dev console, whose press half
            // the LineEdit guard above correctly let through.
            if (acted) GetViewport().SetInputAsHandled();
        }

        public override void _Notification(int what)
        {
            // Alt-tabbing away must not leave the microphone hot.
            if (what == NotificationApplicationFocusOut && _session.State == ReportState.Recording
                && _session.Release()) FinishRecording();
        }

        void StartRecording()
        {
            // Hide the overlay BEFORE the before-shot, not merely "don't show it yet". Toast() makes the
            // overlay visible for 3 s -- including the deferred "report sent ✓" from the previous upload --
            // so pressing again inside that window burned the last report's toast into this report's image.
            _toastLeft = 0;
            if (_overlay != null) _overlay.Visible = false;
            _shotAtPress = CaptureScreenshot();
            _audio = new AudioBuffer((int)(MaxRecordSeconds * AudioServer.GetMixRate()) + 1024);
            _audioStatus = AudioStatus.Disabled;
            if (MicEnabled) StartMic();
            _overlay.Visible = true;
            _label.Text = "● REC 0:00";
        }

        void StartMic()
        {
            try
            {
                _busIdx = AudioServer.BusCount;
                AudioServer.AddBus(_busIdx);
                AudioServer.SetBusName(_busIdx, BusName);
                _capture = new AudioEffectCapture { BufferLength = BusBufferSeconds };
                AudioServer.AddBusEffect(_busIdx, _capture);
                // Muted so the player does not hear themselves. Measured: a muted bus still delivers
                // samples to its effects (86016 frames muted vs unmuted, identical), on both the Dummy and
                // PulseAudio drivers -- this was the single riskiest assumption in the design.
                AudioServer.SetBusMute(_busIdx, true);
                _micPlayer = new AudioStreamPlayer
                {
                    Stream = new AudioStreamMicrophone(),
                    Bus = AudioServer.GetBusName(_busIdx),
                    ProcessMode = ProcessModeEnum.Always,
                };
                AddChild(_micPlayer);
                _micPlayer.Play();
                _audioStatus = AudioStatus.Ok;   // provisional; the amplitude check at finalize decides
            }
            catch (System.Exception e)
            {
                GD.PushWarning($"[bugreport] mic unavailable: {e.Message}");
                _audioStatus = AudioStatus.Error;
                TeardownMic();
            }
        }

        public override void _Process(double delta)
        {
            double ms = delta * 1000.0;
            if (ms > _worstFrameMs) { _worstFrameMs = ms; _worstWindowLeft = 5.0; }
            else if ((_worstWindowLeft -= delta) <= 0) { _worstFrameMs = ms; _worstWindowLeft = 5.0; }

            if (_toastLeft > 0)
            {
                _toastLeft -= delta;
                if (_toastLeft <= 0 && _session.State == ReportState.Idle) _overlay.Visible = false;
            }
            if (_session.State != ReportState.Recording) return;
            Drain();
            _label.Text = $"● REC {(int)_session.Elapsed / 60}:{(int)_session.Elapsed % 60:00}";
        }

        /// <summary>The capture ring holds ~1.5 s, and the frames that overrun it are exactly the frames a
        /// hitch produced -- so draining ONLY on the render frame loses audio precisely when the game is
        /// struggling, which is when people are recording. Draining here too gives a second drain on a fixed
        /// 50 Hz clock; the redundant call simply finds nothing available and costs a branch.
        ///
        /// The elapsed clock lives here rather than in _Process for the same reason it matters in tests: the
        /// L1 host advances the world by PHYSICS ticks, so a timer driven from _Process advances by an
        /// unrelated and unrepeatable number of render frames per `Ticks(n)`. That mismatch is a race the
        /// repo has already been bitten by once (Deployable updating in _Process while the host stepped
        /// _PhysicsProcess made every Ticks(n) a coin flip).</summary>
        public override void _PhysicsProcess(double delta)
        {
            if (_session.State != ReportState.Recording) return;
            Drain();
            // delta / TimeScale, NOT delta. The physics delta is SCALED by Engine.TimeScale, which the dev
            // console exposes to players as `simSpeed` -- while the audio buffer is sized in REAL seconds and
            // the microphone keeps producing real-time frames regardless. At simSpeed 0.02 a genuine ten
            // second hold accumulated 0.2 s of "elapsed", fell under TapSeconds, and was classified a TAP --
            // which DISCARDS the recording and toasts "screenshot only". Ten seconds of narration gone, and
            // the report still looks perfectly well-formed. Dividing recovers wall-clock exactly, and is a
            // no-op at the 1.0 the tests run at.
            double scale = Engine.TimeScale > 0.0 ? Engine.TimeScale : 1.0;
            if (_session.Tick(delta / scale)) FinishRecording();     // hit the hard cap
        }

        void Drain()
        {
            if (_capture == null || _audio == null) return;
            int avail = _capture.GetFramesAvailable();
            if (avail <= 0) return;
            var buf = _capture.GetBuffer(avail);             // Vector2[] stereo pairs
            var l = new float[buf.Length];
            var r = new float[buf.Length];
            for (int i = 0; i < buf.Length; i++) { l[i] = buf[i].X; r[i] = buf[i].Y; }
            _audio.AppendStereo(l, r, buf.Length);
            _audio.DiscardedFrames = _capture.GetDiscardedFrames();
        }

        /// <summary>Everything here is wrapped, and _session.Done() is in the finally.
        ///
        /// Without that, ONE throw anywhere in this method leaves State == Finalizing forever: Press() and
        /// Release() both refuse when the state is not Idle, and both process callbacks early-return unless
        /// it is Recording. So the key silently stops working for the rest of the session, with no toast and
        /// no message -- Godot's C# marshalling logs the exception and the game carries on. Wav.Encode
        /// THROWS by design on a mix rate outside 8000..48000 (a 96 kHz output device is the realistic
        /// case), and GatherContext reads ~20 external properties, so this is not hypothetical.</summary>
        void FinishRecording()
        {
            try { FinishRecordingCore(); }
            catch (System.Exception e)
            {
                GD.PushError($"[bugreport] report failed: {e}");
                Toast("report failed — see the log");
            }
            finally
            {
                _session.Done();
                _shotAtPress = null;
                TeardownMic();          // idempotent; guarantees the mic is cold even on the throwing path
                if (_overlay != null && _toastLeft <= 0) _overlay.Visible = false;
            }
        }

        void FinishRecordingCore()
        {
            Drain();
            TeardownMic();
            // Hidden BEFORE the after-shot, then FORCED through a draw so the hide has actually landed in the
            // render target. Not an `await FramePostDraw`: this must not be async. An await here would leave a
            // half-finished report alive across an arbitrary number of frames during which the player can quit,
            // change scene or start another report -- and in a headless L1 boot there is no guarantee the
            // signal ever arrives at all, which would hang the report forever rather than fail it.
            // Viewmodel.CaptureViewport() already establishes ForceDraw as this repo's answer to exactly this
            // problem ("an in-_Process GetImage otherwise reads an empty/stale render target").
            _overlay.Visible = false;
            RenderingServer.ForceDraw();

            byte[] shotAfter = CaptureScreenshot();

            LastFrameCount = _audio?.Count ?? 0;
            if (_audioStatus == AudioStatus.Ok && _audio != null) _audioStatus = _audio.Classify();
            byte[] wav = null;
            if (_audio != null && _audio.Count > 0 && !_session.WasTap)
            {
                // FitRate first: a 88.2k or 96k output device is an ordinary setting on decent audio
                // hardware, and Encode rejects anything above 48k because that is what the wire accepts.
                // Rejecting was the wrong call -- it lost VoX's first real report outright.
                var (pcm, rate) = Wav.FitRate(_audio.ToArray(), (int)AudioServer.GetMixRate());
                wav = Wav.Encode(pcm, rate);
            }

            string msg = _session.HitTimeout ? "60s max — sending" :
                         _session.WasTap ? "screenshot only" :
                         _audioStatus == AudioStatus.NoMic ? "no mic — screenshot only" : "sending…";
            Toast(msg);

            var ctx = GatherContext();

            LastOutboxDir = WriteOutbox(ctx, _shotAtPress, shotAfter, wav);
            // Disk first, network second, and the network half is detached: the report is already safe on
            // disk, so nothing about the upload can lose it. A failed send leaves the directory in place and
            // the next launch retries it; the server dedupes on client_report_id, so a retry after an
            // ambiguous failure cannot file the same report twice.
            if (LastOutboxDir != null && UploadEnabled) _ = UploadDir(LastOutboxDir);
        }

        /// <summary>The directory the last report was written to -- what a test asserts on, since it must
        /// never be allowed to upload.</summary>
        public string LastOutboxDir { get; private set; }

        /// <summary>How many audio frames the drain actually pulled off the capture effect. Exposed because
        /// silence and NOTHING AT ALL both classify as NoMic: without this number a test cannot tell a
        /// working pipeline on a mic-less box from a drain loop that never ran.</summary>
        public int LastFrameCount { get; private set; }

        void TeardownMic()
        {
            if (_micPlayer != null)
            {
                _micPlayer.Stop();
                _micPlayer.QueueFree();
                _micPlayer = null;
            }
            if (_busIdx >= 0)
            {
                // Look the index up by NAME before removing. The stored index is only valid while no other
                // bus is added or removed below it; a bounds check tests RANGE, not IDENTITY, so on the day
                // something else touches the bus layout mid-recording we would silently delete someone
                // else's bus. Nothing in this repo does that today -- which is exactly the kind of fact
                // that stops being true without anyone revisiting this line.
                int byName = AudioServer.GetBusIndex(BusName);
                if (byName >= 0) AudioServer.RemoveBus(byName);
                else if (_busIdx < AudioServer.BusCount) AudioServer.RemoveBus(_busIdx);
                _busIdx = -1;
            }
            _capture = null;
        }

        byte[] CaptureScreenshot()
        {
            try
            {
                var vp = GetViewport();
                var img = vp?.GetTexture()?.GetImage();
                if (img == null) return null;
                // The gun and arms render in the Viewmodel's own SubViewport, which GetViewport() misses --
                // a still capture came out background-only once already (Viewmodel.cs:250-252). The repo's
                // composite recipe lives in the --vm render harness and is gated behind _vmTest, so it does
                // NOT apply in live play; reach the ACTIVE viewmodel instead.
                var vm = FindActiveViewmodel();
                var gun = vm?.CaptureViewport();
                if (gun != null)
                {
                    if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                    if (gun.GetFormat() != Image.Format.Rgba8) gun.Convert(Image.Format.Rgba8);
                    if (gun.GetSize() != img.GetSize()) gun.Resize(img.GetWidth(), img.GetHeight());
                    img.BlendRect(gun, new Rect2I(Vector2I.Zero, gun.GetSize()), Vector2I.Zero);
                }
                return img.SavePngToBuffer();
            }
            catch (System.Exception e)
            {
                GD.PushWarning($"[bugreport] screenshot failed: {e.Message}");
                return null;
            }
        }

        Viewmodel FindActiveViewmodel()
        {
            foreach (var n in GetTree().GetNodesInGroup(PlayerGroup))
                if (n is PlayerController pc && GodotObject.IsInstanceValid(pc)) return pc.VM;
            return null;
        }

        Dictionary<string, Variant> GatherContext()
        {
            PlayerController pc = null;
            foreach (var n in GetTree().GetNodesInGroup(PlayerGroup))
                if (n is PlayerController p && GodotObject.IsInstanceValid(p)) { pc = p; break; }

            var perf = new Godot.Collections.Dictionary
            {
                ["fps"] = Engine.GetFramesPerSecond(),
                ["frame_ms"] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
                ["draw_calls"] = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
                ["objects"] = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
                ["primitives"] = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
                ["vram_mb"] = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0,
                ["physics_ms"] = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
                ["physics_active"] = Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects),
                ["collision_pairs"] = Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs),
                ["worst_frame_ms_5s"] = _worstFrameMs,
            };

            // Guarded: pressing the key in the MAIN MENU means there is no PlayerController at all.
            var player = new Godot.Collections.Dictionary();
            if (pc != null)
            {
                player["health"] = pc.Health;
                player["max_health"] = pc.MaxHealth;
                player["stamina"] = pc.Stamina;
                player["food"] = pc.Food;
                player["water"] = pc.Water;
                player["infection"] = pc.Infection;
                player["bleeding"] = pc.Bleeding;
                player["broken_legs"] = pc.Broken;
                player["deaths"] = pc.Deaths;
                player["underwater"] = pc.EyesUnderwater;
                // TruePhysicsPosition, not GlobalPosition: the visible body is interpolated, so GlobalPosition
                // is up to a frame behind where the report's screenshot was actually taken from.
                var p = pc.TruePhysicsPosition;
                player["position"] = new Godot.Collections.Array { p.X, p.Y, p.Z };
                player["equipped"] = pc.EquippedNameForReport ?? "";
                // "vehicle", not "driving" -- the server reads player.vehicle, so the old name meant every
                // report from inside a car said "vehicle None", on exactly the reports a vehicle bug needs.
                // GodotObject.IsInstanceValid, not != null: a freed Godot object is NOT equal to null, so the
                // null check passes on a destroyed vehicle and the property access then throws -- inside a
                // method that must never throw.
                player["vehicle"] = GodotObject.IsInstanceValid(pc.Driving)
                    ? (pc.Driving.DisplayName ?? "vehicle") : "";
            }

            return new Dictionary<string, Variant>
            {
                ["client_report_id"] = System.Guid.NewGuid().ToString(),
                ["godot"] = (string)Engine.GetVersionInfo()["string"],
                ["os"] = OS.GetName(),
                ["gpu"] = RenderingServer.Singleton.GetVideoAdapterName(),
                ["uptime_s"] = Time.GetTicksMsec() / 1000.0,
                // The server has a slot for each of these and the client was sending none of them, so the
                // one real report filed so far came back with map and game_commit null. game_commit is the
                // worst of them: without it there is no way to tell which build a report came from.
                ["game_commit"] = BuildStamp.Commit,
                ["map"] = Terrain.MapDir ?? "",
                ["audio_status"] = AudioStatusWire.Of(_audioStatus),
                ["audio_peak"] = _audio?.Peak ?? 0f,
                ["audio_discarded_frames"] = (double)(_audio?.DiscardedFrames ?? 0),
                // AppendStereo silently stops at capacity and those frames are NOT counted in
                // DiscardedFrames (that only reflects the capture effect's own ring), so a truncated
                // recording used to just... end, with nothing anywhere saying it had been cut.
                ["audio_truncated"] = _audio?.Full ?? false,
                ["player"] = player,
                ["perf"] = perf,
            };
        }

        static string OutboxRoot => OutboxRootOverride ?? "user://bugreports/outbox";

        string WriteOutbox(Dictionary<string, Variant> ctx, byte[] shot, byte[] after, byte[] wav)
        {
            try
            {
                string id = (string)ctx["client_report_id"];
                string dir = $"{OutboxRoot}/{id}";
                DirAccess.MakeDirRecursiveAbsolute(dir);
                var jd = new Godot.Collections.Dictionary();
                foreach (var kv in ctx) jd[kv.Key] = kv.Value;
                Write(dir + "/context.json", System.Text.Encoding.UTF8.GetBytes(Json.Stringify(jd)));
                if (shot != null) Write(dir + "/screenshot.png", shot);
                if (after != null) Write(dir + "/screenshot_after.png", after);
                if (wav != null) Write(dir + "/audio.wav", wav);
                return dir;
            }
            catch (System.Exception e)
            {
                GD.PushWarning($"[bugreport] could not write outbox: {e.Message}");
                return null;
            }
        }

        /// <summary>Throws rather than silently skipping. `f?.StoreBuffer(...)` on a failed Open wrote
        /// nothing and returned quietly, and WriteOutbox still reported success -- leaving a directory with
        /// no context.json that UploadDir bails on at every launch, forever, carrying no report.</summary>
        static void Write(string path, byte[] bytes)
        {
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            if (f == null) throw new System.IO.IOException($"could not open {path}: {FileAccess.GetOpenError()}");
            f.StoreBuffer(bytes);
        }

        static byte[] Read(string path) =>
            FileAccess.FileExists(path) ? FileAccess.GetFileAsBytes(path) : null;

        async System.Threading.Tasks.Task UploadDir(string dir)
        {
            await _uploadGate.WaitAsync();
            try
            {
                var ctxBytes = Read(dir + "/context.json");
                if (ctxBytes == null) return;
                using var form = new System.Net.Http.MultipartFormDataContent();
                form.Add(new System.Net.Http.StringContent(
                    System.Text.Encoding.UTF8.GetString(ctxBytes), System.Text.Encoding.UTF8,
                    "application/json"), "context");
                AddFile(form, dir + "/screenshot.png", "screenshot", "image/png");
                AddFile(form, dir + "/screenshot_after.png", "screenshot_after", "image/png");
                AddFile(form, dir + "/audio.wav", "audio", "audio/wav");

                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, Endpoint)
                {
                    Content = form,
                };
                // Header, never a query parameter: those land in the proxy's access log and in shell history.
                if (!string.IsNullOrEmpty(ReportKey))
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ReportKey);
                var resp = await Http.SendAsync(req);
                int code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    DeleteDir(dir);
                    SafeToast("report sent ✓");
                }
                else if (code >= 400 && code < 500 && code != 401 && code != 408 && code != 429)
                {
                    // A 4xx is the server saying THIS REPORT will never be accepted -- a malformed context,
                    // an oversized screenshot, a WAV it rejects. Retrying it forever re-POSTs it on every
                    // single launch for the life of the install, burning the player's own rate budget
                    // (12/10min, 60/day) so their next REAL report gets 429'd instead. Park it: keep the
                    // evidence on disk under a name RetryOutbox skips, so it is diagnosable and inert.
                    // 408 and 429 are excluded because those genuinely mean "try again".
                    Park(dir, code);
                    SafeToast($"report rejected ({code}) — kept locally");
                }
                else if (code == 401)
                    // NOT parked. A 401 means the key is stale, not that this report is unacceptable --
                    // update the build, paste a current key, and everything queued should then file.
                    SafeToast("report key rejected — update it in the launcher; report kept");
                else SafeToast($"upload failed ({code}) — will retry");
            }
            catch (System.Exception e)
            {
                GD.PushWarning($"[bugreport] upload failed, kept in outbox: {e.Message}");
                SafeToast("saved — upload failed, will retry");
            }
            finally { _uploadGate.Release(); }
        }

        /// <summary>Toast only if this node is still alive. UploadDir can be in flight for up to the
        /// HttpClient timeout, and the player can quit to menu inside that window -- PauseMenu's
        /// ReloadCurrentScene frees the whole tree. CallDeferred on a disposed GodotObject throws, and the
        /// catch block used to call it again, so the second throw escaped a detached Task entirely.</summary>
        void SafeToast(string text)
        {
            if (GodotObject.IsInstanceValid(this)) CallDeferred(nameof(Toast), text);
        }

        /// <summary>Rename a permanently-rejected report out of the retry path without destroying it.</summary>
        static void Park(string dir, int code)
        {
            try
            {
                var root = DirAccess.Open(OutboxRoot);
                string name = dir.Substring(dir.LastIndexOf('/') + 1);
                root?.Rename(name, RejectedPrefix + code + "_" + name);
            }
            catch (System.Exception e) { GD.PushWarning($"[bugreport] could not park {dir}: {e.Message}"); }
        }

        static void AddFile(System.Net.Http.MultipartFormDataContent form, string path, string field, string mime)
        {
            var bytes = Read(path);
            if (bytes == null) return;
            var c = new System.Net.Http.ByteArrayContent(bytes);
            c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            form.Add(c, field, System.IO.Path.GetFileName(path));
        }

        static void DeleteDir(string dir)
        {
            var da = DirAccess.Open(dir);
            if (da == null) return;
            foreach (var f in da.GetFiles()) da.Remove(f);
            DirAccess.Open(OutboxRoot)?.Remove(dir.Substring(dir.LastIndexOf('/') + 1));
        }

        const string RejectedPrefix = "rejected_";

        void StartRetry() => _ = RetryOutbox();

        /// <summary>async Task, not async void: an async void throwing puts an unhandled exception straight
        /// onto the synchronization-context pump rather than into a Task nobody observes.</summary>
        async System.Threading.Tasks.Task RetryOutbox()
        {
            var root = DirAccess.Open(OutboxRoot);
            if (root == null) return;
            int sent = 0;
            foreach (var sub in root.GetDirectories())
            {
                if (!GodotObject.IsInstanceValid(this)) return;
                if (sub.StartsWith(RejectedPrefix)) continue;      // parked: the server already refused it
                if (++sent > MaxRetryPerLaunch) break;             // a long backlog must not serialise 60 s each
                await UploadDir($"{OutboxRoot}/{sub}");
            }
        }
        const int MaxRetryPerLaunch = 10;

        void Toast(string text)
        {
            if (_label == null) return;
            _label.Text = text;
            _overlay.Visible = true;
            _toastLeft = 3.0;
        }

        /// <summary>Console `report <text>`: the same pipeline with typed text instead of audio. This is the
        /// fallback when there is no microphone, and the entry point an L1 test can drive.</summary>
        public string SubmitTyped(string text)
        {
            // Refuse while a hold is live. Opening the console mid-hold and typing `report ...` used to null
            // the audio buffer out from under the recording -- the drain then no-opped for the rest of the
            // hold and the voice report came out silent, tagged with the wrong status. Neither report was
            // what the player asked for.
            if (_session.State != ReportState.Idle)
            {
                Toast("finish the current report first");
                return null;
            }
            _audioStatus = AudioStatus.Typed;
            _audio = null;
            LastFrameCount = 0;
            var ctx = GatherContext();
            ctx["typed_text"] = text;
            LastOutboxDir = WriteOutbox(ctx, CaptureScreenshot(), null, null);
            // Was unconditionally "report sent ✓" -- it claimed success when WriteOutbox returned null and
            // when uploading was disabled entirely. A toast that lies about a bug report is its own bug.
            if (LastOutboxDir == null) { Toast("could not save the report"); return null; }
            if (UploadEnabled) { _ = UploadDir(LastOutboxDir); Toast("report sent ✓"); }
            else Toast("report saved");
            return LastOutboxDir;
        }
    }
}
