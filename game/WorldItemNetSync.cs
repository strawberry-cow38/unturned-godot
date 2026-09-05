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

            // ENTITIES THAT HAVE NO NODE GET ONE, or nothing ever simulates them. A player's drop does not create a
            // local node at all: OnDropItem calls SpawnWorldItem directly (MpLoopback documents it as "a server
            // world-item ENTITY with NO local SP node"), and the loop above only ever visits nodes in the
            // "worlditems" group. So the entity sat at its spawn position, wi.Settled was never consulted because
            // there was no wi, SettleWorldItem was never sent -- and the client's replica, which moves only when
            // the entity position changes, drew the item hanging in the air exactly where it left the bag.
            // strawberry 2026-09-05: "dropped item physics are completely broken. dropped items float where they
            // are dropped." They were never falling; there was nothing to fall.
            //
            // The velocity was already there and already thrown away twice -- computed by OnDropItem, broadcast in
            // WorldItemSpawnedEvent.Vel, stored by neither end. It is kept on the entity now and applied here, so a
            // dropped item is tossed the way the drop intended rather than released from rest.
            //
            // The node is the ORDINARY WorldItem: same gravity, same collider, same settle -> the existing
            // node->entity pass above then publishes its resting place with no new command. Under consume its
            // visual is suppressed process-wide (WorldItem.SuppressLocalVisual), so the replica puppet stays the
            // single thing on screen and this is physics only.
            // _host is a plain Node (DedicatedServer / MpLoopback both are), NOT a Node3D -- an `is Node3D` guard
            // here compiles clean and silently disables the whole fix. WorldItem.Spawn takes a Node parent and sets
            // the world position itself, so hand it the host directly.
            foreach (var ent in _server.WorldItems.All)
                {
                    if (ent.Settled || ent.ServerSimulated || ent.ServerItem == null) continue;
                    if (_nodes.ContainsKey(ent.NetIdValue)) continue;
                    var node = WorldItem.Spawn(_host, ent.ServerItem,
                        new Vector3(ent.Pos.x, ent.Pos.y, ent.Pos.z));
                    node.LinearVelocity = new Vector3(ent.ServerVel.x, ent.ServerVel.y, ent.ServerVel.z);
                    ent.ServerSimulated = true;
                    _netIdByInstance[node.GetInstanceId()] = ent.NetIdValue;
                    _nodes[ent.NetIdValue] = (node, node.GetInstanceId());
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
