using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // C2 (CLIENT_PREDICTION_PLAN §4.2): the server ack band -- retail's sub-2cm ack model
    // (U3 PlayerInput.cs:1820-1838, errorToleranceDistance = 0.02) adapted to the port's two-solve
    // reality. A claimed post-move position within AckBandMeters of the avatar's own result (and within
    // the anti-cheat adoption budget) is ADOPTED onto the entity: the healthy skew between two distinct
    // physics solves resolves SERVER-ward, invisibly, instead of as client-visible correction traffic.
    // Beyond band or budget the server position stands and the client corrects exactly as before.
    // These are the engine-free policy tests; the §3 WAN baselines prove it end-to-end in-engine.
    [TestFixture]
    public class AckBandTests
    {
        const float Dt = 0.02f;
        const ushort Owner = 1;
        PlayerReplication _players;

        [SetUp]
        public void Spawn()
        {
            _players = new PlayerReplication();
            _players.ServerSpawn(new NetId(1), Owner, Vector3.zero, tick: 1);
        }

        // One tick of the honest-client model both tests share: the client walks stepMeters, the server
        // avatar lands at (clientPos + solveSkew) -- the two-body residual -- and the server either
        // adopts the wire-quantized claim or writes its own result, exactly like PlayerNetSync.
        Vector3 ServerWriteBack(Vector3 claimRaw, Vector3 serverBodyPos, ushort seq, long tick, out bool adopted)
        {
            var claimOnWire = PlayerReplication.Quantize(claimRaw);   // the wire carries the grid point
            adopted = _players.ServerTryAdoptClaim(Owner, claimOnWire, serverBodyPos, Dt, out var adoptedPos);
            var final = adopted ? adoptedPos : serverBodyPos;
            _players.ServerDrive(Owner, final, 0f, seq, tick);
            _players.TryGetByOwner(Owner, out var e);
            return e.Pos;
        }

        [Test]
        public void AckBand_SubBandDrift_ResolvesServerWard_ZeroClientCorrection()
        {
            // FAILS pre-C2 (the second half below documents the pre-C2 behavior in-place): two healthy
            // bodies carry a bounded few-cm solve-skew that wanders (contacts, quantization, timing).
            // With entity-only adoption every sub-band ack round-trips as EXACTLY the client's recorded
            // prediction (grid point), so the owner applies ZERO correction, ever -- the skew stays
            // server-side, invisible, and the body is never steered.
            var rec = new PredictionReconciler();
            var clientPos = Vector3.zero;
            // the skew model: a sawtooth wandering 0 -> 6 cm over 30 ticks then relaxing -- above the
            // 0.04 client dead-zone (pre-C2 it visibly tugs) yet under the 0.08 band (post-C2 adopted)
            float Skew(int t) => 0.002f * (t % 30);
            int adoptions = 0;
            for (ushort seq = 1; seq <= 300; seq++)
            {
                var slice = rec.Step(Dt);                     // the client's correction slice (must stay zero)
                clientPos += slice;
                if (slice != Vector3.zero) rec.NoteCorrectionApplied(slice);
                clientPos += new Vector3(0.09f, 0f, 0f);      // one walk tick
                rec.Record(seq, clientPos);

                var serverBody = clientPos - new Vector3(Skew(seq), 0f, 0f);
                var ackPos = ServerWriteBack(clientPos, serverBody, seq, seq, out bool adoptedNow);
                if (adoptedNow) adoptions++;
                rec.OnAuthoritative(seq, ackPos);
            }
            Assert.That(adoptions, Is.EqualTo(300), "every sub-band claim published (the wandering skew never duty-cycles the growth budget)");
            Assert.That(rec.CorrectionAppliedMeters, Is.EqualTo(0f),
                        "the owner applied ZERO correction across 6 s of skewed walk -- the skew resolved server-ward");
            Assert.That(rec.PendingError, Is.EqualTo(Vector3.zero));

            // and the PRE-C2 behavior, same skew, no adoption: everything past the dead-zone resolves as
            // client-visible correction (the port had no server-ward path at all -- plan §4.1 H2)
            var recOld = new PredictionReconciler();
            var oldClient = Vector3.zero;
            for (ushort seq = 1; seq <= 300; seq++)
            {
                var slice = recOld.Step(Dt);
                oldClient += slice;
                if (slice != Vector3.zero) recOld.NoteCorrectionApplied(slice);
                oldClient += new Vector3(0.09f, 0f, 0f);
                recOld.Record(seq, oldClient);
                _players.ServerDrive(Owner, oldClient - new Vector3(Skew(seq), 0f, 0f), 0f, seq, seq + 1000);
                _players.TryGetByOwner(Owner, out var e);
                recOld.OnAuthoritative(seq, e.Pos);
            }
            Assert.That(recOld.CorrectionAppliedMeters, Is.GreaterThan(0.2f),
                        "without adoption the same skew rope-tugs the owner (the pre-C2 drizzle this fix removes)");
        }

        [Test]
        public void AckBand_BudgetExceeded_FallsBackToClientCorrection()
        {
            // THE anti-cheat test. Adoption is entity-only, so a claim can never move server physics --
            // the whole cheat surface is the PUBLISHED position's skew off the true body, and it is
            // doubly bounded: the band caps how big the standing lie can ever be (AckBandMeters), the
            // growth budget caps how fast it can ramp (AdoptBudgetMetersPerSecond). A client claiming
            // +1 m/s of free motion outruns the growth budget on tick one -- nothing is ever adopted,
            // the body's truth is published, and the liar's claims blow past the band within 4 ticks.
            var serverTruth = Vector3.zero;
            float lie = 0f;
            int adoptedTicks = 0;
            float worstFastLie = 0f;
            for (int t = 1; t <= 100; t++)
            {
                serverTruth += new Vector3(0.09f, 0f, 0f);    // the avatar integrates the honest inputs
                lie += 0.02f;                                  // +1 m/s of claimed free motion
                var claim = serverTruth + new Vector3(lie, 0f, 0f);
                var ackPos = ServerWriteBack(claim, serverTruth, (ushort)t, t, out bool adopted);
                if (adopted) adoptedTicks++;
                worstFastLie = System.MathF.Max(worstFastLie, (ackPos - PlayerReplication.Quantize(serverTruth)).magnitude);
            }
            Assert.That(adoptedTicks, Is.LessThanOrEqualTo(3),
                        $"a +1 m/s liar outruns the growth budget within a tick or two ({adoptedTicks} adopted) -- then the body's truth is published");
            Assert.That(worstFastLie, Is.LessThan(0.06f),
                        $"the fast lie never published more than a few cm of standing skew ({worstFastLie:0.###} m)");
            _players.TryGetByOwner(Owner, out var e);
            Assert.That((e.Pos - PlayerReplication.Quantize(serverTruth)).magnitude, Is.LessThan(0.001f),
                        "the published position ends on the server's own result -- the client would be corrected, as today");

            // the patient liar: ramping the skew at exactly the budget rate (0.5 m/s) gets adopted only
            // until the STANDING lie hits the band -- ~0.08 m once, never again, and never any faster
            var truth2 = e.Pos;
            float lie2 = 0f, worstLie = 0f;
            int rampAdoptions = 0;
            for (int t = 1; t <= 200; t++)
            {
                truth2 += new Vector3(0.09f, 0f, 0f);         // honest motion continues
                lie2 += PlayerReplication.AdoptBudgetMetersPerSecond * Dt;
                var claim = truth2 + new Vector3(lie2, 0f, 0f);
                var ackPos = ServerWriteBack(claim, truth2, (ushort)(1000 + t), 1000 + t, out bool adopted);
                if (adopted) rampAdoptions++;
                worstLie = System.MathF.Max(worstLie, (ackPos - PlayerReplication.Quantize(truth2)).magnitude);
            }
            Assert.That(rampAdoptions, Is.GreaterThan(0), "sub-band, budget-rate growth IS adopted (legitimate drift must flow)");
            Assert.That(rampAdoptions, Is.LessThanOrEqualTo((int)(PlayerReplication.AckBandMeters / (PlayerReplication.AdoptBudgetMetersPerSecond * Dt)) + 1),
                        "the ramp dies when the standing lie hits the band (~8 ticks at the budget rate)");
            Assert.That(worstLie, Is.LessThanOrEqualTo(PlayerReplication.AckBandMeters + 0.005f),
                        $"the published lie never exceeded the band ({worstLie:0.###} m) -- the standing-skew ceiling");
        }

        [Test]
        public void AckBand_BeyondBand_ServerWins_AndHysteresisGatesReentry()
        {
            // real divergence (a shove, a collision only the server saw) is NEVER adopted, whatever the
            // budget: beyond AckBandMeters the body's truth is published and the reconciler path
            // corrects the client -- the pre-C2 behavior. AND the episode DISENGAGES adoption until the
            // skew has converged under AdoptReentryMeters: re-engaging anywhere higher would flip the
            // ack frame by up to the band mid-correction and undo it -- the limit cycle the §3 sprint
            // baseline caught at 19 m/min when adoption re-entered at the band edge.
            var serverBody = new Vector3(5f, 0f, 5f);
            // steady engaged tracking (dist ~0) banks the budget cap and keeps adoption engaged
            for (int t = 0; t < 100; t++)
                Assert.That(_players.ServerTryAdoptClaim(Owner, serverBody, serverBody, Dt, out _), Is.True);
            // while ENGAGED, a claim just inside the band is adopted (the full band is usable)
            var inBand = serverBody + new Vector3(PlayerReplication.AckBandMeters - 0.01f, 0f, 0f);
            Assert.That(_players.ServerTryAdoptClaim(Owner, inBand, serverBody, Dt, out var adopted), Is.True,
                        "engaged: a claim just inside the band (with budget) is adopted");
            Assert.That(adopted, Is.EqualTo(PlayerReplication.Quantize(inBand)), "adoption hands back the exact grid point");

            // the over-band episode: refused AND disengaged
            var over = serverBody + new Vector3(PlayerReplication.AckBandMeters + 0.01f, 0f, 0f);
            Assert.That(_players.ServerTryAdoptClaim(Owner, over, serverBody, Dt, out _), Is.False,
                        "a claim past the band is refused even with a full budget");
            // in-band but NOT yet converged: still refused (the hysteresis) -- the client is mid-correction
            Assert.That(_players.ServerTryAdoptClaim(Owner, inBand, serverBody, Dt, out _), Is.False,
                        "after an episode, an in-band-but-unconverged claim stays on the body's frame");
            var nearlyConverged = serverBody + new Vector3(PlayerReplication.AdoptReentryMeters + 0.01f, 0f, 0f);
            Assert.That(_players.ServerTryAdoptClaim(Owner, nearlyConverged, serverBody, Dt, out _), Is.False,
                        "still above the re-entry threshold: not yet");
            // converged under the re-entry threshold (inside the client dead-zone): re-engages -- and the
            // frame flip this allows is under the dead-zone, invisible to the owner
            var converged = serverBody + new Vector3(PlayerReplication.AdoptReentryMeters - 0.01f, 0f, 0f);
            Assert.That(_players.ServerTryAdoptClaim(Owner, converged, serverBody, Dt, out _), Is.True,
                        "converged under AdoptReentryMeters: adoption re-engages");
            // and once re-engaged the full band is usable again
            Assert.That(_players.ServerTryAdoptClaim(Owner, inBand, serverBody, Dt, out _), Is.True,
                        "re-engaged: the full band is usable again (growth paid from the budget)");
        }
    }
}
