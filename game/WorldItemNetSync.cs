using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // MP_PLAN §3.3, server side: bridges the world's WorldItem NODES (loot streamed by LootField, items the
    // listen-server's local player drops, salvage debris) into WorldItemReplication entities -- spawn facts
    // broadcast, the settled transform published once physics froze the item, and removals reconciled in
    // BOTH directions: a node that vanished (local pickup / stuck-despawn) removes its entity; an entity a
    // remote pickup command consumed frees its node. Runs at 5 Hz -- loot streaming cadence, not gameplay.
    //
    // Entities spawned by commands WITHOUT a node (a remote player's drop, salvage scrap) are left alone
    // here: materializing server-side nodes for remote actions is deferred with the damage phase.
    public sealed class WorldItemNetSync
    {
        public const int DivisorTicks = 10;   // every 10th 50 Hz tick = 5 Hz

        readonly NetWorldServer _server;
        readonly Node _host;
        readonly Dictionary<ulong, uint> _netIdByInstance = new();          // node instance id -> NetId
        readonly Dictionary<uint, (WorldItem Node, ulong Iid)> _nodes = new();

        public int TrackedCount => _nodes.Count;

        public WorldItemNetSync(NetWorldServer server, Node host)
        {
            _server = server;
            _host = host;
        }

        public void Tick()
        {
            if (_server.Session.CurrentTick % DivisorTicks != 0) return;
            var tree = _host.GetTree();
            if (tree == null) return;

            // nodes -> entities: publish new spawns + settled transforms
            foreach (var n in tree.GetNodesInGroup("worlditems"))
            {
                if (n is not WorldItem wi || !GodotObject.IsInstanceValid(wi) || wi.Item == null) continue;
                ulong iid = wi.GetInstanceId();
                if (!_netIdByInstance.TryGetValue(iid, out uint netId))
                {
                    var gp = wi.GlobalPosition;
                    var lv = wi.LinearVelocity;
                    var e = _server.Transactions.SpawnWorldItem(wi.Item,
                        new UnityEngine.Vector3(gp.X, gp.Y, gp.Z), new UnityEngine.Vector3(lv.X, lv.Y, lv.Z));
                    netId = e.NetIdValue;
                    _netIdByInstance[iid] = netId;
                    _nodes[netId] = (wi, iid);
                }
                if (wi.Settled && _server.WorldItems.TryGet(netId, out var ent) && !ent.Settled)
                {
                    var sp = wi.GlobalPosition;
                    _server.Transactions.SettleWorldItem(netId, new UnityEngine.Vector3(sp.X, sp.Y, sp.Z));
                }
            }

            // reconcile removals both directions
            List<uint> forget = null;
            foreach (var kv in _nodes)
            {
                uint netId = kv.Key;
                var node = kv.Value.Node;
                bool nodeAlive = GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion();
                bool entityAlive = _server.WorldItems.TryGet(netId, out _);
                if (!nodeAlive)
                {
                    // local pickup / stuck-despawn took the node -> retire the entity (idempotent)
                    _server.Transactions.RemoveWorldItem(netId);
                    (forget ??= new List<uint>()).Add(netId);
                }
                else if (!entityAlive)
                {
                    // a remote pickup command consumed the entity -> the physical item leaves the world
                    node.QueueFree();
                    (forget ??= new List<uint>()).Add(netId);
                }
            }
            if (forget != null)
                foreach (uint id in forget)
                {
                    _netIdByInstance.Remove(_nodes[id].Iid);
                    _nodes.Remove(id);
                }
        }
    }
}
