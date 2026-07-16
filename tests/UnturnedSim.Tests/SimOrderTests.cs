using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // MP_PLAN §4 Phase 3 tick-order regression: SimRoot steps systems in REGISTRATION order, every fixed
    // tick, with consecutive tick numbers -- the property replication relies on when it registers LAST
    // (§2.5: "input-apply -> player sim -> ... -> replication send last"). If SimRoot ever reorders,
    // batches, or skips systems within a tick, snapshots would capture torn state; this locks it.
    [TestFixture]
    public class SimOrderTests
    {
        sealed class Recorder : ISimStepped
        {
            readonly string _name;
            readonly List<(string sys, long tick)> _log;
            public Recorder(string name, List<(string, long)> log) { _name = name; _log = log; }
            public void SimStep(long tick, double dt) => _log.Add((_name, tick));
        }

        [Test]
        public void SystemsStep_InRegistrationOrder_EveryTick_WithConsecutiveTicks()
        {
            var log = new List<(string sys, long tick)>();
            var root = new SimRoot();
            root.Add(new Recorder("input", log));
            root.Add(new Recorder("movement", log));
            root.Add(new Recorder("world", log));
            root.Add(new Recorder("replication", log));   // registered LAST, like the real net host

            // one engine frame worth 3 fixed steps + a fractional remainder frame worth 1 more
            int steps = root.Frame(0.06);
            steps += root.Frame(0.025);
            Assert.That(steps, Is.EqualTo(4), "0.06s + 0.025s at 50 Hz = 4 fixed steps (0.005 carries)");

            string[] order = { "input", "movement", "world", "replication" };
            Assert.That(log.Count, Is.EqualTo(4 * order.Length), "every system stepped exactly once per tick");
            for (int tick = 0; tick < 4; tick++)
                for (int s = 0; s < order.Length; s++)
                {
                    var entry = log[tick * order.Length + s];
                    Assert.That(entry.sys, Is.EqualTo(order[s]), $"tick {tick + 1}: system #{s} steps in registration order");
                    Assert.That(entry.tick, Is.EqualTo(tick + 1), $"tick numbers are consecutive (entry {tick * order.Length + s})");
                }
        }

        [Test]
        public void ReplicationRegisteredLast_SeesEveryEarlierSystemsWrites_ForTheSameTick()
        {
            // a "gameplay" system mutates state; the "replication" system snapshots it -- registered last,
            // each per-tick snapshot must already contain that same tick's mutation (never the previous
            // tick's = torn/stale snapshot, the exact §2.5 hazard)
            var root = new SimRoot();
            long stateChangedAtTick = -1;
            var snapshots = new List<(long tick, long observed)>();
            root.Add(new DelegateSimStep((tick, dt) => stateChangedAtTick = tick, "movement"));
            root.Add(new DelegateSimStep((tick, dt) => snapshots.Add((tick, stateChangedAtTick)), "replication"));

            root.Frame(0.1);   // 5 fixed steps in one engine frame
            Assert.That(snapshots.Count, Is.EqualTo(5));
            foreach (var (tick, observed) in snapshots)
                Assert.That(observed, Is.EqualTo(tick), $"snapshot at tick {tick} captured tick {observed}'s state -- replication must step after the sim it snapshots");
        }

        [Test]
        public void DelegateSimStep_CarriesNameAndForwards()
        {
            long seenTick = 0; double seenDt = 0;
            var step = new DelegateSimStep((tick, dt) => { seenTick = tick; seenDt = dt; }, "net.server.replicate");
            step.SimStep(42, 0.02);
            Assert.That(step.Name, Is.EqualTo("net.server.replicate"));
            Assert.That(seenTick, Is.EqualTo(42));
            Assert.That(seenDt, Is.EqualTo(0.02));
        }
    }
}
