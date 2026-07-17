using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The mp-inputbuffer fix (real Unturned's PlayerInput.serversidePackets model): the C2 avatar driver
    // consumes the client's MoveInput stream IN SEQ ORDER via TryConsumeInput -- one dequeue per tick
    // behind a shallow jitter buffer -- instead of re-reading the held latest. These are the engine-free
    // policy tests: in-order consumption (jitter compression never skips an input), coast on starvation,
    // seq-hole substitution that CLAIMS the missing seq (so the published (pos, seq) ack pairs stay
    // exact), the bounded catch-up drain, the depth cap, and the held-keys view (TryGetHeldInput /
    // ServerStep) staying latest-wins for the pre-C2 flat demo path.
    [TestFixture]
    public class InputQueueTests
    {
        PlayerReplication _players;
        const ushort Owner = 1;

        [SetUp]
        public void Spawn()
        {
            _players = new PlayerReplication();
            _players.ServerSpawn(new NetId(1), Owner, Vector3.zero, tick: 1);
        }

        static MoveInput In(ushort seq, float moveY = 1f) => new MoveInput { Seq = seq, MoveY = moveY, YawDegrees = 0f };

        // enqueue seqs, then consume until the buffer primes and returns the first real input
        void PrimeWith(params ushort[] seqs)
        {
            foreach (ushort s in seqs) _players.ServerQueueInput(Owner, In(s));
            for (int i = 0; i < PlayerReplication.PrimeDepth + 1; i++)
                if (_players.TryConsumeInput(Owner, out var inp) && inp.Seq == seqs[0]) return;
            Assert.Fail("queue never primed / first input never consumed");
        }

        [Test]
        public void NoInputEver_ReturnsFalse()
        {
            Assert.That(_players.TryConsumeInput(Owner, out _), Is.False);
        }

        [Test]
        public void IdealOnePerTick_ConsumesEverySeqInOrder()
        {
            // the equivalence that keeps every existing well-behaved test green: with one input arriving
            // per tick, dequeue-one == held-latest (each seq consumed exactly once, in order), the only
            // difference being the PrimeDepth ticks of initial buffer fill
            var consumed = new System.Collections.Generic.List<ushort>();
            ushort seq = 0;
            for (int tick = 0; tick < 30; tick++)
            {
                _players.ServerQueueInput(Owner, In(++seq));
                if (_players.TryConsumeInput(Owner, out var inp)) consumed.Add(inp.Seq);
            }
            Assert.That(consumed.Count, Is.EqualTo(30 - PlayerReplication.PrimeDepth));
            for (int i = 1; i < consumed.Count; i++)
                Assert.That(consumed[i], Is.EqualTo((ushort)(consumed[i - 1] + 1)), "every seq consumed exactly once, in order");
            Assert.That(consumed[0], Is.EqualTo(1));
        }

        [Test]
        public void Compression_TwoArrivalsInOneTick_BothConsumed()
        {
            // THE held-model bug: two inputs landing in one server-tick window integrated only the newest,
            // silently skipping a client-predicted tick of motion. The queue consumes both, in order.
            PrimeWith(1, 2, 3);
            _players.ServerQueueInput(Owner, In(4));
            _players.ServerQueueInput(Owner, In(5));   // jitter compression: both land the same tick
            Assert.That(_players.TryConsumeInput(Owner, out var a), Is.True);
            Assert.That(_players.TryConsumeInput(Owner, out var b), Is.True);
            Assert.That(_players.TryConsumeInput(Owner, out var c), Is.True);
            Assert.That(_players.TryConsumeInput(Owner, out var d), Is.True);
            Assert.That(new[] { a.Seq, b.Seq, c.Seq, d.Seq }, Is.EqualTo(new ushort[] { 2, 3, 4, 5 }),
                        "no input skipped under compression");
        }

        [Test]
        public void Starvation_CoastsOnLastConsumedInput()
        {
            PrimeWith(1, 2, 3);
            for (int i = 0; i < 2; i++) _players.TryConsumeInput(Owner, out _);   // drain to empty (2, 3)
            Assert.That(_players.TryConsumeInput(Owner, out var coast), Is.True, "starved queue coasts, never freezes");
            Assert.That(coast.Seq, Is.EqualTo(3), "coast repeats the last consumed seq (a stale ack for the client)");
            Assert.That(coast.MoveY, Is.EqualTo(In(3).MoveY), "coast keeps the held axes");
        }

        [Test]
        public void SeqHole_SubstitutesCoastTickAndClaimsTheMissingSeq()
        {
            // a dropped datagram's tick: coast in its place (count preserved) under the DROPPED seq --
            // claiming it keeps every published (pos, seq) pair exact; the client recorded a prediction
            // for that seq, and the sequenced channel can never deliver it late once 4 got through
            PrimeWith(1, 2, 3);
            _players.TryConsumeInput(Owner, out _);      // 2
            _players.TryConsumeInput(Owner, out _);      // 3
            _players.ServerQueueInput(Owner, In(5));     // 4 dropped in flight
            Assert.That(_players.TryConsumeInput(Owner, out var sub), Is.True);
            Assert.That(sub.Seq, Is.EqualTo(4), "the hole's seq is claimed by the substituted coast tick");
            Assert.That(sub.MoveY, Is.EqualTo(In(3).MoveY), "the substitute integrates the held axes");
            Assert.That(_players.TryConsumeInput(Owner, out var real), Is.True);
            Assert.That(real.Seq, Is.EqualTo(5), "the real input follows on the next tick");
        }

        [Test]
        public void BigJump_AdoptsImmediately_NoCoastCrawl()
        {
            // a hole bigger than MaxGapCoastTicks (hitch / cap drop) must not crawl through one coast per
            // missing seq -- adopt the fresh stream immediately and let the reconciler absorb the gap
            PrimeWith(1, 2, 3);
            _players.TryConsumeInput(Owner, out _);      // 2
            _players.TryConsumeInput(Owner, out _);      // 3
            _players.ServerQueueInput(Owner, In(20));
            Assert.That(_players.TryConsumeInput(Owner, out var inp), Is.True);
            Assert.That(inp.Seq, Is.EqualTo(20), "big jumps adopt the newest stream at once");
        }

        [Test]
        public void Backlog_DrainsBoundedTwoPerTick()
        {
            // backlog past CatchUpQueueDepth consumes two per tick (one skipped) -- bounded catch-up, so
            // a hitch's queue becomes neither permanent input lag nor an instant teleport
            PrimeWith(1, 2, 3);
            for (ushort s = 4; s <= 10; s++) _players.ServerQueueInput(Owner, In(s));   // depth 9 backlog
            var seqs = new System.Collections.Generic.List<ushort>();
            for (int tick = 0; tick < 8 && _players.TryConsumeInput(Owner, out var inp); tick++)
            {
                if (seqs.Count > 0 && inp.Seq == seqs[seqs.Count - 1]) break;   // started coasting = drained
                seqs.Add(inp.Seq);
            }
            // 9 queued inputs land in well under 9 ticks, each tick still advancing by at most 2 seqs
            Assert.That(seqs[seqs.Count - 1], Is.EqualTo(10), "the backlog drained to the newest input");
            Assert.That(seqs.Count, Is.LessThan(9), "faster than one-per-tick (catch-up engaged)");
            for (int i = 1; i < seqs.Count; i++)
                Assert.That(NetSeq.Diff(seqs[i], seqs[i - 1]), Is.InRange(1, 2), "at most one skip per tick");
        }

        [Test]
        public void DepthCap_DropsOldest_KeepsFreshest()
        {
            PrimeWith(1, 2, 3);
            ushort newest = (ushort)(4 + PlayerReplication.MaxQueuedInputs + 4);
            for (ushort s = 4; s <= newest; s++)
                _players.ServerQueueInput(Owner, In(s));   // one burst, well past the cap
            var seqs = new System.Collections.Generic.List<ushort>();
            for (int tick = 0; tick < 20; tick++)
            {
                Assert.That(_players.TryConsumeInput(Owner, out var inp), Is.True);
                if (seqs.Count > 0 && inp.Seq == seqs[seqs.Count - 1]) break;   // coasting = drained
                seqs.Add(inp.Seq);
            }
            Assert.That(seqs[seqs.Count - 1], Is.EqualTo(newest), "the freshest input survived the cap");
            Assert.That(seqs[0], Is.GreaterThanOrEqualTo((ushort)(newest - PlayerReplication.MaxQueuedInputs - 1)),
                        "the capped-off oldest inputs never surfaced (dropped, not crawled through)");
            Assert.That(seqs.Count, Is.LessThan(10), "the burst drained in bounded time (catch-up), not one-per-tick lag");
        }

        [Test]
        public void StaleReorderedSeq_RejectedAtEnqueue()
        {
            // the dedup/monotonic gate: a reordered stale packet (an older seq arriving after a newer one
            // already got through) must never enter the queue behind the newer one -- its tick is covered
            // by a substituted coast carrying the HELD axes instead
            PrimeWith(1, 2, 3);
            _players.ServerQueueInput(Owner, In(7));
            _players.ServerQueueInput(Owner, In(5, moveY: -1f));   // stale reorder: must be rejected
            var seqs = new System.Collections.Generic.List<MoveInput>();
            for (int tick = 0; tick < 10; tick++)
            {
                Assert.That(_players.TryConsumeInput(Owner, out var inp), Is.True);
                seqs.Add(inp);
                if (inp.Seq == 7) break;
            }
            Assert.That(seqs[seqs.Count - 1].Seq, Is.EqualTo(7), "the stream converges on the newest accepted seq");
            foreach (var inp in seqs)
                if (inp.Seq == 5)
                    Assert.That(inp.MoveY, Is.EqualTo(In(3).MoveY), "seq 5's tick is a held-axes substitute, never the stale packet");
        }

        [Test]
        public void ServerClearInput_DropsQueueAndCoastState()
        {
            PrimeWith(1, 2, 3);
            _players.ServerClearInput(Owner);   // death / vehicle-enter
            Assert.That(_players.TryConsumeInput(Owner, out _), Is.False, "no stale queued intent survives a clear");
            Assert.That(_players.TryGetHeldInput(Owner, out _), Is.False, "the held view clears too (existing semantics)");
            // the resumed stream re-primes and flows again
            PrimeWith(10, 11, 12);
            Assert.That(_players.TryConsumeInput(Owner, out var b), Is.True);
            Assert.That(b.Seq, Is.EqualTo(11));
        }

        [Test]
        public void HeldView_StaysLatestWins_ForTheFlatDemoPath()
        {
            // ServerStep's pre-C2 held-keys integration (loopback walkers, NetDemo) reads TryGetHeldInput
            // -- still the latest received, regardless of what the in-order queue has consumed
            _players.ServerQueueInput(Owner, In(1));
            _players.ServerQueueInput(Owner, In(2));
            _players.ServerQueueInput(Owner, In(3, moveY: -1f));
            Assert.That(_players.TryGetHeldInput(Owner, out var held), Is.True);
            Assert.That(held.Seq, Is.EqualTo(3), "held view = latest received (latest-wins), untouched by the queue");
            Assert.That(held.MoveY, Is.EqualTo(In(3, -1f).MoveY));
        }

        [Test]
        public void SparseSender_SingleInput_StillConsumedAfterPrimeWait()
        {
            // a harness that sends ONE input and relies on the held model must not stall behind the prime
            // fill: the prime-wait counter starts the stream after PrimeDepth ticks regardless of depth
            _players.ServerQueueInput(Owner, In(1));
            bool consumed = false;
            for (int tick = 0; tick <= PlayerReplication.PrimeDepth && !consumed; tick++)
                consumed = _players.TryConsumeInput(Owner, out var inp) && inp.Seq == 1;
            Assert.That(consumed, Is.True, "the lone input flows after the bounded prime wait");
            // and from then on it coasts (held-keys equivalence for sparse senders)
            Assert.That(_players.TryConsumeInput(Owner, out var coast), Is.True);
            Assert.That(coast.Seq, Is.EqualTo(1));
        }
    }
}
