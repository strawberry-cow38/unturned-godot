using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using SDG.NetPak;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // mp-clientauth-foot (wire v9), the L0 on-foot client-authority battery -- the VehicleStateTests
    // shape for walkers: the owner's PlayerStateCommand is validated at the §2.3 choke point (sender
    // identity from the connection; corpses and seated drivers drop), plausibility-bounded by the
    // on-foot envelope (sprint-speed horizontal cap with the 1.25 slack, ValidSpeedUp/Down vertical
    // caps), then ADOPTED via ServerDrive as the entity's truth; a violation rolls the owner back via
    // EventPlayerRecov and the server discards claims until the RecovAck echoes the counter.
    // All deterministic MemTransport sims -- no sockets, no Godot.
    [TestFixture]
    public class PlayerAuthorityTests
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

            public PlayerReplication.PlayerEntity Entity(NetWorldClient c)
            {
                Server.Players.TryGetByOwner(c.PlayerId, out var e);
                return e;
            }
        }

        /// <summary>One walk claim with only the interesting fields varying.</summary>
        static ushort SendState(NetWorldClient c, Vector3 pos, byte recovAck = 0, float yaw = 90f,
                                EPlayerStance stance = EPlayerStance.SPRINT, Vector3? vel = null)
            => c.SendPlayerState(pos, yaw, pitchDegrees: -5f, vel ?? new Vector3(0f, 0f, 7f),
                                 MoveInput.PackStance(stance), grounded: true, recovAck: recovAck);

        [Test]
        public void PlayerState_Adopted_EntityMirrorsClaim_BitExact()
        {
            var h = new Harness(9201).Connected("owner", "observer");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var spawn = h.Entity(a).Pos;

            // the owner streams a sprint-speed track (7 m/s x 0.02 = 0.14 m per tick, one claim per
            // tick -- the shell session's cadence); every claim is inside the envelope and adopts
            var pos = spawn;
            ushort lastSeq = 0;
            h.Step(100, () => { pos.x += 7f * 0.02f; lastSeq = SendState(a, pos); });

            var e = h.Entity(a);
            Assert.That(e.IsExternallyDriven, Is.True, "adoption marked the entity externally driven (ServerStep skips it)");
            Assert.That(h.Server.PlayerHost.IsClientDriven(a.PlayerId), Is.True, "client-driven latched");
            // BIT-EXACT: the published entity position IS the quantized claim (the claim rides the same
            // grid as the snapshot, so the round trip is exact equality, not a tolerance)
            Assert.That(e.Pos, Is.EqualTo(PlayerReplication.Quantize(pos)), "entity == the quantized claim, bit-exact");
            Assert.That(e.YawDegrees, Is.EqualTo(NetQuantization.QuantizeDegrees(90f, NetQuantization.YawBits)), "claimed yaw adopted");
            Assert.That(e.LastProcessedInputSeq, Is.EqualTo(lastSeq), "the adopted claim's seq is stamped");

            // the raw adopted state carries the dressing the follower body needs
            Assert.That(h.Server.PlayerHost.TryGetDrivenState(a.PlayerId, out var st), Is.True);
            Assert.That(st.Stance, Is.EqualTo(EPlayerStance.SPRINT), "claimed stance stored (hitbox/stealth dressing)");
            Assert.That(st.Vel.z, Is.InRange(6.9f, 7.1f), "claimed velocity stored (recov re-seed)");
            Assert.That(st.Grounded, Is.True);

            // the observer's replica mirrors the adopted truth to exact StateHash parity
            Assert.That(h.StepUntil(() => b.Players.StateHash() == h.Server.Players.StateHash()), Is.True,
                        "observer replica reached StateHash parity with the owner-adopted state");
            Assert.That(b.Players.TryGetByOwner(a.PlayerId, out var replica), Is.True);
            Assert.That(replica.Pos, Is.EqualTo(e.Pos), "observer sees the owner's claim bit-exact");
        }

        [Test]
        public void PlayerState_SpeedHack_Recov_Rubberband_AndResume()
        {
            var h = new Harness(9202).Connected("owner");
            var a = h.Clients[0];
            var spawn = h.Entity(a).Pos;

            var recovs = new List<PlayerRecovEvent>();
            a.PlayerRecov += e => recovs.Add(e);

            // a few good claims latch client-driven + a last-good baseline
            var pos = spawn;
            h.Step(10, () => { pos.x += 0.14f; SendState(a, pos); });
            var lastGood = h.Entity(a).Pos;
            Assert.That(h.Server.PlayerHost.IsClientDriven(a.PlayerId), Is.True, "driven latched on good claims");

            // THE TEETH: a 30 m horizontal jump -- past even the full 8.75 m bank ceiling, let alone the
            // flowing-stream reserve. Without the envelope this position would be adopted verbatim.
            var far = lastGood + new Vector3(30f, 0f, 0f);
            SendState(a, far);
            Assert.That(h.StepUntil(() => recovs.Count > 0, 50), Is.True, "the jump triggered a recov event");
            Assert.That(recovs[0].RecovCounter, Is.EqualTo(1), "counter incremented");
            Assert.That((recovs[0].Pos - lastGood).magnitude, Is.LessThan(0.01f), "recov carries the LAST-GOOD position");
            Assert.That(h.Entity(a).Pos, Is.EqualTo(lastGood), "the entity kept the last-good (never adopted the jump)");

            // stale-RecovAck claims (the client still walking its rolled-back-in-flight position) are DISCARDED
            h.Step(10, () => SendState(a, far, recovAck: 0));
            Assert.That(h.Entity(a).Pos, Is.EqualTo(lastGood), "stale-ack far claims discarded -- last-good published");

            // the echo lands (client teleported back, resumes from last-good) -> adoption resumes
            var resume = lastGood;
            SendState(a, resume, recovAck: 1);
            h.Step(5);
            h.Step(30, () => { resume.x += 0.14f; SendState(a, resume, recovAck: 1); });
            Assert.That(h.Entity(a).Pos, Is.EqualTo(PlayerReplication.Quantize(resume)), "after the echo the stream adopts again");
            Assert.That(recovs.Count, Is.EqualTo(1), "no spurious extra recovs after recovery");
        }

        [Test]
        public void PlayerState_SpeedHack_Teeth_EnvelopeDisabledAdoptsVerbatim()
        {
            // proof the ENVELOPE (not luck/ordering) is what rejects the hack: same jump, envelope off ->
            // adopted verbatim. This is the assertion set the recov test flips.
            var h = new Harness(9203).Connected("owner");
            var a = h.Clients[0];
            h.Server.PlayerHost.DisableEnvelope = true;
            var recovs = new List<PlayerRecovEvent>();
            a.PlayerRecov += e => recovs.Add(e);

            var pos = h.Entity(a).Pos;
            h.Step(10, () => { pos.x += 0.14f; SendState(a, pos); });
            var far = h.Entity(a).Pos + new Vector3(30f, 0f, 0f);
            SendState(a, far);
            Assert.That(h.StepUntil(() => h.Entity(a).Pos == PlayerReplication.Quantize(far), 50), Is.True,
                        "with the envelope disabled the 30 m jump ADOPTS -- the envelope is the whole defence");
            Assert.That(recovs.Count, Is.EqualTo(0), "and no recov fires");
        }

        [Test]
        public void PlayerState_HighPing_WithinHeadroom_NeverTrips()
        {
            // THE KEY NON-REGRESSION: a legit high-ping/jittery/lossy stream at FULL sprint must never
            // false-trip the envelope -- rubber-banding honest players is the exact disease this branch
            // cures. Claims arrive in bursts and with holes (only every Nth claim survives), so accepted
            // deltas span multiple intervals; the elapsed-scaled cap + 1.25 slack must absorb all of it.
            var h = new Harness(9204).Connected("owner");
            var a = h.Clients[0];
            var recovs = new List<PlayerRecovEvent>();
            a.PlayerRecov += e => recovs.Add(e);

            var pos = h.Entity(a).Pos;
            int tick = 0;
            // pattern: full sprint, but the client's claims only reach the server every 7th tick
            // (~140 ms between accepted claims -- worse than strawberry's real WAN), for 60 s simulated
            h.Step(3000, () =>
            {
                pos.x += 7f * 0.02f;           // the shell moved at sprint EVERY tick regardless
                if (++tick % 7 == 0) SendState(a, pos);
            });
            Assert.That(recovs.Count, Is.EqualTo(0), "a legit sprint over a 140 ms-gap stream NEVER trips the envelope");
            Assert.That((h.Entity(a).Pos - PlayerReplication.Quantize(pos)).magnitude, Is.LessThan(1.5f),
                        "the entity tracks the sprint (lagging at most the claim gap)");

            // ...and a full 1-second hitch at sprint (the EnvelopeMaxTicks window) also survives
            h.Step(50, () => pos.x += 7f * 0.02f);   // 50 silent ticks: the shell moved 7 m
            SendState(a, pos);
            h.Step(5);
            Assert.That(recovs.Count, Is.EqualTo(0), "a 1 s hitch at full sprint resumes without a rubber-band");
            Assert.That(h.Entity(a).Pos, Is.EqualTo(PlayerReplication.Quantize(pos)), "the post-hitch claim adopted");
        }

        [Test]
        public void PlayerState_Fly_Rejected_JumpArcAdopted()
        {
            var h = new Harness(9205).Connected("owner");
            var a = h.Clients[0];
            var recovs = new List<PlayerRecovEvent>();
            a.PlayerRecov += e => recovs.Add(e);

            var pos = h.Entity(a).Pos;
            h.Step(10, () => { pos.x += 0.14f; SendState(a, pos); });

            // a legal jump arc: takeoff at JUMP = 7 m/s, 0.14 m/tick climb -- well under ValidSpeedUp
            float vy = PlayerMovementDef.JUMP;
            h.Step(20, () =>
            {
                pos.y += vy * 0.02f;
                vy -= PlayerMovementDef.GRAVITY * 0.02f;
                SendState(a, pos, vel: new Vector3(0f, vy, 0f));
            });
            Assert.That(recovs.Count, Is.EqualTo(0), "a real jump arc never trips the vertical cap");
            Assert.That(h.Entity(a).Pos.y, Is.GreaterThan(pos.y - 0.05f).And.LessThan(pos.y + 0.05f), "the arc adopted");

            // the FLY hack: +3 m of instant climb with claims flowing -- over the UpReserve 2 m burst pin
            float goodY = h.Entity(a).Pos.y;
            SendState(a, new Vector3(pos.x, goodY + 3f, pos.z));
            Assert.That(h.StepUntil(() => recovs.Count == 1, 50), Is.True, "the fly claim triggered recov");
            Assert.That(h.Entity(a).Pos.y, Is.InRange(goodY - 0.01f, goodY + 0.01f), "entity Y kept last-good");

            // resume, then a -40 m instant drop with claims flowing: over the DownReserve 22 m burst pin
            // (after a real 1 s silence the same drop would bank legal -- terminal falls are fast)
            SendState(a, new Vector3(pos.x, goodY, pos.z), recovAck: 1);
            h.Step(5);
            SendState(a, new Vector3(pos.x, goodY - 40f, pos.z), recovAck: 1);
            Assert.That(h.StepUntil(() => recovs.Count == 2, 50), Is.True, "the instant-drop teleport triggered recov (down cap)");
        }

        [Test]
        public void PlayerState_Teleport_Rejected()
        {
            // the small-but-instant teleport: 6 m sideways with claims FLOWING every tick -- the bank is
            // pinned at the jitter reserve (1.75 m) by the flowing accepts, so 6 m is a violation even
            // though it would be LEGAL after a 1 s arrival silence (the bank ceiling). Silence banks
            // allowance; a flowing stream never does.
            var h = new Harness(9206).Connected("owner");
            var a = h.Clients[0];
            var recovs = new List<PlayerRecovEvent>();
            a.PlayerRecov += e => recovs.Add(e);

            var pos = h.Entity(a).Pos;
            h.Step(10, () => { pos.x += 0.14f; SendState(a, pos); });
            var lastGood = h.Entity(a).Pos;

            SendState(a, pos + new Vector3(6f, 0f, 0f));
            Assert.That(h.StepUntil(() => recovs.Count == 1, 50), Is.True, "the 6 m blink triggered recov");
            Assert.That(h.Entity(a).Pos, Is.EqualTo(lastGood), "entity kept last-good");
        }

        [Test]
        public void PlayerState_DeadOrSeatedSender_RejectedAtChokePoint()
        {
            var h = new Harness(9207).Connected("owner");
            var a = h.Clients[0];
            var pos = h.Entity(a).Pos;
            h.Step(10, () => { pos.x += 0.14f; SendState(a, pos); });

            // seated: the seat teleport owns the entity (§3.6) -- walk claims drop at validation
            var veh = h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(), 0, 0, h.Entity(a).Pos,
                                                    h.Server.Session.CurrentTick, speedMaxMps: 12.5f);
            a.SendEnterVehicle(veh.NetIdValue);
            Assert.That(h.StepUntil(() => veh.DriverPlayerId == a.PlayerId), Is.True, "seated");
            long rejectedBefore = h.Server.Commands.Diag.ValidationRejected;
            h.Step(10, () => SendState(a, pos + new Vector3(1f, 0f, 0f)));
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejectedBefore),
                        "a seated sender's walk claims are rejected at the choke point");

            // dead: a corpse must not stream itself around
            a.SendExitVehicle();
            Assert.That(h.StepUntil(() => veh.DriverPlayerId == 0), Is.True, "exited");
            Assert.That(h.Server.CombatState.TryGet(a.PlayerId, out var ce), Is.True);
            ce.Alive = false;
            h.Server.CombatState.MarkDirty(ce, h.Server.Session.CurrentTick);
            long rejectedBefore2 = h.Server.Commands.Diag.ValidationRejected;
            h.Step(10, () => SendState(a, pos + new Vector3(1f, 0f, 0f)));
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejectedBefore2),
                        "a dead sender's walk claims are rejected at the choke point");
        }

        [Test]
        public void PlayerState_ServerTeleport_RecovsClientToTarget()
        {
            // the server can still MOVE a client-driven player (console teleport, respawn): ServerTeleport
            // relocates the entity, the owner's next stale-position claim violates the envelope, and the
            // recov payload carries the TELEPORT TARGET -- the rubber-band IS the teleport delivery.
            var h = new Harness(9208).Connected("owner");
            var a = h.Clients[0];
            var recovs = new List<PlayerRecovEvent>();
            a.PlayerRecov += e => recovs.Add(e);

            var pos = h.Entity(a).Pos;
            h.Step(10, () => { pos.x += 0.14f; SendState(a, pos); });

            var target = pos + new Vector3(40f, 0f, 25f);
            h.Server.Players.ServerTeleport(a.PlayerId, target, h.Server.Session.CurrentTick);
            h.Step(1, () => SendState(a, pos));   // the client, unaware, keeps claiming its old spot
            Assert.That(h.StepUntil(() => recovs.Count == 1, 50), Is.True, "the stale claim tripped the envelope");
            Assert.That((recovs[0].Pos - PlayerReplication.Quantize(target)).magnitude, Is.LessThan(0.01f),
                        "the recov payload IS the teleport target -- the client lands where the server sent it");

            // the client acks from the target; the stream resumes there
            var resumed = PlayerReplication.Quantize(target);
            h.Step(20, () => { resumed.x += 0.14f; SendState(a, resumed, recovAck: 1); });
            Assert.That((h.Entity(a).Pos - resumed).magnitude, Is.LessThan(0.01f), "post-teleport stream adopted from the target");
        }

        [Test]
        public void PlayerState_WireRoundTrip_GoldenBytes()
        {
            // Locks the Version 10 PlayerStateCommand layout (mp-event-coalesce: the trailing EventCount
            // byte for the redundant combat carry -- 0 here = no events). An INTENTIONAL change must bump
            // NetProtocol.Version and re-golden this constant in the same commit.
            var cmd = new PlayerStateCommand
            {
                Seq = 0x0102, RecovAck = 3,
                Pos = new Vector3(12.5f, 1.0f, -30.25f),
                YawDegrees = 90f, PitchDegrees = -15f,
                LinVel = new Vector3(0f, -2.5f, 7f),
                Buttons = (byte)(MoveInput.ButtonJump | MoveInput.PackStance(EPlayerStance.SPRINT)),
                Grounded = true,
            };
            byte[] packed = NetMessagePak.Pack(ReplicationIds.CommandPlayerState, cmd.Write);
            Assert.That(ToHex(packed), Is.EqualTo(GoldenStateHex), "PlayerStateCommand golden bytes (v10)");

            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(packed, packed.Length);
            r.ReadUInt8(out byte id);
            Assert.That(id, Is.EqualTo(ReplicationIds.CommandPlayerState));
            Assert.That(PlayerStateCommand.TryRead(r, out var read), Is.True);
            Assert.That(read.Seq, Is.EqualTo(cmd.Seq));
            Assert.That(read.RecovAck, Is.EqualTo(cmd.RecovAck));
            Assert.That(read.Pos, Is.EqualTo(PlayerReplication.Quantize(cmd.Pos)), "position survives the grid exactly");
            Assert.That(read.YawDegrees, Is.InRange(89.5f, 90.5f));
            Assert.That(read.PitchDegrees > 180f ? read.PitchDegrees - 360f : read.PitchDegrees, Is.InRange(-15.5f, -14.5f));
            Assert.That((read.LinVel - cmd.LinVel).magnitude, Is.LessThan(0.05f));
            Assert.That(read.Jump, Is.True);
            Assert.That(read.Stance, Is.EqualTo(EPlayerStance.SPRINT));
            Assert.That(read.Grounded, Is.True);
        }

        [Test]
        public void PlayerRecov_WireRoundTrip_GoldenBytes()
        {
            var evt = new PlayerRecovEvent
            {
                Pos = new Vector3(100.5f, 12.25f, -220f),
                Vel = new Vector3(3f, -0.5f, 12f),
                RecovCounter = 7,
            };
            byte[] packed = NetMessagePak.Pack(ReplicationIds.EventPlayerRecov, evt.Write);
            Assert.That(ToHex(packed), Is.EqualTo(GoldenRecovHex), "PlayerRecovEvent golden bytes (v9)");

            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(packed, packed.Length);
            r.ReadUInt8(out byte id);
            Assert.That(id, Is.EqualTo(ReplicationIds.EventPlayerRecov));
            Assert.That(PlayerRecovEvent.TryRead(r, out var read), Is.True);
            Assert.That(read.Pos, Is.EqualTo(PlayerReplication.Quantize(evt.Pos)));
            Assert.That((read.Vel - evt.Vel).magnitude, Is.LessThan(0.05f));
            Assert.That(read.RecovCounter, Is.EqualTo(7));
        }

        // goldened on first landing (v9); v10 (mp-event-coalesce) appends the EventCount=0 byte
        const string GoldenStateHex = "1B0201030C040C08103E6000A91E043AF004060200";
        const string GoldenRecovHex = "1F6404640844328011F840163800";

        static string ToHex(byte[] buffer)
        {
            var sb = new StringBuilder(buffer.Length * 2);
            foreach (byte b in buffer) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }
}
