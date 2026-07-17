using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // D1 (PEI_COMBAT_PLAN §3): mirrors the replicated in-flight projectiles (Client.Projectiles -- server-
    // flown grenades, SystemId 4) as small glide-following visuals in the joined client's world. Without
    // this a thrown grenade is INVISIBLE until the GrenadeExploded event -- the system replicated since
    // Phase 5 with zero game-side consumers. The WorldItemReplicaView shape: diff-driven per physics tick,
    // the replica registry is the truth, nodes follow; freed when the server retires the entity (detonation).
    public partial class ProjectileReplicaView : Node
    {
        public NetWorldClient Client;

        readonly Dictionary<uint, Node3D> _nodes = new();

        public int NodeCount => _nodes.Count;
        public bool TryGetNode(uint netId, out Node3D node) => _nodes.TryGetValue(netId, out node) && IsInstanceValid(node);

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null) return;
            var parent = GetParent();
            if (parent == null) return;

            var seen = new HashSet<uint>();
            foreach (var e in Client.Projectiles.All)
            {
                seen.Add(e.NetIdValue);
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                if (!_nodes.TryGetValue(e.NetIdValue, out var node) || !IsInstanceValid(node))
                {
                    var vis = new Node3D();
                    vis.AddChild(Grenade.BuildVisual());   // kind is Grenade-only today; switch on e.Kind when more fly
                    parent.AddChild(vis);
                    vis.GlobalPosition = target;
                    vis.ResetPhysicsInterpolation();   // don't smear from (0,0,0) to the throw point
                    _nodes[e.NetIdValue] = vis;
                }
                else
                    node.GlobalPosition = node.GlobalPosition.Lerp(target, 0.4f);   // glide the 25 Hz snaps smooth
            }

            List<uint> gone = null;   // server retired it (detonation) -> the visual leaves; GrenadeExploded renders the bang
            foreach (var kv in _nodes)
                if (!seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
            if (gone != null)
                foreach (uint id in gone)
                {
                    if (IsInstanceValid(_nodes[id])) _nodes[id].QueueFree();
                    _nodes.Remove(id);
                }
        }
    }
}
