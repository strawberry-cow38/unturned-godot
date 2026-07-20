using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using SDG.NetPak;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_PLAN §4 Phase 7, the L0 vehicle battery: enter/exit is arbitrated at the §2.3 choke point (two
    // clients race one seat -> exactly ONE wins, exit frees it, a disconnect frees it), DriveInput only
    // applies from the vehicle's actual driver, a driver's avatar rides the vehicle entity and steps out
    // beside the driver door, and the whole vehicle block round-trips the wire to StateHash parity. All
    // deterministic MemTransport sims -- no sockets, no sleeps, no Godot.
    [TestFixture]
    public class VehicleReplicationTests
    {
        sealed class Harness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public readonly List<NetWorldClient> Clients = new();

            public Harness(int seed)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net));
            }

            public NetWorldClient AddClient(string name)
            {
                var c = new NetWorldClient(new MemClientTransport(Net), name);
                Clients.Add(c);
                c.Connect();
                return c;
            }

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

            public bool StepUntil(System.Func<bool> condition, int maxTicks = 400)
            {
                for (int i = 0; i < maxTicks; i++)
                {
                    if (condition()) return true;
                    Step();
                }
                return condition();
            }

            public Harness Connected(params string[] names)
            {
                foreach (var n in names) AddClient(n);
                Step(25);
                foreach (var c in Clients)
                    Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), $"client connected (seed={Net.Seed})");
                return this;
            }

            /// <summary>The authority seeding a vehicle entity (in-engine this is VehicleNetSync minting
            /// from the node; L0 has no node -- the entity IS the vehicle).</summary>
            public uint SpawnVehicle(Vector3 pos, byte typeId = 0, byte variant = 0)
            {
                var e = Server.Vehicles.ServerSpawn(Server.Ids.Mint(), typeId, variant, pos, Server.Session.CurrentTick);
                return e.NetIdValue;
            }
        }

        [Test]
        public void EnterRace_ExactlyOneWins_ExitFreesSeat()
        {
            var h = new Harness(7001).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            // players spawn at (0,0,0) and (2,0,0) -- a seat at (1,0,0) is in reach of both
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f));

            long rejectedBefore = h.Server.Commands.Diag.ValidationRejected;
            // the race: both claims sent the same tick; ReliableOrdered dispatch decides, occupancy gates
            a.SendEnterVehicle(veh);
            b.SendEnterVehicle(veh);
            Assert.That(h.StepUntil(() => Driver(h, veh) != 0), Is.True, "someone won the seat");

            bool aDrives = h.Server.VehicleHost.IsDriver(a.PlayerId);
            bool bDrives = h.Server.VehicleHost.IsDriver(b.PlayerId);
            Assert.That(aDrives ^ bDrives, Is.True, "exactly one racer holds the seat");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejectedBefore),
                        "the loser's Enter was rejected at the choke point");

            // exit frees it; the loser can then take it
            var winner = aDrives ? a : b;
            var loser = aDrives ? b : a;
            winner.SendExitVehicle();
            Assert.That(h.StepUntil(() => Driver(h, veh) == 0), Is.True, "exit freed the seat");
            loser.SendEnterVehicle(veh);
            Assert.That(h.StepUntil(() => Driver(h, veh) == loser.PlayerId), Is.True, "the freed seat is takeable");
        }

        [Test]
        public void Enter_OutOfReach_Rejected()
        {
            var h = new Harness(7002).Connected("a");
            var a = h.Clients[0];
            uint far = h.SpawnVehicle(new Vector3(100f, 0f, 0f));   // way past EnterReach (6 m)

            long rejectedBefore = h.Server.Commands.Diag.ValidationRejected;
            a.SendEnterVehicle(far);
            h.Step(25);
            Assert.That(Driver(h, far), Is.EqualTo(0), "an out-of-reach vehicle can't be entered");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejectedBefore));
        }

        [Test]
        public void DriveInput_OnlyTheDriverFeedsIt()
        {
            var h = new Harness(7003).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f));

            // nobody drives yet -> input from anyone drops at validation
            b.SendDriveInput(veh, 1f, 0f, false);
            h.Step(10);
            Assert.That(h.Server.Vehicles.TryGetInput(veh, out _), Is.False, "non-driver input never lands");

            a.SendEnterVehicle(veh);
            Assert.That(h.StepUntil(() => Driver(h, veh) == a.PlayerId), Is.True, "A took the seat");

            b.SendDriveInput(veh, -1f, 1f, true);   // still not B's seat
            h.Step(10);
            Assert.That(h.Server.Vehicles.TryGetInput(veh, out _), Is.False, "the passenger-less seat rejects B's input");

            a.SendDriveInput(veh, 1f, -0.5f, true);
            Assert.That(h.StepUntil(() => h.Server.Vehicles.TryGetInput(veh, out _)), Is.True, "the driver's input lands");
            h.Server.Vehicles.TryGetInput(veh, out var inp);
            Assert.That(inp.Throttle, Is.GreaterThan(0.9f), "throttle survived the 8-bit wire");
            Assert.That(inp.Steer, Is.LessThan(-0.4f), "steer survived the 8-bit wire");
            Assert.That(inp.Handbrake, Is.True, "handbrake bit survived");
        }

        [Test]
        public void Driver_RidesTheVehicle_MoveInputDropped_ExitStepsOutBeside()
        {
            var h = new Harness(7004).Connected("a");
            var a = h.Clients[0];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f));
            a.SendEnterVehicle(veh);
            Assert.That(h.StepUntil(() => Driver(h, veh) == a.PlayerId), Is.True);

            // a driver's walk input drops at the choke point (the seat teleport owns the avatar)
            long rejectedBefore = h.Server.Commands.Diag.ValidationRejected;
            h.Step(10, () => a.SendMoveInput(0f, 1f, 0f));
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejectedBefore),
                        "MoveInput while driving is dropped");

            // the vehicle moves (authority publish, as VehicleNetSync would from the node) -> the avatar rides it
            var moved = new Vector3(10f, 0f, 5f);
            h.Server.Vehicles.ServerPublish(new NetId(veh), moved, Vector3.zero, Vector3.zero, Vector3.zero,
                                            0f, 0f, 0f, 0f, 0, h.Server.Session.CurrentTick);
            h.Step(2);
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var rider), Is.True);
            Assert.That((rider.Pos - PlayerReplication.Quantize(moved)).magnitude, Is.LessThan(0.05f),
                        "the driver's avatar rides the vehicle entity");

            // exit steps out beside the driver door: pos + right(yaw 0 -> +X) * 2.4 + up
            a.SendExitVehicle();
            Assert.That(h.StepUntil(() => Driver(h, veh) == 0), Is.True, "exited");
            h.Server.Players.TryGetByOwner(a.PlayerId, out var outside);
            var expected = PlayerReplication.Quantize(moved + new Vector3(2.4f, 1.0f, 0f));
            Assert.That((outside.Pos - expected).magnitude, Is.LessThan(0.05f), "exit placed the avatar beside the seat");
        }

        [Test]
        public void ExitEvent_CarriesTheAuthoritativeSpot_PostAdjust()
        {
            // Regression for docs/EXIT_POSITION_ROOTCAUSE.md: the exit spot must ride the reliable
            // VehicleExited fact itself (the exiting client's replica can be frozen by a snapshot
            // starvation hold, so a replica-derived spot is only as good as the stream's health). The
            // event's Pos must be the FINAL server spot: beside-the-door THEN AdjustExitSpot (§7 risk 6).
            var h = new Harness(7007).Connected("a");
            var a = h.Clients[0];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f));
            a.SendEnterVehicle(veh);
            Assert.That(h.StepUntil(() => Driver(h, veh) == a.PlayerId), Is.True, "seated");

            // drive the entity away from the entry (authority publish, as VehicleNetSync would)
            var moved = new Vector3(40f, 0f, -25f);
            h.Server.Vehicles.ServerPublish(new NetId(veh), moved, Vector3.zero, Vector3.zero, Vector3.zero,
                                            0f, 0f, 0f, 0f, 0, h.Server.Session.CurrentTick);
            // a visible clamp so the test proves the POST-adjust spot is what rides the wire
            h.Server.VehicleHost.AdjustExitSpot = p => new Vector3(p.x, p.y + 2.5f, p.z);

            VehicleExitedEvent? got = null;
            a.VehicleExited += evt => { if (evt.PlayerId == a.PlayerId) got = evt; };
            a.SendExitVehicle();
            Assert.That(h.StepUntil(() => got.HasValue), Is.True, "the exited fact reached the client");

            var expected = moved + new Vector3(2.4f, 1.0f, 0f) + new Vector3(0f, 2.5f, 0f);   // yaw 0 -> right = +X, then the adjust
            Assert.That((got.Value.Pos - expected).magnitude, Is.LessThan(0.001f),
                        "the event carries the exact post-AdjustExitSpot exit spot");
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var outside), Is.True);
            Assert.That((outside.Pos - PlayerReplication.Quantize(expected)).magnitude, Is.LessThan(0.05f),
                        "...and it matches where the server actually teleported the entity");
        }

        [Test]
        public void ExitEvent_WireRoundTrip_And_LegacyPayloadFailsClosed()
        {
            // Round-trip: the float32 x3 spot survives the wire exactly (the terrain clamp's height must
            // arrive verbatim -- no quantization on this rare reliable event).
            var evt = new VehicleExitedEvent { NetId = 0xDEAD01u, PlayerId = 7, Pos = new Vector3(1912.53f, 41.0625f, -755.19f) };
            byte[] packed = NetMessagePak.Pack(ReplicationIds.EventVehicleExited, evt.Write);
            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(packed, packed.Length);
            r.ReadUInt8(out _);   // the id byte, as EventRegistry.TryDispatch consumes it
            Assert.That(VehicleExitedEvent.TryRead(r, out var read), Is.True);
            Assert.That(read.NetId, Is.EqualTo(evt.NetId));
            Assert.That(read.PlayerId, Is.EqualTo(evt.PlayerId));
            Assert.That(read.Pos, Is.EqualTo(evt.Pos), "float32 x3 is byte-exact");

            // Fail-closed: a LEGACY (pre-v4) payload without the spot must be refused, never misparsed --
            // the EventRegistry then counts it MalformedSkipped and drops it. (Cross-build sessions can't
            // reach this point anyway: the NetProtocol.Version 3->4 bump rejects them at connect.)
            var legacy = NetMessagePak.Pack(ReplicationIds.EventVehicleExited,
                w => { w.WriteUInt32(evt.NetId); w.WriteUInt16(evt.PlayerId); });
            var lr = new SDG.NetPak.NetPakReader();
            lr.SetBufferSegment(legacy, legacy.Length);
            lr.ReadUInt8(out _);
            Assert.That(VehicleExitedEvent.TryRead(lr, out _), Is.False, "a truncated/legacy payload fails the read cleanly");
        }

        [Test]
        public void Disconnect_FreesTheSeat()
        {
            var h = new Harness(7005).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f));
            a.SendEnterVehicle(veh);
            Assert.That(h.StepUntil(() => Driver(h, veh) == a.PlayerId), Is.True);

            a.Disconnect();
            Assert.That(h.StepUntil(() => Driver(h, veh) == 0, 600), Is.True, "the dropped driver's seat freed");
            b.SendEnterVehicle(veh);
            Assert.That(h.StepUntil(() => Driver(h, veh) == b.PlayerId), Is.True, "the seat is takeable again");
        }

        [Test]
        public void VehicleState_ReplicatesToClients_StateHashParity()
        {
            var h = new Harness(7006).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            uint veh = h.SpawnVehicle(new Vector3(4f, 1f, -8f), typeId: 3, variant: 7);

            // churn the authoritative state (what VehicleNetSync publishes from the node each tick) --
            // staying inside EnterReach of player A's (0,0,0) spawn so the Enter below validates
            h.Server.Vehicles.ServerPublish(new NetId(veh), new Vector3(3f, 0.5f, -2f),
                new Vector3(2f, 45f, -3f), new Vector3(8f, 0f, -1f), new Vector3(0f, 0.5f, 0f),
                steerDegrees: -14f, fuel: 1832.5f, health: 542f, battery: 9980f,
                flags: (byte)(VehicleReplication.FlagEngineOn | VehicleReplication.FlagHeadlights),
                tick: h.Server.Session.CurrentTick);
            a.SendEnterVehicle(veh);

            Assert.That(h.StepUntil(() =>
                a.Vehicles.Count == 1 && b.Vehicles.Count == 1
                && a.Vehicles.StateHash() == h.Server.Vehicles.StateHash()
                && b.Vehicles.StateHash() == h.Server.Vehicles.StateHash()), Is.True,
                $"both replicas reached exact StateHash parity (seed={h.Net.Seed})");

            Assert.That(b.Vehicles.TryGet(veh, out var replica), Is.True);
            Assert.That(replica.DriverPlayerId, Is.EqualTo(a.PlayerId), "occupancy replicated to the observer");
            Assert.That(replica.TypeId, Is.EqualTo(3), "spec TypeId replicated");
            Assert.That(replica.Variant, Is.EqualTo(7), "paint variant replicated");
            Assert.That(replica.EngineOn, Is.True, "flags replicated");
            Assert.That(replica.SteerSigned, Is.InRange(-15f, -13f), "steer summary replicated (signed)");
        }

        [Test]
        public void WireRoundTrip_FullThenDelta_HashParity()
        {
            var server = new VehicleReplication();
            var client = new VehicleReplication();
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { server });
            var applier = new SnapshotApplier(new List<IReplicatedSystem> { client });

            server.ServerSpawn(new NetId(1), typeId: 12, variant: 3, new Vector3(10f, 2f, -20f), tick: 1);
            server.ServerSpawn(new NetId(2), typeId: 0, variant: 0, new Vector3(-5f, 0f, 3f), tick: 1);
            server.ServerPublish(new NetId(1), new Vector3(11f, 2.1f, -19f), new Vector3(1f, 180f, 359f),
                new Vector3(3f, -0.5f, 12f), new Vector3(0.1f, 0f, -0.2f), 22f, 2000f, 999.5f, 10000f,
                VehicleReplication.FlagEngineOn, tick: 2);

            var full = composer.Compose(serverTick: 2, clientPlayerId: 1, Vector3.zero);
            Assert.That(applier.Apply(full, full.Length), Is.True, "full snapshot applied");
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "full round-trip parity");

            // delta: one entity changes, one is removed
            composer.SetClientBaseline(1, 2);
            server.ServerPublish(new NetId(1), new Vector3(12f, 2.1f, -18f), new Vector3(1f, 181f, 359f),
                new Vector3(3f, -0.5f, 12f), new Vector3(0.1f, 0f, -0.2f), 20f, 1999f, 999.5f, 10000f,
                VehicleReplication.FlagEngineOn, tick: 3);
            server.ServerRemove(new NetId(2), tick: 3);

            var delta = composer.Compose(serverTick: 3, clientPlayerId: 1, Vector3.zero);
            Assert.That(applier.Apply(delta, delta.Length), Is.True, "delta snapshot applied");
            Assert.That(client.Count, Is.EqualTo(1), "removal rode the delta");
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "delta round-trip parity");
        }

        [Test]
        public void TowRelationship_RoundTrips_HashParity()
        {
            // A6: the rope-tow relationship (TowedNetId + TowRestLen -- the disjoint THIRD writer,
            // ServerPublishTow) survives a full AND a delta snapshot to exact StateHash parity, and the
            // tower's TowedNetId + quantized rest length land verbatim on the client replica; the towed end
            // carries NO TowedNetId of its own ("am I towed" is derived client-side, never replicated).
            // Teeth: pre-fix VehicleEntity has no tow fields (won't compile). And the rest length is tied at
            // 3.3 m, which is NOT wire-exact -- it quantizes to 3.25 @ 3int/4frac -- so if ServerPublishTow
            // stored the RAW value instead of quantizing, the server would hash 3.3 while the client hashes
            // the wire-reconstructed 3.25 and the parity asserts fail.
            var server = new VehicleReplication();
            var client = new VehicleReplication();
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { server });
            var applier = new SnapshotApplier(new List<IReplicatedSystem> { client });

            server.ServerSpawn(new NetId(1), typeId: 0, variant: 0, new Vector3(0f, 0f, 0f), tick: 1);   // the tower
            server.ServerSpawn(new NetId(2), typeId: 0, variant: 0, new Vector3(0f, 0f, 3f), tick: 1);   // the towed
            server.ServerPublishTow(new NetId(1), towedNetId: 2, restLen: 3.3f, tick: 2);

            var full = composer.Compose(serverTick: 2, clientPlayerId: 1, Vector3.zero);
            Assert.That(applier.Apply(full, full.Length), Is.True, "full snapshot applied");
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "full round-trip parity with a tow set");

            Assert.That(client.TryGet(1u, out var tower), Is.True);
            Assert.That(tower.TowedNetId, Is.EqualTo(2u), "the tower's TowedNetId replicated");
            Assert.That(tower.TowRestLen, Is.EqualTo(3.25f).Within(0.001f), "the rest length rode the wire quantized (3.3 -> 3.25)");
            Assert.That(client.TryGet(2u, out var towedReplica), Is.True);
            Assert.That(towedReplica.TowedNetId, Is.EqualTo(0u), "the towed end carries no TowedNetId -- 'am I towed' is derived, not replicated");

            // delta: the tower detaches -> both tow fields clear, and the change rides the delta to parity
            composer.SetClientBaseline(1, 2);
            server.ServerPublishTow(new NetId(1), towedNetId: 0, restLen: 0f, tick: 3);
            var delta = composer.Compose(serverTick: 3, clientPlayerId: 1, Vector3.zero);
            Assert.That(applier.Apply(delta, delta.Length), Is.True, "delta snapshot applied");
            Assert.That(client.StateHash(), Is.EqualTo(server.StateHash()), "delta round-trip parity after the detach");
            Assert.That(client.TryGet(1u, out var detached) && detached.TowedNetId == 0u && detached.TowRestLen == 0f, Is.True,
                        "the detach cleared both tow fields on the replica");
        }

        [Test]
        public void AttachDetachTow_WireRoundTrip_FailClosed()
        {
            // B11: the two new tow COMMANDS (30 AttachTow {tower,towed}, 31 DetachTow {netId}) round-trip
            // their payload write->read intact, and TryRead FAILS CLOSED on a short buffer (a truncated /
            // hostile datagram must never yield a partially-applied command at the choke point). Teeth: if
            // AttachTowCommand.TryRead didn't bail on the second ReadUInt32, it would return true on 4 bytes
            // and the handler would act on a garbage TowedNetId; the fail-closed assert catches exactly that.
            var w = new NetPakWriter { buffer = new byte[64] };
            var r = new NetPakReader();

            // AttachTow round-trip: both NetIds survive
            var attach = new AttachTowCommand { TowerNetId = 7, TowedNetId = 42 };
            w.Reset(); attach.Write(w); w.Flush();
            r.Reset(); r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(AttachTowCommand.TryRead(r, out var readAttach), Is.True, "AttachTow round-trips");
            Assert.That(readAttach.TowerNetId, Is.EqualTo(7u));
            Assert.That(readAttach.TowedNetId, Is.EqualTo(42u));

            // DetachTow round-trip: the single NetId survives
            var detach = new DetachTowCommand { NetId = 99 };
            w.Reset(); detach.Write(w); w.Flush();
            r.Reset(); r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(DetachTowCommand.TryRead(r, out var readDetach), Is.True, "DetachTow round-trips");
            Assert.That(readDetach.NetId, Is.EqualTo(99u));

            // fail-closed: AttachTow needs TWO u32s (8 bytes); a 4-byte buffer (only the tower id) must reject
            w.Reset(); w.WriteUInt32(7u); w.Flush();
            r.Reset(); r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(AttachTowCommand.TryRead(r, out _), Is.False, "AttachTow fails closed on a short read (missing TowedNetId)");

            // fail-closed: DetachTow needs one u32 (4 bytes); an empty buffer must reject
            w.Reset(); w.Flush();
            r.Reset(); r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(DetachTowCommand.TryRead(r, out _), Is.False, "DetachTow fails closed on an empty read");
        }

        [Test]
        public void VehicleBlock_GoldenBytes()
        {
            // Locks the §3.6 vehicle wire format. An INTENTIONAL format change must bump
            // NetProtocol.Version and re-golden this constant in the same commit.
            var vehicles = new VehicleReplication();
            vehicles.ServerSpawn(new NetId(9), typeId: 5, variant: 2, new Vector3(12.5f, 1.0f, -30.25f), tick: 1);
            vehicles.ServerPublish(new NetId(9), new Vector3(12.5f, 1.0f, -30.25f), new Vector3(10f, 90f, 350f),
                new Vector3(6f, 0f, -12.5f), new Vector3(0f, 0.25f, 0f), steerDegrees: -14f,
                fuel: 1500f, health: 600f, battery: 10000f,
                flags: (byte)(VehicleReplication.FlagEngineOn | VehicleReplication.FlagTaillights), tick: 1);
            var composer = new SnapshotComposer(new List<IReplicatedSystem> { vehicles });

            var bytes = composer.Compose(serverTick: 1000, clientPlayerId: 5, Vector3.zero);
            // serverTick:32 + baselineTick:32 (0 = full) + systemId:8 (9) + byteLen:16 + one entity:
            // id:32 + type:8 + variant:8 + driver:16 + pos (11.8/9.8/11.8) + yaw/pitch/roll (11 ea)
            // + linvel/angvel (6.6 x3 ea) + steer (9) + fuel (12.1) + health (10.1) + battery (14.0) + flags:8
            // + A6: towedNetId (u32) + towRestLen (3int/4frac clamped) -- appended after flags. This golden
            // MOVED +5 bytes when A6 landed under v11 (byteLen 0x25->0x2A): not towing here, so the tail
            // gains towedNetId=0 (00000000) + restLen=0. Re-baselined to the actual (run 2026-07-20).
            Assert.That(ToHex(bytes), Is.EqualTo("E803000000000000092A00010009000000050200000C040C08103E6000E1E0F8260002130802200402ECB9DBFFFFFF020000000002"));
        }

        static ushort Driver(Harness h, uint veh)
            => h.Server.Vehicles.TryGet(veh, out var e) ? e.DriverPlayerId : (ushort)0;

        static string ToHex(byte[] buffer)
        {
            var sb = new StringBuilder(buffer.Length * 2);
            foreach (byte b in buffer) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }
}
