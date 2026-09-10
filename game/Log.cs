using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>THE GAME'S LOG, buffered in memory and shown in the in-game console instead of being written to a
    /// Windows console window (strawberry 2026-09-10: "writing to the windows console is really slow and causes a
    /// stutter when we do a lot of prints").
    ///
    /// ⚠ WHY THIS IS NOT JUST "TURN PRINTING OFF". The cost is not printing, it is conhost: a write to an ATTACHED
    /// console window is a synchronous round-trip to another process, and a frame that logs a few dozen lines pays
    /// for every one of them. A write to a REDIRECTED stdout is an ordinary buffered file write and costs nothing
    /// worth measuring -- which matters, because the render harness redirects stdout to prof/*.err and those logs
    /// are a real diagnostic (they are how the threaded-renderer errors got dated to before the work being blamed).
    /// Killing stdout outright would have fixed the stutter and blinded every offline run.
    ///
    /// So the discriminator is the thing that actually differs: <see cref="System.Console.IsOutputRedirected"/>.
    /// Redirected (a render, a piped run, headless CI) -> mirror to stdout as before. A live console window
    /// attached (master playing) -> buffer only, and read it in-game with the console key.
    ///
    /// UG_LOG_STDOUT=1 forces the mirror back on, =0 forces it off, for when the automatic answer is wrong.</summary>
    public static class Log
    {
        public const int Capacity = 1024;                  // ring; a session's worth of scrollback without unbounded growth
        static readonly Queue<string> _lines = new();
        static readonly object _gate = new();
        static bool? _mirror;
        static ulong _seq;

        /// <summary>Does this run also write to stdout? See the class note -- redirected is cheap, a console is not.</summary>
        public static bool Mirror
        {
            get
            {
                if (_mirror.HasValue) return _mirror.Value;
                string forced = System.Environment.GetEnvironmentVariable("UG_LOG_STDOUT");
                if (forced == "1") { _mirror = true; return true; }
                if (forced == "0") { _mirror = false; return false; }
                bool redirected;
                try { redirected = System.Console.IsOutputRedirected; }
                catch { redirected = true; }              // cannot tell -> keep the logs, the stutter is the lesser bug
                bool headless = DisplayServer.GetName() == "headless";
                _mirror = redirected || headless;
                return _mirror.Value;
            }
        }

        /// <summary>Every buffered line, oldest first. The console renders the tail of this.</summary>
        public static string[] Lines { get { lock (_gate) return _lines.ToArray(); } }
        public static ulong Sequence { get { lock (_gate) return _seq; } }   // bumped per line, so a viewer can repaint only on a change

        static void Add(string s)
        {
            lock (_gate)
            {
                _lines.Enqueue(s);
                while (_lines.Count > Capacity) _lines.Dequeue();
                _seq++;
            }
        }

        public static void Print(string msg)
        {
            Add(msg);
            if (Mirror) GD.Print(msg);
        }

        public static void Print(params object[] what)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var o in what) sb.Append(o);
            Print(sb.ToString());
        }

        /// <summary>Errors ALWAYS reach stderr as well as the buffer. stderr is not the slow path -- it is not the
        /// console window being written to in bulk -- and an error that only exists inside a running game is one
        /// nobody can send me after a crash.</summary>
        public static void Err(string msg)
        {
            Add("[err] " + msg);
            GD.PrintErr(msg);
        }

        public static void Err(params object[] what)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var o in what) sb.Append(o);
            Err(sb.ToString());
        }

        public static void ClearForTest() { lock (_gate) { _lines.Clear(); _seq = 0; } }
        public static void SetMirrorForTest(bool? v) => _mirror = v;
    }
}
