using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;
using UnturnedNet.Tests.Mocks;

namespace UnturnedNet.Tests
{
    // Pure framing-logic tests: SnapshotComposer + SnapshotApplier wired directly, no transport/network in
    // the loop. The network-conditioned equivalents (packet loss, join-mid-scenario) live in
    // SnapshotNetworkTests.cs -- these prove the baseline/dirty-tracking/forward-compat MECHANICS in
    // isolation, fast and deterministic.
    [TestFixture]
    public class SnapshotUnitTests
    {
        [Test]
        public void FullThenDelta_AddMoveRemove_StateHashMatchesThroughout()
        {
            var minter = new NetIdMinter();
            var server = new MockEntitySystem(systemId: 10);
            var client = new MockEntitySystem(systemId: 10);
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { server });
            var applier = new SnapshotApplier(new List<IReplicatedSystem> { client });
            const ushort playerId = 1;

            var a = minter.Mint();
            server.Set(a, new Vector3(1f, 2f, 3f), 90f, 50, tick: 1);

            // first compose: no baseline yet -> full
            var snap1 = composer.Compose(serverTick: 5, playerId, Vector3.zero);
            Assert.That(applier.Apply(snap1, snap1.Length), Is.True);
            Assert.That(applier.LastAppliedWasFull, Is.True);
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "full snapshot must match exactly");
            composer.SetClientBaseline(playerId, applier.LastAppliedServerTick);

            // move the entity + add a second one; next compose should be a delta
            server.Set(a, new Vector3(10f, 0f, -5f), 180f, 40, tick: 10);
            var b = minter.Mint();
            server.Set(b, new Vector3(-3f, 1f, 3f), 270f, 90, tick: 10);

            var snap2 = composer.Compose(serverTick: 12, playerId, Vector3.zero);
            Assert.That(applier.Apply(snap2, snap2.Length), Is.True);
            Assert.That(applier.LastAppliedWasFull, Is.False, "within the dirty ring -- must be a delta");
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "delta add+move must converge");
            composer.SetClientBaseline(playerId, applier.LastAppliedServerTick);

            // remove the first entity; the delta's removal list must propagate
            server.Remove(a, tick: 20);
            var snap3 = composer.Compose(serverTick: 22, playerId, Vector3.zero);
            Assert.That(applier.Apply(snap3, snap3.Length), Is.True);
            Assert.That(client.EntityCount, Is.EqualTo(1), "removed entity must disappear client-side");
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "delta removal must converge");
        }

        [Test]
        public void BaselineOlderThanRingDepth_ForcesFullResend()
        {
            var minter = new NetIdMinter();
            var server = new MockEntitySystem(systemId: 10);
            var client = new MockEntitySystem(systemId: 10);
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { server });
            var applier = new SnapshotApplier(new List<IReplicatedSystem> { client });
            const ushort playerId = 7;

            var id = minter.Mint();
            server.Set(id, new Vector3(1f, 0f, 1f), 0f, 100, tick: 1);

            var snap1 = composer.Compose(serverTick: 10, playerId, Vector3.zero);
            Assert.That(applier.Apply(snap1, snap1.Length), Is.True);
            Assert.That(applier.LastAppliedWasFull, Is.True, "first ever snapshot is always full (baseline 0)");
            composer.SetClientBaseline(playerId, applier.LastAppliedServerTick); // baseline = 10

            // client goes stale: keep mutating server state but never ack again (baseline stays 10)
            server.Set(id, new Vector3(2f, 0f, 2f), 45f, 90, tick: 20);

            Assert.That(composer.WillSendFull(playerId, 10 + NetQuantization.DirtyRingDepthTicks), Is.False,
                "exactly at the ring depth is still eligible for a delta");
            Assert.That(composer.WillSendFull(playerId, 10 + NetQuantization.DirtyRingDepthTicks + 1), Is.True,
                "one tick past the ring depth must force a full resend");

            var staleSnap = composer.Compose(10 + NetQuantization.DirtyRingDepthTicks + 1, playerId, Vector3.zero);
            Assert.That(applier.Apply(staleSnap, staleSnap.Length), Is.True);
            Assert.That(applier.LastAppliedWasFull, Is.True, "composer must have fallen back to a full snapshot");
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "must re-converge via the full resend");
        }

        [Test]
        public void UnknownSystemId_IsSkipped_OtherBlocksStillApply()
        {
            var minter = new NetIdMinter();
            var serverA = new MockEntitySystem(systemId: 10);
            var serverB = new MockEntitySystem(systemId: 11);
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { serverA, serverB });

            var idA = minter.Mint();
            serverA.Set(idA, new Vector3(1f, 1f, 1f), 10f, 5, tick: 1);
            var idB = minter.Mint();
            serverB.Set(idB, new Vector3(2f, 2f, 2f), 20f, 6, tick: 1);

            // client build only knows about system 10 -- system 11 is a stand-in for a system this client
            // predates (forward compat, MP_PLAN §2.4).
            var clientA = new MockEntitySystem(systemId: 10);
            var applier = new SnapshotApplier(new List<IReplicatedSystem> { clientA });

            var snap = composer.Compose(serverTick: 3, clientPlayerId: 1, Vector3.zero);
            bool ok = applier.Apply(snap, snap.Length);

            Assert.That(ok, Is.True, "an unknown-but-well-formed block must not fail the whole datagram");
            Assert.That(applier.Diag.UnknownSystemBlocksSkipped, Is.EqualTo(1));
            Assert.That(clientA.EntityCount, Is.EqualTo(1), "system 10's block still applied");
            Assert.That(clientA.StateHash(), Is.EqualTo(serverA.StateHash()));
        }

        [Test]
        public void DuplicateSystemId_ThrowsAtConstruction()
        {
            var sys1 = new MockEntitySystem(systemId: 5);
            var sys2 = new MockEntitySystem(systemId: 5);
            Assert.Throws<System.InvalidOperationException>(() =>
                new SnapshotComposer(new List<IReplicatedSystem> { sys1, sys2 }));
            Assert.Throws<System.InvalidOperationException>(() =>
                new SnapshotApplier(new List<IReplicatedSystem> { sys1, sys2 }));
        }
    }
}
