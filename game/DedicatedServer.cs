using Godot;
using SDG.NetTransport;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // The dedicated server's net host (MP_PLAN §4 Phase 3): wraps the engine-free NetWorldServer
    // (NetServerSession + CommandRegistry + PlayerReplication + SnapshotComposer) and registers its two
    // tick phases on the world's SimRoot in §2.5 order -- simulation (receive + input-apply + player sim)
    // first, replication (snapshot compose/send) LAST. Boots from Main's --dedicated after WorldBuilder
    // has assembled the dedicated world; the L1 net.dedicated_boot test injects a MemServerTransport.
    public partial class DedicatedServer : Node
    {
        public ushort Port = 47872;
        public SimDriver Driver;                     // the world's sim spine (WorldBuildResult.Sim)
        public IServerTransport TransportOverride;   // tests inject MemTransport; null = real UDP

        public NetWorldServer Server { get; private set; }

        long _lastStatusTick;

        public override void _Ready()
        {
            Server = new NetWorldServer(TransportOverride ?? new UdpServerTransport(Port),
                (conn, reason, isError) => GD.Print($"[DEDICATED] connection dropped ({conn.GetAddressString(true)}): {reason}"));
            Server.Session.PeerConnected += peer => GD.Print($"[DEDICATED] player {peer.PlayerId} '{peer.Name}' joined ({Server.Session.Peers.Count} online)");
            Server.Session.PeerDisconnected += (peer, reason) => GD.Print($"[DEDICATED] player {peer.PlayerId} left ({reason})");

            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Server.TickSimulation(), "net.server.sim"));
            // gameplay systems join the spine here per-phase as their authority splits land (Phases 4-8)
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Replicate(tick), "net.server.replicate"));   // LAST (MP_PLAN §2.5)
        }

        void Replicate(long tick)
        {
            Server.TickReplication();
            if (tick - _lastStatusTick >= 500)   // 10 s heartbeat so a headless console shows life
            {
                _lastStatusTick = tick;
                GD.Print($"[DEDICATED] tick {Server.Session.CurrentTick} | players {Server.Session.Peers.Count} | snapshots full={Server.Composer.Diag.FullSnapshotsComposed} delta={Server.Composer.Diag.DeltaSnapshotsComposed}");
            }
        }

        public override void _ExitTree() => Server?.TearDown();
    }
}
