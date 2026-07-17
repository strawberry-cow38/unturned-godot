using Godot;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // --netobserve (headless net-observer): a diagnostics-only joined client -- the FULL netcode +
    // replica state of ClientWorldSession, NONE of its render surface. Purpose: observe, from a real
    // over-the-wire client, whether a driven vehicle's motion (a) arrives in the replica registry
    // (epos, straight off the snapshot plane) and (b) lands on the VehicleReplicaView puppet (the
    // node the driver's screen chases) -- separating a replication bug from an apply/render bug.
    //
    // Why NOT ClientWorldSession: its ShellStep spawns a full PlayerController shell (first-person
    // Camera3D.Current + viewmodel + mouse capture) the moment the server replicates our own player
    // entity, and streams MoveInput every tick -- render machinery and traffic an observer must not
    // have. This node is the MINIMAL slice of it instead: the SAME NetWorldClient construction
    // (UdpClientTransport + NetContent.Hash), the SAME wire schemas, the SAME "net.client.pump" sim
    // step, and a VehicleReplicaView -- nothing that touches a window, camera, or input.
    public partial class NetObserver : Node3D
    {
        public string Host = "127.0.0.1";
        public ushort Port = 47872;
        public SimDriver Driver;   // the scaffold world's sim spine (WorldBuildResult.Sim) -- pumps the client at 50 Hz

        public NetWorldClient Client { get; private set; }
        public VehicleReplicaView VehicleView { get; private set; }

        NetSessionState _lastState = NetSessionState.Disconnected;
        bool _announced;   // the once-on-connect census (proves replication reaches this client at all)
        int _sinceLog;     // physics ticks since the last vehicle report (25 ticks = 0.5 s @ 50 Hz)
        int _sinceHb;      // physics ticks since the last liveness heartbeat (5 s)

        public override void _Ready()
        {
            // net diagnostics -- same toggle as the server/client: UG_NETLOG=1 or --netlog
            NetLog.Sink = s => GD.Print(s);
            NetLog.ErrorSink = s => GD.PrintErr(s);
            if (System.Environment.GetEnvironmentVariable("UG_NETLOG") == "1") NetLog.Enabled = true;

            Client = new NetWorldClient(new UdpClientTransport(Host, Port), "netobserver", contentHash: NetContent.Hash);
            // wire-format parity with the real client: snapshot application resolves deployable/crop
            // defs through these schemas -- register them exactly like ClientWorldSession does
            DeployableNetSchema.RegisterAll(Client.Deployables.Schema);
            CropNetSchema.RegisterAll(Client.Crops.Schema);
            Client.DesyncDetected += report => GD.PrintErr($"[NETOBS] DESYNC DETECTED -- {report}");
            Client.Connect();

            // the replica under test: server vehicles -> dead-reckoned puppets (its _Process is pure
            // node math on ArrayMesh puppets -- no camera, no viewport, headless-fine)
            VehicleView = new VehicleReplicaView { Client = Client };
            AddChild(VehicleView);

            // ClientWorldSession's §2.5 net pump, verbatim: receive datagrams + apply snapshots + ack
            Driver.Sim.Add(new DelegateSimStep((t, dt) => Client.Tick(), "net.client.pump"));
            GD.Print($"[NETOBS] observer up; connecting to {Host}:{Port} (contentHash {NetContent.Hash:x16})");
        }

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null) return;

            if (Client.State != _lastState)
            {
                GD.Print($"[NETOBS] session state {_lastState} -> {Client.State}");
                _lastState = Client.State;
            }

            // once, after the join snapshot landed: the replica census -- nonzero vehicles here proves
            // the vehicle system replicates to this client at all (vehicles are globally mirrored, not
            // relevancy-filtered, so the count is the server's full census)
            if (!_announced && Client.State == NetSessionState.Connected && Client.JoinSnapshotsApplied > 0)
            {
                _announced = true;
                GD.Print($"[NETOBS] connected as player {Client.PlayerId}; join snapshot applied (server tick {Client.Applier.LastAppliedServerTick}): vehicles={Client.Vehicles.Count} players={Client.Players.Count}");
            }

            if (++_sinceHb >= 250)   // 5 s liveness: distinguishes "connected, nothing moving" from wedged
            {
                _sinceHb = 0;
                GD.Print($"[NETOBS] hb state={Client.State} tick={Client.Applier.LastAppliedServerTick} vehicles={Client.Vehicles.Count} players={Client.Players.Count} puppets={VehicleView.PuppetCount}");
            }

            if (++_sinceLog < 25) return;   // 0.5 s vehicle report
            _sinceLog = 0;
            foreach (var e in Client.Vehicles.All)
            {
                if (e.DriverPlayerId == 0 && e.LinVel.sqrMagnitude <= 0.25f) continue;   // only driven or moving
                string puppet = VehicleView.TryGetPuppet(e.NetIdValue, out var pup)
                    ? $"({pup.GlobalPosition.X:0.00},{pup.GlobalPosition.Y:0.00},{pup.GlobalPosition.Z:0.00})"
                    : "none";
                GD.Print($"[NETOBS] veh={e.NetIdValue} drv={e.DriverPlayerId} vel={e.LinVel.magnitude:0.00} epos=({e.Pos.x:0.00},{e.Pos.y:0.00},{e.Pos.z:0.00}) puppet={puppet}");
            }
        }

        public override void _ExitTree() => Client?.Disconnect();
    }
}
