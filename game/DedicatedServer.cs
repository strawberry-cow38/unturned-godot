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
        public Terrain Terr;                         // optional (WorldBuildResult.Terr): grounds server grenade bounces on real terrain

        public NetWorldServer Server { get; private set; }
        public ZombieNetSync ZombieSync { get; private set; }

        long _lastStatusTick;

        public override void _Ready()
        {
            Server = new NetWorldServer(TransportOverride ?? new UdpServerTransport(Port),
                (conn, reason, isError) => GD.Print($"[DEDICATED] connection dropped ({conn.GetAddressString(true)}): {reason}"),
                contentHash: NetContent.Hash);   // §2.2: joiners with a different content identity are rejected
            Server.Session.PeerConnected += peer => GD.Print($"[DEDICATED] player {peer.PlayerId} '{peer.Name}' joined ({Server.Session.Peers.Count} online)");
            Server.Session.PeerDisconnected += (peer, reason) => GD.Print($"[DEDICATED] player {peer.PlayerId} left ({reason})");
            // Phase 5 combat hooks: server bullets/blasts stop at the world's real geometry, grenades
            // bounce on real ground height. Both are optional seams on the engine-free ServerCombat.
            Server.Combat.WorldRay = GodotWorldRay;
            if (Terr != null) Server.Combat.GroundHeight = (x, z) => Terr.SampleHeight(x, z);

            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Server.TickSimulation(), "net.server.sim"));
            // zombie brains -> ZombieReplication at 12.5 Hz (§3.5), BEFORE the snapshot send
            ZombieSync = new ZombieNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => ZombieSync.Tick(), "net.zombies.publish"));
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Replicate(tick), "net.server.replicate"));   // LAST (MP_PLAN §2.5)
        }

        // Server-side bullet/LoS raycast against static world geometry (terrain/buildings on layer 0,
        // see-through props on 6) -- runs inside the SimRoot's _PhysicsProcess, where space access is safe.
        bool GodotWorldRay(UnityEngine.Vector3 from, UnityEngine.Vector3 to, out UnityEngine.Vector3 point)
        {
            point = default;
            var world = GetViewport()?.World3D;
            if (world == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.x, from.y, from.z), new Vector3(to.x, to.y, to.z), (1u << 0) | (1u << 6));
            var hit = world.DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            var p = (Vector3)hit["position"];
            point = new UnityEngine.Vector3(p.X, p.Y, p.Z);
            return true;
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
