using Godot;

namespace UnturnedGodot
{
    /// <summary>Frame cost, averaged over a second, for any harness that wants a before/after number.
    ///
    /// ⭐ THE EXISTING UG_PERFPROBE LIVES IN A NODE ONLY SOME MODES BUILD, so a harness like --throwtest got
    /// nothing from it -- the flag was set, the env var was read, and no line ever appeared. This is the same
    /// measurement with no dependency on which world was built, so a mode can opt in with one line.
    ///
    /// ⚠ AVERAGED, NOT INSTANTANEOUS. A single frame varies by more than most changes being measured, so an
    /// instantaneous FPS readout will happily show a regression as an improvement. Draw calls are logged beside it
    /// because they separate the two things that look identical in a frame time: too much GEOMETRY submitted, and
    /// too many PIXELS shaded. A fill-rate problem moves the milliseconds and leaves the draw count alone.</summary>
    public partial class FramePerfProbe : Node
    {
        public string Tag = "perf";
        double _elapsed; int _frames;

        public override void _Process(double delta)
        {
            _elapsed += delta; _frames++;
            if (_elapsed < 1.0) return;
            double ms = _elapsed * 1000.0 / _frames;
            Log.Print($"[{Tag}] {_frames / _elapsed:0.0} fps  {ms:0.000} ms/frame  "
                    + $"draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}");
            _elapsed = 0.0; _frames = 0;
        }
    }
}
