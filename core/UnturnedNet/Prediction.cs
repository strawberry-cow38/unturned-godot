using System;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Prediction v1: predict + smooth-correct (MP_PLAN §2.5 fork b, the recommended v1 -- NO
    /// re-simulation/rollback; that stays a deferred client-only upgrade because the protocol already
    /// carries everything it needs). The client applies its own input immediately, records the predicted
    /// position per input seq, and when a snapshot acks a seq (lastProcessedInputSeq + authoritative
    /// transform, wired since Phase 3) compares authoritative vs recorded. The residual error is folded
    /// into the player a fraction per tick (exponential smoothing) -- unless it exceeds SnapThreshold,
    /// in which case the caller snaps.
    ///
    /// Convergence shape: predictions are recorded AFTER the tick's correction slice is applied, so each
    /// fresh authoritative sample measures the REMAINING error (replace semantics, no double-count) and
    /// the error decays geometrically even while the player keeps moving.
    /// </summary>
    public sealed class PredictionReconciler
    {
        /// <summary>Above this error (metres) smoothing would look like ice-skating -- snap instead.</summary>
        public float SnapThresholdMeters = 2f;
        /// <summary>Exponential correction rate (1/s): each tick folds in 1-exp(-rate*dt) of the error.
        /// 8/s halves a sub-threshold error roughly every 4.3 ticks (~90 ms) -- visible as a glide, not a pop.</summary>
        public float CorrectionRatePerSecond = 8f;
        /// <summary>Below this remaining error the whole tail is consumed in ONE slice. Positions live on
        /// the floor-quantized wire grid (1/256 m), which cannot express a sub-step nudge -- exponential
        /// slices smaller than a grid step would persist nothing and the error would limit-cycle forever.
        /// Right after an ack the pending error is an exact multiple of grid steps, so the single tail
        /// slice lands the prediction EXACTLY on the authoritative grid point. 5 cm in one tick is under
        /// the perception floor at 50 Hz.</summary>
        public float FinishThresholdMeters = 0.05f;

        const int RingSize = 256;   // > any plausible input round-trip in ticks
        struct Entry { public ushort Seq; public bool Used; public Vector3 Pos; public Vector3 CumCorrection; }
        readonly Entry[] _ring = new Entry[RingSize];

        Vector3 _pending;         // residual error still to fold in (authoritative - predicted, minus corrections already applied)
        Vector3 _cumCorrection;   // running total of every correction handed out (Step deltas + TakeAll)
        ushort _lastAckSeq;
        bool _hasAck;

        public Vector3 PendingError => _pending;
        public long Snaps { get; private set; }
        public long AcksApplied { get; private set; }

        /// <summary>Remember where the local sim put the player after processing input `seq` (call once
        /// per tick, after stepping AND after applying this tick's correction slice).</summary>
        public void Record(ushort seq, Vector3 predictedPos)
            => _ring[seq % RingSize] = new Entry { Seq = seq, Used = true, Pos = predictedPos, CumCorrection = _cumCorrection };

        /// <summary>Feed one authoritative sample (own entity's pos + lastProcessedInputSeq from an applied
        /// snapshot). Stale/duplicate acks are ignored. The measured error is reduced by every correction
        /// applied SINCE that prediction was recorded (acks lag by the RTT; without this the stale part of
        /// the error would be applied twice and the correction would overshoot). Returns true when the
        /// remaining error exceeds the snap threshold -- the caller should TakeAll() and teleport.</summary>
        public bool OnAuthoritative(ushort lastProcessedInputSeq, Vector3 authoritativePos)
        {
            if (lastProcessedInputSeq == 0) return false;                                  // server hasn't processed any input yet
            if (_hasAck && !NetSeq.IsNewer(lastProcessedInputSeq, _lastAckSeq)) return false; // stale or duplicate ack
            var rec = _ring[lastProcessedInputSeq % RingSize];
            if (!rec.Used || rec.Seq != lastProcessedInputSeq) return false;               // no prediction stored for that input
            _lastAckSeq = lastProcessedInputSeq;
            _hasAck = true;
            AcksApplied++;
            var appliedSince = _cumCorrection - rec.CumCorrection;                          // corrections newer than the ack's knowledge
            _pending = (authoritativePos - rec.Pos) - appliedSince;
            if (Magnitude(_pending) >= SnapThresholdMeters) { Snaps++; return true; }
            return false;
        }

        /// <summary>One 50 Hz tick of smoothing: returns the position delta to apply to the local player
        /// this tick (zero when converged). Tiny tails are consumed whole so the error reaches exact zero.
        /// The caller applies the delta (possibly quantized) and reports what actually landed via
        /// NoteCorrectionApplied -- counting the RAW delta would drift the accounting by whatever the
        /// position grid swallowed each tick.</summary>
        public Vector3 Step(float dt)
        {
            if (_pending == Vector3.zero) return Vector3.zero;
            if (Magnitude(_pending) <= FinishThresholdMeters)
            {
                var all = _pending;   // the grid-aligned tail, consumed whole (see FinishThresholdMeters)
                _pending = Vector3.zero;
                return all;
            }
            float a = 1f - MathF.Exp(-CorrectionRatePerSecond * dt);
            var delta = _pending * a;
            _pending -= delta;
            return delta;
        }

        /// <summary>Consume the whole pending error at once (the snap path). Like Step, the caller reports
        /// the actually-applied movement via NoteCorrectionApplied.</summary>
        public Vector3 TakeAll()
        {
            var p = _pending;
            _pending = Vector3.zero;
            return p;
        }

        /// <summary>Record how much correction ACTUALLY moved the predicted position this tick (after any
        /// quantization) -- the amount future acks subtract as already-applied.</summary>
        public void NoteCorrectionApplied(Vector3 actuallyApplied) => _cumCorrection += actuallyApplied;

        static float Magnitude(Vector3 v) => MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
    }

    /// <summary>
    /// The predicted local player for a networked client shell (MP_PLAN §4 Phase 4): integrates each sent
    /// MoveInput immediately through PlayerReplication.IntegrateFlat -- literally the same sim-core the
    /// server steps -- so under identical input streams the prediction is bit-identical to the
    /// authoritative result (inputs are pre-quantized through the exact wire encoding first). Reconcile()
    /// feeds authoritative samples back after each applied snapshot.
    /// Loopback/listen-server local players don't use this (their node IS the authority via ServerDrive);
    /// this is the remote-client path.
    /// </summary>
    public sealed class ClientPrediction
    {
        public readonly PlayerMovementSim Sim = new PlayerMovementSim();
        public readonly PredictionReconciler Reconciler = new PredictionReconciler();

        public Vector3 Pos;            // the predicted position (immediate, what the local camera follows)
        public float YawDegrees;
        public bool Spawned { get; private set; }   // false until the first authoritative sample seeds Pos

        /// <summary>One 50 Hz tick: quantize the input exactly like the wire does, integrate the same
        /// sim-core the server runs, fold in this tick's correction slice, and record the result under the
        /// input's seq. Call with the seq SendMoveInput returned. No-op until the join snapshot has seeded
        /// the spawn position -- predicting from an unadopted origin would only record garbage baselines.</summary>
        public void PredictAndRecord(ushort seq, float moveX, float moveY, float yawDegrees, float dt)
        {
            if (seq == 0 || !Spawned) return;   // nothing sent, or spawn not adopted yet
            var input = new MoveInput
            {
                Seq = seq,
                MoveX = NetQuantization.QuantizeSignedNormalizedFloat(Clamp1(moveX), 8),
                MoveY = NetQuantization.QuantizeSignedNormalizedFloat(Clamp1(moveY), 8),
                YawDegrees = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits),
            };
            Pos = PlayerReplication.IntegrateFlat(Sim, in input, Pos, dt);
            YawDegrees = input.YawDegrees;
            var slice = Reconciler.Step(dt);         // smooth-correct: a slice of any pending error
            if (slice != Vector3.zero)
            {
                // corrections land on the same wire-quantization grid the authority lives on, and the
                // reconciler is told what ACTUALLY persisted -- so error accounting is grid-exact and the
                // prediction converges to exact equality, not to within a quantization-residue offset
                var before = Pos;
                Pos = PlayerReplication.Quantize(Pos + slice);
                Reconciler.NoteCorrectionApplied(Pos - before);
            }
            Reconciler.Record(seq, Pos);             // record post-correction (replace-semantics contract)
        }

        /// <summary>Feed the own-entity authoritative state from the newest applied snapshot. Seeds the
        /// spawn position on first sight; snaps when the reconciler says the error is too big to glide.</summary>
        public void Reconcile(PlayerReplication players, ushort selfPlayerId)
        {
            if (!players.TryGetByOwner(selfPlayerId, out var e)) return;
            if (!Spawned)
            {
                Pos = e.Pos;             // adopt the server's spawn placement
                YawDegrees = e.YawDegrees;
                Spawned = true;
                return;
            }
            if (Reconciler.OnAuthoritative(e.LastProcessedInputSeq, e.Pos))
            {
                var before = Pos;   // beyond the threshold: snap, don't glide (grid-aligned, accounted)
                Pos = PlayerReplication.Quantize(Pos + Reconciler.TakeAll());
                Reconciler.NoteCorrectionApplied(Pos - before);
            }
        }

        static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    }
}
