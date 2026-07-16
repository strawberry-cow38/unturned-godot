using Godot;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Headless demo SERVER (the 2-process --server/--client demo, re-founded on the Phase 1-3 stack):
    // a real NetWorldServer + one scripted bot client on loopback so a joining --client always has
    // somebody to watch. Steps on the SimRoot spine in MP_PLAN §2.5 order, replication LAST. Runs under
    // `godot --headless -- --server`. (The REAL world server is --dedicated; this is the bare demo.)
    public partial class ServerNode : Node
    {
        public ushort Port = 47872;

        NetWorldServer _server;
        NetWorldClient _bot;

        public override void _Ready()
        {
            _server = new NetWorldServer(new UdpServerTransport(Port));
            _server.Session.PeerConnected += peer => GD.Print($"[SERVER] player {peer.PlayerId} '{peer.Name}' joined ({_server.Session.Peers.Count} online)");
            _server.Session.PeerDisconnected += (peer, reason) => GD.Print($"[SERVER] player {peer.PlayerId} left ({reason})");
            _bot = new NetWorldClient(new UdpClientTransport("127.0.0.1", Port), "bot");
            _bot.Connect();

            var sim = new SimDriver();
            AddChild(sim);
            sim.Sim.Add(new DelegateSimStep((tick, dt) => _bot.SendMoveInput(0f, 1f, (float)(tick * dt) * 40f), "server.botinput"));   // bot walks a circle
            sim.Sim.Add(new DelegateSimStep((t, dt) => _server.TickSimulation(), "net.server.sim"));
            sim.Sim.Add(new DelegateSimStep((t, dt) => _bot.Tick(), "server.botclient"));
            sim.Sim.Add(new DelegateSimStep((t, dt) => _server.TickReplication(), "net.server.replicate"));   // LAST (MP_PLAN §2.5)
        }

        public override void _ExitTree()
        {
            _bot?.Disconnect();
            _server?.TearDown();
        }
    }
}
