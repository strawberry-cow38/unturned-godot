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
                    // v27: the kind byte finally means something. A representative id per family, because the
                    // wire carries the FAMILY in flight and not the exact item -- so somebody else's blue smoke
                    // flies as a generic canister and only becomes blue when it pops (GrenadeExploded carries
                    // the real id). A generic model of the right SHAPE beats a frag standing in for everything.
                    var kind = (ProjectileKind)e.Kind;
                    ushort model = kind switch { ProjectileKind.Smoke => (ushort)267, ProjectileKind.Flare => (ushort)259, _ => (ushort)254 };
                    vis.AddChild(Grenade.BuildVisual(model));
                    // A flare is BURNING for its whole flight -- it was lit before it was thrown. The server
                    // keeps the entity alive for the burn and retires it at the end, so the light living on this
                    // node is exactly as long-lived as the flare.
                    if (kind == ProjectileKind.Flare)
                        vis.AddChild(new FlareBurn { Tint = new Color(0.95f, 0.45f, 0.2f), Duration = 3600f });   // Duration is a safety net only: the node is freed when the server retires the entity
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
