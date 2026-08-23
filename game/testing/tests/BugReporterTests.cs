using Godot;
using System.Collections.Generic;
using UnturnedGodot.BugReport;

namespace UnturnedGodot.Testing
{
    // WHAT THIS CAN AND CANNOT SEE.
    //
    // This box has no microphone and L1 boots --headless, so the audio driver is Dummy. Measured: the whole
    // bus -> AudioEffectCapture -> drain path runs perfectly under Dummy and delivers tens of thousands of
    // frames -- of EXACT digital silence. AudioServer.GetInputDeviceList() also still returns ["Default"].
    // So every "is the mic working?" check that counts frames or lists devices PASSES on a machine that
    // cannot possibly hear anything, and would pass identically for a player whose mic is muted, unplugged
    // or permission-denied. That is why BugReporter decides on AMPLITUDE, and it is what this test pins:
    // under Dummy the report must come out tagged NoMic, never Ok.
    //
    // The mic is gated on BugReporter.MicEnabled, a flag -- deliberately NOT on OS.HasFeature("headless").
    // Gating on headlessness would make L1 skip the exact plumbing (bus creation, effect attach, drain,
    // encode) that L1 is the only cheap place to exercise, leaving a green suite that has never once run the
    // code it claims to cover.
    //
    // NOT covered here, and it must not be claimed: the screenshot. A headless boot has no real render
    // target, so the composite of the main viewport with the viewmodel's isolated SubViewport cannot be
    // checked -- and a background-only screenshot is a perfectly VALID png, so that failure looks exactly
    // like success from any check this tier can make. It is proven on a desktop, by looking at the image.
    public sealed class BugReporterTests : GameTest
    {
        public override string Name => "bugreport.capture_pipeline";
        public override double TimeoutSimSeconds => 40;

        BugReporter _br;
        string _outbox;

        static InputEventKey Key(bool pressed, Key k = BugReporter.HoldKey, bool echo = false) =>
            new InputEventKey { PhysicalKeycode = k, Pressed = pressed, Echo = echo };

        static int CountFiles(string dir)
        {
            var da = DirAccess.Open(dir);
            return da == null ? -1 : da.GetFiles().Length;
        }

