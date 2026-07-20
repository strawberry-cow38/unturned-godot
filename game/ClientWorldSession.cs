using Godot;
using SDG.NetTransport;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // The joined-client composition node -- the MpLoopback shape for the REMOTE case. Owns the
    // NetWorldClient session, the replica views, and the LOCAL PLAYER SHELL: a real PlayerController
    // (real input/camera/viewmodel -- NOT a NetAvatar) spawned at the FIRST authoritative own-entity
    // sample (the server-adopted spawn).
    //
    // ON-FOOT MOVEMENT IS CLIENT-AUTHORITATIVE (mp-clientauth-foot, wire v9 -- the Part A vehicle model
    // widened to walking): the shell runs the exact SP movement path and each 50 Hz tick STREAMS its
    // transform (SendPlayerState); the server envelope-validates the claim (ServerPlayerAuthority) and
    // adopts it bit-exact. There is ONE body per player and NOTHING to reconcile -- the C1-C3
    // predict/reconcile/rewind-replay stack is deleted, and in normal play the owner feels zero
    // correction at any RTT. The only rubber-band left is PlayerRecovEvent: a genuine envelope
    // violation (speed/fly/teleport) or a server-side teleport (console/respawn), applied in ShellStep
    // through TeleportTo (§7 risk 5: corrections must shift the render-interp samples WITH the node).
    // Driving is the same model for the vehicle (Part A); every OTHER entity is a replica view.
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
        // P3 holiday parity (wire v6): wired by Main.BuildClient to WorldBuildResult.ApplyHoliday --
        // invoked exactly once, on the first Connected tick, with the SERVER's activeHoliday from the
        // Accept, so the deferred holiday props/colliders + the resource field build as the server's
        // world, never this machine's wall clock's.
        public System.Action<string> ApplyServerHoliday;
        bool _holidayApplied;
        ResourceAliveView _resourceView;

        public NetWorldClient Client { get; private set; }
        public RemotePlayers Remotes { get; private set; }
        public ZombiePuppets Puppets { get; private set; }        // C5: server zombies as interpolated puppets
        public WorldItemReplicaView Items { get; private set; }   // C5: replicated world items as static visuals
        public CropReplicaView Crops { get; private set; }        // A4: replicated crops as real CropNodes (the SOLE crop materializer on a joined client -- no client CropManager)
        public DeployableReplicaView Deploys { get; private set; }   // Phase 6/8: replicated deployables/wires as real nodes (L1 tests reach the netId-stamped nodes through this)
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
        // mp-clientauth-foot (v9): walk-recov state -- the counter echo every state send carries, the
        // pending rollback ShellStep applies in physics context, and the counter the L1 teeth assert on.
        // In normal play RecovsApplied stays 0 at any RTT: there is no server sim of the owner left to
        // diverge from, so the ONLY rubber-band left is a genuine envelope violation / server teleport.
        byte _recovAck;
        PlayerRecovEvent? _pendingRecov;
        public long RecovsApplied { get; private set; }

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
            // mp-clientauth-foot (v9): the server rolled our on-foot claim back (envelope violation, or a
            // server-side teleport delivered as recov). Latch the ack ALWAYS -- the state stream must echo
            // it even if a seat interleaves; the rollback itself applies in ShellStep (physics context,
            // before the shell node's own _PhysicsProcess), on foot only.
            Client.PlayerRecov += e =>
            {
                _recovAck = e.RecovCounter;
                if (_ridingNetId == 0) _pendingRecov = e;
            };
            DeployableNetSchema.RegisterAll(Client.Deployables.Schema);
            CropNetSchema.RegisterAll(Client.Crops.Schema);   // §3.7: growth stages derive from the synced defs + snapshot tick
            DevConsole.RemoteClient = Client;                 // server-gated cheats: give/xp/skill route over the console command plane (§2.3)

            // replica views -- puppets/replicas land INSIDE the real world (they parent to this node / its parent)
            Remotes = new RemotePlayers { Client = Client };  // remote players as CharacterModel puppets; self never puppets (the shell owns self)
            AddChild(Remotes);
            Deploys = new DeployableReplicaView { Client = Client };
            AddChild(Deploys);
            VehicleView = new VehicleReplicaView { Client = Client };   // server vehicles as dead-reckoned puppets (§3.6)
            AddChild(VehicleView);
            // C6 ride mode: the server's seat facts drive the shell's enter/exit -- the client never seats
            // itself (SendEnterVehicle is a REQUEST; the seat lands only when the validated fact comes back)
            Client.VehicleEntered += e => { if (e.PlayerId == Client.PlayerId) { _ridingNetId = e.NetId; Client.ClearCombatRing(); } };
            Client.VehicleExited += OnVehicleExited;
            // v10 (mp-event-coalesce): a dead or just-seated owner's un-acked combat backlog must not
            // resurrect when the gate re-opens (respawn / vehicle exit). The server's alive/not-seated
            // validate rejects those state packets, so they never ack and never drain -- drop the ring the
            // moment we observe our OWN seat/death/respawn. (Seat is handled just above.)
            // P3a (SP/MP-unify): the server owns this owner's HP + death/respawn lifecycle. On our OWN death,
            // render it on the shell (Die() corpse + death-cam, local self-respawn clock DISABLED -- the server
            // owns the 3.5 s timer) and drop the un-acked combat backlog. On our OWN respawn, render the
            // Respawn() visuals WITHOUT a local reposition -- the move to SpawnPos rides the server's
            // PlayerRecovEvent (freeze-until-echo), which lands via _pendingRecov in ShellStep.
            Client.PlayerDied += e => { if (e.Victim == Client.PlayerId) { if (Shell != null && IsInstanceValid(Shell)) Shell.NetDie(); Client.ClearCombatRing(); } };
            Client.PlayerRespawned += e => { if (e.PlayerId == Client.PlayerId) { if (Shell != null && IsInstanceValid(Shell)) Shell.NetRespawn(reposition: false); Client.ClearCombatRing(); } };
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
            // A4: the SOLE crop materializer on a joined client -- the shell has no CropManager (server owns
            // growth), so this view is the only thing that renders crops. NEVER on MpLoopback (double-render).
            Crops = new CropReplicaView { Client = Client };
            AddChild(Crops);
            AddChild(new WorldClockView { Client = Client, DayNight = DayNight });
            _resourceView = new ResourceAliveView { Client = Client, Field = Resources };
            AddChild(_resourceView);
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
            // Phase 6/8 storage arbitration facts (sent only to the opener): the dashboard opens/latches on
            // the SERVER's say-so -- the crate grid itself rides the owner inventory echo (STORAGE page 7)
            Client.StorageOpened += e => { if (Shell != null && IsInstanceValid(Shell)) Shell.OnReplicatedStorageOpened(e.NetId); };
            Client.StorageClosed += e => { if (Shell != null && IsInstanceValid(Shell)) Shell.OnReplicatedStorageClosed(); };

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
            if (!_holidayApplied)
            {
                // P3: first Connected tick -- build the deferred holiday world content with the SERVER's
                // holiday (Accept, wire v6), then let the alive-bitmap view re-apply onto the fresh field
                _holidayApplied = true;
                ApplyServerHoliday?.Invoke(Client.ServerHoliday ?? "");
                _resourceView?.Refresh();
            }
            if (Shell == null || !IsInstanceValid(Shell))
            {
                // the reconcile-seed pattern (ClientPrediction.Reconcile): the FIRST authoritative
                // own-entity sample IS the spawn adoption -- the shell spawns exactly where the server
                // put the entity (real Spawns/Players.dat point at terrain height, C2's SpawnProvider)
                if (Client.Players.TryGetByOwner(Client.PlayerId, out var me)) SpawnShell(me);
                return;
            }
            // owner skills block -> the shell's local PlayerSkills (the AdoptReplicatedInventory analogue;
            // SkillsReplication has no per-echo event, so mirror every tick -- 23 bytes, idempotent)
            if (Client.Skills.TryGet(Client.PlayerId, out var sk)) Shell.AdoptReplicatedSkills(sk.Skills);
            // P3a (SP/MP-unify): owner HP is server-authoritative -- mirror the replicated CombatEntity coarse
            // health (0..100) into the shell each tick (the AdoptReplicatedSkills analogue; PlayerCombatReplication
            // has no per-echo event either). Adoption is the LAST HP writer so local regen/starve can't move it;
            // the HUD keeps its exact Player.Health read. Runs before the riding branch so HP tracks while seated too.
            if (Client.CombatState.TryGet(Client.PlayerId, out var vit)) Shell.AdoptReplicatedVitals(vit.Health);
            // Part A DRIVING (CLIENT_PREDICTION_PLAN §5.2 A1, replacing the C6 v1 puppet-ride): seated in a
            // replicated vehicle -- the shell drives a CLIENT-LOCAL real Vehicle through the SP direct-drive
            // path (0 ms wheel response; retail client authority) and this step streams VehicleState @25 Hz
            // INSTEAD of MoveInput -- the server drops a seated peer's walk input at the choke point anyway,
            // and the reconciler idles (the seat teleport owns the entity; corrections must never fight it).
            if (_ridingNetId != 0) { DriveStep(); return; }
            if (Shell.IsDriving) return;   // the SP direct-drive guard (unreachable in MP -- the seat always latches _ridingNetId first)

            // mp-clientauth-foot (v9): the shell OWNS its on-foot movement -- there is no server sim of
            // the owner left to diverge from, so there are no corrections in normal play at ANY RTT.
            // The shell just REPORTS its transform; the server envelope-validates + adopts (the Part A
            // vehicle model, ServerPlayerAuthority). Recov -- the rubber-band -- is reserved for genuine
            // envelope violations and server-side teleports.
            // 1) a server rollback lands FIRST: teleport the shell to the last-good payload (TeleportTo
            //    resets the interp snapshots, §7 risk 5), re-seed the ballistic velocity, and let this
            //    very tick's state send carry the counter echo -- the resume claim.
            if (_pendingRecov is PlayerRecovEvent rc)
            {
                _pendingRecov = null;
                RecovsApplied++;
                Shell.TeleportTo(new Vector3(rc.Pos.x, rc.Pos.y, rc.Pos.z));
                Shell.NetRecovRestore(rc.Vel);
                GD.Print($"[CLIENT] walk recov #{rc.RecovCounter} -- rolled back to ({rc.Pos.x:0.0},{rc.Pos.y:0.0},{rc.Pos.z:0.0})");
            }

            // 2) stream this tick's transform (the VehicleState analogue, @50 Hz): position on the exact
            //    snapshot grid + facing + sim velocity + stance/jump dressing + grounded. An adopted
            //    claim replicates back bit-exact, so observers render the owner's own view of itself.
            var p = Shell.TruePhysicsPosition;
            byte buttons = (byte)((Shell.LastJumpInput ? MoveInput.ButtonJump : (byte)0) | MoveInput.PackStance(Shell.Stance));
            Client.SendPlayerState(new UnityEngine.Vector3(p.X, p.Y, p.Z), Shell.RotationDegrees.Y, Shell.LookPitchDegrees,
                                   Shell.MoveSimVelocity, buttons, Shell.LastGroundedInput, _recovAck);

            if (NetLog.Enabled) LogClientAuthRollupIfDue();
        }

        // ---- Part A driving-local machinery (CLIENT_PREDICTION_PLAN §5.2 A1/A4) ----

        void DriveStep()
        {
            // mp-clientauth-foot (v9): a walk recov in flight when the seat latched is void -- the seat
            // teleport owns the entity (the ACK stays latched, so the post-exit stream still echoes it
            // and a pre-enter Recovering window on the server clears on the first post-exit claim).
            _pendingRecov = null;

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

        // ---- observability: the client mirror of the server's "[NET] 1s:" line. Under client-auth the
        // interesting numbers are the recov count (0 in normal play -- ANY nonzero means the envelope
        // tripped) and how far the published own-entity lags the shell (~one uplink of claims). This is
        // what --netlog shows on a real WAN link. Zero overhead when NetLog is off (call site gated). ----
        int _caTicks; long _caRecovs;

        void LogClientAuthRollupIfDue()
        {
            if (++_caTicks < NetProtocol.TicksPerSecond) return;
            float lag = Client.Players.TryGetByOwner(Client.PlayerId, out var own) && Shell != null && IsInstanceValid(Shell)
                ? (own.Pos - ToU(Shell.TruePhysicsPosition)).magnitude : 0f;
            NetLog.Sink($"[NET-CLIENT] 1s: recovs {RecovsApplied - _caRecovs} | own-entity lag {lag:0.###} m");
            _caTicks = 0; _caRecovs = RecovsApplied;
        }

        void SpawnShell(PlayerReplication.PlayerEntity me)
        {
            // mp-clientauth-foot (v9): the MP shell IS the SP player -- no DeterministicGround fork left
            // (that seam existed so shell and server avatar made the same grounded decision; there is no
            // server avatar sim anymore). One movement code path, SP-identical feel.
            var shell = new PlayerController { CaptureMouse = true };
            AddChild(shell);
            // P3b (review finding 5): this MP shell's HP is server-authoritative (adopted each ShellStep). Latch
            // the spawn-window guard NOW so a fall/starvation death in the 1-3 ticks before the first
            // AdoptReplicatedVitals cannot fire a LOCAL death that would fight the server clock. Server-owned
            // fall/OOB for this owner are DERIVED from its state claims; its local TakeDamage stays a no-op.
            shell.ExpectServerVitals();
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
            // Phase 6/8 shell seams (mp-parity-clientseams): every remaining UI action routes as INTENT --
            // grid ops + consume echo back through the owner inventory block, deployable/wire ops through
            // the broadcast facts + replica view, storage through StorageOpened/Closed + the STORAGE page,
            // skills through the owner skills block (adopted each tick in ShellStep).
            shell.NetMoveItem = (p0, x0, y0, p1, x1, y1, rot1) => Client.SendMoveItem(p0, x0, y0, p1, x1, y1, rot1);
            shell.NetEquipItem = (page, x, y, slot) => Client.SendEquipItem(page, x, y, slot);
            shell.NetDropItem = (page, x, y) => Client.SendDropItem(page, x, y);
            shell.NetConsume = (page, x, y) => Client.SendConsume(page, x, y);
            shell.NetCraft = index => Client.SendCraft(index);
            shell.NetPlaceDeployable = (defId, pos, yaw) => Client.SendPlaceDeployable(defId, ToU(pos), yaw);
            shell.NetSalvageDeployable = netId => Client.SendSalvageDeployable(netId);
            shell.NetPickupDeployable = netId => Client.SendPickupDeployable(netId);   // B2: hold-F returns the live deployable to the bag over the wire
            shell.NetConnectWire = (srcId, srcPort, dstId, dstPort) => Client.SendConnectWire(srcId, srcPort, dstId, dstPort);
            shell.NetRemoveWire = wireId => Client.SendRemoveWire(wireId);
            shell.NetToggleDeployable = (netId, on) => Client.SendToggleDeployable(netId, on);
            shell.NetOpenStorage = netId => Client.SendOpenStorage(netId);
            shell.NetCloseStorage = () => Client.SendCloseStorage();
            shell.NetUpgradeSkill = (spec, index) => Client.SendUpgradeSkill(spec, index);
            // A4: crops route as intents -- plant sends seed+point, harvest sends the grown replica's NetId;
            // the CropReplicaView renders the result (materialize / grow / despawn) + the yield rides Items.
            shell.NetPlantCrop = (seedId, pos) => Client.SendPlantCrop(seedId, ToU(pos));
            shell.NetHarvestCrop = netId => Client.SendHarvestCrop(netId);
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
