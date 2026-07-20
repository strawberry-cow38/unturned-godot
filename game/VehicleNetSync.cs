using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server side of the vehicle authority split (MP_PLAN §3.6): the real Vehicle nodes (VehicleBody3D on
    // the server world -- physics runs headless) stay authoritative; this sync
    //   1. mints a NetId for every "vehicles"-group node and publishes its physics state into
    //      VehicleReplication every 50 Hz tick (the composer's SnapshotDivisorTicks makes the wire cadence
    //      the plan's 25 Hz; a frozen parked car dirty-checks to zero delta bytes);
    //   2. applies the current REMOTE driver's held DriveInput to the node through the SAME Vehicle.Drive
    //      call the SP shell uses -- one drive seam, no second physics path;
    //   3. mirrors enter/exit side effects onto the node (EngineOn+Wake on enter, EngineOff+Park on exit --
    //      the SP EnterVehicle/ExitVehicle effects) and stamps Vehicle.NetDriverId so the local direct
    //      path can't take an occupied seat;
    //   4. publishes the LISTEN-SERVER local player's direct-path enter/exit as entity occupancy, so remote
    //      Enter commands validate against it (the entity's DriverPlayerId is the one occupancy truth);
    //   5. reconciles removals (a despawned wreck node retires its entity + ejects any driver).
    // Ticked between "net.server.sim" and "net.server.replicate" on the world's SimRoot (registered by
    // DedicatedServer/MpLoopback) so published state is this tick's, and replication still sends LAST.
    public sealed class VehicleNetSync
    {
        readonly NetWorldServer _server;
        readonly Node _host;

        public PlayerController LocalPlayer;        // listen-server/loopback local shell (null on dedicated)
        public System.Func<ushort> LocalPlayerId;   // loopback client id resolver (null on dedicated)

        sealed class Tracked
        {
            public uint NetId;
            public Vehicle Node;
            public ushort AppliedRemoteDriver;   // remote driver whose enter side effects are on the node
            public bool Held;                    // Part A: node frozen + teleport-adopted (the driver's client owns physics)
        }
        readonly Dictionary<Vehicle, Tracked> _tracked = new();
        readonly Dictionary<uint, Tracked> _byId = new();
        readonly List<Vehicle> _stale = new();

        public int TrackedCount => _tracked.Count;
        public bool TryGetNode(uint netId, out Vehicle node)
        {
            node = _byId.TryGetValue(netId, out var t) ? t.Node : null;
            return node != null && GodotObject.IsInstanceValid(node);
        }
        // The reverse of TryGetNode: a real node -> its minted entity NetId (0 = untracked). Used by the B11
        // tow choke point's neighbours and the L1 tie tests to address a specific host vehicle over the wire.
        public bool TryGetNetId(Vehicle node, out uint netId)
        {
            if (node != null && _tracked.TryGetValue(node, out var t)) { netId = t.NetId; return true; }
            netId = 0; return false;
        }

        public VehicleNetSync(NetWorldServer server, Node host)
        {
            _server = server;
            _host = host;
        }

        /// <summary>B11 server-side reach: the requester must be near BOTH vehicles being roped. The client aims
        /// within RopeReach (6 m, PlayerController) of a tow NODE, and a tow node can sit up to ~2.5 m out from
        /// the vehicle CENTER; the server bounds player&lt;-&gt;center generously (like ServerVehicles.EnterReach 6 m
        /// + that node offset), so a legit tie never false-rejects while a cross-map cheat still does.</summary>
        public const float RopeReach = 9f;

        /// <summary>B11: register the tow tie/untie commands on the SERVER command registry. GAME-side (not core
        /// ServerVehicles) because the apply mutates real Vehicle NODES (AttachTow/DetachTow). Dispatched inside
        /// Server.TickSimulation (net.server.sim), which runs BEFORE net.vehicles.sync -> the tie applied this
        /// tick is published by A6's ServerPublishTow the SAME tick and replicated LAST (§2.5). DedicatedServer +
        /// the consuming MpLoopback host both call this at setup; the pure-SP shell never does (no server peers).</summary>
        public void RegisterCommands()
        {
            _server.Commands.Register<AttachTowCommand>(ReplicationIds.CommandAttachTow, AttachTowCommand.TryRead,
                (sender, cmd) => OnAttachTow(sender, cmd),
                validate: (sender, cmd) => cmd.TowerNetId != 0 && cmd.TowedNetId != 0 && cmd.TowerNetId != cmd.TowedNetId);
            _server.Commands.Register<DetachTowCommand>(ReplicationIds.CommandDetachTow, DetachTowCommand.TryRead,
                (sender, cmd) => OnDetachTow(sender, cmd),
                validate: (sender, cmd) => cmd.NetId != 0);
        }

        // B11: the tie choke point + anti-cheat gate. Resolve both NetIds to real nodes, then validate: both
        // exist + in-tree, NEITHER remote-driven (a held/client-auth vehicle can't be a rope end), neither
        // already roped, and the requester is within reach of both. AttachTow does the FINAL physics gate
        // (same-car / wrecked / tow-point gap <= TowAttachReach) and computes restLen from the live gap, so the
        // command carries no length. A6's ServerPublishTow (net.vehicles.sync, this tick) mirrors the result back.
        void OnAttachTow(ushort sender, AttachTowCommand cmd)
        {
            if (!TryGetNode(cmd.TowerNetId, out var towerNode) || !TryGetNode(cmd.TowedNetId, out var towedNode)) return;
            if (!towerNode.IsInsideTree() || !towedNode.IsInsideTree()) return;
            if (IsRemoteDriven(cmd.TowerNetId) || IsRemoteDriven(cmd.TowedNetId)) return;
            if (towerNode.TowRoped || towedNode.TowRoped) return;   // neither already a rope end (AttachTow re-checks; fail-closed here too)
            if (!WithinReach(sender, towerNode) || !WithinReach(sender, towedNode)) return;
            towerNode.AttachTow(towedNode);
        }

        // B11: the untie choke point. NetId is EITHER end; DetachTow resolves to the tower like Vehicle.DetachTow
        // (no-ops if the node carries no rope). Validate existence + in-tree + reach; detaching only REMOVES a
        // constraint, so no remote-driven gate is needed (the enter-guard already drops a boarded car's rope).
        void OnDetachTow(ushort sender, DetachTowCommand cmd)
        {
            if (!TryGetNode(cmd.NetId, out var node) || !node.IsInsideTree()) return;
            if (!WithinReach(sender, node)) return;
            node.DetachTow();
        }

        // "remote-driven" = the entity has a driver that isn't the listen-server host (localId). On a dedicated
        // server localId==0, so any driver is remote; mirrors the `remote` bool Tick() computes.
        bool IsRemoteDriven(uint netId)
        {
            ushort localId = LocalPlayerId?.Invoke() ?? 0;
            return _server.Vehicles.TryGet(netId, out var e) && e.DriverPlayerId != 0 && e.DriverPlayerId != localId;
        }

        bool WithinReach(ushort sender, Vehicle node)
            => _server.Players.TryGetByOwner(sender, out var p)
               && (p.Pos - ToU(node.GlobalPosition)).magnitude <= RopeReach;

        /// <summary>B8 (SP/MP-unify): reconcile the LISTEN-SERVER local player's direct SP enter/exit into the
        /// entity occupancy (§3.6). A remote EnterVehicle command validates against DriverPlayerId (the one
        /// occupancy truth), so the host's direct-path Driving state must claim/free that seat here. Split OUT
        /// of Tick() and run as a PRE-SIM step (MpLoopback registers net.vehicles.occupancy BEFORE net.server.sim)
        /// so the seat reflects the host's CURRENT Driving state before the sim dispatches+validates any remote
        /// Enter that tick -- pre-B8 this ran inside Tick() (AFTER net.server.sim), so a same-tick remote Enter
        /// validated against a stale DriverPlayerId==0 and double-seated the host. Applies ONLY the claim/free;
        /// no mint/publish (Tick() owns those). No-op on a dedicated server (no local player -> localId==0) and
        /// in solo (no remotes to arbitrate). Iterates only already-tracked nodes -- a node minted THIS tick (in
        /// Tick, post-sim) is reconciled next tick, before which no entity exists for a remote to contest.</summary>
        public void ReconcileLocalOccupancy()
        {
            ushort localId = LocalPlayerId?.Invoke() ?? 0;
            if (localId == 0) return;
            long tick = _server.Session.CurrentTick;
            Vehicle localDriving = LocalPlayer != null && GodotObject.IsInstanceValid(LocalPlayer) ? LocalPlayer.Driving : null;
            foreach (var kv in _tracked)
            {
                var v = kv.Key;
                if (!GodotObject.IsInstanceValid(v)) continue;
                if (!_server.Vehicles.TryGet(kv.Value.NetId, out var e)) continue;
                if (v == localDriving && e.DriverPlayerId == 0)
                    _server.Vehicles.ServerSetDriver(new NetId(kv.Value.NetId), localId, tick);
                else if (v != localDriving && e.DriverPlayerId == localId)
                    _server.Vehicles.ServerSetDriver(new NetId(kv.Value.NetId), 0, tick);
            }
        }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            var tree = _host.GetTree();
            if (tree == null) return;
            ushort localId = LocalPlayerId?.Invoke() ?? 0;

            foreach (var n in tree.GetNodesInGroup("vehicles"))
            {
                if (n is not Vehicle v || !GodotObject.IsInstanceValid(v)) continue;
                if (!_tracked.TryGetValue(v, out var t))
                {
                    var id = _server.Ids.Mint();
                    int typeIdx = System.Array.IndexOf(Vehicle.SpecNames, v.SpecKey);
                    _server.Vehicles.ServerSpawn(id, (byte)(typeIdx < 0 ? 0 : typeIdx), (byte)v.SpawnVariant,
                                                 ToU(v.GlobalPosition), tick, v.SpeedMaxMps);   // Part A: the spec Speed_Max feeds the envelope's horizontal cap
                    t = new Tracked { NetId = id.Value, Node = v };
                    _tracked[v] = t;
                    _byId[t.NetId] = t;
                }
                if (!_server.Vehicles.TryGet(t.NetId, out var e)) continue;

                // B8 (SP/MP-unify): the LISTEN-SERVER local player's direct SP enter/exit -> occupancy truth
                // (§3.6) is now reconciled by ReconcileLocalOccupancy(), which MpLoopback registers as a PRE-SIM
                // step (net.vehicles.occupancy, before net.server.sim). Running it there -- rather than here,
                // AFTER net.server.sim -- claims/frees the seat BEFORE a remote EnterVehicle validates against
                // DriverPlayerId that same tick, closing the 1-tick double-seat race. Publish/drive/hold stays here.

                // a REMOTE driver's held DriveInput feeds the one drive seam every tick (the SP shell's cadence)
                ushort driver = e.DriverPlayerId;
                bool remote = driver != 0 && driver != localId;
                if (remote)
                {
                    if (t.AppliedRemoteDriver != driver)   // enter side effects (SP EnterVehicle: engine on)
                    {
                        t.AppliedRemoteDriver = driver;
                        v.NetDriverId = driver;
                        v.EngineOn = true;
                        v.Wake();
                        // B11: a remote driver taking a rope-end vehicle DROPS the rope. A client-auth/held
                        // vehicle can't stay a tow end -- the host's tow spring (Vehicle.UpdateTow) would fight
                        // the adopted transform. OnAttachTow's not-remote-driven validate blocks NEW ties on a
                        // driven car; this drops an EXISTING rope the moment such a car is boarded (either end).
                        if (v.Towing != null || v.TowedBy != null) v.DetachTow();
                        // Part A: ghost the driven body (layer bit0 -> bit6) the moment the seat is taken --
                        // the driver's CLIENT builds a real physics twin of this node; ghosting from enter
                        // (not first-adopt) closes the few-tick overlap window in a shared-tree L1 host.
                        // Players/bullets keep colliding via bit6; see Vehicle.NetGhost for the trade.
                        v.NetGhost(true);
                    }
                    if (v.Exploded)
                    {
                        // the blast ejects the driver (SP DriveVehicle exits + takes 150 damage; the remote
                        // kill-damage is deferred with the server vitals split -- see PROGRESS notes)
                        if (t.Held) { t.Held = false; v.NetAbortHold(); }   // Explode() already unfroze + flung the body
                        _server.VehicleHost.ServerExit(driver);
                    }
                    else if (_server.VehicleHost.TryGetPredictedState(t.NetId, out var st))
                    {
                        // Part A hold (retail updatePhysics kinematic, U3 InteractableVehicle.cs:1490-1519):
                        // the driver's client owns physics; this node freezes and teleports to the adopted
                        // state each tick so ballistics/occlusion/interaction/shove still see it live.
                        if (!t.Held) { t.Held = true; v.NetBeginHold(); }
                        var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(st.PitchDegrees),
                            Mathf.DegToRad(st.YawDegrees), Mathf.DegToRad(st.RollDegrees)));
                        v.NetHoldTeleport(new Transform3D(basis, new Vector3(st.Pos.x, st.Pos.y, st.Pos.z)));
                    }
                    else
                    {
                        _server.Vehicles.TryGetInput(t.NetId, out var inp);   // held-input model / pre-predict window: none yet = coast
                        v.Drive(inp.Throttle, inp.Steer, inp.Handbrake);
                    }
                }
                else if (t.AppliedRemoteDriver != 0)   // seat freed -> exit side effects (SP ExitVehicle)
                {
                    t.AppliedRemoteDriver = 0;
                    v.NetDriverId = 0;
                    v.EngineOn = false;
                    if (t.Held)
                    {
                        // Part A handoff: authority returns to the server exactly as retail
                        // removePlayer -> updatePhysics -- unfreeze, seed from the last adopted state
                        t.Held = false;
                        _server.Vehicles.TryGet(t.NetId, out var fe);
                        v.NetEndHold(fe != null ? new Vector3(fe.LinVel.x, fe.LinVel.y, fe.LinVel.z) : Vector3.Zero,
                                     fe != null ? new Vector3(fe.AngVel.x, fe.AngVel.y, fe.AngVel.z) : Vector3.Zero);
                    }
                    v.NetGhost(false);
                    if (!v.Exploded) v.Park();   // never Park a wreck -- it would kill the explosion tumble
                }

                if (t.Held)
                {
                    // Part A: adoption (core) owns the entity's transform/vel/steer/dressing -- the node
                    // contributes only the SERVER-owned scalars (fuel burn, damage, the Exploded flag)
                    _server.Vehicles.ServerPublishVitals(new NetId(t.NetId), v.Fuel, v.Health, v.Battery, v.Exploded, tick);
                }
                else
                {
                    // publish the node's physics state -> the wire entity (quantized + dirty-checked in core)
                    var euler = v.GlobalTransform.Basis.GetEuler() * (180f / Mathf.Pi);   // YXZ euler, degrees
                    byte flags = (byte)((v.EngineOn ? VehicleReplication.FlagEngineOn : 0)
                                      | (v.HeadlightsOn ? VehicleReplication.FlagHeadlights : 0)
                                      | (v.TaillightsOn ? VehicleReplication.FlagTaillights : 0)
                                      | (v.SirenOn ? VehicleReplication.FlagSiren : 0)
                                      | (v.BrakingNow ? VehicleReplication.FlagBraking : 0)
                                      | (v.Exploded ? VehicleReplication.FlagExploded : 0));
                    _server.Vehicles.ServerPublish(new NetId(t.NetId), ToU(v.GlobalPosition), ToU(euler),
                        ToU(v.LinearVelocity), ToU(v.AngularVelocity), v.SteerAngleDegrees,
                        v.Fuel, v.Health, v.Battery, flags, tick);
                }

                // A6: publish the rope-tow relationship UNCONDITIONALLY -- the tow fields are field-disjoint
                // from both the held (vitals) and non-held (transform) publish above, so this THIRD writer is
                // safe under either path and never touches the driver-state hot path. The node
                // (Vehicle.Towing + _towRestLen) is the sole truth; resolve the towed node to its entity
                // NetId (0 = not towing). The dirty-check in ServerPublishTow makes a static rope -- or a car
                // that isn't towing -- cost zero delta bytes. A one-tick lag if the towed node was minted
                // later in this same iteration is benign (next tick's dirty-check publishes it).
                uint towedNetId = 0;
                if (v.Towing != null && GodotObject.IsInstanceValid(v.Towing)
                    && _tracked.TryGetValue(v.Towing, out var towedTracked))
                    towedNetId = towedTracked.NetId;
                _server.Vehicles.ServerPublishTow(new NetId(t.NetId), towedNetId, v.TowRestLenValue, tick);
            }

            // freed nodes (despawned wrecks / teardown) -> eject any driver, retire the entity
            _stale.Clear();
            foreach (var kv in _tracked)
                if (!GodotObject.IsInstanceValid(kv.Key) || !kv.Key.IsInsideTree()) _stale.Add(kv.Key);
            foreach (var v in _stale)
            {
                var t = _tracked[v];
                _server.VehicleHost.OnVehicleRemoved(t.NetId);
                _server.Vehicles.ServerRemove(new NetId(t.NetId), tick);
                _byId.Remove(t.NetId);
                _tracked.Remove(v);
            }
        }

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
    }
}
