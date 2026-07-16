using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // In-process 2-player NETWORK demo, re-founded on the Phase 1-3 stack: a real NetWorldServer
    // (NetServerSession + snapshot/command planes) + two NetWorldClients over loopback UDP. Each 50 Hz
    // sim tick the clients send MoveInput commands (the first real Cmd); the server integrates them
    // authoritatively (PlayerReplication, the first real IReplicatedSystem) and snapshots back at 25 Hz.
    // The orange capsule's position literally travelled bot input -> UDP -> server sim -> snapshot ->
    // UDP -> local client -> render. Stepping rides the SimRoot spine in MP_PLAN §2.5 order: inputs,
    // server simulation, client apply -- with the server's replication send registered LAST.
    public partial class NetDemoNode : Node3D
    {
        public ushort Port = 47871;

        NetWorldServer _server;
        NetWorldClient _local;   // playerId 1 (connects first)
        NetWorldClient _bot;     // playerId 2

        readonly Dictionary<ushort, MeshInstance3D> _avatars = new();

        public override void _Ready()
        {
            _server = new NetWorldServer(new UdpServerTransport(Port), contentHash: NetContent.Hash);
            _local = new NetWorldClient(new UdpClientTransport("127.0.0.1", Port), "local", contentHash: NetContent.Hash);
            _bot = new NetWorldClient(new UdpClientTransport("127.0.0.1", Port), "bot", contentHash: NetContent.Hash);
            _local.Connect();
            _bot.Connect();

            var sim = new SimDriver();
            AddChild(sim);
            sim.Sim.Add(new DelegateSimStep(FeedInputs, "netdemo.inputs"));
            sim.Sim.Add(new DelegateSimStep((t, dt) => _server.TickSimulation(), "net.server.sim"));
            sim.Sim.Add(new DelegateSimStep((t, dt) => { _local.Tick(); _bot.Tick(); }, "netdemo.clients"));
            sim.Sim.Add(new DelegateSimStep((t, dt) => _server.TickReplication(), "net.server.replicate"));   // LAST (MP_PLAN §2.5)
        }

        // Both players walk a circle by holding "forward" while the facing sweeps -- opposite directions,
        // so the demo shows two independent server-authoritative movers.
        void FeedInputs(long tick, double dt)
        {
            float t = (float)(tick * dt);
            _local.SendMoveInput(0f, 1f, t * 40f);            // ~6.4 m radius circle at walk speed
            _bot.SendMoveInput(0f, 1f, 180f - t * 40f);       // mirrored circle
        }

        public override void _Process(double delta)
        {
            // render whatever the LOCAL client's replica store says -- every position came through the server
            foreach (var e in _local.Players.All)
            {
                if (!_avatars.TryGetValue(e.OwnerPlayerId, out var av))
                {
                    var color = e.OwnerPlayerId == _local.PlayerId
                        ? new Color(0.30f, 0.55f, 0.95f)    // "me" (blue)
                        : new Color(0.95f, 0.55f, 0.20f);   // remote player (orange)
                    av = new MeshInstance3D
                    {
                        Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f },
                        MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
                    };
                    AddChild(av);
                    _avatars[e.OwnerPlayerId] = av;
                }
                av.Position = new Vector3(e.Pos.x, e.Pos.y + 0.9f, e.Pos.z);   // replicated pos is feet; capsule origin is its middle
                av.Rotation = new Vector3(0f, Mathf.DegToRad(e.YawDegrees), 0f);
            }
        }

        public override void _ExitTree()
        {
            _local?.Disconnect();
            _bot?.Disconnect();
            _server?.TearDown();
        }
    }
}
