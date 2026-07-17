using Godot;
using SDG.Unturned;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // PEI_CLIENT_PLAN §3 Phase C5: materializes the replicated world items (Client.WorldItems -- LootField
    // streaming, player drops, salvage scrap) as STATIC visual props in the joined client's world. Reuses
    // the WorldItem model cache (mesh/texture by item id) but deliberately NOT the WorldItem node itself:
    // replicas carry no RigidBody3D physics (the server's physics owns the transform -- Pos mirrors it,
    // spawn point first, then the settled rest point), never join the "worlditems" group (no pickup focus,
    // and a listen-server's WorldItemNetSync must never re-mint them), and despawn only when the server
    // retires the entity. Pickup over the wire is deferred (§6) -- these are visible-only.
    //
    // Diff-driven per physics tick like DeployableReplicaView: idempotent against event/snapshot races and
    // join snapshots -- the replica registry IS the truth, the nodes follow.
    public partial class WorldItemReplicaView : Node
    {
        public NetWorldClient Client;

        readonly Dictionary<uint, Node3D> _nodes = new();
        bool _catalogReady;

        public int NodeCount => _nodes.Count;
        public bool TryGetNode(uint netId, out Node3D node) => _nodes.TryGetValue(netId, out node) && IsInstanceValid(node);

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null) return;
            var parent = GetParent();
            if (parent == null) return;
            // item id -> asset resolution (rarity tint for modelless ids) needs ItemCatalog.RegisterAll,
            // which runs in the shell's _Ready -- stay dormant until something registered the catalog
            if (!_catalogReady)
            {
                foreach (var _ in Assets.all()) { _catalogReady = true; break; }
                if (!_catalogReady) return;
            }

            var seen = new HashSet<uint>();
            foreach (var e in Client.WorldItems.All)
            {
                seen.Add(e.NetIdValue);
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                if (!_nodes.TryGetValue(e.NetIdValue, out var node) || !IsInstanceValid(node))
                {
                    node = BuildReplica(e);
                    parent.AddChild(node);
                    node.GlobalPosition = target;
                    node.ResetPhysicsInterpolation();   // don't smear from (0,0,0) to the spawn point (the WorldItem.Spawn lesson)
                    _nodes[e.NetIdValue] = node;
                }
                if (node.GlobalPosition != target) node.GlobalPosition = target;   // the settle event moves it once; snapshots correct it
            }

            List<uint> gone = null;   // server retired an entity (pickup/despawn) -> the visual leaves too
            foreach (var kv in _nodes)
                if (!seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
            if (gone != null)
                foreach (uint id in gone)
                {
                    if (IsInstanceValid(_nodes[id])) _nodes[id].QueueFree();
                    _nodes.Remove(id);
                }
        }

        static Node3D BuildReplica(WorldItemReplication.WorldItemEntity e)
        {
            var asset = Assets.find(e.ItemId);
            var rarity = asset != null ? ItemTool.RarityColorUI(asset.rarity) : Colors.White;
            var node = new Node3D();
            node.AddChild(WorldItem.BuildReplicaVisual(e.ItemId, rarity));
            // the SP drop pose (+90 X lays the model flat right-side-up) with a NetId-derived yaw for
            // variety -- deterministic, since the server's actual rest orientation never crosses the wire
            node.RotationDegrees = new Vector3(90f, (e.NetIdValue * 137u) % 360u, 0f);
            return node;
        }
    }
}
