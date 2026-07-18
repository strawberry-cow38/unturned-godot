using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using SDG.NetPak;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Part A (CLIENT_PREDICTION_PLAN §5.2/§5.4), the L0 client-authority battery: the predicted driver's
    // VehicleStateCommand is validated at the §2.3 choke point (sender identity from the connection),
    // plausibility-bounded by the RETAIL envelope (horizontal delta cap from the spec SpeedMax with the
    // fuel-empty override, vertical 12.5/25 m/s caps -- U3 InteractableVehicle.cs:3096-3152 /
    // VehicleAsset.cs:2319-2349), then ADOPTED as the vehicle entity's truth; a violation rolls the driver
    // back via EventVehicleRecov and the server discards state packets until the RecovAck echoes the
    // counter. All deterministic MemTransport sims -- no sockets, no Godot.
    [TestFixture]
    public class VehicleStateTests
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

            /// <summary>Seed a vehicle entity the way VehicleNetSync would from a node: spec SpeedMax for
            /// the envelope, vitals published (fuel > 0) so the fuel-empty tight cap stays out of the way
            /// unless a test asks for it.</summary>
            public uint SpawnVehicle(Vector3 pos, float speedMaxMps, float fuel = 2000f)
            {
                var e = Server.Vehicles.ServerSpawn(Server.Ids.Mint(), typeId: 0, variant: 0, pos,
                                                    Server.Session.CurrentTick, speedMaxMps);
                Server.Vehicles.ServerPublishVitals(new NetId(e.NetIdValue), fuel, 600f, 10000f, false,
                                                    Server.Session.CurrentTick);
                return e.NetIdValue;
            }

            public VehicleReplication.VehicleEntity Entity(uint veh)
            {
                Server.Vehicles.TryGet(veh, out var e);
                return e;
            }

            /// <summary>Seat a client and wait for occupancy -- the state stream is only valid from the
            /// vehicle's actual driver.</summary>
            public void Seat(NetWorldClient c, uint veh)
            {
                c.SendEnterVehicle(veh);
                Assert.That(StepUntil(() => Entity(veh).DriverPlayerId == c.PlayerId), Is.True, "seated");
            }
        }

        /// <summary>One state packet with only the interesting fields varying: position + recov ack.</summary>
        static void SendState(NetWorldClient c, uint veh, Vector3 pos, byte recovAck = 0, float yaw = 0f)
            => c.SendVehicleState(veh, pos, new Vector3(0f, yaw, 0f), new Vector3(8f, 0f, 0f), Vector3.zero,
                                  steerDegrees: -10f, throttle: 1f, steer: 0.2f, handbrake: false,
                                  flags: VehicleReplication.FlagEngineOn, recovAck: recovAck);

        [Test]
        public void VehicleState_DriverAdopted_ObserverSeesDriverTruth()
        {
            var h = new Harness(9101).Connected("driver", "observer");
            var a = h.Clients[0];
            var b = h.Clients[1];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f), speedMaxMps: 12.5f);
            h.Seat(a, veh);

            // the driver's client streams a scripted 8 m/s track, one state every 2nd tick (25 Hz):
            // 0.32 m per packet -- comfortably inside the (12.5 x 0.04 x 1.25)^2 = 0.625^2 envelope
            var pos = new Vector3(1f, 0f, 0f);
            int parity = 0;
            h.Step(100, () =>
            {
                pos.x += 8f * 0.02f;
                if ((parity++ & 1) == 0) SendState(a, veh, pos, yaw: 90f);
            });

            var e = h.Entity(veh);
            Assert.That((e.Pos - PlayerReplication.Quantize(pos)).magnitude, Is.LessThan(0.4f),
                        "the entity ADOPTED the driver's track (observers dead-reckon off the driver's truth)");
            Assert.That(e.YawDegrees, Is.InRange(89f, 91f), "reported rotation adopted");
            Assert.That(e.LinVel.x, Is.InRange(7.5f, 8.5f), "reported velocity adopted (observer dead-reckon input)");
            Assert.That(e.SteerSigned, Is.InRange(-11f, -9f), "reported steer summary adopted");
            Assert.That((e.Flags & VehicleReplication.FlagEngineOn) != 0, "reported dressing flags adopted");
            Assert.That(h.Server.VehicleHost.IsPredictedDriven(veh), Is.True, "predicted mode latched");

            // the driver's player entity rides the adopted vehicle truth (ServerVehicles.Step teleport)
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var rider), Is.True);
            Assert.That((rider.Pos - e.Pos).magnitude, Is.LessThan(0.05f), "driver avatar rides the adopted entity");

            // the observer's replica mirrors the adopted truth to exact StateHash parity
            Assert.That(h.StepUntil(() => b.Vehicles.StateHash() == h.Server.Vehicles.StateHash()), Is.True,
                        "observer replica reached StateHash parity with the driver-adopted state");
            Assert.That(b.Vehicles.TryGet(veh, out var replica), Is.True);
            Assert.That((replica.Pos - e.Pos).magnitude, Is.LessThan(0.01f), "observer sees the driver's track");
        }

        [Test]
        public void VehicleState_TeleportBeyondEnvelope_TriggersRecov_AndFreezeUntilAck()
        {
            var h = new Harness(9102).Connected("driver");
            var a = h.Clients[0];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f), speedMaxMps: 12.5f);
            h.Seat(a, veh);

            var recovs = new List<VehicleRecovEvent>();
            a.VehicleRecov += e => recovs.Add(e);

            // a few good packets latch predicted mode + a last-good baseline
            var pos = new Vector3(1f, 0f, 0f);
            int parity = 0;
            h.Step(10, () => { pos.x += 0.16f; if ((parity++ & 1) == 0) SendState(a, veh, pos); });
            var lastGood = h.Entity(veh).Pos;
            Assert.That(h.Server.VehicleHost.IsPredictedDriven(veh), Is.True, "predicted latched on good packets");

            // THE TEETH: a 50 m horizontal jump -- far past cap = SpeedMax x 0.5 s x 1.25 = 7.8 m even at
            // the clamped max interval. Without the envelope this position would be adopted verbatim.
            var far = lastGood + new Vector3(50f, 0f, 0f);
            SendState(a, veh, far);
            Assert.That(h.StepUntil(() => recovs.Count > 0, 50), Is.True, "the jump triggered a recov event");
            Assert.That(recovs[0].RecovCounter, Is.EqualTo(1), "counter incremented");
            Assert.That((recovs[0].Pos - lastGood).magnitude, Is.LessThan(0.01f), "recov carries the LAST-GOOD state");
            Assert.That((h.Entity(veh).Pos - lastGood).magnitude, Is.LessThan(0.01f), "the entity kept the last-good (never adopted the jump)");

            // stale-RecovAck packets (the client still driving its rolled-back-in-flight position) are DISCARDED
            h.Step(10, () => SendState(a, veh, far, recovAck: 0));
            Assert.That((h.Entity(veh).Pos - lastGood).magnitude, Is.LessThan(0.01f),
                        "stale-ack far states discarded -- server keeps publishing last-good");

            // the echo lands (client teleported back, resumes from last-good) -> adoption resumes
            var resume = lastGood;
            SendState(a, veh, resume, recovAck: 1);
            h.Step(5);
            int par2 = 0;
            h.Step(30, () => { resume.x += 0.16f; if ((par2++ & 1) == 0) SendState(a, veh, resume, recovAck: 1); });
            Assert.That((h.Entity(veh).Pos - PlayerReplication.Quantize(resume)).magnitude, Is.LessThan(0.4f),
                        "after the echo the driver's stream is adopted again");
            Assert.That(recovs.Count, Is.EqualTo(1), "no spurious extra recovs after recovery");
        }

        [Test]
        public void VehicleState_VerticalSpeedCap_Rejected()
        {
            var h = new Harness(9103).Connected("driver");
            var a = h.Clients[0];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f), speedMaxMps: 12.5f);
            h.Seat(a, veh);

            var recovs = new List<VehicleRecovEvent>();
            a.VehicleRecov += e => recovs.Add(e);

            var pos = new Vector3(1f, 0f, 0f);
            int parity = 0;
            h.Step(10, () => { pos.x += 0.16f; if ((parity++ & 1) == 0) SendState(a, veh, pos); });
            var baseline = h.Entity(veh);
            float baseY = baseline.Pos.y;

            // legal climb first: +0.4 m over one 2-tick packet = 10 m/s < validSpeedUp 12.5 -> adopted
            var legalUp = new Vector3(pos.x, baseY + 0.4f, 0f);
            SendState(a, veh, legalUp);
            h.Step(5);
            Assert.That(h.Entity(veh).Pos.y, Is.InRange(baseY + 0.3f, baseY + 0.5f), "a 10 m/s climb is inside the cap");
            Assert.That(recovs.Count, Is.EqualTo(0), "no recov on legal vertical motion");

            // +2 m in one packet = 50 m/s climb > 12.5 -> recov (U3 validSpeedUp, VehicleAsset.cs:2336-2349)
            float goodY = h.Entity(veh).Pos.y;
            SendState(a, veh, new Vector3(pos.x, goodY + 2f, 0f));
            Assert.That(h.StepUntil(() => recovs.Count == 1, 50), Is.True, "the 50 m/s climb triggered recov");
            Assert.That(h.Entity(veh).Pos.y, Is.InRange(goodY - 0.01f, goodY + 0.01f), "entity Y kept last-good");

            // resume with the echo, then a -6 m drop in one packet: even at the few ticks this rig lets
            // elapse (dt <= ~0.14 s) that is > 40 m/s of fall, well past validSpeedDown 25 -> recov.
            // (A -1.9 m drop was legal here: the envelope divides by REAL elapsed time, retail :3141.)
            SendState(a, veh, new Vector3(pos.x, goodY, 0f), recovAck: 1);
            h.Step(5);
            SendState(a, veh, new Vector3(pos.x, goodY - 6f, 0f), recovAck: 1);
            Assert.That(h.StepUntil(() => recovs.Count == 2, 50), Is.True, "the fast fall triggered recov (down cap 25)");
            Assert.That(h.Entity(veh).Pos.y, Is.InRange(goodY - 0.01f, goodY + 0.01f), "entity Y still last-good after the fall reject");
        }

        [Test]
        public void VehicleState_NonDriver_Rejected()
        {
            var h = new Harness(9104).Connected("driver", "spoofer");
            var a = h.Clients[0];
            var b = h.Clients[1];
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f), speedMaxMps: 12.5f);
            h.Seat(a, veh);
            var before = h.Entity(veh).Pos;

            // the §2.3 choke-point rule: sender identity comes from the CONNECTION -- a non-driver's state
            // for someone else's vehicle never reaches the envelope, let alone adoption
            long rejectedBefore = h.Server.Commands.Diag.ValidationRejected;
            h.Step(10, () => SendState(b, veh, before + new Vector3(0.2f, 0f, 0f)));
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejectedBefore),
                        "the spoofed state was rejected at validation");
            Assert.That((h.Entity(veh).Pos - before).magnitude, Is.LessThan(0.001f), "entity untouched by the spoofer");
            Assert.That(h.Server.VehicleHost.IsPredictedDriven(veh), Is.False, "no predicted latch from a spoofer");
        }

        [Test]
        public void VehicleState_FuelEmpty_TightCap()
        {
            var h = new Harness(9105).Connected("driver");
            var a = h.Clients[0];
            // roadster pace: SpeedMax 19 -> normal per-packet cap 19 x 0.04 x 1.25 = 0.95 m
            uint veh = h.SpawnVehicle(new Vector3(1f, 0f, 0f), speedMaxMps: 19f);
            h.Seat(a, veh);

            var recovs = new List<VehicleRecovEvent>();
            a.VehicleRecov += e => recovs.Add(e);

            // control: with fuel, a 0.8 m step (> sqrt(0.5) = 0.71, < 0.95) is inside the envelope
            var pos = new Vector3(1f, 0f, 0f);
            SendState(a, veh, pos);
            h.Step(2);
            pos.x += 0.8f;
            SendState(a, veh, pos);
            h.Step(5);
            Assert.That(h.Entity(veh).Pos.x, Is.InRange(pos.x - 0.05f, pos.x + 0.05f), "with fuel the 0.8 m step adopts");
            Assert.That(recovs.Count, Is.EqualTo(0));

            // fuel runs dry (server truth via vitals) -> retail's 0.5f SQUARED-metres override
            // (U3 InteractableVehicle.cs:3096): the same 0.8 m step (0.64 > 0.5) now violates
            h.Server.Vehicles.ServerPublishVitals(new NetId(veh), 0f, 600f, 10000f, false, h.Server.Session.CurrentTick);
            h.Step(2);
            pos.x += 0.8f;
            SendState(a, veh, pos);
            Assert.That(h.StepUntil(() => recovs.Count == 1, 50), Is.True, "fuel-empty tightened the cap -- 0.8 m now recovs");
        }

        [Test]
        public void VehicleState_WireRoundTrip_GoldenBytes()
        {
            // Locks the Version 6 VehicleStateCommand layout. An INTENTIONAL change must bump
            // NetProtocol.Version and re-golden this constant in the same commit.
            var cmd = new VehicleStateCommand
            {
                Seq = 0x0102, NetId = 0xAABB01u, RecovAck = 3,
                Pos = new Vector3(12.5f, 1.0f, -30.25f),
                YawDegrees = 90f, PitchDegrees = 10f, RollDegrees = 350f,
                LinVel = new Vector3(6f, 0f, -12.5f), AngVel = new Vector3(0f, 0.25f, 0f),
                SteerDegrees = -14f, Throttle = 1f, Steer = -0.5f, Handbrake = true,
                Flags = (byte)(VehicleReplication.FlagEngineOn | VehicleReplication.FlagBraking),
            };
            byte[] packed = NetMessagePak.Pack(ReplicationIds.CommandVehicleState, cmd.Write);
            Assert.That(ToHex(packed), Is.EqualTo("1A020101BBAA00030C040C08103E6000E1E0F8260002130802200402ECFF804700"));

            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(packed, packed.Length);
            r.ReadUInt8(out byte id);
            Assert.That(id, Is.EqualTo(ReplicationIds.CommandVehicleState));
            Assert.That(VehicleStateCommand.TryRead(r, out var read), Is.True);
            Assert.That(read.Seq, Is.EqualTo(cmd.Seq));
            Assert.That(read.NetId, Is.EqualTo(cmd.NetId));
            Assert.That(read.RecovAck, Is.EqualTo(cmd.RecovAck));
            Assert.That((read.Pos - cmd.Pos).magnitude, Is.LessThan(0.01f), "position survives the grid");
            Assert.That(read.YawDegrees, Is.InRange(89.5f, 90.5f));
            Assert.That(read.RollDegrees, Is.InRange(349.5f, 350.5f));
            Assert.That((read.LinVel - cmd.LinVel).magnitude, Is.LessThan(0.05f));
            Assert.That(read.SteerDegrees > 180f ? read.SteerDegrees - 360f : read.SteerDegrees, Is.InRange(-14.5f, -13.5f));
            Assert.That(read.Throttle, Is.GreaterThan(0.95f));
            Assert.That(read.Steer, Is.InRange(-0.55f, -0.45f));
            Assert.That(read.Handbrake, Is.True);
            Assert.That(read.Flags, Is.EqualTo(cmd.Flags));
        }

        [Test]
        public void Recov_WireRoundTrip_GoldenBytes()
        {
            var evt = new VehicleRecovEvent
            {
                NetId = 0xDEAD02u,
                Pos = new Vector3(100.5f, 12.25f, -220f),
                YawDegrees = 180f, PitchDegrees = 5f, RollDegrees = 355f,
                LinVel = new Vector3(3f, -0.5f, 12f),
                RecovCounter = 7,
            };
            byte[] packed = NetMessagePak.Pack(ReplicationIds.EventVehicleRecov, evt.Write);
            Assert.That(ToHex(packed), Is.EqualTo("1D02ADDE0064046408443200007260FC23F0812C7000"));

            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(packed, packed.Length);
            r.ReadUInt8(out byte id);
            Assert.That(id, Is.EqualTo(ReplicationIds.EventVehicleRecov));
            Assert.That(VehicleRecovEvent.TryRead(r, out var read), Is.True);
            Assert.That(read.NetId, Is.EqualTo(evt.NetId));
            Assert.That((read.Pos - evt.Pos).magnitude, Is.LessThan(0.01f));
            Assert.That(read.YawDegrees, Is.InRange(179.5f, 180.5f));
            Assert.That(read.PitchDegrees, Is.InRange(4.5f, 5.5f));
            Assert.That((read.LinVel - evt.LinVel).magnitude, Is.LessThan(0.05f));
            Assert.That(read.RecovCounter, Is.EqualTo(7));
        }

        static string ToHex(byte[] buffer)
        {
            var sb = new StringBuilder(buffer.Length * 2);
            foreach (byte b in buffer) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }
}
