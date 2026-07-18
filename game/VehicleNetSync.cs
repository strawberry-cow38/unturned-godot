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

        public VehicleNetSync(NetWorldServer server, Node host)
        {
            _server = server;
            _host = host;
        }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            var tree = _host.GetTree();
            if (tree == null) return;
            ushort localId = LocalPlayerId?.Invoke() ?? 0;
            Vehicle localDriving = LocalPlayer != null && GodotObject.IsInstanceValid(LocalPlayer) ? LocalPlayer.Driving : null;

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

                // the LISTEN-SERVER local player's direct SP enter/exit -> occupancy truth (§3.6): remote
                // Enter commands validate against DriverPlayerId, so the direct path must claim/free it too
                if (localId != 0)
                {
                    if (v == localDriving && e.DriverPlayerId == 0)
                        _server.Vehicles.ServerSetDriver(new NetId(t.NetId), localId, tick);
                    else if (v != localDriving && e.DriverPlayerId == localId)
                        _server.Vehicles.ServerSetDriver(new NetId(t.NetId), 0, tick);
                }

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
