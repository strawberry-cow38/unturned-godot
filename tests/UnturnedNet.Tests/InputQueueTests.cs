using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The mp-inputbuffer fix (real Unturned's PlayerInput.serversidePackets model): the C2 avatar driver
    // consumes the client's MoveInput stream IN SEQ ORDER via TryConsumeInput -- one dequeue per tick
    // behind a shallow jitter buffer -- instead of re-reading the held latest. These are the engine-free
    // policy tests: in-order consumption (jitter compression never skips an input; a backlog is bounded
    // added latency, never a discard), starvation coast bounded by MaxCoastTicks then HOLD, coast-debt
    // repayment (a stall's delayed inputs are claimed ack-only, never integrated twice), seq-hole
    // substitution that CLAIMS the missing seq (so the published (pos, seq) ack pairs stay exact), the
    // enqueue-side depth cap -- the ONLY drop point -- and the held-keys view (TryGetHeldInput /
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
        public void Backlog_NeverSkipsAnInput_EveryQueuedTickIntegrated()
        {
            // THE catch-up regression (adversarial review, fix 1): a backlog used to drain by DISCARDING
            // one queued input per tick past a depth threshold -- but every queued input is a tick the
            // client already predicted, so each discard put the server one integration behind and the
            // deficit resolved as a correction the moment the player stopped (the sprint-stop yank at
            // smaller magnitude). Real Unturned (PlayerInput.cs:1723-1734) never skips: one dequeue per
            // qualifying tick, the buffer absorbs the burst as bounded added latency.
            PrimeWith(1, 2, 3);
            for (ushort s = 4; s <= 9; s++) _players.ServerQueueInput(Owner, In(s));   // burst to the cap (depth 8)
            var seqs = new System.Collections.Generic.List<ushort>();
            for (int tick = 0; tick < 8; tick++)
            {
                Assert.That(_players.TryConsumeInput(Owner, out var inp), Is.True);
                Assert.That(inp.MoveY, Is.EqualTo(In(inp.Seq).MoveY), "a backlogged input integrates real motion (no phantom ack-only consume without a starved coast)");
                seqs.Add(inp.Seq);
            }
            Assert.That(seqs, Is.EqualTo(new ushort[] { 2, 3, 4, 5, 6, 7, 8, 9 }),
                        "every queued input consumed, in order, exactly once -- none discarded to drain faster");
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
            Assert.That(seqs.Count, Is.EqualTo(PlayerReplication.MaxQueuedInputs),
                        "the enqueue cap bounds the drain: at most MaxQueuedInputs ticks of added latency, no consume-side skip");
        }

        [Test]
        public void StallBurst_DelayedInputsRepayCoastDebt_CountExact()
        {
            // FIX 1's invariant under the exact review scenario: a link stall starves the queue while the
            // client keeps sending, so the server coasts the held axes -- integrations of ticks the client
            // ALSO predicted (the inputs were delayed, not lost). When the stall releases and the backlog
            // arrives bunched (no seq hole), the coasted ticks' inputs must be claimed instantly ACK-ONLY:
            // never integrated a second time (double-count), never discarded (the old catch-up skip), and
            // never left as a standing backlog. The first post-burst consume therefore acks straight
            // through the repaid seqs to the first not-yet-integrated input.
            PrimeWith(1, 2, 3);
            _players.TryConsumeInput(Owner, out _);      // 2
            _players.TryConsumeInput(Owner, out _);      // 3 -> queue empty
            int motionTicks = 3;                          // seqs 1..3 integrated with motion
            for (int i = 0; i < 3; i++)                   // 3-tick stall: coast on the held axes
            {
                Assert.That(_players.TryConsumeInput(Owner, out var coast), Is.True);
                Assert.That(coast.Seq, Is.EqualTo(3), "coast repeats the stale seq (no fresh ack)");
                Assert.That(coast.MoveY, Is.EqualTo(1f), "coast integrates the held axes");
                motionTicks++;
            }
            for (ushort s = 4; s <= 8; s++) _players.ServerQueueInput(Owner, In(s));   // the stall releases: 5 bunched
            Assert.That(_players.TryConsumeInput(Owner, out var first), Is.True);
            Assert.That(first.Seq, Is.EqualTo(7),
                        "the 3 coasted ticks' delayed inputs (4,5,6) were claimed ack-only in one call; this tick integrates 7");
            Assert.That(first.MoveY, Is.EqualTo(1f));
            motionTicks++;
            Assert.That(_players.TryConsumeInput(Owner, out var second), Is.True);
            Assert.That(second.Seq, Is.EqualTo(8), "the stream continues in order past the repaid segment");
            Assert.That(second.MoveY, Is.EqualTo(1f));
            motionTicks++;
            Assert.That(motionTicks, Is.EqualTo(8), "integrated-motion ticks == the 8 ticks the client predicted (seqs 1..8), exactly once each");
            Assert.That(_players.TryConsumeInput(Owner, out var after), Is.True);
            Assert.That(after.Seq, Is.EqualTo(8), "the burst left NO standing backlog (repayment drained it) -- back to coasting on the latest");
        }

        [Test]
        public void StarvationPastCap_HoldsStill_UntilRealInputResumes()
        {
            // FIX 2: the starvation coast is bounded. Up to MaxCoastTicks of empty queue is a jitter gap
            // and coasts (held axes keep the avatar consistent with the client's own held keys); past it
            // this is an outage (heavy loss / stall / pre-disconnect) and the avatar must HOLD -- zero
            // motion, stance STAND, stale seq (no fresh ack) -- instead of ghost-sprinting stale intent
            // for as long as the outage lasts.
            PrimeWith(1, 2, 3);
            _players.TryConsumeInput(Owner, out _);      // 2
            _players.TryConsumeInput(Owner, out _);      // 3 -> queue empty
            for (int i = 0; i < PlayerReplication.MaxCoastTicks; i++)
            {
                Assert.That(_players.TryConsumeInput(Owner, out var coast), Is.True);
                Assert.That(coast.MoveY, Is.EqualTo(1f), $"coast tick {i + 1} keeps the held axes (short gaps stay smooth)");
            }
            for (int i = 0; i < 20; i++)
            {
                Assert.That(_players.TryConsumeInput(Owner, out var hold), Is.True, "the hold still returns true (yaw/gravity tick), just without motion");
                Assert.That(hold.MoveY, Is.EqualTo(0f), "past the cap the avatar stands -- no unbounded ghost-run on stale intent");
                Assert.That(hold.MoveX, Is.EqualTo(0f));
                Assert.That(hold.Buttons, Is.EqualTo(0), "no jump, stance STAND while holding");
                Assert.That(hold.Seq, Is.EqualTo(3), "hold repeats the stale seq -- never a fresh ack for a tick the client didn't predict");
            }
            // the stream resumes after the outage: a big seq jump adopts immediately and motion returns
            _players.ServerQueueInput(Owner, In(60));
            Assert.That(_players.TryConsumeInput(Owner, out var back), Is.True);
            Assert.That(back.Seq, Is.EqualTo(60), "the resumed stream is adopted at once");
            Assert.That(back.MoveY, Is.EqualTo(1f), "motion resumes with real input (the outage voided the coast debt)");
        }

        [Test]
        public void LossAfterStarvation_HolesRepayCoastDebt_NoDoubleIntegration()
        {
            // the starve-then-hole seam: the queue drains, the server coasts (integrating the held axes),
            // and the datagrams for those same ticks turn out LOST -- so when the stream resumes the seq
            // hole must be claimed against the coast debt WITHOUT a second integration. Pre-fix the hole
            // substitution integrated motion on top of the starved coasts: +1 integrated tick per loss
            // after a starve, the same count break as the discard, in the other direction.
            PrimeWith(1, 2, 3);
            _players.TryConsumeInput(Owner, out _);      // 2
            _players.TryConsumeInput(Owner, out _);      // 3 -> queue empty
            for (int i = 0; i < 2; i++) _players.TryConsumeInput(Owner, out _);   // 2 starved coasts; seqs 4,5 lost in flight
            _players.ServerQueueInput(Owner, In(6));
            Assert.That(_players.TryConsumeInput(Owner, out var inp), Is.True);
            Assert.That(inp.Seq, Is.EqualTo(6),
                        "the lost seqs 4,5 were claimed against the 2 coasted ticks (ack-only); this tick integrates 6");
            Assert.That(inp.MoveY, Is.EqualTo(1f));
            // total: seqs 1..3 real + 2 coasts standing in for 4,5 + seq 6 real = 6 motion ticks for the
            // 6 the client predicted; pre-fix this path integrated 8
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
        public void SeqAdvancedFlag_FreshConsumesTrue_StaleRepeatsFalse()
        {
            // C1.5 (the phantom-pairing fix, CLIENT_PREDICTION_PLAN §3 harness finding): the avatar
            // driver must know whether a consumed tick carries a FRESH seq (real dequeue, or a
            // hole-substitution that claimed the lost seq -- both produce an exact (pos, seq) pairing to
            // write back) or a stale REPEAT (starved coast / hold / prime-wait -- the body still
            // integrates, but publishing that advanced position under the already-acked seq is the
            // phantom correction the jittered 25 Hz snapshot stream shows the owner).
            PrimeWith(1, 2, 3);
            Assert.That(_players.TryConsumeInput(Owner, out var real, out bool adv), Is.True);
            Assert.That(real.Seq, Is.EqualTo(2));
            Assert.That(adv, Is.True, "a real dequeue advances the seq -- exact pairing, write back");
            _players.TryConsumeInput(Owner, out _, out _);   // 3 -> queue empty

            // starved coast: motion integrates (held axes) but the seq repeats -- NOT write-back-safe
            Assert.That(_players.TryConsumeInput(Owner, out var coast, out adv), Is.True);
            Assert.That(coast.Seq, Is.EqualTo(3));
            Assert.That(adv, Is.False, "a starved coast repeats the stale seq -- publishing it re-pairs an acked seq with newer motion");

            // hole substitution: the missing seq is CLAIMED, its pairing is exact -- write-back-safe.
            // The one coasted tick's debt repays seq 4; seq 5's hole substitutes a coast tick that
            // CLAIMS 5 (adv true -- fresh seq, exact pairing); then 6 consumes for real.
            _players.ServerQueueInput(Owner, In(6));   // 4 was coasted (debt repays it); 5 lost in flight
            Assert.That(_players.TryConsumeInput(Owner, out var sub, out adv), Is.True);
            Assert.That(sub.Seq, Is.EqualTo(5), "the hole's seq is claimed by the substituted coast tick");
            Assert.That(adv, Is.True, "the claimed hole seq is FRESH -- its (pos, seq) pairing is exact, write back");
            Assert.That(_players.TryConsumeInput(Owner, out var real6, out adv), Is.True);
            Assert.That(real6.Seq, Is.EqualTo(6));
            Assert.That(adv, Is.True, "a fresh-seq consume after the gap is exact again");

            // hold (past the coast cap): zero motion, stale seq -- NOT write-back-safe
            for (int i = 0; i < PlayerReplication.MaxCoastTicks; i++) _players.TryConsumeInput(Owner, out _, out _);
            Assert.That(_players.TryConsumeInput(Owner, out var hold, out adv), Is.True);
            Assert.That(hold.MoveY, Is.EqualTo(0f), "past the cap the avatar holds");
            Assert.That(adv, Is.False, "a hold tick repeats the stale seq too");
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
