using Godot;

namespace UnturnedGodot
{
    // F3 in-game profiler overlay (master, for diagnosing framerate/stutter): FPS + frame time (with the WORST frame in
    // the sampling window, which is what stutter shows up as), CPU process/physics timings, render draw-calls/objects/
    // primitives, node count, and static + video memory. ProcessMode.Always so it keeps reading even while the sim is
    // paused. Toggle with F3. Refreshes 4x/sec so the text is readable; the worst-frame is tracked every frame.
    public partial class Profiler : CanvasLayer
    {
        Label _label;
        bool _on;
        double _accum, _worstFrame;
        // Engine CPU time accumulated PER FRAME across the window, so the coverage line compares an average
        // against an average. TimeProcess is an instantaneous sample; holding it up against a window-summed
        // attribution would make coverage swing with whichever frame the refresh happened to land on.
        double _procAccum, _physAccum;
        long _physFrames0;      // physics-step count at window start -- physics cost is per STEP, not per frame
        int _frames;
        int _gc0, _gc1, _gc2;   // last-window GC collection counts (gen0/1/2) -> deltas show allocation churn = the spike cause

        public override void _Ready()
        {
            Instance = this;
            Layer = 90;
            ProcessMode = Node.ProcessModeEnum.Always;
            _label = new Label { Position = new Vector2(10, 10), Visible = false };
            _label.AddThemeFontSizeOverride("font_size", 14);
            _label.AddThemeColorOverride("font_color", new Color(0.6f, 1f, 0.6f));
            _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            _label.AddThemeConstantOverride("outline_size", 4);
            AddChild(_label);
            SetProcess(false);   // start hidden: _Process is fully DISABLED (not even called) until F3 -> zero cost while off (master)
        }

        // F4 is a discriminator for a fill-vs-stall fps tank (originally paired with an F5 zombie-shadow
        // toggle -- docs/ZOMBIE_FPS_NOTES.md -- removed with the zombie system). Neither question can be
        // answered off a counter, and neither can be answered on the ARM box at all -- lavapipe's frame
        // timing sits on a fixed ~95-160ms floor -- so it has to be answerable in one keypress in the live
        // session where the tank actually happens.
        //
        //   F4  3D render scale 1.0 -> 0.5 -> 0.25. Fragment cost scales with pixels and NOTHING else does, so
        //       fps roughly doubling at 0.5 means it is fill (overdraw / shadow-map rasterisation); fps barely
        //       moving means it is not fill, and what is left is a stall.
        float _scale3D = 1f;

        // Driven by console verbs (`profiler`, `renderscale`) rather than F3/F4/F5. The function keys moved
        // to vehicle seat selection (strawberry 2026-08-16: "move the dev tools to be console commands
        // instead"), and a debug overlay is exactly the sort of thing that should not outrank a control the
        // player uses while driving.
        public static Profiler Instance;

        /// <summary>Show/hide the overlay. Sampling only runs while it is visible.</summary>
        public bool ToggleOverlay()
        {
            _on = !_on;
            _label.Visible = _on;
            SetProcess(_on);   // only run the per-frame sampling while the overlay is actually shown
            if (_on) { _accum = 0; _frames = 0; _worstFrame = 0; _procAccum = 0; _physAccum = 0; _physFrames0 = (long)Engine.GetPhysicsFrames(); }   // fresh sampling window on show
            return _on;
        }

        /// <summary>Cycle the 3D render scale 1 -> 0.5 -> 0.25 -> 1.</summary>
        public float CycleRenderScale()
        {
            _scale3D = _scale3D > 0.75f ? 0.5f : (_scale3D > 0.35f ? 0.25f : 1f);
            GetViewport().Scaling3DScale = _scale3D;
            return _scale3D;
        }

        // (zombie rig shadow toggle removed with the zombie system: ToggleZombieShadows/ShadowState/SetShadows
        // drove ZombieDirector's rig shadow casting and the DevConsole "zshadows" verb that called them; both
        // are gone now too.)

        public override void _Process(double delta)
        {
            if (delta > _worstFrame) _worstFrame = delta;   // track the worst (longest) frame this window -- that's the stutter
            _accum += delta; _frames++;
            _procAccum += Performance.GetMonitor(Performance.Monitor.TimeProcess);
            _physAccum += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess);
            if (_accum < 0.25) return;                       // refresh the text 4x/sec

