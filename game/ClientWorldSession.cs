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
        // Part A (CLIENT_PREDICTION_PLAN §5.2 A1): the driver's CLIENT-LOCAL real Vehicle -- one physics
        // instance, zero steady-state corrections at any RTT (retail client authority). Built from the
        // replica on the seat fact, destroyed on exit; the puppet VIEW for its NetId is suppressed while
        // it lives. Exposed for the L1 rigs.
        public Vehicle LocalVehicle => _localVehicle;
        Vehicle _localVehicle;
        byte _vehRecovAck;    // the last VehicleRecovEvent counter received -- echoed on every state send
        bool _vehRecovHold;   // frozen by a recov; released after the echo goes out (retail isFrozen, 1 send)
        // The session's OWN reconciler -- NOT Client.Prediction.Reconciler: NetWorldClient.Tick feeds that
        // one through ClientPrediction.Reconcile (the headless-walker path), which would consume the snap
        // onto the dead Prediction.Pos and corrupt the node's correction accounting.
        public readonly PredictionReconciler Reconciler = new PredictionReconciler();

        CanvasLayer _statusLayer;
        Label _status;
        Label _desyncLabel;
        string _desyncAlert = "";
        Label _toast; float _toastT;   // brief interaction feedback line (pickup denied, etc.)
        Label _disconnectBanner;       // big centered DISCONNECTED overlay when the server link drops
        bool _wasConnected;            // latches once Connected -> a later non-Connected state is a LOST link (not the initial connect)

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
            // Part A: the server rolled our driven vehicle back (out-of-envelope state) -- retail tellRecov
            // (U3 InteractableVehicle.cs:2095-2109): teleport the LOCAL vehicle to the last-good payload,
            // zero velocity, freeze; DriveStep sends the RecovAck echo next tick and releases.
            Client.VehicleRecov += e =>
            {
                if (e.NetId != _ridingNetId || _localVehicle == null || !IsInstanceValid(_localVehicle)) return;
                var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(e.PitchDegrees),
                    Mathf.DegToRad(e.YawDegrees), Mathf.DegToRad(e.RollDegrees)));
                _localVehicle.NetBeginHold();   // freeze + zero velocities (retail isFrozen/kinematic)
                _localVehicle.NetHoldTeleport(new Transform3D(basis, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z)));
                _vehRecovAck = e.RecovCounter;
                _vehRecovHold = true;
                GD.Print($"[CLIENT] vehicle recov #{e.RecovCounter} -- rolled back to ({e.Pos.x:0.0},{e.Pos.y:0.0},{e.Pos.z:0.0})");
            };
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
            Client.ItemPickupDenied += e =>
            {
                // a LEGAL pickup the server grid had no room for -- the item stays in the world; tell the
                // player "no room" instead of silence (the request made no local change to roll back)
                GD.Print($"[CLIENT] pickup denied (world item {e.NetId}) -- no room in the bag");
                if (_toast != null) { _toast.Text = "No room in inventory"; _toastT = 2.5f; }
            };
            // Phase 6 owner inventory (pickup Step 4): the shell's bag MIRRORS the server's authoritative
            // grid -- every owner-block echo re-adopts into the shell's EXISTING Inventory instance
            // (copy-in-place; the UI's signature poll repaints). The initial pull rides SpawnShell: the
            // join snapshot's owner block applies BEFORE the shell exists, so its ReplicaUpdated already
            // fired by then. Known wrinkle (accepted, PROGRESS.md): locally-mutated state MP hasn't routed
            // yet (mag accounting, consume decrement) is resurrected by the next full-state echo.
            Client.Inventories.ReplicaUpdated += owner =>
            {
                if (owner != Client.PlayerId || Shell == null || !IsInstanceValid(Shell)) return;
                if (Client.Inventories.TryGet(owner, out var inv)) Shell.AdoptReplicatedInventory(inv.Inventory);
            };

            // pre-join status: there is NO camera until the shell spawns (its first-person cam IS the
            // view) -- surface the session state so an unreachable server isn't a silent black screen
            _statusLayer = new CanvasLayer();
            _status = new Label { Position = new Vector2(24, 22) };
            _status.AddThemeFontSizeOverride("font_size", 22);
            _statusLayer.AddChild(_status);
            _desyncLabel = new Label { Position = new Vector2(24, 54), Modulate = new Color(1f, 0.35f, 0.30f) };
            _desyncLabel.AddThemeFontSizeOverride("font_size", 20);
            _statusLayer.AddChild(_desyncLabel);
            _toast = new Label { Position = new Vector2(24, 86), Modulate = new Color(1f, 0.85f, 0.4f) };
            _toast.AddThemeFontSizeOverride("font_size", 20);
            _statusLayer.AddChild(_toast);
            // DISCONNECTED overlay: full-rect, centered, big + red -- shown only after a link that WAS connected drops
            _disconnectBanner = new Label
            {
                Text = "⚠  DISCONNECTED\nconnection to server lost",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Modulate = new Color(1f, 0.30f, 0.28f),
                Visible = false,
            };
            _disconnectBanner.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _disconnectBanner.AddThemeFontSizeOverride("font_size", 48);
            _statusLayer.AddChild(_disconnectBanner);
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
            // Part A DRIVING (CLIENT_PREDICTION_PLAN §5.2 A1, replacing the C6 v1 puppet-ride): seated in a
            // replicated vehicle -- the shell drives a CLIENT-LOCAL real Vehicle through the SP direct-drive
            // path (0 ms wheel response; retail client authority) and this step streams VehicleState @25 Hz
            // INSTEAD of MoveInput -- the server drops a seated peer's walk input at the choke point anyway,
            // and the reconciler idles (the seat teleport owns the entity; corrections must never fight it).
            if (_ridingNetId != 0) { DriveStep(); return; }
            if (Shell.IsDriving) return;   // the SP direct-drive guard (unreachable in MP -- the seat always latches _ridingNetId first)

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
            //    C2: the SAME position rides the wire as the claim (retail's clientPosition,
            //    U3 PlayerInput.cs:867-873/:1607) -- the server ack band adopts it when the avatar
            //    agrees to within AckBandMeters, so this ack round-trips as exactly rec[seq].
            float yaw = Shell.RotationDegrees.Y;
            byte buttons = (byte)((Shell.LastJumpInput ? MoveInput.ButtonJump : (byte)0) | MoveInput.PackStance(Shell.Stance));
            var p = Shell.TruePhysicsPosition;
            var claim = new UnityEngine.Vector3(p.X, p.Y, p.Z);
            ushort seq = Client.SendMoveInput(Shell.LastMoveInput.x, Shell.LastMoveInput.y, yaw, buttons, claim);
            if (seq != 0) Reconciler.Record(seq, claim);

            if (NetLog.Enabled) LogReconcileRollupIfDue();
        }

        // ---- Part A driving-local machinery (CLIENT_PREDICTION_PLAN §5.2 A1/A4) ----

        void DriveStep()
        {
            // The seat owns the entity (per-tick vehicle teleport) -- the walk reconciler must IDLE, but
            // its ack CURSOR has to keep tracking: acks in flight when the seat latched land DURING the
            // drive (the entity's LastProcessedInputSeq froze at the last pre-seat input), and leaving them
            // unconsumed made the FIRST post-exit walk tick measure that stale seq against a PRE-ENTER
            // recorded claim -- a garbage ~26 m "error" that snapped the shell clean off the exit spot
            // (found by net.shell_drive_predicted under WAN, where 2-4 acks are always in flight across
            // the seat latch). Consume + DISCARD: the cursor stays current, the node is never touched.
            if (Client.Players.TryGetByOwner(Client.PlayerId, out var me))
            {
                Reconciler.OnAuthoritative(me.LastProcessedInputSeq, me.Pos);
                Reconciler.TakeAll();
            }

            bool haveRep = Client.Vehicles.TryGet(_ridingNetId, out var rep);
            if (_localVehicle != null && IsInstanceValid(_localVehicle))
            {
                // A4 mid-drive despawn/explode: the snapshot's Exploded flag (or a vanished entity)
                // force-exits locally -- the reliable VehicleExited fact may lag or never come (despawn)
                if (!haveRep || rep.Exploded) { ForceExitLocal(); return; }
                if (_vehRecovHold)
                {
                    // the recov echo: one state send carrying the new RecovAck, then release the freeze
                    // (retail clears isFrozen on the next simulate tick, U3 InteractableVehicle.cs:3438)
                    SendVehicleState();
                    _vehRecovHold = false;
                    _localVehicle.NetEndHold(Vector3.Zero, Vector3.Zero);   // resume from rest at the rolled-back pose (retail zeroes velocity)
                    return;
                }
                if (Client.Session.CurrentTick % 2 == 0) SendVehicleState();   // 25 Hz -- the snapshot cadence
                return;
            }
            // seat fact latched but no local vehicle yet: build it from the replica (the entity may lag
            // the reliable fact by a snapshot; never drive blind)
            if (!haveRep) return;
            if (rep.Exploded) { _ridingNetId = 0; return; }   // seated-into-a-wreck race -- don't build
            BuildLocalVehicle(rep);
        }

        void BuildLocalVehicle(VehicleReplication.VehicleEntity e)
        {
            // VIEW-only suppression: the replica STORE keeps mirroring snapshots verbatim (hash parity),
            // only the puppet render for this NetId stops -- retail tellState's isDriver early-return
            VehicleView.Suppressed.Add(_ridingNetId);
            string key = e.TypeId < Vehicle.SpecNames.Length ? Vehicle.SpecNames[e.TypeId] : "jeep";
            var v = Vehicle.BuildByName(key, e.Variant);
            v.NetClientPredicted = true;    // server owns health/explosion (replica Exploded flag); local damage is a no-op
            v.RemoveFromGroup("vehicles");  // never a group-scan target (VehicleNetSync minting in a shared-tree L1 host, tow/roadkill/grenade scans)
            v.CollisionLayer = 0;           // nothing collides INTO the local car; it collides OUT via its mask (bit0: the static world -- plan A4's collision posture)
            AddChild(v);
            var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(e.PitchDegrees),
                Mathf.DegToRad(e.YawDegrees), Mathf.DegToRad(e.RollDegrees)));
            v.GlobalTransform = new Transform3D(basis, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z));
            v.Wake();
            v.LinearVelocity = new Vector3(e.LinVel.x, e.LinVel.y, e.LinVel.z);
            v.AngularVelocity = new Vector3(e.AngVel.x, e.AngVel.y, e.AngVel.z);
            _localVehicle = v;
            _vehRecovAck = 0; _vehRecovHold = false;
            Shell.EnterVehicle(v);   // the EXACT SP direct-drive seat (hide shell, free cam, HUD binds, engine on)
            GD.Print($"[CLIENT] driving {key} LOCALLY (NetId {_ridingNetId}) -- Part A client authority, 0-tick wheel");
        }

        void SendVehicleState()
        {
            var v = _localVehicle;
            var euler = v.GlobalTransform.Basis.GetEuler() * (180f / Mathf.Pi);   // YXZ euler, degrees (the VehicleNetSync publish convention)
            byte flags = (byte)((v.EngineOn ? VehicleReplication.FlagEngineOn : 0)
                              | (v.HeadlightsOn ? VehicleReplication.FlagHeadlights : 0)
                              | (v.TaillightsOn ? VehicleReplication.FlagTaillights : 0)
                              | (v.SirenOn ? VehicleReplication.FlagSiren : 0)
                              | (v.BrakingNow ? VehicleReplication.FlagBraking : 0));
            Client.SendVehicleState(_ridingNetId, ToU(v.GlobalPosition), new UnityEngine.Vector3(euler.X, euler.Y, euler.Z),
                ToU(v.LinearVelocity), ToU(v.AngularVelocity), v.SteerAngleDegrees,
                Shell.LastDriveInput.y, Shell.LastDriveInput.x, Shell.LastHandbrakeInput, flags, _vehRecovAck);
        }

        /// <summary>A4: despawn/explode force-exit -- destroy the local vehicle, restore the shell at the
        /// SP beside-the-door spot (the authoritative VehicleExited fact, if it comes later, no-ops on the
        /// cleared _ridingNetId; post-exit walk reconciliation absorbs any residual).</summary>
        void ForceExitLocal()
        {
            uint id = _ridingNetId;
            _ridingNetId = 0;
            var v = _localVehicle;
            Vector3 spot = Shell.GlobalPosition;
            if (v != null && IsInstanceValid(v))
                spot = v.GlobalPosition + v.GlobalTransform.Basis.X * 2.4f + Vector3.Up * 1.0f;
            if (Terr != null)
            {
                float h = Terr.SampleHeight(spot.X, spot.Z);
                if (spot.Y < h + 0.1f) spot = new Vector3(spot.X, h + 0.5f, spot.Z);
            }
            Shell.ExitVehicleAt(spot);
            CleanupLocalVehicle(id);
        }

        void CleanupLocalVehicle(uint netId)
        {
            if (_localVehicle != null && IsInstanceValid(_localVehicle)) _localVehicle.QueueFree();
            _localVehicle = null;
            _vehRecovAck = 0; _vehRecovHold = false;
            VehicleView.Suppressed.Remove(netId);   // the puppet view resumes rendering the server's truth
        }

        // ---- CLIENT_PREDICTION_PLAN §3 Phase-0 observability: the client mirror of the server's
        // "[NET] 1s:" line -- one row per second of how hard the reconciler's rope actually pulled
        // (applied correction metres, ticks with a live pending error, acks, snaps). This is what
        // --netlog shows on a real WAN link to confirm the worm numbers live. Zero overhead when
        // NetLog is off (the call site is gated). ----
        int _rlTicks; int _rlHotTicks;
        float _rlCorr; long _rlAcks, _rlSnaps;

        void LogReconcileRollupIfDue()
        {
            if (Reconciler.PendingError != UnityEngine.Vector3.zero) _rlHotTicks++;
            if (++_rlTicks < NetProtocol.TicksPerSecond) return;
            float corr = Reconciler.CorrectionAppliedMeters;
            long acks = Reconciler.AcksApplied, snaps = Reconciler.Snaps;
            NetLog.Sink($"[NET-CLIENT] 1s: corr {corr - _rlCorr:0.###} m | hot {_rlHotTicks}/{_rlTicks} ticks" +
                        $" | acks {acks - _rlAcks} | snaps {snaps - _rlSnaps} | pending {Reconciler.PendingError.magnitude:0.###} m");
            _rlTicks = 0; _rlHotTicks = 0;
            _rlCorr = corr; _rlAcks = acks; _rlSnaps = snaps;
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
            // Phase 6 pickup: F on a focused WorldItemPuppet is a REQUEST -- "the client never pockets the
            // item itself": the bag fills only when the owner-block echo lands, the puppet despawns only
            // when WorldItemRemoved broadcasts (WorldItemReplicaView is already diff-driven).
            shell.NetPickupItem = netId => Client.SendPickupItem(netId);
            // owner-grid initial pull (Step 4): the join snapshot's owner block landed before this shell
            // existed -- adopt it now; the ReplicaUpdated subscription (in _Ready) carries every echo after
            if (Client.Inventories.TryGet(Client.PlayerId, out var invEntry))
                shell.AdoptReplicatedInventory(invEntry.Inventory);
            Shell = shell;
            if (System.Environment.GetEnvironmentVariable("UG_MPWALK") == "1")   // scripted-walk hook for headless connect-and-render checks (the UG_AUTOFIRE spirit)
                shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            GD.Print($"[CLIENT] shell spawned at server-adopted spawn ({me.Pos.x:0.0},{me.Pos.y:0.0},{me.Pos.z:0.0}) -- first-person, predicted, reconciled");
        }

        // C6 exit: unhide the shell BESIDE THE DOOR -- at the AUTHORITATIVE spot the event carries
        // (ServerExit computes it beside the REAL car, post-AdjustExitSpot, and the fact rides
        // ReliableOrdered), so the exit lands right even when the snapshot stream is starved or held and
        // the vehicle replica is stale (docs/EXIT_POSITION_ROOTCAUSE.md: a 7ce2305 recovery hold froze
        // every replica at hold-start ≈ drive start, and the old replica-computed spot put the driver
        // back at his ENTRY). Fallbacks, in order: evt.Pos zero (the vehicle despawned server-side before
        // the exit computed a spot) -> the old replica computation; no replica either -> exit in place
        // (the shell rode the puppet). The terrain clamp stays as belt-and-braces -- the server already
        // clamped, so it normally no-ops.
        void OnVehicleExited(VehicleExitedEvent evt)
        {
            if (evt.PlayerId != Client.PlayerId || evt.NetId != _ridingNetId) return;
            _ridingNetId = 0;
            if (Shell == null || !IsInstanceValid(Shell)) { CleanupLocalVehicle(evt.NetId); return; }
            bool drivingLocal = Shell.IsDriving && _localVehicle != null;   // Part A mode (else the legacy puppet-ride shape)
            if (!drivingLocal && !Shell.IsRiding) { CleanupLocalVehicle(evt.NetId); return; }
            Vector3 exit;
            if (evt.Pos != UnityEngine.Vector3.zero)
                exit = new Vector3(evt.Pos.x, evt.Pos.y, evt.Pos.z);
            else if (Client.Vehicles.TryGet(evt.NetId, out var v))
            {
                float yawRad = Mathf.DegToRad(v.YawDegrees);
                var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));   // Godot yaw basis: right = (cos, 0, -sin)
                exit = new Vector3(v.Pos.x, v.Pos.y, v.Pos.z) + right * 2.4f + Vector3.Up * 1.0f;
            }
            else exit = Shell.GlobalPosition;   // no spot, no replica -- exit in place (the shell rode along)
            if (Terr != null)
            {
                float h = Terr.SampleHeight(exit.X, exit.Z);
                if (exit.Y < h + 0.1f) exit = new Vector3(exit.X, h + 0.5f, exit.Z);   // §7 risk 6: never below the slope
            }
            if (drivingLocal)
            {
                // Part A: restore the shell at the AUTHORITATIVE spot (the server computed it from ITS node,
                // which holds the adopted transform -- consistent by construction), destroy the local vehicle
                Shell.ExitVehicleAt(exit);
                CleanupLocalVehicle(evt.NetId);
            }
            else Shell.ExitPuppet(exit);
        }

        public override void _Process(double delta)
        {
            if (_status != null)
                _status.Text = Shell == null ? $"connecting to {Host}:{Port}   ·   {Client.State}   ·   players {Client.Players.Count}" : "";
            if (_desyncLabel != null) _desyncLabel.Text = _desyncAlert;
            if (_toast != null && _toastT > 0f) { _toastT -= (float)delta; if (_toastT <= 0f) _toast.Text = ""; }
            // DISCONNECTED overlay: latch once we've been Connected, then show the banner whenever the link
            // is no longer Connected -- so a server bounce / dropped link reads as DISCONNECTED, not a silent freeze
            if (Client.State == NetSessionState.Connected) _wasConnected = true;
            if (_disconnectBanner != null)
                _disconnectBanner.Visible = _wasConnected && Client.State != NetSessionState.Connected;
        }

        public override void _ExitTree()
        {
            if (DevConsole.RemoteClient == Client) DevConsole.RemoteClient = null;
            Client?.Disconnect();
        }
    }
}
