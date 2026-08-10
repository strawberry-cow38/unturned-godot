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

        // F4/F5 are the two discriminators for the zombie fps tank (docs/ZOMBIE_FPS_NOTES.md). Neither question
        // can be answered off a counter, and neither can be answered on the ARM box at all -- lavapipe's frame
        // timing sits on a fixed ~95-160ms floor -- so they have to be answerable in one keypress in the live
        // session where the tank actually happens.
        //
        //   F4  3D render scale 1.0 -> 0.5 -> 0.25. Fragment cost scales with pixels and NOTHING else does, so
        //       fps roughly doubling at 0.5 means it is fill (overdraw / shadow-map rasterisation); fps barely
        //       moving means it is not fill, and what is left is a stall.
        //   F5  zombie shadow casting off. Recovers -> the shadow pass (which is 178 of 303 zombie draws and 75%
        //       of their triangles, measured). Doesn't -> the zombie render path itself.
        //
        // F5 applies to the zombies that exist right now; press it again after a wave spawns.
        float _scale3D = 1f;
        bool _zShadows = true;

        public override void _Input(InputEvent e)
        {
            if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
            if (k.Keycode == Key.F3)
            {
                _on = !_on;
                _label.Visible = _on;
                SetProcess(_on);   // only run the per-frame sampling while the overlay is actually shown
                if (_on) { _accum = 0; _frames = 0; _worstFrame = 0; _procAccum = 0; _physAccum = 0; _physFrames0 = (long)Engine.GetPhysicsFrames(); }   // fresh sampling window on show
            }
            else if (k.Keycode == Key.F4)
            {
                _scale3D = _scale3D > 0.75f ? 0.5f : (_scale3D > 0.35f ? 0.25f : 1f);
                GetViewport().Scaling3DScale = _scale3D;
            }
            else if (k.Keycode == Key.F5)
            {
                _zShadows = !_zShadows;
                foreach (var z in GetTree().GetNodesInGroup("zombies")) SetShadows(z, _zShadows);
                // ...and the REWRITE's rigs, which are not in that group and so were never toggled. F5
                // silently did nothing under --newzombies while the overlay claimed a state: pressing it
                // produced no change, which reads as "shadows are not the cost" rather than "the control
                // is not wired". A debug toggle that no-ops is a false negative generator.
                ZombieDirector.Instance?.SetRigShadows(_zShadows);
            }
        }

        /// What the zombies are ACTUALLY doing, preferring the live director over this class's own flag.
        string ShadowState()
        {
            var zd = ZombieDirector.Instance;
            if (zd != null) return zd.RigShadowsOn ? "ON" : "OFF";
            return _zShadows ? "ON" : "OFF";
        }

        static void SetShadows(Node root, bool on)
        {
            if (root is GeometryInstance3D gi)
                gi.CastShadow = on ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
            foreach (var c in root.GetChildren()) SetShadows(c, on);
        }

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
                $"{Coverage(procMs, physMs, _frames, (long)Engine.GetPhysicsFrames() - _physFrames0)}\n" +
                $"GC/window: gen0 +{d0}  gen1 +{d1}  gen2 +{d2}    managed heap {heapMB:0.0} MB\n" +
                $"physics: {M(Performance.Monitor.Physics3DActiveObjects):0} active   {M(Performance.Monitor.Physics3DCollisionPairs):0} pairs   {M(Performance.Monitor.Physics3DIslandCount):0} islands\n" +
                $"render: {M(Performance.Monitor.RenderTotalDrawCallsInFrame):0} draws   {M(Performance.Monitor.RenderTotalObjectsInFrame):0} objs   {M(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1.0e6:0.0}M prims\n" +
                $"scene: {M(Performance.Monitor.ObjectNodeCount):0} nodes   {M(Performance.Monitor.ObjectCount):0} objects   {M(Performance.Monitor.ObjectResourceCount):0} res   {M(Performance.Monitor.ObjectOrphanNodeCount):0} orphans\n" +
                $"mem: static {M(Performance.Monitor.MemoryStatic) / 1048576.0:0} MB   vram {M(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:0} MB\n" +
                $"systems (ms/win, big = the spike): {SystemsBreakdown()}\n" +
                $"counts/win: {CountsBreakdown()}\n" +
                // Report the rigs' ACTUAL casting state when the rewrite is live, not this toggle's flag.
                // The flag said "ON" while ZombieDirector.BuildRig had set them Off, and that readout was
                // acted on as evidence.
                $"3d scale {_scale3D:0.00} [F4]   zombie shadows {ShadowState()} [F5]   [F3 to hide]";
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
        static string Coverage(double procMs, double physMs, int frames, long physSteps)
        {
            var (totalUs, physUs) = Prof.Totals();
            double attrProc = frames > 0 ? ((totalUs - physUs) / 1000.0) / frames : 0.0;
            double attrPhys = physSteps > 0 ? (physUs / 1000.0) / physSteps : 0.0;
            double pp = procMs > 0.01 ? 100.0 * attrProc / procMs : 0.0;
            double hp = physMs > 0.01 ? 100.0 * attrPhys / physMs : 0.0;
            return $"coverage: process {attrProc:0.0}/{procMs:0.0} ms ({pp:0}%)   physics {attrPhys:0.0}/{physMs:0.0} ms ({hp:0}%)" +
                   $"   UNATTRIBUTED {System.Math.Max(0.0, procMs - attrProc):0.0} ms/frame + {System.Math.Max(0.0, physMs - attrPhys):0.0} ms/step";
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
