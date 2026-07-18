using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // C1 (CLIENT_PREDICTION_PLAN §4.2): the MoveInput datagram carries the newest input plus the two
    // previous ones, so a single lost (or jitter-overtaken -- the UnreliableSequenced channel drops
    // overtaken datagrams stale) MoveInput datagram no longer punches a hole in the server's input
    // queue. Pre-C1 a hole made the server GUESS: TryConsumeInput substituted a coast tick carrying the
    // PREVIOUS input's axes/yaw for the missing seq -- wrong motion whenever the player's input changed
    // during the gap, surfacing as a correction (the residual high-RTT inchworm, plan §4.1 H1). Retail
    // never guesses: inputs ride the reliable channel (U3 PlayerInput.cs:1713); redundancy is the
    // port's loss-tolerant equivalent.
    [TestFixture]
    public class MoveInputRedundancyTests
    {
        // A real client + server over MemTransport (the PredictionTests harness shape), with the test
        // playing the avatar driver's role: one TryConsumeInput per tick, like PlayerNetSync.
        sealed class Rig
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public readonly NetWorldClient Client;

            public Rig(int seed)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net));
                Client = new NetWorldClient(new MemClientTransport(Net), "redundant");
                Client.Connect();
                // the entity spawns on the server's PeerConnected (pump 1) but the client's Accept is
                // still in flight then -- wait for BOTH ends of the handshake
                for (int i = 0; i < 200 && (Client.State != NetSessionState.Connected || Server.Players.Count == 0); i++) Pump();
                Assert.That(Client.State, Is.EqualTo(NetSessionState.Connected), $"client connected (seed={seed})");
                Assert.That(Server.Players.Count, Is.EqualTo(1), $"player entity spawned (seed={seed})");
            }

            public void Pump()
            {
                Net.Tick();
                Client.Tick();
                Server.TickSimulation();
                Server.TickReplication();
            }
        }

        // every seq carries a UNIQUE wire-exact yaw, so a hole-substituted or coasted tick is
        // detectable: it would pair the claimed seq with its PREDECESSOR's yaw
        static float YawFor(int seq) => NetQuantization.QuantizeDegrees((seq * 7) % 360, NetQuantization.YawBits);

        [Test]
        public void MoveInputRedundancy_SingleLoss_NoHole_NoCoast()
        {
            // FAILS pre-C1: with one input per datagram, the one dropped datagram's seq becomes a queue
            // hole; the server substitutes a coast tick that CLAIMS the seq but integrates the previous
            // input's yaw/axes -- the (seq -> yaw) pairing below breaks exactly at the hole. Post-C1 the
            // next datagram backfills the real input and every consumed seq carries its own yaw.
            var rig = new Rig(seed: 424242);
            ushort owner = rig.Client.PlayerId;

            var consumed = new List<MoveInput>();
            ushort lastConsumedSeq = 0;
            int lostAt = 30;                          // exactly ONE datagram eaten mid-stream
            const int SendTicks = 60;
            ushort lastSent = 0;
            for (int t = 0; t < SendTicks; t++)
            {
                rig.Net.ClientToServer.LossProbability = (t == lostAt) ? 1.0 : 0.0;
                int expectedSeq = t + 1;              // _inputSeq starts at 1 and increments per send
                lastSent = rig.Client.SendMoveInput(0f, 1f, YawFor(expectedSeq));
                Assert.That(lastSent, Is.EqualTo((ushort)expectedSeq), "seq stream is deterministic");
                rig.Pump();
                if (rig.Server.Players.TryConsumeInput(owner, out var inp) && NetSeq.IsNewer(inp.Seq, lastConsumedSeq))
                {
                    consumed.Add(inp);
                    lastConsumedSeq = inp.Seq;
                }
            }
            rig.Net.ClientToServer.LossProbability = 0.0;
            for (int t = 0; t < 20; t++)              // drain the jitter buffer's tail
            {
                rig.Pump();
                if (rig.Server.Players.TryConsumeInput(owner, out var inp) && NetSeq.IsNewer(inp.Seq, lastConsumedSeq))
                {
                    consumed.Add(inp);
                    lastConsumedSeq = inp.Seq;
                }
            }

            Assert.That(consumed.Count, Is.GreaterThan(0), "inputs flowed");
            Assert.That(lastConsumedSeq, Is.EqualTo(lastSent), "the stream drained to the newest sent input");
            for (int i = 1; i < consumed.Count; i++)
                Assert.That(consumed[i].Seq, Is.EqualTo((ushort)(consumed[i - 1].Seq + 1)),
                            "every seq consumed exactly once, in order -- the single loss left NO hole");
            foreach (var inp in consumed)
                Assert.That(inp.YawDegrees, Is.EqualTo(YawFor(inp.Seq)),
                            $"seq {inp.Seq} integrated ITS OWN input -- never a substituted coast on the predecessor's (the pre-C1 hole guess)");
        }

        [Test]
        public void MoveInputRedundancy_BackfillIsIdempotent_NoLoss_NoDoubleConsume()
        {
            // the redundant entries re-deliver every input up to 3 times on a CLEAN link; the
            // strictly-increasing-seq enqueue guard must drop the repeats so nothing is integrated twice
            var rig = new Rig(seed: 515151);
            ushort owner = rig.Client.PlayerId;

            var consumed = new List<MoveInput>();
            ushort lastConsumedSeq = 0;
            for (int t = 0; t < 40; t++)
            {
                rig.Client.SendMoveInput(0f, 1f, YawFor(t + 1));
                rig.Pump();
                if (rig.Server.Players.TryConsumeInput(owner, out var inp) && NetSeq.IsNewer(inp.Seq, lastConsumedSeq))
                {
                    consumed.Add(inp);
                    lastConsumedSeq = inp.Seq;
                }
            }
            for (int i = 1; i < consumed.Count; i++)
                Assert.That(consumed[i].Seq, Is.EqualTo((ushort)(consumed[i - 1].Seq + 1)),
                            "clean link: strictly consecutive consumes -- redundancy added no duplicates");
            foreach (var inp in consumed)
                Assert.That(inp.YawDegrees, Is.EqualTo(YawFor(inp.Seq)), "every consumed seq carries its own input");
        }

        [Test]
        public void MoveInputRedundancy_SendPause_VoidsTheRing()
        {
            // a pause in the send stream (ride mode, respawn) must NOT backfill the stale pre-pause
            // inputs into the resumed stream: the ring resets, the first resumed datagram carries only
            // the fresh input. Detectable on the wire: enqueue order after the pause starts at the fresh
            // seq (the two pre-pause seqs never re-enter the queue after a ServerClearInput).
            var rig = new Rig(seed: 616161);
            ushort owner = rig.Client.PlayerId;

            for (int t = 0; t < 10; t++) { rig.Client.SendMoveInput(0f, 1f, YawFor(t + 1)); rig.Pump(); }
            // the server clears the peer's input state (death / vehicle-enter does this)
            rig.Server.Players.ServerClearInput(owner);
            // the client PAUSES sending for a while (rides a vehicle); the wire goes quiet
            for (int t = 0; t < 20; t++) rig.Pump();
            Assert.That(rig.Server.Players.TryConsumeInput(owner, out _), Is.False, "input state stayed clear across the pause");

            // the stream resumes: seq 11. Pre-reset-ring this datagram would carry seqs 9,10 -- two
            // STALE pre-pause walk intents the cleared server would integrate at the resume spot.
            rig.Client.SendMoveInput(0f, 1f, YawFor(11));
            rig.Pump();
            // drain the prime: the first consumable input must be seq 11, never a stale 9/10
            MoveInput first = default;
            bool got = false;
            for (int t = 0; t < 10 && !got; t++)
            {
                got = rig.Server.Players.TryConsumeInput(owner, out first);
                if (!got) { rig.Client.SendMoveInput(0f, 1f, YawFor(12 + t)); rig.Pump(); }
            }
            Assert.That(got, Is.True, "the resumed stream flows");
            Assert.That(first.Seq, Is.EqualTo((ushort)11), "the resumed stream starts at the fresh seq -- the ring was voided, no stale backfill");
        }
    }
}
