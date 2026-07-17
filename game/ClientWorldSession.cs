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
        public DayNightCycle DayNight;              // the client world's clock (WorldBuildResult.DayNight) -- C5: WorldClockView anchors it on the replicated clock
        public ResourceField Resources;             // the client world's trees/rocks (WorldBuildResult.Resources) -- C5: ResourceAliveView mirrors the alive-bitmap
        public Terrain Terr;                        // the client world's terrain (WorldBuildResult.Terr) -- C6: terrain-snaps a slope vehicle-exit (§7 risk 6)
        public IClientTransport TransportOverride;  // tests inject MemClientTransport; null = real UDP to Host:Port

        public NetWorldClient Client { get; private set; }
        public RemotePlayers Remotes { get; private set; }
        public ZombiePuppets Puppets { get; private set; }        // C5: server zombies as interpolated puppets
        public WorldItemReplicaView Items { get; private set; }   // C5: replicated world items as static visuals
        public VehicleReplicaView VehicleView { get; private set; }   // C6: the puppet registry -- ride mode chases these
        public PlayerController Shell { get; private set; }   // null until the first authoritative own-entity sample
        public uint RidingVehicle => _ridingNetId;             // NetId of the vehicle the server seated us in (0 = on foot)
        uint _ridingNetId;                                     // latched by VehicleEntered(self), cleared by VehicleExited(self)
        // The session's OWN reconciler -- NOT Client.Prediction.Reconciler: NetWorldClient.Tick feeds that
        // one through ClientPrediction.Reconcile (the headless-walker path), which would consume the snap
        // onto the dead Prediction.Pos and corrupt the node's correction accounting.
        public readonly PredictionReconciler Reconciler = new PredictionReconciler();

        CanvasLayer _statusLayer;
        Label _status;
        Label _desyncLabel;
        string _desyncAlert = "";

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

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
            VehicleView = new VehicleReplicaView { Client = Client };   // server vehicles as dead-reckoned puppets (§3.6)
            AddChild(VehicleView);
            // C6 ride mode: the server's seat facts drive the shell's enter/exit -- the client never seats
            // itself (SendEnterVehicle is a REQUEST; the seat lands only when the validated fact comes back)
            Client.VehicleEntered += e => { if (e.PlayerId == Client.PlayerId) _ridingNetId = e.NetId; };
            Client.VehicleExited += OnVehicleExited;
            // C5 (§3): the remaining world-state views -- all read-only replica consumers. Zombie puppets,
            // world items as static visuals, the synced clock anchoring the local sky, and server-felled
            // resources dropping their client visual + trunk collider (§7 risk 7).
            Puppets = new ZombiePuppets { Client = Client };
            AddChild(Puppets);
            Items = new WorldItemReplicaView { Client = Client };
            AddChild(Items);
            AddChild(new WorldClockView { Client = Client, DayNight = DayNight });
            AddChild(new ResourceAliveView { Client = Client, Field = Resources });
            AddChild(new ProjectileReplicaView { Client = Client });   // D1: server-flown grenades render while fused

            // D1 combat facts -> render consumers (PEI_COMBAT_PLAN §3 D1). All read-only fx/HUD -- nothing
            // here writes a replica. The shell's own bullets are cosmetic, so these events are the ONE
            // authority for hitmarkers + impact fx (shooter included -- no local/echo double-render).
            Client.HitConfirmed += e =>
            {
                HitmarkerHUD.Instance?.Show(e.Headshot);   // the hitmarker now only ever tells the server's truth
                GD.Print($"[combat] hit {(HitTargetKind)e.TargetKind} {e.TargetId} for {e.Damage:0}{(e.Headshot ? " HEADSHOT" : "")}{(e.Killed ? " -- KILLED" : "")}");
            };
            Client.ImpactFx += e =>
            {
                if (Shell != null && IsInstanceValid(Shell))
                    Shell.RenderImpactFx(new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.Surface == (byte)ImpactSurface.Flesh);
            };
            Client.ZombieHit += e =>
            {
                // melee blood (melee broadcasts no ImpactFx); bullet hits also land here -- their ImpactFx
                // arrives at the exact hit point too, so a shot zombie just bleeds a little harder
                if (Shell != null && IsInstanceValid(Shell) && Puppets.TryGetPuppet(e.NetId, out var pup) && IsInstanceValid(pup))
                    Shell.RenderImpactFx(pup.GlobalPosition + Vector3.Up, flesh: true);
            };
            Client.ZombieDied += e =>
                GD.Print($"[combat] zombie {e.NetId} killed by {(e.Killer == Client.PlayerId ? "you" : $"player {e.Killer}")}");
            Client.GrenadeExploded += e =>
                // the SP blast "fx" is the camera flinch (PlayerController.Explode -> FlinchAllFromExplosion,
                // same params) -- fx only, zero damage: the server already applied the authoritative damage
                PlayerRegistry.FlinchAllFromExplosion(new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), Mathf.Max(e.Radius * 2f, 12f), 30f);

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
            if (Shell.IsDriving) return;   // the SP direct-drive path (never taken in MP -- no Vehicle nodes on a client world)

            // C6 RIDE MODE (§3.6 v1): seated in a replicated vehicle. The shell is hidden/frozen (EnterPuppet);
            // this step streams its captured drive intent @50 Hz (held-input model, latest-wins server-side)
            // INSTEAD of MoveInput -- the server drops a seated peer's walk input at the choke point anyway,
            // and the reconciler idles (the seat teleport owns the entity; corrections must never fight it).
            if (_ridingNetId != 0)
            {
                if (Shell.IsRiding)
                    Client.SendDriveInput(_ridingNetId, Shell.LastDriveInput.y, Shell.LastDriveInput.x, Shell.LastHandbrakeInput);
                else if (VehicleView.TryGetPuppet(_ridingNetId, out var pup))
                    Shell.EnterPuppet(pup);   // seat confirmed; puppet materialized -> hide/freeze + chase-cam it
                return;                       // (else: puppet not spawned yet -- wait, never walk-send from under the seat)
            }

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
            //    record AFTER the tick's correction slice, replace semantics, no double-count).
            //    The stance the sim consumed rides in the buttons bits (the mp-inchworm fix): the
            //    server avatar must integrate at the SAME speed this shell just predicted at.
            float yaw = Shell.RotationDegrees.Y;
            byte buttons = (byte)((Shell.LastJumpInput ? MoveInput.ButtonJump : (byte)0) | MoveInput.PackStance(Shell.Stance));
            ushort seq = Client.SendMoveInput(Shell.LastMoveInput.x, Shell.LastMoveInput.y, yaw, buttons);
            if (seq != 0)
            {
                var p = Shell.TruePhysicsPosition;
                Reconciler.Record(seq, new UnityEngine.Vector3(p.X, p.Y, p.Z));
            }
        }

        void SpawnShell(PlayerReplication.PlayerEntity me)
        {
            // DeterministicGround: the shell and its server avatar must make the SAME grounded decision
            // (a deterministic spherecast, not each body's own IsOnFloor) or downhill walking rubberbands
            // -- see PlayerController.DeterministicGround. MP bodies only; SP stays byte-identical.
            var shell = new PlayerController { CaptureMouse = true, DeterministicGround = true };
            AddChild(shell);
            // D1: spawn holding the EAGLEFIRE (demo-inventory primary slot) -- the server validates every
            // shot as the default Eaglefire profile (ServerCombat), so client + server agree on rate/ammo;
            // a faster demo gun would get half its shots silently rate-rejected and feel broken. Per-gun
            // server profiles are the deferred Phase 6 equip seam.
            shell.EquipHotbar(1);
            if (Sun != null && Env != null) shell.LinkWorldLighting(Sun, Env);   // FP gun takes the world day/night sun + ambient
            shell.GlobalPosition = new Vector3(me.Pos.x, me.Pos.y, me.Pos.z);
            shell.RotationDegrees = new Vector3(0f, me.YawDegrees, 0f);
            shell.Spawn = shell.GlobalPosition;   // local OOB respawn point; server-auth death/respawn is deferred (§6)
            WorldBuilder.AttachPlayerShell(this, shell, withCropManager: false);   // the SP shell block verbatim; crops: the SERVER owns growth
            // C6: the shell's F-interact near a VehiclePuppet requests the seat over the wire (the MP
            // analogue of the SP direct EnterVehicle); F while riding requests the exit. Server validates.
            shell.NetEnterVehicle = netId => Client.SendEnterVehicle(netId);
            shell.NetExitVehicle = () => Client.SendExitVehicle();
            // D1: trigger pulls route over the wire (fx stay local + immediate, damage waits for the server;
            // the null default of each seam keeps SP/loopback byte-identical -- only THIS session wires them)
            shell.NetFire = (muzzle, aim) => Client.SendFire(ToU(muzzle), ToU(aim));
            shell.NetMelee = (strong, yaw) => Client.SendMelee(strong, yaw);
            shell.NetGrenade = (origin, vel) => Client.SendGrenade(ToU(origin), ToU(vel));
            shell.NetReload = () => Client.SendReload();
            Shell = shell;
            if (System.Environment.GetEnvironmentVariable("UG_MPWALK") == "1")   // scripted-walk hook for headless connect-and-render checks (the UG_AUTOFIRE spirit)
                shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            GD.Print($"[CLIENT] shell spawned at server-adopted spawn ({me.Pos.x:0.0},{me.Pos.y:0.0},{me.Pos.z:0.0}) -- first-person, predicted, reconciled");
        }

        // C6 exit: unhide the shell BESIDE THE DOOR. The server already teleported our entity there
        // (ServerVehicles.ServerExit), but that position rides a 25 Hz snapshot -- the reliable exit fact
        // usually lands first. So compute the SAME spot from the vehicle replica (pos + right*2.4 + 1 up,
        // the identical formula + the identical §7 risk 6 terrain clamp the server applies) and place the
        // shell immediately; the resumed MoveInput/reconcile loop absorbs any residual within a few acks.
        void OnVehicleExited(VehicleExitedEvent evt)
        {
            if (evt.PlayerId != Client.PlayerId || evt.NetId != _ridingNetId) return;
            _ridingNetId = 0;
            if (Shell == null || !IsInstanceValid(Shell) || !Shell.IsRiding) return;
            Vector3 exit;
            if (Client.Vehicles.TryGet(evt.NetId, out var v))
            {
                float yawRad = Mathf.DegToRad(v.YawDegrees);
                var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));   // Godot yaw basis: right = (cos, 0, -sin)
                exit = new Vector3(v.Pos.x, v.Pos.y, v.Pos.z) + right * 2.4f + Vector3.Up * 1.0f;
            }
            else exit = Shell.GlobalPosition;   // vehicle despawned under us -- exit in place (the shell rode along)
            if (Terr != null)
            {
                float h = Terr.SampleHeight(exit.X, exit.Z);
                if (exit.Y < h + 0.1f) exit = new Vector3(exit.X, h + 0.5f, exit.Z);   // §7 risk 6: never below the slope
            }
            Shell.ExitPuppet(exit);
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
