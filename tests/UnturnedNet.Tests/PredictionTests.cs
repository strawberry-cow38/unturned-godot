using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Prediction v1 (MP_PLAN §4 Phase 4 / §2.5b): the client integrates its own input immediately through
    // THE SAME sim-core the server runs (PlayerReplication.IntegrateFlat), records per input seq, and each
    // snapshot's lastProcessedInputSeq + authoritative transform reconciles the residual -- smoothed below
    // the snap threshold, snapped above it. The convergence sims run a real server + predicted client over
    // MemTransport with link latency; mispredictions are injected client-side (the client predicts with
    // different axes than it sent) and must smooth out, never accumulate.
    [TestFixture]
    public class PredictionTests
    {
        const float Dt = 0.02f;

        // ---- reconciler unit behavior ----

        [Test]
        public void Reconciler_SmoothsBelowThreshold_DecaysToExactZero()
        {
            var r = new PredictionReconciler();
            r.Record(7, new Vector3(0f, 0f, 0f));
            Assert.That(r.OnAuthoritative(7, new Vector3(1f, 0f, 0f)), Is.False, "1 m error is below the 2 m snap threshold");
            Assert.That(r.Snaps, Is.EqualTo(0));

            float prev = 1f;
            for (int i = 0; i < 200 && r.PendingError != Vector3.zero; i++)
            {
                r.Step(Dt);
                float mag = r.PendingError.magnitude;
                Assert.That(mag, Is.LessThan(prev), "error decays monotonically");
                prev = mag;
            }
            Assert.That(r.PendingError, Is.EqualTo(Vector3.zero), "tiny tail is consumed whole -> exact zero");
        }

        [Test]
        public void Reconciler_SnapsAboveThreshold_AndIgnoresStaleAcks()
        {
            var r = new PredictionReconciler();
            r.Record(5, Vector3.zero);
            r.Record(6, Vector3.zero);
            Assert.That(r.OnAuthoritative(6, new Vector3(3f, 0f, 0f)), Is.True, "3 m error demands a snap");
            Assert.That(r.Snaps, Is.EqualTo(1));
            Assert.That(r.TakeAll().x, Is.EqualTo(3f).Within(1e-5f), "TakeAll hands the caller the whole error");
            Assert.That(r.PendingError, Is.EqualTo(Vector3.zero));

            Assert.That(r.OnAuthoritative(5, new Vector3(9f, 0f, 0f)), Is.False, "an OLDER ack (5 after 6) is stale -> ignored");
            Assert.That(r.PendingError, Is.EqualTo(Vector3.zero), "stale ack didn't poison the pending error");
            Assert.That(r.OnAuthoritative(0, new Vector3(9f, 0f, 0f)), Is.False, "seq 0 = server processed nothing yet");
        }

        [Test]
        public void Reconciler_DeadZone_NoCorrectionBelowTolerance_SnapStillArmed()
        {
            // the mp-rubberband "roped back" fix: real Unturned only corrects past errorToleranceDistance
            // (PlayerInput.cs:1817, 2 cm) -- below the dead-zone the shell keeps its own prediction, so
            // healthy per-tick float/contact residue between two physics bodies produces ZERO tug.
            var r = new PredictionReconciler();
            r.Record(3, Vector3.zero);
            Assert.That(r.OnAuthoritative(3, new Vector3(0.03f, 0f, 0f)), Is.False, "3 cm is under the dead-zone");
            Assert.That(r.PendingError, Is.EqualTo(Vector3.zero), "sub-tolerance error is held at ZERO, not smoothed");
            Assert.That(r.Step(Dt), Is.EqualTo(Vector3.zero), "so no correction is handed out");
            Assert.That(r.AcksApplied, Is.EqualTo(1), "the ack still counted (sample consumed, not ignored)");
            Assert.That(r.CorrectionAppliedMeters, Is.EqualTo(0f), "and the applied-correction meter stayed at zero");

            r.Record(4, Vector3.zero);
            Assert.That(r.OnAuthoritative(4, new Vector3(0.5f, 0f, 0f)), Is.False, "0.5 m: above the dead-zone, below the snap threshold");
            Assert.That(r.PendingError.magnitude, Is.EqualTo(0.5f).Within(1e-5f), "smoothed exactly as before");
            r.NoteCorrectionApplied(r.TakeAll());   // consume it so the next ack measures clean

            r.Record(5, Vector3.zero);
            Assert.That(r.OnAuthoritative(5, new Vector3(0f, 0f, 3f) + new Vector3(0.5f, 0f, 0f)), Is.True, "3 m still hard-snaps -- the dead-zone must not disarm the teleport/anti-cheat guard");
            Assert.That(r.Snaps, Is.EqualTo(1));
        }

        [Test]
        public void Reconciler_SliceCap_NoSingleTickTugAboveTheCeiling()
        {
            // the F3 companion (geometry WAN baselines): a swept correction blocked by geometry piles
            // pending up; the exponential slice of the piled error must land as a bounded glide when the
            // obstruction clears, never one released-dam tug (0.098 m single-tick measured at the curb
            // baseline pre-cap, vs the 0.08 felt bar)
            var r = new PredictionReconciler();
            r.Record(9, Vector3.zero);
            Assert.That(r.OnAuthoritative(9, new Vector3(1.5f, 0f, 0f)), Is.False, "1.5 m: eased, not snapped");
            float total = 0f;
            for (int i = 0; i < 400 && r.PendingError != Vector3.zero; i++)
            {
                var slice = r.Step(Dt);
                Assert.That(slice.magnitude, Is.LessThanOrEqualTo(r.MaxSliceMeters + 1e-5f),
                            $"tick {i}: no slice exceeds the per-tick ceiling");
                total += slice.magnitude;
            }
            Assert.That(r.PendingError, Is.EqualTo(Vector3.zero), "the capped glide still converges to exact zero");
            Assert.That(total, Is.EqualTo(1.5f).Within(1e-3f), "and hands out exactly the whole error");
        }

        // ---- C3 replay ring (PREDICTION_GEOMETRY_DIAGNOSIS §7.2 step 1): the per-seq ring carries the
        // FULL replay input -- axes/yaw/buttons pre-quantized through the exact wire encoding, plus the
        // post-move velocity/grounded state -- and CollectReplayWindow hands back the unacked tail a
        // rewind+replay must re-step. ----

        static void RecordReplay(PredictionReconciler r, ushort seq, float x = 0f, float y = 1f, float yaw = 0f, byte buttons = 0)
            => r.Record(seq, new Vector3(seq, 0f, 0f), x, y, yaw, buttons, new Vector3(0f, -1f, 7f), grounded: true);

        [Test]
        public void ReplayWindow_CollectsUnackedTailInSeqOrder_WireQuantized()
        {
            var r = new PredictionReconciler();
            for (ushort s = 10; s <= 15; s++)
                r.Record(s, new Vector3(s, 0f, 0f), moveX: 0.37f, moveY: 1f, yawDegrees: 123.456f,
                         buttons: (byte)(MoveInput.ButtonJump | MoveInput.PackStance(EPlayerStance.SPRINT)),
                         postMoveVel: new Vector3(0f, -0.2f, 7f), grounded: s % 2 == 0);
            Assert.That(r.LastRecordedSeq, Is.EqualTo(15));

            var into = new System.Collections.Generic.List<PredictionReconciler.ReplayInput>();
            Assert.That(r.CollectReplayWindow(12, into), Is.True);
            Assert.That(into.Count, Is.EqualTo(3), "the window is (acked, last] = 13,14,15");
            Assert.That(into[0].Seq, Is.EqualTo(13));
            Assert.That(into[2].Seq, Is.EqualTo(15));
            Assert.That(into[0].MoveX, Is.EqualTo(NetQuantization.QuantizeSignedNormalizedFloat(0.37f, 8)),
                        "axes are stored WIRE-quantized -- the replay integrates what the server integrates");
            Assert.That(into[0].YawDegrees, Is.EqualTo(NetQuantization.QuantizeDegrees(123.456f, NetQuantization.YawBits)));
            Assert.That(into[0].Jump, Is.True);
            Assert.That(into[0].Stance, Is.EqualTo(EPlayerStance.SPRINT), "stance decodes from the same buttons bits the wire carries");
            Assert.That(into[1].Grounded, Is.True, "post-move grounded rides the record (seq 14 was even)");
        }

        [Test]
        public void ReplayWindow_EmptyWhenEverythingAcked_TornWhenNewer()
        {
            var r = new PredictionReconciler();
            RecordReplay(r, 40);
            RecordReplay(r, 41);
            var into = new System.Collections.Generic.List<PredictionReconciler.ReplayInput>();
            Assert.That(r.CollectReplayWindow(41, into), Is.True, "from == last: everything acked");
            Assert.That(into, Is.Empty, "-> adopt-only, nothing to re-step");
            Assert.That(r.CollectReplayWindow(45, into), Is.False, "an ack NEWER than anything recorded is torn");
        }

        [Test]
        public void ReplayWindow_WrapWalksTheSenderConvention_SkippingSeqZero()
        {
            // NetWorldClient.SendMoveInput never emits seq 0 (the reconciler's "none" sentinel) -- the
            // window walk must skip it too or one wrap per 65535 inputs tears every replay
            var r = new PredictionReconciler();
            RecordReplay(r, 65534);
            RecordReplay(r, 65535);
            RecordReplay(r, 1);
            RecordReplay(r, 2);
            var into = new System.Collections.Generic.List<PredictionReconciler.ReplayInput>();
            Assert.That(r.CollectReplayWindow(65534, into), Is.True);
            Assert.That(into.Count, Is.EqualTo(3));
            Assert.That(into[0].Seq, Is.EqualTo(65535));
            Assert.That(into[1].Seq, Is.EqualTo(1), "seq 0 skipped exactly like the sender");
            Assert.That(into[2].Seq, Is.EqualTo(2));
        }

        [Test]
        public void ReplayWindow_HoleOrPlainRecord_Bails()
        {
            var r = new PredictionReconciler();
            RecordReplay(r, 20);
            RecordReplay(r, 21);
            r.Record(22, new Vector3(22f, 0f, 0f));   // the PLAIN overload -- no replay input stored
            RecordReplay(r, 23);
            var into = new System.Collections.Generic.List<PredictionReconciler.ReplayInput>();
            Assert.That(r.CollectReplayWindow(20, into), Is.False, "a non-replayable entry tears the window");
            Assert.That(into, Is.Empty, "torn window hands back nothing (no partial replays)");

            var r2 = new PredictionReconciler();
            RecordReplay(r2, 300);
            Assert.That(r2.CollectReplayWindow(1, into), Is.False, "a from-seq the ring no longer covers bails");
        }

        // ---- full-stack convergence sims (server + predicted client over MemTransport with latency) ----

        sealed class PredictedHarness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public readonly NetWorldClient Client;

            public PredictedHarness(int seed, int latencyTicks)
            {
                Net = new MemNetwork(seed);
                Net.ClientToServer = new FaultyLinkConfig { LatencyTicks = latencyTicks };
                Net.ServerToClient = new FaultyLinkConfig { LatencyTicks = latencyTicks };
                Server = new NetWorldServer(new MemServerTransport(Net));
                Client = new NetWorldClient(new MemClientTransport(Net), "predicted");
                Client.Connect();
                // pump (with idle inputs) until connected AND the reliable join snapshot seeded the spawn --
                // prediction starts from the adopted authoritative spawn, aligned with the server's stream
                for (int i = 0; i < 100 && !Client.Prediction.Spawned; i++) Step(0f, 0f, 0f);
                Assert.That(Client.State, Is.EqualTo(NetSessionState.Connected), $"client connected (seed={seed})");
                Assert.That(Client.Prediction.Spawned, Is.True, $"join snapshot adopted (seed={seed})");
            }

            /// <summary>One 50 Hz tick: send input, predict (optionally with DIFFERENT axes -- an injected
            /// misprediction), then advance transport + sessions + server sim + replication (§2.5 order).</summary>
            public void Step(float moveX, float moveY, float yaw, float? predictMoveY = null)
            {
                ushort seq = Client.SendMoveInput(moveX, moveY, yaw);
                Client.Prediction.PredictAndRecord(seq, moveX, predictMoveY ?? moveY, yaw, Dt);
                Net.Tick();
                Client.Tick();
                Server.TickSimulation();
                Server.TickReplication();
            }

            public Vector3 ServerPos()
            {
                Assert.That(Server.Players.TryGetByOwner(Client.PlayerId, out var e), Is.True);
                return e.Pos;
            }

            public float Error() => (ServerPos() - Client.Prediction.Pos).magnitude;
        }

        [Test]
        public void CleanPrediction_TracksServerExactly_WhileMoving()
        {
            var h = new PredictedHarness(seed: 31337, latencyTicks: 3);
            // walk a weave: same sim-core + same (wire-quantized) inputs => the predicted trajectory is
            // bit-identical to the authoritative one, so every ack measures EXACT zero error
            for (int t = 0; t < 200; t++) h.Step(0f, 1f, (t * 7) % 360);
            Assert.That(h.Client.Prediction.Reconciler.AcksApplied, Is.GreaterThan(50), "acks flowed");
            Assert.That(h.Client.Prediction.Reconciler.PendingError.magnitude, Is.EqualTo(0f),
                        "identical sim-core + identical inputs -> zero prediction error, even mid-motion");
            Assert.That(h.Client.Prediction.Reconciler.Snaps, Is.EqualTo(0));

            // stop; both fixed points must agree exactly (positions are wire-quantized at both ends)
            for (int t = 0; t < 30; t++) h.Step(0f, 0f, 0f);
            Assert.That(h.Error(), Is.LessThan(1e-4f), "predicted position == authoritative position at rest");
        }

        [Test]
        public void Misprediction_SmoothsOut_NoSnap_BelowThreshold()
        {
            var h = new PredictedHarness(seed: 555, latencyTicks: 3);
            for (int t = 0; t < 60; t++) h.Step(0f, 1f, 90f);

            // 10 ticks of misprediction (predicts standing still while actually walking = ~0.9 m of drift),
            // then correct prediction resumes; watch the residual the acks measure across the whole window
            float peak = 0f;
            for (int t = 0; t < 10; t++) { h.Step(0f, 1f, 90f, predictMoveY: 0f); peak = System.MathF.Max(peak, h.Client.Prediction.Reconciler.PendingError.magnitude); }
            for (int t = 0; t < 10; t++) { h.Step(0f, 1f, 90f); peak = System.MathF.Max(peak, h.Client.Prediction.Reconciler.PendingError.magnitude); }
            Assert.That(peak, Is.GreaterThan(0.2f), "misprediction produced a measurable residual");
            Assert.That(peak, Is.LessThan(2f), "but stayed under the snap threshold");
            Assert.That(h.Client.Prediction.Reconciler.Snaps, Is.EqualTo(0), "under the threshold nothing snaps");

            // the residual smooths out while STILL moving
            for (int t = 0; t < 60; t++) h.Step(0f, 1f, 90f);
            Assert.That(h.Client.Prediction.Reconciler.PendingError.magnitude, Is.LessThan(0.02f),
                        "error effectively gone while walking (smooth exponential correction)");
            Assert.That(h.Client.Prediction.Reconciler.Snaps, Is.EqualTo(0), "the whole correction was smooth");

            // convergence tolerance = the dead-zone: below DeadZoneMeters the reconciler deliberately
            // stops correcting (the shell keeps its own prediction), so the residual offset of a healed
            // misprediction parks anywhere inside the dead-zone instead of at exact zero.
            for (int t = 0; t < 40; t++) h.Step(0f, 0f, 90f);
            Assert.That(h.Error(), Is.LessThan(h.Client.Prediction.Reconciler.DeadZoneMeters + 1e-3f),
                        "predicted and authoritative positions converged to within the dead-zone");
        }

        [Test]
        public void LargeDivergence_Snaps_ThenConverges()
        {
            var h = new PredictedHarness(seed: 9001, latencyTicks: 3);
            for (int t = 0; t < 60; t++) h.Step(0f, 1f, 0f);

            // a 10 m client-side divergence in one tick (hiccup/teleport-sized -- way past the 2 m
            // threshold): the next acks must SNAP the prediction back, not glide it
            h.Client.Prediction.Pos += new Vector3(10f, 0f, 0f);
            for (int t = 0; t < 20; t++) h.Step(0f, 1f, 0f);
            Assert.That(h.Client.Prediction.Reconciler.Snaps, Is.GreaterThan(0), "beyond the threshold the client snaps instead of gliding");

            for (int t = 0; t < 60; t++) h.Step(0f, 0f, 0f);
            Assert.That(h.Error(), Is.LessThan(1e-3f), "post-snap the prediction re-converges to the authority");
        }
    }
}
