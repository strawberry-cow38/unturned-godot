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
        }

        public override void _Input(InputEvent e)
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.F3, Echo: false }) { _on = !_on; _label.Visible = _on; }
        }

        public override void _Process(double delta)
        {
            if (!_on) return;
            if (delta > _worstFrame) _worstFrame = delta;   // track the worst (longest) frame this window -- that's the stutter
            _accum += delta; _frames++;
            if (_accum < 0.25) return;                       // refresh the text 4x/sec

            double M(Performance.Monitor m) => Performance.GetMonitor(m);
            double fps = M(Performance.Monitor.TimeFps);
            double frameMs = _accum / _frames * 1000.0;
            _label.Text =
                $"FPS {fps:0}    frame {frameMs:0.0} ms   (worst {_worstFrame * 1000.0:0.0} ms)\n" +
                $"cpu: process {M(Performance.Monitor.TimeProcess) * 1000.0:0.0} ms   physics {M(Performance.Monitor.TimePhysicsProcess) * 1000.0:0.0} ms\n" +
                $"render: {M(Performance.Monitor.RenderTotalDrawCallsInFrame):0} draw calls   {M(Performance.Monitor.RenderTotalObjectsInFrame):0} objects   {M(Performance.Monitor.RenderTotalPrimitivesInFrame):0} prims\n" +
                $"nodes {M(Performance.Monitor.ObjectNodeCount):0}   mem {M(Performance.Monitor.MemoryStatic) / 1048576.0:0} MB   vram {M(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:0} MB\n" +
                $"[F3 to hide]";

            _accum = 0; _frames = 0; _worstFrame = 0;
        }
    }
}
