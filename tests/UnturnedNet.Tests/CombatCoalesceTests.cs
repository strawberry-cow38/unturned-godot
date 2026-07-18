using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // mp-event-coalesce (wire v10), the L1 combat-coalescing battery. The four combat commands
    // (Fire/Melee/Grenade/Reload) no longer ride their OWN ReliableOrdered datagram -- they fold
    // REDUNDANTLY into the 50 Hz unreliable PlayerStateCommand transform stream, re-included every tick
    // until the server ACKs them, deduped server-side by a strictly-increasing combat seq. This kills the
    // reliable-ordered head-of-line-block combat stutter on a lossy link. Each mechanism has a test seam
    // (the DisableEnvelope pattern) so every test proves its teeth: with the mechanism off, the test FAILS.
    // All deterministic MemTransport sims -- no sockets, no Godot.
    [TestFixture]
    public class CombatCoalesceTests
    {
        sealed class Harness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public NetWorldClient Client;

            public Harness(int seed)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net));
            }

            public NetWorldClient Connect(string name)
            {
                Client = new NetWorldClient(new MemClientTransport(Net), name);
                Client.Connect();
                for (int i = 0; i < 200 && Client.State != NetSessionState.Connected; i++) Step();
                Assert.That(Client.State, Is.EqualTo(NetSessionState.Connected), $"connected (seed={Net.Seed})");
                return Client;
            }

            // one 50 Hz tick. pumpState = the shell client's per-tick transform send, which is the ONLY
            // thing that flushes the pending combat ring onto the wire (v10). We echo the entity's own
            // current server position so the flush is envelope-neutral (delta ~0) and the player stays put.
            public void Step(bool pumpState = true)
            {
                if (pumpState && Client != null && Client.State == NetSessionState.Connected)
                {
                    Vector3 pos = Vector3.zero; float yaw = 0f;
                    if (Server.Players.TryGetByOwner(Client.PlayerId, out var e)) { pos = e.Pos; yaw = e.YawDegrees; }
                    Client.SendPlayerState(pos, yaw, 0f, Vector3.zero, 0, grounded: true, recovAck: 0);
                }
                Net.Tick();
                Client?.Tick();
                Server.TickSimulation();
                Server.TickReplication();
            }

            public void Step(int ticks, bool pumpState = true) { for (int i = 0; i < ticks; i++) Step(pumpState); }

            public Vector3 EntityPos => Server.Players.TryGetByOwner(Client.PlayerId, out var e) ? e.Pos : Vector3.zero;
            public int Ammo => Server.Combat.AmmoOf(Client.PlayerId);
        }

        static Vector3 Eye(Vector3 feet) => feet + new Vector3(0f, 1.5f, 0f);

        // ---------------------------------------------------------------- redundancy survives a dropped packet

        // A single dropped state datagram must not lose the fire: the next state packet re-carries it. With
        // the redundancy off (send-once, the pre-v10 behaviour) the dropped fire is gone forever.
        static long RunDroppedPacket(bool disableRedundancy)
        {
            var h = new Harness(70101);
            var c = h.Connect("shooter");
            c.DisableCombatRedundancy = disableRedundancy;

            var origin = Eye(h.EntityPos);
            c.SendFire(origin, new Vector3(0f, 0f, 1f));   // enqueue; nothing on the wire yet

            // DROP the very first state datagram that carries the fire...
            h.Net.ClientToServer.LossProbability = 1.0;
            h.Step();
            // ...then let the stream through: the redundant re-include backfills the hole.
            h.Net.ClientToServer.LossProbability = 0.0;
            h.Step(6);

            return h.Server.Combat.Diag.ShotsAccepted;
        }

        [Test]
        public void Redundancy_SurvivesDroppedStatePacket()
        {
            Assert.That(RunDroppedPacket(disableRedundancy: false), Is.EqualTo(1),
                        "the fire still reached the server after its first state packet was dropped");
            // TEETH: with re-inclusion disabled, the dropped fire never arrives -> 0 shots.
            Assert.That(RunDroppedPacket(disableRedundancy: true), Is.EqualTo(0),
                        "send-once (redundancy off) loses the fire on the drop -- proves the redundancy has teeth");
        }

        // ---------------------------------------------------------------- server dedups (no double-fire)

        // The same fire event re-rides every tick (the ack is blocked, so the ring never drains). The
        // strictly-increasing combat-seq guard must apply it EXACTLY once -- not once per delivery. Teeth:
        // with the guard off, the redundant re-deliveries slip past OnFire's incidental fire-rate gate
        // (min gap 5 ticks) and fire multiple times.
        static long RunDedup(bool disableDedup)
        {
            var h = new Harness(70102);
            var c = h.Connect("shooter");
            h.Server.PlayerHost.DisableCombatDedup = disableDedup;
            // block server->client so the combat ack never comes back: the fire keeps being re-included
            h.Net.ServerToClient.LossProbability = 1.0;

            var origin = Eye(h.EntityPos);
            c.SendFire(origin, new Vector3(0f, 0f, 1f));   // ONE fire, enqueued once
            h.Step(20);                                    // re-carried on ~20 successive state packets

            return h.Server.Combat.Diag.ShotsAccepted;
        }

        [Test]
        public void Dedup_SameEventCarriedManyTimes_FiresExactlyOnce()
        {
            Assert.That(RunDedup(disableDedup: false), Is.EqualTo(1),
                        "one fire event, carried on ~20 state packets, is applied exactly once");
            // TEETH: without the strictly-increasing guard, the re-deliveries double-fire past the
            // fire-rate gate (a shot every ~5 ticks over the 20-tick window).
            Assert.That(RunDedup(disableDedup: true), Is.GreaterThan(1),
                        "no dedup guard -> the redundant carry double-fires -- proves the guard has teeth");
        }

        // ---------------------------------------------------------------- ack drains the ring

        // Once the server acks the applied combat seq (via the snapshot's LastProcessedCombatSeq), the
        // client must drop it from the pending ring so it stops re-including it. Teeth: with the ack drain
        // off, the ring never empties.
        static int RunAckDrain(bool disableAck)
        {
            var h = new Harness(70103);
            var c = h.Connect("shooter");
            c.DisableCombatAck = disableAck;

            ushort seq = c.SendFire(Eye(h.EntityPos), new Vector3(0f, 0f, 1f));
            Assert.That(c.PendingCombatContains(seq), Is.True, "the fire is in the ring before any ack");
            Assert.That(c.PendingCombatCount, Is.EqualTo(1));

            h.Step(12);   // server applies + acks; the ack rides a snapshot back and drains the ring
            return c.PendingCombatCount;
        }

        [Test]
        public void Ack_DrainsPendingRing()
        {
            Assert.That(RunAckDrain(disableAck: false), Is.EqualTo(0),
                        "the server's combat ack drained the applied event from the client's pending ring");
            // TEETH: with the drain disabled the ring never empties (it keeps re-including forever).
            Assert.That(RunAckDrain(disableAck: true), Is.GreaterThan(0),
                        "ack drain off -> the ring never empties -- proves the drain has teeth");
        }

        // ---------------------------------------------------------------- oldest-first dispatch ordering

        // Carried events dispatch in ascending seq. Because the server's dedup guard is strictly-increasing,
        // oldest-first ordering is what lets ALL THREE apply; newest-first would make the dedup guard drop
        // everything but the newest.
        static List<ushort> RunOrdering(bool reverseRing)
        {
            var h = new Harness(70104);
            var c = h.Connect("shooter");
            c.CombatRingReverseOrder = reverseRing;

            // record the seq of every event the authority dispatches, in dispatch order
            var order = new List<ushort>();
            var inner = h.Server.PlayerHost.CombatDispatch;
            h.Server.PlayerHost.CombatDispatch = (sender, ev, tick) => { order.Add(ev.Seq); inner(sender, ev, tick); };

            var origin = Eye(h.EntityPos);
            c.SendFire(origin, new Vector3(0f, 0f, 1f));    // seq 1
            c.SendMelee(false, 0f);                          // seq 2
            c.SendGrenade(origin, Vector3.zero);             // seq 3
            h.Step(4);

            return order;
        }

        [Test]
        public void Ordering_EventsDispatchOldestFirst()
        {
            Assert.That(RunOrdering(reverseRing: false), Is.EqualTo(new List<ushort> { 1, 2, 3 }),
                        "the three carried events dispatched oldest-first, all three, in ascending seq");
            // TEETH: newest-first through the strictly-increasing dedup guard drops all but the newest.
            Assert.That(RunOrdering(reverseRing: true), Is.EqualTo(new List<ushort> { 3 }),
                        "newest-first -> the dedup guard drops the older two -- proves oldest-first ordering has teeth");
        }

        // ---------------------------------------------------------------- SP / loopback insulation

        // The listen-server / SP-loopback local player drives combat DIRECTLY (never over the wire) and its
        // movement wire is SendMoveInput, not SendPlayerState/SendFire. So the combat coalesce machinery
        // must never engage on that path: a client that only sends MoveInput has an empty combat ring.
        [Test]
        public void Loopback_MovementPath_DoesNotTouchTheCombatRing()
        {
            var h = new Harness(70105);
            var c = h.Connect("local");
            // stream MoveInput the way MpLoopback.TickLocal does -- never a PlayerStateCommand or a fire
            for (int i = 0; i < 15; i++)
            {
                c.SendMoveInput(0f, 1f, 0f);
                h.Net.Tick(); c.Tick(); h.Server.TickSimulation(); h.Server.TickReplication();
            }
            Assert.That(c.PendingCombatCount, Is.EqualTo(0), "the movement-only (loopback) path never populates the combat ring");
        }
    }
}
