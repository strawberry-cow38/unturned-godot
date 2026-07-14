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

        public override void _Input(InputEvent e)
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.F3, Echo: false })
            {
                _on = !_on;
                _label.Visible = _on;
                SetProcess(_on);   // only run the per-frame sampling while the overlay is actually shown
                if (_on) { _accum = 0; _frames = 0; _worstFrame = 0; }   // fresh sampling window on show
            }
        }

        public override void _Process(double delta)
        {
            if (delta > _worstFrame) _worstFrame = delta;   // track the worst (longest) frame this window -- that's the stutter
            _accum += delta; _frames++;
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
            _label.Text =
                $"FPS {fps:0}    frame {frameMs:0.0} ms    worst {_worstFrame * 1000.0:0.0} ms{gcFlag}\n" +
                $"cpu: process {M(Performance.Monitor.TimeProcess) * 1000.0:0.0} ms   physics {M(Performance.Monitor.TimePhysicsProcess) * 1000.0:0.0} ms\n" +
                $"GC/window: gen0 +{d0}  gen1 +{d1}  gen2 +{d2}    managed heap {heapMB:0.0} MB\n" +
                $"physics: {M(Performance.Monitor.Physics3DActiveObjects):0} active   {M(Performance.Monitor.Physics3DCollisionPairs):0} pairs   {M(Performance.Monitor.Physics3DIslandCount):0} islands\n" +
                $"render: {M(Performance.Monitor.RenderTotalDrawCallsInFrame):0} draws   {M(Performance.Monitor.RenderTotalObjectsInFrame):0} objs   {M(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1.0e6:0.0}M prims\n" +
                $"scene: {M(Performance.Monitor.ObjectNodeCount):0} nodes   {M(Performance.Monitor.ObjectCount):0} objects   {M(Performance.Monitor.ObjectResourceCount):0} res   {M(Performance.Monitor.ObjectOrphanNodeCount):0} orphans\n" +
                $"mem: static {M(Performance.Monitor.MemoryStatic) / 1048576.0:0} MB   vram {M(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:0} MB\n" +
                $"systems (ms/win, big = the spike): {SystemsBreakdown()}\n" +
                $"[F3 to hide]";
            Prof.Reset();
            _accum = 0; _frames = 0; _worstFrame = 0;
        }

        static string SystemsBreakdown()
        {
            if (Prof.Us.Count == 0) return "(none instrumented / idle)";
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(Prof.Us);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));   // biggest spender first
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in list) parts.Add($"{kv.Key} {kv.Value / 1000.0:0.0}");
            return string.Join("   ", parts);
        }
    }
}
