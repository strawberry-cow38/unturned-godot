using System;
using Godot;

namespace UnturnedGodot
{
    /// <summary>Attributes frame hitches to the GC, or rules it out.
    ///
    /// Master reports hard stutters "either when streaming props (driving or flying) or just FAT GCs". Those are
    /// two hypotheses, and they may well be ONE: streaming a prop allocates meshes, and allocation is what buys
    /// a collection. Guessing between them is how you tune the wrong thing for a week, so this measures.
    ///
    /// ⭐⭐ IT READS THE RUNTIME'S OWN PAUSE TIMES, IT DOES NOT INFER THEM. `GC.GetGCMemoryInfo()` on .NET 8
    /// reports the last collection's generation, whether it was compacting, and its actual PauseDurations. So
    /// "that 180 ms frame was a gen2" is a fact off the runtime rather than a correlation I talked myself into.
    ///
    /// ⚠⚠ AND IT CHECKS THE PREVIOUS FRAME TOO. A collection that completes near the end of frame N shows up as
    /// a long frame N+1 -- the hitch lands AFTER the GC, not on it. Looking only at the current frame finds
    /// nothing and concludes, wrongly, that the GC is innocent. (Learned the hard way on an unrelated project:
    /// the hitch is the frame after the collection.)
    ///
    /// ⚠ OFF BY DEFAULT (`UG_GCWATCH=1`). It is a diagnostic, and a diagnostic that costs frames in the shipped
    /// game is itself a stutter.</summary>
    public partial class GcWatch : Node
    {
        // ⚠ Fully qualified: `using Godot;` puts Godot.Environment (the 3D world environment) in scope, so an
        // unqualified `Environment` is CS0104 ambiguous in every file in this project. BugReporter carries the
        // same note about HttpClient -- it is a standing trap here, not a one-off.
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("UG_GCWATCH") == "1";

        /// <summary>A frame this long is a hitch worth explaining. 33 ms is two frames at 60 -- below that it is
        /// jitter, and master's complaint is "hard" stutters, not microjitter.</summary>
        static double HitchMs =>
            double.TryParse(System.Environment.GetEnvironmentVariable("UG_GCWATCH_MS"), out var v) ? v : 33.0;

        // ---- rolling state ----------------------------------------------------------------------------
        long _lastGcIndex = -1;
        long _lastAlloc;
        int[] _lastCounts = new int[3];
        bool _prevFrameHadGc;
        int _prevFrameGen;
        double _prevPauseMs;

        // ---- totals -----------------------------------------------------------------------------------
        long _frames, _hitches, _hitchesWithGc, _hitchesNearButInnocent, _gcs;

        /// <summary>How much of a hitch the GC pause must account for before the GC is blamed for it. A
        /// collection that explains 3 ms of a 174 ms frame did not cause that frame.</summary>
        const double BlameShare = 0.30;
        double _worstMs, _worstGcPauseMs, _totalPauseMs;
        readonly int[] _byGen = new int[3];
        double _sinceReport;

        public override void _Ready()
        {
            if (!Enabled) { SetProcess(false); return; }
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            _lastAlloc = GC.GetTotalAllocatedBytes(false);
            for (int g = 0; g < 3; g++) _lastCounts[g] = GC.CollectionCount(g);
            GD.Print($"[gcwatch] on. hitch threshold {HitchMs:F0} ms. " +
                     $"ServerGC={System.Runtime.GCSettings.IsServerGC} " +
                     $"Latency={System.Runtime.GCSettings.LatencyMode}");
        }

