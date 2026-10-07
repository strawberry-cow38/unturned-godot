using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>What the mover costs the server tick. Explicit: timings, not assertions -- run by name.</summary>
    [TestFixture, Explicit]
    public class ItemPipeBench
    {
        [SetUp] public void SetUp() => PipeFixtures.RegisterAssets();

        public static long Bytes, Gcs;
        static (double avgUs, double p99Us, double maxUs) Time(PipeRig r, int steps)
        {
            r.Run(50);   // warm the JIT
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();   // the rig's own setup garbage is not the mover's
            long b0 = GC.GetAllocatedBytesForCurrentThread(); int g0 = GC.CollectionCount(0);
            var t = new double[steps];
            var sw = new Stopwatch();
            for (int i = 0; i < steps; i++)
            {
                sw.Restart(); r.S.ItemMovers.Step(0.02f); sw.Stop();
                t[i] = sw.Elapsed.TotalMilliseconds * 1000.0;
            }
            Bytes = (GC.GetAllocatedBytesForCurrentThread() - b0) / steps; Gcs = GC.CollectionCount(0) - g0;
            Array.Sort(t);
            double sum = 0; foreach (var x in t) sum += x;
            return (sum / steps, t[(int)(steps * 0.99)], t[steps - 1]);
        }

        static void Clutter(PipeRig r, int n)
        {
            // a big base's worth of unrelated deployables: the mover scan and the power solve walk all of them
            for (int i = 0; i < n; i++) r.Place(PipeFixtures.GEN, new Vector3(-200f - (i % 40) * 3f, 0f, (i / 40) * 3f));
        }

        // crate -> adapter -> mover(32/s) -> splitter -> 3 adapters -> 3 crates, `lines` times
        static PipeRig Lines(int lines, int clutter, bool fullDest)
        {
            var r = new PipeRig();
            Clutter(r, clutter);
            for (int l = 0; l < lines; l++)
            {
                float z = (l / 10) * 40f, x0 = (l % 10) * 40f;
                var src = r.Crate(new Vector3(x0, 0f, z), 8, 6);
                r.Fill(src, PipeFixtures.NAILS, 48, 64);   // 3072 units: ~96 s of flow at 32/s
                uint mover = r.PoweredMover(new Vector3(x0 + 3f, 0f, z));
                uint split = r.Place(PipeFixtures.SPLITTER, new Vector3(x0 + 6f, 0f, z));
                r.Pipe(r.Adapter(src), 1, mover, 0);
                r.Pipe(mover, 1, split, 0);
                for (int i = 0; i < 3; i++)
                {
                    var c = r.Crate(new Vector3(x0 + 10f, 0f, z + i * 6f), 8, 6);
                    if (fullDest) { c.Storage.loadSize(1, 1); c.Storage.tryAddItem(new Item(PipeFixtures.SINGLE, 1)); }
                    r.Pipe(split, (byte)(1 + i), r.Adapter(c), 0);
                }
            }
            return r;
        }

        // one mover into k splitter->combiner diamonds in series, then one crate. Each diamond is 3 parallel pipes,
        // so the number of distinct PATHS to the crate is 3^k -- the routing walk is per path.
        static PipeRig Diamonds(int k)
        {
            var r = new PipeRig();
            var src = r.Crate(new Vector3(0f, 0f, 0f), 8, 6);
            r.Fill(src, PipeFixtures.NAILS, 48, 64);
            uint mover = r.PoweredMover(new Vector3(3f, 0f, 0f));
            r.Pipe(r.Adapter(src), 1, mover, 0);
            uint prev = mover; byte prevOut = 1;
            for (int d = 0; d < k; d++)
            {
                uint s = r.Place(PipeFixtures.SPLITTER, new Vector3(6f + d * 4f, 0f, 0f));
                uint c = r.Place(PipeFixtures.COMBINER, new Vector3(8f + d * 4f, 0f, 0f));
                r.Pipe(prev, prevOut, s, 0);
                for (byte i = 0; i < 3; i++) r.Pipe(s, (byte)(1 + i), c, i);
                prev = c; prevOut = 3;
            }
            var dst = r.Crate(new Vector3(6f + k * 4f + 4f, 0f, 0f), 8, 6);
            r.Pipe(prev, prevOut, r.Adapter(dst), 0);
            return r;
        }

        [Test]
        public void bench()
        {
            foreach (var (lines, clutter, full) in new[] { (1, 0, false), (20, 0, false), (20, 1000, false), (100, 1000, false), (100, 1000, true), (0, 1000, false) })
            {
                var r = lines == 0 ? new PipeRig() : Lines(lines, clutter, full);
                if (lines == 0) { Clutter(r, clutter); r.PoweredMover(new Vector3(0f, 0f, -50f)); }
                long before = r.S.ItemMovers.Diag.UnitsMoved;
                var (avg, p99, max) = Time(r, 500);
                long moved = r.S.ItemMovers.Diag.UnitsMoved - before;
                TestContext.Out.WriteLine($"lines={lines,3} clutter={clutter,4} destFull={full,-5}  avg {avg,8:F1} us  p99 {p99,8:F1} us  max {max,8:F1} us  moved {moved} units in 10 s  alloc {Bytes} B/step  gen0 {Gcs}  solves {r.S.ItemMovers.Diag.PowerSolves}");
            }
            foreach (int k in new[] { 1, 3, 5, 7 })
            {
                var r = Diamonds(k);
                long before = r.S.ItemMovers.Diag.UnitsMoved;
                var (avg, p99, max) = Time(r, 200);
                long moved = r.S.ItemMovers.Diag.UnitsMoved - before;
                TestContext.Out.WriteLine($"diamonds={k} (3^{k}={Math.Pow(3, k)} paths)  avg {avg,9:F1} us  p99 {p99,9:F1} us  max {max,9:F1} us  moved {moved} in 4 s");
            }
        }
    }
}
