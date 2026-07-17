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
        readonly Dictionary<uint, Wire> _wires = new();

        public int NodeCount => _nodes.Count;
        public bool TryGetNode(uint netId, out Deployable node) => _nodes.TryGetValue(netId, out node) && IsInstanceValid(node);

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
                if (!_nodes.TryGetValue(e.NetIdValue, out var node) || !IsInstanceValid(node))
                {
                    var def = DeployableDef.ById(e.DefId);
                    if (def == null) continue;
                    node = Deployable.Spawn(parent, def, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), e.YawDegrees);
                    _nodes[e.NetIdValue] = node;
                }
                node.Health = e.Health;
                node.Fuel = e.Fuel;
                node.NetSetPowered(e.ToggledOn);
            }
            RetireMissing(_nodes, seen, node => { if (IsInstanceValid(node)) node.QueueFree(); });

            // wires: create between the mapped ports (port index = def port order, the §2.6 sub-address)
            var seenWires = new HashSet<uint>();
            foreach (var w in Client.Deployables.AllWires)
            {
                seenWires.Add(w.NetIdValue);
                if (_wires.TryGetValue(w.NetIdValue, out var wire) && IsInstanceValid(wire)) continue;
                if (!TryGetNode(w.SrcId, out var src) || !TryGetNode(w.DstId, out var dst)) continue;
                if (w.SrcPort >= src.Ports.Count || w.DstPort >= dst.Ports.Count) continue;
                wire = new Wire();
                parent.AddChild(wire);
                wire.Source = src.Ports[w.SrcPort];
                wire.Consumer = dst.Ports[w.DstPort];
                wire.AddToGroup("wires");
                wire.SetPoints(new List<Vector3> { wire.Source.GlobalPosition, wire.Consumer.GlobalPosition }, true);
                _wires[w.NetIdValue] = wire;
                PowerNet.MarkDirty();
            }
            RetireMissing(_wires, seenWires, wire => { if (IsInstanceValid(wire)) wire.QueueFree(); PowerNet.MarkDirty(); });
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
