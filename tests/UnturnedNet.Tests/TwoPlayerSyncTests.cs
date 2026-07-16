using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The Phase 3 re-founding of the 2-player loop (MP_PLAN §4): two NetWorldClients join a NetWorldServer
    // over MemTransport, send MoveInput commands (the first real Cmd), and see each other's positions move
    // through the server's authoritative PlayerReplication (the first real IReplicatedSystem) + snapshot
    // plane. Fully tick-driven and deterministic -- this retires the Thread.Sleep/UDP pump the old
    // prototype-era test used: no sockets, no wall clock, same result every run.
    //
    // Note the input model is held-keys (the server keeps applying the latest MoveInput every tick), so a
    // scenario "settles" by sending a zero-axes input and then pumping a few input-free ticks: once no new
    // seq arrives and the axes are zero, the entity stops dirtying and replicas converge to exact parity.
    [TestFixture]
    public class TwoPlayerSyncTests
    {
        sealed class WorldHarness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public readonly NetWorldClient A;
            public readonly NetWorldClient B;

            public WorldHarness(int seed)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net));
                A = new NetWorldClient(new MemClientTransport(Net), "a");
                B = new NetWorldClient(new MemClientTransport(Net), "b");
                A.Connect();
                B.Connect();
            }

            public readonly System.Collections.Generic.List<NetWorldClient> Extra = new System.Collections.Generic.List<NetWorldClient>();

            // One 50 Hz tick, in MP_PLAN §2.5 order: client input -> transport delivery -> client sessions
            // -> server receive/input-apply/player-sim -> replication send LAST.
            public void Step(System.Action perTickInputs = null)
            {
                perTickInputs?.Invoke();
                Net.Tick();
                A.Tick();
                B.Tick();
                foreach (var c in Extra) c.Tick();
                Server.TickSimulation();
                Server.TickReplication();
            }

            public void Step(int ticks, System.Action perTickInputs = null)
            {
                for (int i = 0; i < ticks; i++) Step(perTickInputs);
            }

            // Freeze the world so replicas can reach exact StateHash parity: a short zero-axes input burst
            // (held-keys -> stops the movers), then input-free ticks so the final snapshots flush + apply.
            public void Settle(float yawA = 0f, float yawB = 0f)
            {
                Step(5, () => { A.SendMoveInput(0f, 0f, yawA); B.SendMoveInput(0f, 0f, yawB); });
                Step(15);
            }
        }

        static WorldHarness ConnectedPair(int seed)
        {
            var h = new WorldHarness(seed);
            h.Step(20);
            Assert.That(h.A.State, Is.EqualTo(NetSessionState.Connected), $"A connected (seed={seed})");
            Assert.That(h.B.State, Is.EqualTo(NetSessionState.Connected), $"B connected (seed={seed})");
            return h;
        }

        [Test]
        public void TwoClients_JoinAndMove_SeeEachOther_ThroughServer()
        {
            var h = ConnectedPair(9301);

            // A walks +Z (forward at yaw 0), B walks -Z (forward at yaw 180)
            h.Step(100, () =>
            {
                h.A.SendMoveInput(0f, 1f, 0f);
                h.B.SendMoveInput(0f, 1f, 180f);
            });
            h.Settle(yawA: 0f, yawB: 180f);

            Assert.That(h.Server.Session.Peers.Count, Is.EqualTo(2), "server sees 2 players");
            Assert.That(h.A.Players.Count, Is.EqualTo(2), "client A sees 2 players (incl. itself, echoed through the server)");
            Assert.That(h.B.Players.Count, Is.EqualTo(2), "client B sees 2 players");

            // authoritative movement actually happened: ~100 ticks * 0.02 s * SPEED_STAND (4.5 m/s) = ~9 m
            // (± a couple of ticks of command latency around the start/stop edges, and quantization grain)
            Assert.That(h.A.Players.TryGetByOwner(h.A.PlayerId, out var aSelf), Is.True);
            Assert.That(h.A.Players.TryGetByOwner(h.B.PlayerId, out var bSeenByA), Is.True, "A sees B's avatar");
            float expected = 100 * (float)SimClock.FixedDelta * PlayerMovementDef.SPEED_STAND;
            Assert.That(aSelf.Pos.z, Is.EqualTo(expected).Within(0.5f), "A moved +Z under server authority");
            Assert.That(bSeenByA.Pos.z, Is.EqualTo(-expected).Within(0.5f), "B moved -Z, visible to A through the server");
            Assert.That(aSelf.LastProcessedInputSeq, Is.GreaterThan(0), "snapshots carry lastProcessedInputSeq (prediction hook, MP_PLAN §5.6)");

            // sync correctness: exact StateHash parity, server vs both replicas (MP_PLAN §6)
            Assert.That(h.A.Players.StateHash(), Is.EqualTo(h.Server.Players.StateHash()), "A replica == server state");
            Assert.That(h.B.Players.StateHash(), Is.EqualTo(h.Server.Players.StateHash()), "B replica == server state");
        }

        [Test]
        public void SameSeedAndScript_ProducesIdenticalStateHash_EveryRun()
        {
            ulong Run()
            {
                var h = ConnectedPair(555);
                h.Step(80, () =>
                {
                    h.A.SendMoveInput(1f, 0.25f, 42f);
                    h.B.SendMoveInput(-0.5f, 1f, 300f);
                });
                h.Settle(yawA: 42f, yawB: 300f);
                Assert.That(h.A.Players.StateHash(), Is.EqualTo(h.Server.Players.StateHash()), "replica parity inside the scripted run");
                return h.Server.Players.StateHash();
            }

            ulong first = Run();
            ulong second = Run();
            Assert.That(second, Is.EqualTo(first), "the whole two-client scenario is deterministic (no sleeps, no wall clock)");
            Assert.That(first, Is.Not.EqualTo(new PlayerReplication().StateHash()), "scenario actually produced state");
        }

        [Test]
        public void LateJoiner_GetsFullSnapshot_AndConverges()
        {
            var h = ConnectedPair(777);
            h.Step(60, () => h.A.SendMoveInput(0f, 1f, 90f));
            h.Settle(yawA: 90f);

            // C joins mid-game against the frozen world: its first applied snapshot must be a FULL one
            // (WriteFull IS the join path -- no per-system resend code) and land it on exact server parity.
            var c = new NetWorldClient(new MemClientTransport(h.Net), "late");
            h.Extra.Add(c);
            c.Connect();
            h.Step(40);

            Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), "late client connected");
            Assert.That(c.Applier.Diag.FullSnapshotsApplied, Is.GreaterThan(0), "join path = WriteFull");
            Assert.That(c.Players.Count, Is.EqualTo(3), "late joiner sees all 3 players");
            Assert.That(c.Players.StateHash(), Is.EqualTo(h.Server.Players.StateHash()), "late joiner converged to server state");
        }
    }
}
