using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // A1 (SP/MP-unify), client side: materializes the replicated world containers (Client.Containers -- the server-owned
    // ContainerReplication fixtures the world build placed) as real StoreShelf nodes in the joined client's / consuming
    // loopback host's world. Each fixture carries its KindId (-> ContainerSchema: mesh/display/label/grid dims), pos/yaw,
    // and a display DIGEST (what item sits in each visible tier cell). The materialized shelf is ServerOwned -- it does
    // NO local loot roll; its tier display rides the digest via ApplyDisplay, and its real grid lives server-side (opened
    // over the wire on F -- B9). Mirrors DeployableReplicaView / CropReplicaView.
    //
    // Diff-driven per physics tick: the replica registry IS the truth, the nodes follow (idempotent against
    // event/snapshot races + join snapshots). CONSUME path only -- attached exactly where the SP-local
    // SpawnMapContainers is gated OFF, so a container never doubles (SP node + puppet).
    public partial class StorageReplicaView : Node
    {
        public NetWorldClient Client;

        struct Entry { public StoreShelf Node; public ulong DisplaySig; }
        readonly Dictionary<uint, Entry> _nodes = new();

        public int NodeCount => _nodes.Count;
        public bool TryGetNode(uint netId, out StoreShelf node)
        {
            if (_nodes.TryGetValue(netId, out var e) && IsInstanceValid(e.Node)) { node = e.Node; return true; }
            node = null; return false;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null) return;
            var parent = GetParent();
            if (parent == null) return;

            var seen = new HashSet<uint>();
            foreach (var e in Client.Containers.All)
            {
                seen.Add(e.NetIdValue);
                var kind = ContainerSchema.Get(e.KindId);
                if (!_nodes.TryGetValue(e.NetIdValue, out var entry) || !IsInstanceValid(entry.Node))
                {
                    // table 0: the client never rolls (ServerOwned) -- contents ride the digest / open over the wire
                    var node = StoreShelf.Spawn(parent, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z), kind.Mesh, 0,
                                                e.YawDegrees, kind.Display, kind.Label, renderMesh: true, serverOwned: true);
                    node.NetId = e.NetIdValue;             // the shell's F-open request addresses the server entity by this (B9)
                    node.ResetPhysicsInterpolation();      // don't smear from (0,0,0) to the placement (the WorldItem.Spawn lesson)
                    entry = new Entry { Node = node, DisplaySig = ulong.MaxValue };   // MaxValue forces the first ApplyDisplay
                    _nodes[e.NetIdValue] = entry;
                }
                ulong sig = DisplaySig(e.Display);
                if (sig != entry.DisplaySig)
                {
                    entry.Node.ApplyDisplay(e.Display);
                    entry.DisplaySig = sig;
                    _nodes[e.NetIdValue] = entry;
                }
            }

            List<uint> gone = null;   // server retired a fixture -> the visual leaves too
            foreach (var kv in _nodes)
                if (!seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
            if (gone != null)
                foreach (uint id in gone)
                {
                    if (IsInstanceValid(_nodes[id].Node)) _nodes[id].Node.QueueFree();
                    _nodes.Remove(id);
                }
        }

        // cheap digest signature so ApplyDisplay only re-runs when the tiers actually change
        static ulong DisplaySig(ContainerDisplayCell[] display)
        {
            ulong h = NetHash.FnvOffset;
            if (display != null)
                foreach (var d in display) { h = NetHash.MixByte(h, d.Cell); h = NetHash.MixUInt32(h, d.ItemId); h = NetHash.MixByte(h, d.Rot); }
            return h;
        }
    }
}
