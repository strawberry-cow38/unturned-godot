using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    [TestFixture]
    public class SimClockTests
    {
        [Test]
        public void ExactStep_OneFixedDelta_IsOneStep()
        {
            var c = new SimClock();
            Assert.That(c.Advance(SimClock.FixedDelta), Is.EqualTo(1));
            Assert.That(c.Tick, Is.EqualTo(1));
            Assert.That(c.Accumulator, Is.EqualTo(0.0).Within(1e-12));
        }

        [Test]
        public void TwoAndAHalf_Deltas_StepsTwice_KeepsRemainder()
        {
            var c = new SimClock();
            int steps = c.Advance(SimClock.FixedDelta * 2.5);
            Assert.That(steps, Is.EqualTo(2));
            Assert.That(c.Tick, Is.EqualTo(2));
            Assert.That(c.Accumulator, Is.EqualTo(SimClock.FixedDelta * 0.5).Within(1e-9));
        }

        [Test]
        public void SubStepDeltas_Accumulate_ThenFire()
        {
            var c = new SimClock();
            Assert.That(c.Advance(0.01), Is.EqualTo(0)); // below 0.02
            Assert.That(c.Advance(0.01), Is.EqualTo(1)); // now hits 0.02
            Assert.That(c.Tick, Is.EqualTo(1));
        }

        [Test]
        public void HugeDelta_IsClampedToMaxFrameDelta()
        {
            var c = new SimClock();
            int steps = c.Advance(10.0); // clamped to 0.33
            // floor(0.33 / 0.02) = 16
            Assert.That(steps, Is.EqualTo(16));
            Assert.That(c.SimTime, Is.EqualTo(16 * SimClock.FixedDelta).Within(1e-9));
        }

        [Test]
        public void Determinism_SameDeltaSequence_SameTicks()
        {
            var rng = new Random(1234);
            var seq = new List<double>();
            for (int i = 0; i < 5000; i++) seq.Add(rng.NextDouble() * 0.05);

            var a = new SimClock();
            var b = new SimClock();
            long stepsA = 0, stepsB = 0;
            foreach (var d in seq) stepsA += a.Advance(d);
            foreach (var d in seq) stepsB += b.Advance(d);

            Assert.That(a.Tick, Is.EqualTo(b.Tick));
            Assert.That(stepsA, Is.EqualTo(stepsB));
            Assert.That(a.Tick, Is.EqualTo(stepsA)); // every step increments tick exactly once
            Assert.That(a.Accumulator, Is.EqualTo(b.Accumulator).Within(0.0));
        }

        [Test]
        public void SimRoot_RunsConsecutiveTicks_InOrder()
        {
            var root = new SimRoot();
            var log = new List<long>();
            root.Add(new RecordingSystem(log, "A"));
            var order = new List<string>();
            root.Add(new OrderSystem(order, "X"));
            root.Add(new OrderSystem(order, "Y"));

            int steps = root.Frame(0.1); // 0.1 / 0.02 = 5
            Assert.That(steps, Is.EqualTo(5));
            Assert.That(log, Is.EqualTo(new long[] { 1, 2, 3, 4, 5 })); // ticks consecutive from 1
            Assert.That(order.Count, Is.EqualTo(10));                    // 5 steps * 2 systems
            Assert.That(order[0], Is.EqualTo("X"));                      // registration order preserved
            Assert.That(order[1], Is.EqualTo("Y"));
        }

        [Test]
        public void SimRoot_SecondFrame_ContinuesTickNumbering()
        {
            var root = new SimRoot();
            var log = new List<long>();
            root.Add(new RecordingSystem(log, "A"));
            root.Frame(0.04); // ticks 1,2
            log.Clear();
            root.Frame(0.04); // ticks 3,4
            Assert.That(log, Is.EqualTo(new long[] { 3, 4 }));
        }

        sealed class RecordingSystem : ISimStepped
        {
            readonly List<long> _log; public RecordingSystem(List<long> log, string _) { _log = log; }
            public void SimStep(long tick, double dt) => _log.Add(tick);
        }
        sealed class OrderSystem : ISimStepped
        {
            readonly List<string> _order; readonly string _name;
            public OrderSystem(List<string> order, string name) { _order = order; _name = name; }
            public void SimStep(long tick, double dt) => _order.Add(_name);
        }
    }
}