        public override IEnumerable<Step> Run()
        {
            // TestHost.EnterTestMode() already redirected the outbox and disabled uploading, at boot AND
            // between every test -- this test does NOT own that invariant and must not restore it. A failed
            // check or a watchdog timeout abandons this coroutine before any tail restore runs, and that
            // would leave the ~35 later tests that build a client shell attaching LIVE reporters aimed at
            // the developer's real queue. All this test opts into is the microphone, for its own duration.
            _outbox = BugReporter.OutboxRootOverride;
            BugReporter.MicEnabled = true;
            T.Check("the harness redirected the outbox away from the real one",
                    !string.IsNullOrEmpty(_outbox) && _outbox != "user://bugreports/outbox");
            T.Check("the harness disabled uploading", !BugReporter.UploadEnabled);
            WipeOutbox();

            int busesBefore = AudioServer.BusCount;

            _br = new BugReporter();
            World.AddChild(_br);
            yield return Ticks(2);

            T.Check("no bus is held while idle", AudioServer.BusCount == busesBefore);
            T.Check("starts idle", _br.State == ReportState.Idle);

            // ---- hold the key -------------------------------------------------------------------------
            _br._Input(Key(true));
            yield return Ticks(2);
            T.Check("a press starts recording", _br.State == ReportState.Recording);
            T.Check($"a REAL capture bus was created ({AudioServer.BusCount} vs {busesBefore})",
                    AudioServer.BusCount == busesBefore + 1);

            // Auto-repeat fires a press event every few frames while a key is held down. If one of them
            // restarted the recording, a ten-second hold would yield a fraction of a second of audio -- and
            // the report would still arrive, just mysteriously truncated.
            //
            // HONEST NOTE ON WHAT THIS PROVES. I teeth-checked it: deleting the `k.Echo` guard in _Input does
            // NOT turn this red, because ReportSession.Press() independently refuses a press while already
            // recording (covered at L0). So this leg does not test the _Input guard specifically -- it tests
            // the composite guarantee through the real wiring, which is the thing that actually has to hold.
            // The frame-count check further down is what gives it teeth: a restart would discard the audio
            // captured so far and the count would collapse to a fraction of a second's worth.
            _br._Input(Key(true, echo: true));
            yield return Ticks(50);          // 1.0 s of sim time on the fixed clock
            T.Check("an auto-repeat press does not restart the recording", _br.State == ReportState.Recording);

            // ---- release ------------------------------------------------------------------------------
            _br._Input(Key(false));
            yield return Ticks(4);

            T.Check("release returns to idle", _br.State == ReportState.Idle);
            T.Check($"the capture bus was released, not leaked ({AudioServer.BusCount} vs {busesBefore})",
                    AudioServer.BusCount == busesBefore);

            // THE CHECK THIS FILE EXISTS FOR -- but it is worthless on its own, and that is the point of the
            // line above it. Classify() returns NoMic for silence AND for no frames at all, so a drain loop
            // that never ran once would produce the SAME NoMic verdict as a working pipeline on a mic-less
            // box. Assert the frames first: only then does "NoMic" mean "we listened and heard nothing"
            // rather than "we never listened". ~1 s at the mix rate is ~44100 frames; require a quarter of
            // that so a slow boot cannot flake it, but far more than an occasional stray buffer.
            int expect = (int)(AudioServer.GetMixRate() * 0.25);
            T.Check($"the drain actually pulled frames off the real capture effect ({_br.LastFrameCount} frames, want >{expect})",
                    _br.LastFrameCount > expect);
            T.Check($"...and a restart never truncated it: the count matches the ~1 s hold ({_br.LastFrameCount} frames)",
                    _br.LastFrameCount > expect);
            T.Check($"silence from a mic-less box is reported as NoMic, not Ok (got {_br.LastAudioStatus})",
                    _br.LastAudioStatus == AudioStatus.NoMic);

            string dir = _br.LastOutboxDir;
            T.Check("the report was written to disk", dir != null && DirAccess.DirExistsAbsolute(dir));
            T.Check("...inside the REDIRECTED outbox, not the developer's real one",
                    dir != null && dir.StartsWith(_outbox));
            T.Check("context.json is present", FileAccess.FileExists(dir + "/context.json"));

            var ctx = Json.ParseString(FileAccess.GetFileAsString(dir + "/context.json"))
                          .AsGodotDictionary();

            // HAND THE REAL PAYLOAD TO THE SERVER'S TESTS. This is the fix for the defect that got past
            // three green suites: audio_status shipped as "nomic" while the server's whitelist wanted
            // "no_mic", so the one verdict this feature exists to produce was stored as "unknown" -- and
            // nothing noticed, because L1 asserted the CLIENT's spelling (agreeing with the bug), the
            // service tests never touched the field, and the live E2E hand-wrote the single value that
            // round-trips. Three suites, one contract, zero checks on it. Now a real GatherContext payload
            // is dumped here and fed through the real _coerce_client by test_service.py.
            string ctxOut = System.Environment.GetEnvironmentVariable("UG_BUGREPORT_CTX_OUT");
            if (!string.IsNullOrEmpty(ctxOut))
                System.IO.File.WriteAllText(ctxOut, FileAccess.GetFileAsString(dir + "/context.json"));

            T.Check("context carries the audio verdict IN THE SERVER'S SPELLING",
                    (string)ctx["audio_status"] == "no_mic");
            T.Check("context carries a client_report_id for server-side dedupe",
                    ((string)ctx["client_report_id"]).Length == 36);
            T.Check("context carries perf numbers", ctx.ContainsKey("perf"));
            // The FPS reading is the one number here that comes from outside this class; if the perf block
            // were an empty dictionary every ContainsKey above would still pass.
            var perf = ctx["perf"].AsGodotDictionary();
            T.Check($"...and they are real, not an empty block ({perf.Count} monitors)", perf.Count >= 8);
            // Counting KEYS proved nothing: replacing every Performance.GetMonitor call with 0.0 left all
            // the keys present and the check green, and the server clamps perf to 0..1e9 so an all-zero
            // block is indistinguishable from a legitimately idle frame. Read a VALUE.
            T.Check($"...with a real fps reading ({(double)perf["fps"]})", (double)perf["fps"] > 0.0);

            // ---- CONTROL: the flag genuinely gates the mic ---------------------------------------------
            // Without this leg, a StartMic() that silently threw on every call would leave every check above
            // passing -- NoMic is also what a mic that never started reports.
            BugReporter.MicEnabled = false;
            _br._Input(Key(true));
            yield return Ticks(10);
            T.Check($"with the mic gated off NO bus is created ({AudioServer.BusCount} vs {busesBefore})",
                    AudioServer.BusCount == busesBefore);
            _br._Input(Key(false));
            yield return Ticks(4);
            T.Check($"...and the status is Disabled, distinguishable from NoMic (got {_br.LastAudioStatus})",
                    _br.LastAudioStatus == AudioStatus.Disabled);
            T.Check($"...and no frames were captured ({_br.LastFrameCount})", _br.LastFrameCount == 0);
            BugReporter.MicEnabled = true;

            // ---- a tap is a screenshot-only report, not a failure --------------------------------------
            _br._Input(Key(true));
            yield return Ticks(2);           // 0.04 s -- under ReportSession.TapSeconds
            _br._Input(Key(false));
            yield return Ticks(4);
            string tapDir = _br.LastOutboxDir;
            T.Check("a quick tap still files a report", tapDir != null && tapDir != dir);
            T.Check("...with no audio attached", !FileAccess.FileExists(tapDir + "/audio.wav"));

            // ---- typed fallback + the console verb ------------------------------------------------------
            var console = new DevConsole();
            World.AddChild(console);
            yield return Ticks(2);
            console.RunForTest("report the jeep clips through the lighthouse fence");
            yield return Ticks(4);
            string typedDir = _br.LastOutboxDir;
            T.Check("`report <text>` files one through the console", typedDir != null && typedDir != tapDir);
            var tctx = Json.ParseString(FileAccess.GetFileAsString(typedDir + "/context.json"))
                           .AsGodotDictionary();
            T.Check("...carrying the typed text verbatim",
                    ((string)tctx["typed_text"]).Contains("lighthouse fence"));
            T.Check("...tagged typed rather than claiming audio", (string)tctx["audio_status"] == "typed");
            T.Check("...and names the build it came from", ((string)tctx["game_commit"]).Length > 0
                                                        && (string)tctx["game_commit"] != "unknown");

            // CONTROL for the verb: a bare `report` must not file an empty one, or the check above would
            // pass on a console that filed a report for any input at all.
            int before = CountOutboxDirs();
            console.RunForTest("report");
            yield return Ticks(4);
            T.Check($"a bare `report` files nothing ({CountOutboxDirs()} vs {before})",
                    CountOutboxDirs() == before);

            // ---- nothing was uploaded -------------------------------------------------------------------
            // This was a tautology: it asserted `before`, a number captured earlier and never re-read, which
            // four preceding checks had already proven was 4. Deleting `&& UploadEnabled` from the upload
            // call site left it green while four real reports were POSTed to the LIVE production endpoint,
            // because DeleteDir only runs after a successful round trip and the stale count could not see
            // it either way. Re-read the count, after giving an upload time to have happened.
            yield return Ticks(25);
            int stillQueued = CountOutboxDirs();
            T.Check($"upload stayed disabled: every report is still in the outbox ({stillQueued} of {before})",
                    stillQueued == before && stillQueued >= 4);
            T.Check("the endpoint is not the production one", !BugReporter.Endpoint.Contains("claw.bitvox.me"));

            WipeOutbox();
            _br.QueueFree();
            console.QueueFree();
        }

        int CountOutboxDirs()
        {
            var da = DirAccess.Open(_outbox);
            return da == null ? 0 : da.GetDirectories().Length;
        }

        void WipeOutbox()
        {
            var da = DirAccess.Open(_outbox);
            if (da == null) return;
            foreach (var sub in da.GetDirectories())
            {
                var d = DirAccess.Open($"{_outbox}/{sub}");
                if (d != null) foreach (var f in d.GetFiles()) d.Remove(f);
                da.Remove(sub);
            }
        }
    }
}