            double M(Performance.Monitor m) => Performance.GetMonitor(m);
            double fps = M(Performance.Monitor.TimeFps);
            double frameMs = _accum / _frames * 1000.0;
            // C# GC churn = the usual cause of the frame spikes. Deltas are collections THIS window; a gen0 bump on a
            // bad-worst-frame window means that spike was a GC pause -> go hunt whatever allocates every frame.
            int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
            int d0 = g0 - _gc0, d1 = g1 - _gc1, d2 = g2 - _gc2; _gc0 = g0; _gc1 = g1; _gc2 = g2;
            double heapMB = System.GC.GetTotalMemory(false) / 1048576.0;
            string gcFlag = d0 > 0 ? "  <-- GC ran" : "";
            double procMs = _procAccum / _frames * 1000.0, physMs = _physAccum / _frames * 1000.0;
            _label.Text =
                $"FPS {fps:0}    frame {frameMs:0.0} ms    worst {_worstFrame * 1000.0:0.0} ms{gcFlag}\n" +
                $"cpu: process {procMs:0.0} ms   physics {physMs:0.0} ms   (window avg)\n" +
                $"{Coverage(frameMs, procMs, physMs, _frames, (long)Engine.GetPhysicsFrames() - _physFrames0)}\n" +
                $"GC/window: gen0 +{d0}  gen1 +{d1}  gen2 +{d2}    managed heap {heapMB:0.0} MB\n" +
                $"physics: {M(Performance.Monitor.Physics3DActiveObjects):0} active   {M(Performance.Monitor.Physics3DCollisionPairs):0} pairs   {M(Performance.Monitor.Physics3DIslandCount):0} islands\n" +
                $"render: {M(Performance.Monitor.RenderTotalDrawCallsInFrame):0} draws   {M(Performance.Monitor.RenderTotalObjectsInFrame):0} objs   {M(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1.0e6:0.0}M prims\n" +
                $"scene: {M(Performance.Monitor.ObjectNodeCount):0} nodes   {M(Performance.Monitor.ObjectCount):0} objects   {M(Performance.Monitor.ObjectResourceCount):0} res   {M(Performance.Monitor.ObjectOrphanNodeCount):0} orphans\n" +
                $"mem: static {M(Performance.Monitor.MemoryStatic) / 1048576.0:0} MB   vram {M(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:0} MB\n" +
                $"systems (ms/win, big = the spike): {SystemsBreakdown()}\n" +
                $"counts/win: {CountsBreakdown()}\n" +
                $"3d scale {_scale3D:0.00} [F4]   [F3 to hide]";
            Prof.Reset();
            _accum = 0; _frames = 0; _worstFrame = 0; _procAccum = 0; _physAccum = 0;
            _physFrames0 = (long)Engine.GetPhysicsFrames();
        }

        /// Tallies, printed as whole numbers. Never folded into the ms line: see Prof.Counts.
        static string CountsBreakdown()
        {
            if (Prof.Counts.Count == 0) return "(none)";
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(Prof.Counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in list) parts.Add($"{kv.Key} {kv.Value}");
            return string.Join("   ", parts);
        }

        /// <summary>THE LINE THIS OVERLAY WAS MISSING. Everything above ranks what is instrumented; this says
        /// how much of the frame that ranking actually explains. Without it the systems row reads as "the
        /// frame" -- on the real map it showed `lookat 2.7` under a `process 6.4 ms` line and the other 3.7 ms
        /// had no representation on screen at all.
        ///
        /// Physics is divided by PHYSICS STEPS, not process frames. At 169 fps against a 60 Hz physics tick
        /// those differ by nearly 3x, and dividing physics microseconds by frames would report a third of the
        /// real per-step cost -- flattering coverage exactly where the time is suspected to be.</summary>
        static string Coverage(double frameMs, double procMs, double physMs, int frames, long physSteps)
        {
            var (totalUs, physUs) = Prof.Totals();
            // Denominator is the FRAME, not TimeProcess. Every instrumented scope -- process and physics
            // alike -- divided by rendered frames is directly comparable to frame time, and frame time is
            // both what you care about and independently corroborated by the FPS counter.
            //
            // TimeProcess is NOT trustworthy as a denominator: observed reading 12.9 ms on a 6.8 ms frame at
            // 150 fps, which is impossible -- a frame cannot contain more process than frame. Rather than
            // quietly divide by it and publish a percentage built on a broken number, it is shown as raw
            // engine detail and CALLED OUT when it exceeds the frame.
            double attr = frames > 0 ? (totalUs / 1000.0) / frames : 0.0;
            double pct = frameMs > 0.01 ? 100.0 * attr / frameMs : 0.0;
            double attrPhys = physSteps > 0 ? (physUs / 1000.0) / physSteps : 0.0;
            string suspect = procMs > frameMs * 1.05 ? "  <-- engine process>frame, monitor suspect" : "";
            return $"coverage: {attr:0.00} of {frameMs:0.0} ms frame ({pct:0}%)   UNATTRIBUTED {System.Math.Max(0.0, frameMs - attr):0.0} ms/frame" +
                   $"   [engine: process {procMs:0.0} ms, physics {physMs:0.0} ms/step, attributed {attrPhys:0.00}/step]{suspect}";
        }

        /// Biggest spender first, with the two things a bare total cannot tell apart: how many CALLS made it
        /// (2 ms in one call is a slow function; 2 ms over 300 is per-node _Process overhead on 148 lamps),
        /// and the worst SINGLE frame, because a hitch averaged over a 250 ms window disappears.
        static string SystemsBreakdown()
        {
            if (Prof.Us.Count == 0) return "(none instrumented / idle)";
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(Prof.Us);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));   // biggest spender first
            var parts = new System.Collections.Generic.List<string>();
            int shown = 0; long rest = 0;
            foreach (var kv in list)
            {
                if (shown >= 8) { rest += kv.Value; continue; }   // a HUD row that wraps is a HUD row nobody reads
                Prof.Calls.TryGetValue(kv.Key, out int n);
                Prof.Peak.TryGetValue(kv.Key, out long pk);
                parts.Add($"{kv.Key} {kv.Value / 1000.0:0.0}(x{n},pk{pk / 1000.0:0.0})");
                shown++;
            }
            if (rest > 0) parts.Add($"+{list.Count - shown} more {rest / 1000.0:0.0}");
            return string.Join("   ", parts);
        }
    }
}
