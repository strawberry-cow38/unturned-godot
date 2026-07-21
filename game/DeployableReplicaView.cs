using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // MP_PLAN §3.1, client side: materializes the replicated deployable GRAPH as real Deployable/Wire nodes
    // and lets the existing LOCAL power path light the lamps -- PowerNet.RecomputeIfDirty walks these nodes
    // into the same pure PowerSolver the server ran, so Live/Powered/Draw, lamp ramps, flicker and
    // vibration all derive locally from replicated INPUTS, exactly as they derive in single-player.
    //
    // Diff-driven (poll the replica registry per physics tick) rather than event-driven: idempotent against
    // event/snapshot races, join snapshots, and wire cascades -- the replica IS the truth, the nodes follow.
    //
    // Deliberately visual-only fidelity gaps (deferred with the damage phase): a replica node mirrors
    // health/fuel/toggle, but the local fire/explosion LIFECYCLE (TakeDamage -> Explode -> ExplodeDamage)
    // never runs here -- a client must not apply area damage locally. Removal comes as the entity vanishing.
    public partial class DeployableReplicaView : Node
    {
        public NetWorldClient Client;

        readonly Dictionary<uint, Deployable> _nodes = new();
        readonly Dictionary<uint, GridPowerSource> _grids = new();   // A3: server-placed grid-power SOURCE fixtures (a GridPowerSource node, not a Deployable body)
        readonly Dictionary<uint, GasPump> _gaspumps = new();        // A2: server-placed gas-pump fixtures (a GasPump node, not a Deployable body)
        readonly Dictionary<uint, OilPump> _oilpumps = new();        // oil pump/derrick fixtures (a view-only OilPump node; server owns the Fuel reservoir)
        readonly Dictionary<uint, Sentry> _sentries = new();         // auto-turret fixtures (a view-only Sentry node; the server-side ServerSentries owns scan/fire/kill)
        readonly Dictionary<uint, Trap> _traps = new();              // trap fixtures (a view-only Trap node rendering the archetype; the server-side ServerTraps owns trigger/damage)
        readonly Dictionary<uint, Wire> _wires = new();

        public int NodeCount => _nodes.Count;
        public bool TryGetNode(uint netId, out Deployable node) => _nodes.TryGetValue(netId, out node) && IsInstanceValid(node);
        public int GridCount => _grids.Count;   // A3: how many grid-source fixtures have materialized
        public bool TryGetGrid(uint netId, out GridPowerSource grid) => _grids.TryGetValue(netId, out grid) && IsInstanceValid(grid);
        public int GasPumpCount => _gaspumps.Count;   // A2: how many gas-pump fixtures have materialized
        public bool TryGetGasPump(uint netId, out GasPump pump) => _gaspumps.TryGetValue(netId, out pump) && IsInstanceValid(pump);
        public int OilPumpCount => _oilpumps.Count;   // how many oil-pump fixtures have materialized
        public bool TryGetOilPump(uint netId, out OilPump pump) => _oilpumps.TryGetValue(netId, out pump) && IsInstanceValid(pump);
        public int SentryCount => _sentries.Count;   // how many sentry fixtures have materialized
        public bool TryGetSentry(uint netId, out Sentry sentry) => _sentries.TryGetValue(netId, out sentry) && IsInstanceValid(sentry);
        public int TrapCount => _traps.Count;   // how many trap fixtures have materialized
        public bool TryGetTrap(uint netId, out Trap trap) => _traps.TryGetValue(netId, out trap) && IsInstanceValid(trap);

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null) return;
            var parent = GetParent();
            if (parent == null) return;

            // deployables: spawn missing, retire gone, mirror scalars/toggle on the rest
            var seen = new HashSet<uint>();
            foreach (var e in Client.Deployables.All)
            {
                seen.Add(e.NetIdValue);
                var def = DeployableDef.ById(e.DefId);
                if (def == null) continue;   // FAIL-CLOSED: an unregistered def (content-hash drift) never materializes -- a missing render, never a desync
                if (def.Fixture == FixtureKind.GridSource)
                {
                    // A3: a server-placed grid-power mains SOURCE -- a GridPowerSource node (NOT a Deployable body).
                    // Its producing derives from the replicated entity.ToggledOn (mains bit), never local GlobalPower.
                    if (!_grids.TryGetValue(e.NetIdValue, out var grid) || !IsInstanceValid(grid))
                    {
                        float watts = def.Ports.Length > 0 ? def.Ports[0].Watts : GridPowerSource.DefaultWatts;
                        grid = GridPowerSource.Materialize(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees, watts, e.NetIdValue);
                        _grids[e.NetIdValue] = grid;
                    }
                    grid.NetProducingOverride = e.ToggledOn;
                    continue;
                }
                if (def.Fixture == FixtureKind.GasPump)
                {
                    // A2: a server-placed gas-station pump -- a GasPump node (NOT a Deployable body). Its fuel bar
                    // rides the replicated 0..100 station-fill percent (entity.Fuel); it owns no local tank, and
                    // an RMB extract routes over the wire (the server drains the shared tank -> re-broadcasts the
                    // percent onto every same-station pump). _input.Powered still solves locally from the replicated
                    // wire graph, so "no power" shows correctly.
                    if (!_gaspumps.TryGetValue(e.NetIdValue, out var pump) || !IsInstanceValid(pump))
                    {
                        pump = GasPump.Materialize(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees, e.NetIdValue);
                        _gaspumps[e.NetIdValue] = pump;
                    }
                    pump.FillPercent = e.Fuel;   // the replicated 0..100 percent of the shared station tank
                    continue;
                }
                if (def.Fixture == FixtureKind.OilPump)
                {
                    // an oil pump / derrick -- a VIEW-ONLY OilPump node (NOT a Deployable body). The server owns the
                    // fuel reservoir (regen/siphon run server-side); the replica just mirrors entity.Fuel so the beam
                    // rocks + the fuel bar reads right. No local regen (OilPump.IsReplica guards it in Materialize).
                    if (!_oilpumps.TryGetValue(e.NetIdValue, out var opump) || !IsInstanceValid(opump))
                    {
                        opump = OilPump.Materialize(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees, e.NetIdValue);
                        _oilpumps[e.NetIdValue] = opump;
                    }
                    opump.Fuel = e.Fuel;   // server-owned reservoir level, mirrored onto the view
                    continue;
                }
                if (def.Fixture == FixtureKind.Sentry)
                {
                    // an auto-turret -- a VIEW-ONLY Sentry node. It renders + aims off the already-replicated zombies
                    // (Fire draws a tracer, NEVER DamageHit); the server-side ServerSentries owns the authoritative
                    // scan/fire/kill. Nothing to mirror per-tick beyond existence (Health drives no view state yet).
                    if (!_sentries.TryGetValue(e.NetIdValue, out var sentry) || !IsInstanceValid(sentry))
                    {
                        sentry = Sentry.Materialize(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees, e.NetIdValue);
                        _sentries[e.NetIdValue] = sentry;
                    }
                    continue;
                }
                if (def.Fixture == FixtureKind.Trap)
                {
                    // a trap -- a VIEW-ONLY Trap node rendering the archetype (picked from the entity DefId); the
                    // server-side ServerTraps owns the edge-trigger/bite/landmine. Static visual: nothing to mirror
                    // per-tick beyond existence (a worn-down/spent trap vanishes when the server retires the entity).
                    if (!_traps.TryGetValue(e.NetIdValue, out var trap) || !IsInstanceValid(trap))
                    {
                        trap = Trap.Materialize(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees, e.NetIdValue, e.DefId);
                        _traps[e.NetIdValue] = trap;
                    }
                    continue;
                }
                if (!_nodes.TryGetValue(e.NetIdValue, out var node) || !IsInstanceValid(node))
                {
                    node = Deployable.Spawn(parent, def, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees);
                    node.NetId = e.NetIdValue;   // the shell's salvage/toggle/wire requests address the entity by this
                    _nodes[e.NetIdValue] = node;
                }
                node.Health = e.Health;
                node.Fuel = e.Fuel;
                node.NetSetPowered(e.ToggledOn);
            }
            RetireMissing(_nodes, seen, node => { if (IsInstanceValid(node)) node.QueueFree(); });
            RetireMissing(_grids, seen, grid => { if (IsInstanceValid(grid)) grid.QueueFree(); PowerNet.MarkDirty(); });
            RetireMissing(_gaspumps, seen, pump => { if (IsInstanceValid(pump)) pump.QueueFree(); PowerNet.MarkDirty(); });
            RetireMissing(_oilpumps, seen, pump => { if (IsInstanceValid(pump)) pump.QueueFree(); });
            RetireMissing(_sentries, seen, sentry => { if (IsInstanceValid(sentry)) sentry.QueueFree(); });
            RetireMissing(_traps, seen, trap => { if (IsInstanceValid(trap)) trap.QueueFree(); });

            // wires: create between the mapped ports (port index = def port order, the §2.6 sub-address).
            // Endpoints may be a Deployable body OR a GridPowerSource fixture (A3), so resolve ports from both.
            var seenWires = new HashSet<uint>();
            foreach (var w in Client.Deployables.AllWires)
            {
                seenWires.Add(w.NetIdValue);
                if (_wires.TryGetValue(w.NetIdValue, out var wire) && IsInstanceValid(wire)) continue;
                if (!TryGetPort(w.SrcId, w.SrcPort, out var srcPort) || !TryGetPort(w.DstId, w.DstPort, out var dstPort)) continue;
                wire = new Wire { NetId = w.NetIdValue };   // the shell's remove-wire requests address it by this
                parent.AddChild(wire);
                wire.Source = srcPort;
                wire.Consumer = dstPort;
                wire.AddToGroup("wires");
                wire.SetPoints(new List<Vector3> { srcPort.GlobalPosition, dstPort.GlobalPosition }, true);
                _wires[w.NetIdValue] = wire;
                PowerNet.MarkDirty();
            }
            RetireMissing(_wires, seenWires, wire => { if (IsInstanceValid(wire)) wire.QueueFree(); PowerNet.MarkDirty(); });
        }

        // Resolve a wire endpoint's ConnectionPort by (netId, portIndex), from either a materialized Deployable
        // body or a GridPowerSource fixture (A3). Both expose their ports in def-authored order (the §2.6 sub-address).
        bool TryGetPort(uint netId, byte portIndex, out ConnectionPort port)
        {
            port = null;
            if (_nodes.TryGetValue(netId, out var d) && IsInstanceValid(d))
            {
                if (portIndex >= d.Ports.Count) return false;
                port = d.Ports[portIndex];
                return true;
            }
            if (_grids.TryGetValue(netId, out var g) && IsInstanceValid(g))
            {
                if (portIndex >= g.PowerPorts.Count) return false;
                port = g.PowerPorts[portIndex];
                return true;
            }
            if (_gaspumps.TryGetValue(netId, out var gp) && IsInstanceValid(gp))   // A2: a gas pump is a wire-able Consumer endpoint
            {
                if (portIndex >= gp.PowerPorts.Count) return false;
                port = gp.PowerPorts[portIndex];
                return true;
            }
            return false;
        }

        static void RetireMissing<T>(Dictionary<uint, T> nodes, HashSet<uint> seen, System.Action<T> retire)
        {
            List<uint> gone = null;
            foreach (var kv in nodes)
                if (!seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
            if (gone == null) return;
            foreach (uint id in gone)
            {
                retire(nodes[id]);
                nodes.Remove(id);
            }
        }
    }
}
