using Godot;
using SDG.NetTransport;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // The joined-client composition node (PEI_CLIENT_PLAN §2.2 / §3 C3) -- the MpLoopback shape for the
    // REMOTE case. Owns the NetWorldClient session, the replica views, and the LOCAL PLAYER SHELL: a real
    // PlayerController (real input/camera/viewmodel -- NOT a NetAvatar) spawned at the FIRST authoritative
    // own-entity sample (the server-adopted spawn), whose captured input goes over the wire each 50 Hz tick
    // (SendMoveInput, held-keys model) and whose node is reconciled against the server's authority through
    // a PredictionReconciler. The one mechanism new vs MpLoopback: corrections are CONSUMED and applied TO
    // THE NODE (loopback discards them -- there the node IS the authority via ServerDrive).
    //
    // Why this converges (§2.3): shell and server avatar step the SAME PlayerMovementSim constants on the
    // SAME world geometry (client world == server world, same files), so residuals are quantization- and
    // timing-sized (a walk-start/stop transient of ~2 ticks of velocity from the send->apply->write-back
    // pipeline). Real divergence (a shove only one side saw) trips the 2 m SnapThreshold -> TakeAll.
    //
    // Corrections apply in the sim step that runs BEFORE the shell's _PhysicsProcess (SimDriver is first
    // in the tree), through PlayerController.ApplyNetCorrection -- the seam that shifts the manual
    // render-interp samples WITH the node (§7 risk 5: a bare GlobalPosition write would be overwritten by
    // the interp restore one tick later and never render).
    public partial class ClientWorldSession : Node3D
    {
        public string Host = "127.0.0.1";
        public ushort Port = 47872;
        public string PlayerName = "player";
        public SimDriver Driver;                    // the world's sim spine (WorldBuildResult.Sim) -- steps registered here in §2.5 order
        public DirectionalLight3D Sun;              // world lighting (WorldBuildResult.Sun/Env): the late-spawned shell's FP gun links to it, like Playable
        public Godot.Environment Env;
        public IClientTransport TransportOverride;  // tests inject MemClientTransport; null = real UDP to Host:Port

        public NetWorldClient Client { get; private set; }
        public RemotePlayers Remotes { get; private set; }
        public PlayerController Shell { get; private set; }   // null until the first authoritative own-entity sample
        // The session's OWN reconciler -- NOT Client.Prediction.Reconciler: NetWorldClient.Tick feeds that
        // one through ClientPrediction.Reconcile (the headless-walker path), which would consume the snap
        // onto the dead Prediction.Pos and corrupt the node's correction accounting.
        public readonly PredictionReconciler Reconciler = new PredictionReconciler();

        CanvasLayer _statusLayer;
        Label _status;
        Label _desyncLabel;
        string _desyncAlert = "";

        public override void _Ready()
        {
            // net diagnostics (hardening Part B) -- same toggle as the server: UG_NETLOG=1 or --netlog
            NetLog.Sink = s => GD.Print(s);
            NetLog.ErrorSink = s => GD.PrintErr(s);
            if (System.Environment.GetEnvironmentVariable("UG_NETLOG") == "1") NetLog.Enabled = true;

            Client = new NetWorldClient(TransportOverride ?? new UdpClientTransport(Host, Port), PlayerName, contentHash: NetContent.Hash);
            // desync detection (hardening Part C): a confirmed replica-vs-server StateHash mismatch --
            // log loudly + banner the player. The C3 bar: a reconciled walking shell NEVER trips this
            // (corrections touch the NODE only; replicas mirror snapshots verbatim).
            Client.DesyncDetected += report =>
            {
                GD.PrintErr($"[CLIENT] DESYNC DETECTED -- {report}");
                _desyncAlert = $"!! DESYNC detected (system {report.SystemId} @ tick {report.ServerTick}) -- state may be out of sync";
            };
            Client.Connect();
            DeployableNetSchema.RegisterAll(Client.Deployables.Schema);
            CropNetSchema.RegisterAll(Client.Crops.Schema);   // §3.7: growth stages derive from the synced defs + snapshot tick
            DevConsole.RemoteClient = Client;                 // server-gated cheats: give/xp/skill route over the console command plane (§2.3)

            // replica views -- puppets/replicas land INSIDE the real world (they parent to this node / its parent)
            Remotes = new RemotePlayers { Client = Client };  // remote players as CharacterModel puppets; self never puppets (the shell owns self)
            AddChild(Remotes);
            AddChild(new DeployableReplicaView { Client = Client });
            AddChild(new VehicleReplicaView { Client = Client });   // server vehicles as dead-reckoned puppets (§3.6)

            // pre-join status: there is NO camera until the shell spawns (its first-person cam IS the
            // view) -- surface the session state so an unreachable server isn't a silent black screen
            _statusLayer = new CanvasLayer();
            _status = new Label { Position = new Vector2(24, 22) };
            _status.AddThemeFontSizeOverride("font_size", 22);
            _statusLayer.AddChild(_status);
            _desyncLabel = new Label { Position = new Vector2(24, 54), Modulate = new Color(1f, 0.35f, 0.30f) };
            _desyncLabel.AddThemeFontSizeOverride("font_size", 20);
            _statusLayer.AddChild(_desyncLabel);
            AddChild(_statusLayer);

            // §2.5 step order on the world's SimRoot: net pump FIRST (receive datagrams + apply snapshots
            // + ack -- Client.Tick is the whole client session tick), the shell step second (consume the
            // fresh correction, then send input + record). The shell NODE's physics runs after both
            // (SimDriver sits before the session in the tree), so a correction lands before MoveAndSlide.
            Driver.Sim.Add(new DelegateSimStep((t, dt) => Client.Tick(), "net.client.pump"));
            Driver.Sim.Add(new DelegateSimStep((t, dt) => ShellStep((float)dt), "client.shell"));
        }

        void ShellStep(float dt)
        {
            if (Client.State != NetSessionState.Connected) return;
            if (Shell == null || !IsInstanceValid(Shell))
            {
                // the reconcile-seed pattern (ClientPrediction.Reconcile): the FIRST authoritative
                // own-entity sample IS the spawn adoption -- the shell spawns exactly where the server
                // put the entity (real Spawns/Players.dat point at terrain height, C2's SpawnProvider)
                if (Client.Players.TryGetByOwner(Client.PlayerId, out var me)) SpawnShell(me);
                return;
            }
            if (Shell.IsDriving) return;   // C6 wires driving; walk corrections must never fight the seat teleport

            // 1) consume the newest authoritative own-entity sample (stale/duplicate acks no-op inside),
            //    and apply this tick's correction TO THE NODE -- the one real new mechanism vs MpLoopback
            if (Client.Players.TryGetByOwner(Client.PlayerId, out var e))
            {
                bool snap = Reconciler.OnAuthoritative(e.LastProcessedInputSeq, e.Pos);
                var delta = snap ? Reconciler.TakeAll() : Reconciler.Step(dt);   // past 2 m: snap, don't ice-skate
                if (delta != UnityEngine.Vector3.zero)
                {
                    Shell.ApplyNetCorrection(new Vector3(delta.x, delta.y, delta.z));
                    Reconciler.NoteCorrectionApplied(delta);   // raw node floats -- the delta lands in full, accounting stays exact
                }
            }

            // 2) this tick's captured input over the wire (held-keys model), THEN record the
            //    post-correction TRUE physics position under the sent seq (the Record contract:
            //    record AFTER the tick's correction slice, replace semantics, no double-count)
            float yaw = Shell.RotationDegrees.Y;
            ushort seq = Client.SendMoveInput(Shell.LastMoveInput.x, Shell.LastMoveInput.y, yaw,
                                              Shell.LastJumpInput ? MoveInput.ButtonJump : (byte)0);
            if (seq != 0)
            {
                var p = Shell.TruePhysicsPosition;
                Reconciler.Record(seq, new UnityEngine.Vector3(p.X, p.Y, p.Z));
            }
        }

        void SpawnShell(PlayerReplication.PlayerEntity me)
        {
            var shell = new PlayerController { CaptureMouse = true };
            AddChild(shell);
            shell.EquipUnarmed();   // spawn UNARMED, exactly like Playable (combat over the wire is deferred, §6)
            if (Sun != null && Env != null) shell.LinkWorldLighting(Sun, Env);   // FP gun takes the world day/night sun + ambient
            shell.GlobalPosition = new Vector3(me.Pos.x, me.Pos.y, me.Pos.z);
            shell.RotationDegrees = new Vector3(0f, me.YawDegrees, 0f);
            shell.Spawn = shell.GlobalPosition;   // local OOB respawn point; server-auth death/respawn is deferred (§6)
            WorldBuilder.AttachPlayerShell(this, shell, withCropManager: false);   // the SP shell block verbatim; crops: the SERVER owns growth
            Shell = shell;
            if (System.Environment.GetEnvironmentVariable("UG_MPWALK") == "1")   // scripted-walk hook for headless connect-and-render checks (the UG_AUTOFIRE spirit)
                shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            GD.Print($"[CLIENT] shell spawned at server-adopted spawn ({me.Pos.x:0.0},{me.Pos.y:0.0},{me.Pos.z:0.0}) -- first-person, predicted, reconciled");
        }

        public override void _Process(double delta)
        {
            if (_status != null)
                _status.Text = Shell == null ? $"connecting to {Host}:{Port}   ·   {Client.State}   ·   players {Client.Players.Count}" : "";
            if (_desyncLabel != null) _desyncLabel.Text = _desyncAlert;
        }

        public override void _ExitTree()
        {
            if (DevConsole.RemoteClient == Client) DevConsole.RemoteClient = null;
            Client?.Disconnect();
        }
    }
}
