using System;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// The client-side reconciliation ledger: the per-seq prediction ring (now the full C3 replay
    /// record), the ack cursor, the dead zone, the snap threshold, and the correction accounting.
    /// The SHELL client (ClientWorldSession) resolves errors retail-style since C3
    /// (PREDICTION_GEOMETRY_DIAGNOSIS §7): below DeadZone -- ack, no correction; above SnapThreshold --
    /// hard adopt; the middle band -- rewind+replay driven by the server's MispredictionEvent (the
    /// eased glide is GONE there; retail has no easing anywhere, U3 PlayerInput.cs). The exponential
    /// Step() glide survives only for the headless flat demo walker (ClientPrediction), which has no
    /// body to replay.
    ///
    /// Convergence shape: predictions are recorded AFTER the tick's correction/replay is applied, so
    /// each fresh authoritative sample measures the REMAINING error (replace semantics, no
    /// double-count); a replay re-records its whole window, which is what drives the next ack to ~zero.
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
        /// <summary>Errors at or below this (metres) are NOT corrected at all: the pending error is held
        /// at zero and the shell keeps its own prediction. Two separate physics bodies produce small
        /// per-tick float/contact residuals even when healthy; correcting every non-zero sample every
        /// tick reads as a constant "roped back" tug. Real Unturned corrects only past
        /// errorToleranceDistance = 0.02 m (PlayerInput.cs:1817) -- ours is a little wider because the
        /// two ends are distinct physics solves, not a bit-identical re-sim. Client-side only, no wire
        /// change; the SnapThreshold teleport guard above is unaffected. C3 note: post-replay residue
        /// must land inside this zone for adoption to re-engage -- the dead zone is what absorbs the
        /// two-solve noise a replay cannot erase (diagnosis §7.2 item 4).</summary>
        public float DeadZoneMeters = 0.04f;

        const int RingSize = 256;   // > any plausible input round-trip in ticks
        struct Entry
        {
            public ushort Seq; public bool Used; public Vector3 Pos; public Vector3 CumCorrection;
            // C3 replay record (diagnosis §7.2 step 1): the FULL input the shell sim consumed for this
            // seq -- pre-quantized through the exact wire encoding, so a replay integrates the same
            // values the server avatar does -- plus the shell's post-move state (velocity + det-grounded)
            // for diagnostics/future local rewinds. HasReplay = recorded through the replay overload.
            public float MoveX, MoveY, YawDegrees; public byte Buttons;
            public Vector3 PostVel; public bool Grounded; public bool HasReplay;
        }
        readonly Entry[] _ring = new Entry[RingSize];

        Vector3 _pending;         // residual error still to fold in (authoritative - predicted, minus corrections already applied)
        Vector3 _cumCorrection;   // running total of every correction handed out (Step deltas + TakeAll)
        ushort _lastAckSeq;
        bool _hasAck;

        public Vector3 PendingError => _pending;
        public long Snaps { get; private set; }
        public long AcksApplied { get; private set; }
        /// <summary>Total metres of correction actually applied (sum of |NoteCorrectionApplied|) -- the
        /// "how hard is the rope pulling" observability the dead-zone tests assert on.</summary>
        public float CorrectionAppliedMeters { get; private set; }

        /// <summary>Remember where the local sim put the player after processing input `seq` (call once
        /// per tick, after stepping AND after applying this tick's correction slice).</summary>
        public void Record(ushort seq, Vector3 predictedPos)
        {
            _ring[seq % RingSize] = new Entry { Seq = seq, Used = true, Pos = predictedPos, CumCorrection = _cumCorrection };
            LastRecordedSeq = seq;
        }

        /// <summary>C3 (diagnosis §7.2): the replay-record overload -- position plus the full input this
        /// seq's tick consumed (axes/yaw/buttons, quantized here through the exact wire encoding so a
        /// replay steps the same values the server integrates) and the post-move velocity/grounded state.
        /// The shell session records through THIS; the headless demo walker keeps the plain overload.</summary>
        public void Record(ushort seq, Vector3 predictedPos, float moveX, float moveY, float yawDegrees,
                           byte buttons, Vector3 postMoveVel, bool grounded)
        {
            _ring[seq % RingSize] = new Entry
            {
                Seq = seq, Used = true, Pos = predictedPos, CumCorrection = _cumCorrection,
                MoveX = NetQuantization.QuantizeSignedNormalizedFloat(Clamp1(moveX), 8),
                MoveY = NetQuantization.QuantizeSignedNormalizedFloat(Clamp1(moveY), 8),
                YawDegrees = NetQuantization.QuantizeDegrees(yawDegrees, NetQuantization.YawBits),
                Buttons = buttons, PostVel = postMoveVel, Grounded = grounded, HasReplay = true,
            };
            LastRecordedSeq = seq;
        }

        /// <summary>The newest recorded input seq (0 = none yet) -- the replay window's upper bound.</summary>
        public ushort LastRecordedSeq { get; private set; }

        /// <summary>One replayable input pulled back out of the ring (C3): what the shell must re-step,
        /// exactly as the wire carried it.</summary>
        public struct ReplayInput
        {
            public ushort Seq;
            public float MoveX, MoveY, YawDegrees;
            public byte Buttons;
            public Vector3 PostVel;   // the shell's ORIGINAL post-move sim velocity for this seq (diagnostic)
            public bool Grounded;
            public bool Jump => (Buttons & MoveInput.ButtonJump) != 0;
            public EPlayerStance Stance => new MoveInput { Buttons = Buttons }.Stance;
        }

        /// <summary>Collect the unacked replay window (fromSeqExclusive, LastRecordedSeq] in seq order --
        /// the inputs a C3 replay must re-step after teleporting to the server's state for
        /// fromSeqExclusive. False (and an empty list) when the window is torn: an entry evicted from the
        /// ring, recorded without the replay overload, longer than maxCount, or a from-seq newer than
        /// anything recorded -- the caller skips the replay and lets the snap/next-event path recover.
        /// Empty-and-true when from == LastRecordedSeq (everything acked: adopt-only). Walks the sender's
        /// seq convention (seq 0 is skipped on wrap, NetWorldClient.SendMoveInput).</summary>
        public bool CollectReplayWindow(ushort fromSeqExclusive, System.Collections.Generic.List<ReplayInput> into, int maxCount = 64)
        {
            into.Clear();
            if (LastRecordedSeq == 0) return false;
            if (fromSeqExclusive == LastRecordedSeq) return true;
            if (NetSeq.IsNewer(fromSeqExclusive, LastRecordedSeq)) return false;
            ushort seq = fromSeqExclusive;
            while (seq != LastRecordedSeq)
            {
                seq++;
                if (seq == 0) seq++;   // the sender never emits seq 0 (the reconciler's "none" sentinel)
                var rec = _ring[seq % RingSize];
                if (!rec.Used || rec.Seq != seq || !rec.HasReplay || into.Count >= maxCount) { into.Clear(); return false; }
                into.Add(new ReplayInput
                {
                    Seq = seq, MoveX = rec.MoveX, MoveY = rec.MoveY, YawDegrees = rec.YawDegrees,
                    Buttons = rec.Buttons, PostVel = rec.PostVel, Grounded = rec.Grounded,
                });
            }
            return true;
        }

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
            if (Magnitude(_pending) <= DeadZoneMeters) _pending = Vector3.zero;   // dead-zone: under the tolerance the shell keeps its own prediction -- zero correction, zero tug
            return false;
        }

        /// <summary>One 50 Hz tick of smoothing: returns the position delta to apply to the local player
        /// this tick (zero when converged). Tiny tails are consumed whole so the error reaches exact zero.
        /// The caller applies the delta (possibly quantized) and reports what actually landed via
        /// NoteCorrectionApplied -- counting the RAW delta would drift the accounting by whatever the
        /// position grid swallowed each tick.
        /// C3 NOTE: the SHELL client no longer calls this -- retail has no easing anywhere
        /// (U3 PlayerInput.cs: corrections are rare, discrete, replay-complete), and the port's shell
        /// resolves the middle band by rewind+replay (ClientWorldSession.ReplayMisprediction). Step()
        /// survives ONLY for the headless flat demo walker (ClientPrediction below), which has no body
        /// to replay. Kept-with-doubt: delete it if the demo walker ever grows a replay path.</summary>
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
        public void NoteCorrectionApplied(Vector3 actuallyApplied)
        {
            _cumCorrection += actuallyApplied;
            CorrectionAppliedMeters += Magnitude(actuallyApplied);
        }

        static float Magnitude(Vector3 v) => MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        static float Clamp1(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
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
