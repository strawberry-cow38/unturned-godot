using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // A4 (SP/MP-unify): materializes the replicated crops (Client.Crops -- server-owned CropReplication:
    // remote/console Plant commands, plus the listen-server host's own locally-planted crops mirrored by
    // CropNetSync) as real CropNode visuals in the joined client's world. The SOLE crop materializer on a
    // joined client: the shell attaches withCropManager:false, so there is NO client CropManager and the SP
    // direct plant/harvest branch never fires -- the server owns growth.
    //
    // DOUBLE-AUTHORITY GUARD: this view is CLIENT-ONLY. Do NOT attach it on MpLoopback -- the loopback host
    // owns real CropManager nodes AND CropNetSync mints entities from them, so a view there would double
    // every crop (the P2b passive-loot doubling failure). ClientWorldSession is the only attach site.
    //
    // Growth is DERIVED per tick, never clocked: grown = Client.Crops.IsGrown(e, LastAppliedServerTick)
    // against the content-hash-matched schema (PlantedAtTick + GrowthSeconds), read straight off the applied
    // snapshot tick -- no client CropManager clock, no wall/frame time, so it stays bit-consistent with the
    // server both directions. Diff-driven per physics tick like WorldItemReplicaView: the replica registry
    // IS the truth and the nodes follow (idempotent against event/snapshot races + join snapshots).
    public partial class CropReplicaView : Node
    {
        public NetWorldClient Client;

        readonly Dictionary<uint, CropNode> _nodes = new();

        public int NodeCount => _nodes.Count;
        public bool TryGetNode(uint netId, out CropNode node) => _nodes.TryGetValue(netId, out node) && IsInstanceValid(node);

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null) return;
            var parent = GetParent();
            if (parent == null) return;

            long tick = Client.Applier.LastAppliedServerTick;   // the tick every growth stage derives against
            var seen = new HashSet<uint>();
            foreach (var e in Client.Crops.All)
            {
                if (!CropRegistry.TryBySeed(e.SeedId, out string name)) continue;   // unknown seed: nothing to grow into (skip -- never materialized)
                seen.Add(e.NetIdValue);
                if (!_nodes.TryGetValue(e.NetIdValue, out var node) || !IsInstanceValid(node))
                {
                    node = CropNode.Spawn(name);
                    node.NetId = e.NetIdValue;              // the harvest scan addresses the server entity by this id
                    node.AddToGroup("crop");               // RequestHarvestNearestCrop scans this group
                    parent.AddChild(node);
                    node.GlobalPosition = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                    node.ResetPhysicsInterpolation();      // don't smear from (0,0,0) to the plant point (the WorldItem.Spawn lesson)
                    _nodes[e.NetIdValue] = node;
                }
                node.SetGrown(Client.Crops.IsGrown(e, tick));   // tick-derived stage, straight off the snapshot tick (no client clock)
            }

            List<uint> gone = null;   // server retired an entity (harvest/despawn) -> the visual leaves too
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