        void HubProcess(double delta)
        {
            _frames++;
            double frameMs = delta * 1000.0;
            if (frameMs > _worstMs) _worstMs = frameMs;

            long alloc = GC.GetTotalAllocatedBytes(false);
            long allocThisFrame = alloc - _lastAlloc;
            _lastAlloc = alloc;

            // Did a collection COMPLETE since we last looked? Index is monotonic, so this cannot miss one
            // the way comparing CollectionCount per generation can when several land in a frame.
            var info = GC.GetGCMemoryInfo();
            bool newGc = info.Index != _lastGcIndex;
            int gen = 0;
            double pauseMs = 0;
            if (newGc)
            {
                _lastGcIndex = info.Index;
                _gcs++;
                gen = info.Generation;
                if (gen >= 0 && gen < 3) _byGen[gen]++;
                foreach (var p in info.PauseDurations) pauseMs += p.TotalMilliseconds;
                _totalPauseMs += pauseMs;
                if (pauseMs > _worstGcPauseMs) _worstGcPauseMs = pauseMs;
            }

            if (frameMs >= HitchMs)
            {
                _hitches++;
                // ⚠⚠ THIS frame or the PREVIOUS one. A collection finishing late in frame N is paid for in
                // frame N+1, so requiring the GC to land on the hitch frame itself under-counts badly.
                //
                // ⚠⚠⚠ BUT PROXIMITY IS NOT CAUSE, AND THE FIRST VERSION OF THIS COUNTED ONLY PROXIMITY. On a
                // real 1800-frame boot it reported "3 hitches, 3 near a GC = 100%" -- which reads as a smoking
                // gun and was nonsense: the TOTAL GC pause over the whole run was 9 ms against a 174 ms worst
                // frame. The collection was a bystander standing next to the crime.
                // ⭐ So blame is weighed: the pause has to explain a real SHARE of the frame before it counts.
                double nearPauseMs = newGc ? pauseMs : (_prevFrameHadGc ? _prevPauseMs : 0.0);
                double explains = frameMs > 0 ? nearPauseMs / frameMs : 0.0;
                if (explains >= BlameShare) _hitchesWithGc++;
                else if (nearPauseMs > 0) _hitchesNearButInnocent++;
                string who = nearPauseMs <= 0 ? "no GC nearby"
                           : $"gen{(newGc ? gen : _prevFrameGen)} pause {nearPauseMs:F1}ms "
                             + $"({(newGc ? "this" : "last")} frame) = {explains * 100:F0}% of it"
                             + (explains >= BlameShare ? "  <- GC" : "  <- NOT the cause");
                // ⭐⭐ THE OTHER SUSPECT, ON THE SAME LINE. Master named two causes -- fat GCs and prop
                // streaming -- and they produce an identical symptom while wanting opposite fixes. Printing
                // both next to the hitch means one log answers which it was, instead of two investigations.
                string coll = ColliderBudget.LastFlipShapes > 0
                    ? $" | collbudget flipped {ColliderBudget.LastFlipChunks} cells / " +
                      $"{ColliderBudget.LastFlipShapes} shapes in {ColliderBudget.LastRebalanceMs:F1}ms"
                    : "";
                GD.Print($"[gcwatch] HITCH {frameMs:F1}ms  alloc {allocThisFrame / 1024.0:F0} KB  " +
                         $"heap {info.HeapSizeBytes / 1048576.0:F0} MB  -> {who}{coll}");
                ColliderBudget.LastFlipShapes = 0;   // consumed: don't blame the next hitch on this flip
            }

            _prevFrameHadGc = newGc;
            _prevFrameGen = gen;
            _prevPauseMs = pauseMs;

            _sinceReport += delta;
            if (_sinceReport >= 10.0) { _sinceReport = 0; Report(); }
        }

        /// <summary>⭐ The line that actually answers the question: what SHARE of hitches had a collection next
        /// to them. A high share means chase allocation; a low one means the GC is a bystander and the cost is
        /// in the streaming itself -- and those are opposite week-long projects.</summary>
        public void Report()
        {
            double mb = _lastAlloc / 1048576.0;
            double share = _hitches > 0 ? 100.0 * _hitchesWithGc / _hitches : 0.0;
            GD.Print($"[gcwatch] {_frames} frames, worst {_worstMs:F0}ms | " +
                     $"hitches {_hitches} (GC explains {_hitchesWithGc} = {share:F0}%; " +
                     $"{_hitchesNearButInnocent} had a GC nearby that did NOT explain them) | " +
                     $"GCs {_gcs} (gen0 {_byGen[0]} gen1 {_byGen[1]} gen2 {_byGen[2]}) " +
                     $"worst pause {_worstGcPauseMs:F1}ms, total {_totalPauseMs:F0}ms | " +
                     $"allocated {mb:F0} MB | collbudget worst {ColliderBudget.WorstFlipShapes} shapes " +
                     $"in {ColliderBudget.WorstRebalanceMs:F1}ms");
        }

        public override void _ExitTree() { if (Enabled) Report(); }
    }
}
