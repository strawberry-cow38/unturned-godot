using NUnit.Framework;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Regression battery for the "driven vehicle frozen on the DRIVER's own client" wedge
    // (docs/DRIVE_DRIVER_VIEW_ROOTCAUSE.md): a >=64-tick gap in ONE client's snapshot acks flips that
    // client to FULL snapshots on the unreliable channel (1187-byte budget) -- but at PEI scale the full
    // vehicles block (~85 x ~35 B) can never fit that datagram, so it is budget-skipped on every compose,
    // its per-system baseline can never advance (advancing requires acking a snapshot that CONTAINED the
    // block), and WillSendFull stays latched forever. That client's vehicle replica silently freezes at
    // wedge-time state while every other client keeps tracking. The fix: a starvation-recovery full must
    // ride the RELIABLE channel with the join-path budget (NetWorldHost.TickReplication), sent once per
    // wedge, holding the unreliable stream until the client acks past it -- exactly the join mechanism.
    [TestFixture]
    public class SnapshotRecoveryTests
    {
        // 85 = the live PEI drivable-vehicle count; the full vehicles block is ~2.9 KB, ~2.5x the
        // 1187-byte unreliable budget (NetProtocol.MaxUnreliablePayload), so it can NEVER ride an
        // unreliable full -- the wedge's precondition.
        const int VehicleCount = 85;
        const float Speed = 12.5f;             // m/s -- a car at speed, 0.25 m per 50 Hz tick
        const int GapTicks = 80;               // > DirtyRingDepthTicks (64) -> flips the client to full mode

        sealed class Harness
        {
            public readonly MemNetwork Net;
            public readonly NetWorldServer Server;
            public NetWorldClient Driver;
            public NetWorldClient Observer;
            public System.Action<long> PerTickPublish;   // runs after TickSimulation, before TickReplication (the VehicleNetSync slot)

            public Harness(int seed)
            {
                Net = new MemNetwork(seed);
                Server = new NetWorldServer(new MemServerTransport(Net));
            }

            /// <summary>One 50 Hz tick. tickDriver=false = the driver client's process is hitched (no
            /// receive, no apply, no ack -- the frame stall / WAN gap of the root-cause doc §1.3); the
            /// server and the observer keep running at wall speed.</summary>
            public void Step(bool tickDriver = true)
            {
                Net.Tick();
                if (tickDriver) Driver?.Tick();
                Observer?.Tick();
                Server.TickSimulation();
                PerTickPublish?.Invoke(Server.Session.CurrentTick);
                Server.TickReplication();
            }

            public void Step(int ticks, bool tickDriver = true)
            {
                for (int i = 0; i < ticks; i++) Step(tickDriver);
            }

            public void Connect()
            {
                Driver = new NetWorldClient(new MemClientTransport(Net), "driver");
                Observer = new NetWorldClient(new MemClientTransport(Net), "observer");
                Driver.Connect();
                Observer.Connect();
                Step(25);
                Assert.That(Driver.State, Is.EqualTo(NetSessionState.Connected), $"driver connected (seed={Net.Seed})");
                Assert.That(Observer.State, Is.EqualTo(NetSessionState.Connected), $"observer connected (seed={Net.Seed})");
            }
        }

        [Test]
        public void AckGap_OversizedVehiclesBlock_DriverReplicaRecovers()
        {
            var h = new Harness(9101);

            // the world exists BEFORE anyone joins (the dedicated server's boot order): all 85 vehicles
            // ride each client's reliable join snapshot, then delta mode carries the one moving car.
            uint moving = 0;
            for (int i = 0; i < VehicleCount; i++)
            {
                var e = h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(), typeId: 0, variant: 0,
                                                      new Vector3(i * 6f, 0f, -30f), h.Server.Session.CurrentTick);
                if (i == 0) moving = e.NetIdValue;
            }
            float x = 0f;
            h.PerTickPublish = tick =>
            {
                x += Speed * 0.02f;
                h.Server.Vehicles.ServerPublish(new NetId(moving), new Vector3(x, 0f, -30f), Vector3.zero,
                    new Vector3(Speed, 0f, 0f), Vector3.zero, 0f, 0f, 0f, 0f,
                    VehicleReplication.FlagEngineOn, tick);
            };

            h.Connect();

            // steady state: both clients in delta mode, both replicas track the moving car
            h.Step(100);
            Assert.That(TrackError(h.Driver, h, moving), Is.LessThan(2.5f), "pre-gap: driver tracks the moving car");
            Assert.That(TrackError(h.Observer, h, moving), Is.LessThan(2.5f), "pre-gap: observer tracks the moving car");
            long joinFullsBefore = h.Driver.JoinSnapshotsApplied;

            // the hitch: the driver's client stalls past the 64-tick dirty ring while the car drives on
            h.Step(GapTicks, tickDriver: false);

            // acks resume every tick -- pre-fix the wedge is already latched and nothing ever clears it
            h.Step(250);

            h.Server.Vehicles.TryGet(moving, out var serverCar);
            h.Driver.Vehicles.TryGet(moving, out var driverCar);
            h.Observer.Vehicles.TryGet(moving, out var observerCar);
            float driverErr = driverCar != null ? (driverCar.Pos - serverCar.Pos).magnitude : float.MaxValue;
            float observerErr = observerCar != null ? (observerCar.Pos - serverCar.Pos).magnitude : float.MaxValue;
            var diag = h.Server.Composer.Diag;
            Assert.Multiple(() =>
            {
                Assert.That(observerErr, Is.LessThan(2.5f),
                    $"control: the never-gapped observer tracked throughout (err {observerErr:0.00} m)");
                Assert.That(driverErr, Is.LessThan(2.5f),
                    $"the gapped driver's replica RECOVERED after acks resumed (err {driverErr:0.00} m -- server x {serverCar.Pos.x:0.0} vs replica x {(driverCar != null ? driverCar.Pos.x : float.NaN):0.0}; " +
                    $"composer: fulls {diag.FullSnapshotsComposed}, deltas {diag.DeltaSnapshotsComposed}, oversized skips {diag.OversizedBlocksSkipped})");
                Assert.That(h.Server.Composer.WillSendFull(h.Driver.PlayerId, h.Server.Session.CurrentTick), Is.False,
                    "the composer returned to delta mode for the gapped client (the wedge cleared)");
                Assert.That(h.Driver.JoinSnapshotsApplied, Is.EqualTo(joinFullsBefore + 1),
                    $"exactly ONE reliable recovery full rode the join mechanism (applied {h.Driver.JoinSnapshotsApplied - joinFullsBefore} after the gap) -- no reliable spam while waiting for the ack, no unreliable full that can't carry the world");
            });
        }

        static float TrackError(NetWorldClient client, Harness h, uint netId)
        {
            h.Server.Vehicles.TryGet(netId, out var server);
            return client.Vehicles.TryGet(netId, out var replica) ? (replica.Pos - server.Pos).magnitude : float.MaxValue;
        }
    }
}
