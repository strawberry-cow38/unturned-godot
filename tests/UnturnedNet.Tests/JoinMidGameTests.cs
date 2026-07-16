using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The Phase 4 join flow end-to-end (MP_PLAN §4: "version + content hash -> Accept -> reliable FULL
    // snapshot -> spawn -> deltas"), proven at the L0 layer: a client that connects at tick ~500 -- while
    // the world keeps CHANGING under it -- lands on exact StateHash parity with the server and with a
    // from-the-start client; the join snapshot demonstrably arrived over the ReliableOrdered channel; and
    // a content-hash mismatch is rejected before any state flows.
    [TestFixture]
    public class JoinMidGameTests
    {
        const ulong Hash = 0xC0FFEE_2026UL;

        sealed class Harness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public readonly System.Collections.Generic.List<NetWorldClient> Clients = new();

            public Harness(int seed, ulong serverHash = Hash)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net), contentHash: serverHash);
            }

            public NetWorldClient AddClient(string name, ulong hash = Hash)
            {
                var c = new NetWorldClient(new MemClientTransport(Net), name, contentHash: hash);
                Clients.Add(c);
                c.Connect();
                return c;
            }

            // one 50 Hz tick in §2.5 order: inputs (caller), transport, client sessions, server sim, replication LAST
            public void Step(System.Action perTickInputs = null)
            {
                perTickInputs?.Invoke();
                Net.Tick();
                foreach (var c in Clients) c.Tick();
                Server.TickSimulation();
                Server.TickReplication();
            }

            public void Step(int ticks, System.Action perTickInputs = null)
            {
                for (int i = 0; i < ticks; i++) Step(perTickInputs);
            }
        }

        [Test]
        public void JoinAtTick500_ReachesStateHashParity_WithServerAndFromStartClient()
        {
            var h = new Harness(20260716);
            var a = h.AddClient("fromstart");
            h.Step(20);
            Assert.That(a.State, Is.EqualTo(NetSessionState.Connected), "from-start client connected");

            // ~500 ticks of a deterministic weave so the late joiner arrives against real accumulated state
            long tick = 0;
            void Walk() { a.SendMoveInput(((tick / 100) % 2 == 0) ? 0.5f : -0.5f, 1f, (tick * 3) % 360); tick++; }
            h.Step(480, Walk);

            var b = h.AddClient("late");   // joins at tick ~500 -- and A KEEPS MOVING through the join
            h.Step(100, Walk);
            Assert.That(b.State, Is.EqualTo(NetSessionState.Connected), "late client connected mid-game");
            Assert.That(b.JoinSnapshotsApplied, Is.GreaterThanOrEqualTo(1),
                        "the join-time FULL snapshot arrived over the ReliableOrdered channel (§2.2)");
            Assert.That(b.Players.Count, Is.EqualTo(2), "late joiner sees both players");

            // settle (held-keys: zero axes, then flush) -> exact parity, never a tolerance (MP_PLAN §6)
            h.Step(5, () => a.SendMoveInput(0f, 0f, 0f));
            h.Step(20);
            ulong server = h.Server.Players.StateHash();
            Assert.That(a.Players.StateHash(), Is.EqualTo(server), "from-start replica == server");
            Assert.That(b.Players.StateHash(), Is.EqualTo(server), "tick-500 joiner == server (join-mid-game parity)");
            Assert.That(server, Is.Not.EqualTo(new PlayerReplication().StateHash()), "the scenario actually produced state");
        }

        [Test]
        public void ContentHashMismatch_IsRejected_BeforeAnyStateFlows()
        {
            var h = new Harness(777);
            bool joined = false;
            h.Server.Session.PeerConnected += _ => joined = true;

            var imposter = h.AddClient("otherbuild", hash: Hash ^ 0xDEAD);
            h.Step(80);
            Assert.That(imposter.State, Is.EqualTo(NetSessionState.Disconnected), "mismatched content is refused");
            Assert.That(imposter.Session.DisconnectReason, Is.EqualTo(NetDisconnectReason.Rejected));
            Assert.That(imposter.Session.RejectReason, Is.EqualTo(NetRejectReason.ContentMismatch));
            Assert.That(joined, Is.False, "PeerConnected never fired for the rejected client");
            Assert.That(h.Server.Session.Peers.Count, Is.EqualTo(0), "no session lingers for a rejected join");
            Assert.That(h.Server.Players.Count, Is.EqualTo(0), "no avatar was spawned");

            var legit = h.AddClient("samebuild");   // matching hash still joins fine afterwards
            h.Step(40);
            Assert.That(legit.State, Is.EqualTo(NetSessionState.Connected), "matching content hash is accepted");
            Assert.That(h.Server.Players.Count, Is.EqualTo(1));
        }

        [Test]
        public void ServerDrive_TakesOverAnEntity_AndReplicatesTheDrivenTransform()
        {
            // The listen-server / SP-loopback seam (MP_PLAN §2.1): an in-process shell steps the real
            // sim-core + collision and writes the result through ServerDrive; the internal flat-ground
            // integration must stop touching that entity, and replicas must see the DRIVEN transform.
            var h = new Harness(4444);
            var shell = h.AddClient("shell");
            var observer = h.AddClient("observer");
            h.Step(20);
            Assert.That(shell.State, Is.EqualTo(NetSessionState.Connected));
            Assert.That(observer.State, Is.EqualTo(NetSessionState.Connected));

            // the shell walks per its OWN sim (here: a scripted diagonal), inputs still flow for the ack loop
            var pos = new UnityEngine.Vector3(0f, 0f, 0f);
            ushort lastSeq = 0;
            h.Step(100, () =>
            {
                pos.x += 0.05f; pos.z += 0.03f;   // "real" collision-stepped result, not IntegrateFlat
                lastSeq = shell.SendMoveInput(1f, 1f, 45f);
                h.Server.Players.ServerDrive(shell.PlayerId, pos, 45f, lastSeq, h.Server.Session.CurrentTick);
            });
            h.Step(20);   // flush snapshots

            Assert.That(h.Server.Players.TryGetByOwner(shell.PlayerId, out var authoritative), Is.True);
            Assert.That(authoritative.Pos.x, Is.EqualTo(pos.x).Within(0.01f), "internal integration never fought the drive");
            Assert.That(authoritative.Pos.z, Is.EqualTo(pos.z).Within(0.01f));
            Assert.That(authoritative.LastProcessedInputSeq, Is.EqualTo(lastSeq), "the drive acks the shell's input seqs");

            Assert.That(observer.Players.TryGetByOwner(shell.PlayerId, out var seen), Is.True, "observer sees the shell's avatar");
            Assert.That(seen.Pos.x, Is.EqualTo(authoritative.Pos.x), "replica carries the driven transform exactly");
            Assert.That(seen.Pos.z, Is.EqualTo(authoritative.Pos.z));
            Assert.That(observer.Players.StateHash(), Is.EqualTo(h.Server.Players.StateHash()), "full parity");
        }
    }
}
