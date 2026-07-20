using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server side of the animal brain/puppet split (A5, mirrors ZombieNetSync §3.5): the REAL AnimalAgent
    // brains own the wander behaviour (on the loopback/listen-server world that spawns them via AnimalField);
    // this sync publishes each brain's transform + anim byte + species into AnimalReplication every 4th tick
    // (12.5 Hz -- shared with zombies). Ticked between "net.server.sim" and "net.server.replicate" on the
    // world's SimRoot (registered by DedicatedServer/MpLoopback) so published state is this tick's and
    // replication still sends LAST. Animals are ambient wildlife -- no server-combat host (no wire damage path).
    public sealed class AnimalNetSync
    {
        public const int PublishDivisorTicks = 4;   // 12.5 Hz (MP_PLAN §2.5: zombies/animals every 4th tick)

        readonly NetWorldServer _server;
        readonly Node _root;   // any in-tree node -> "animals" group queries

        sealed class Tracked { public uint NetId; public AnimalAgent Brain; }
        readonly Dictionary<AnimalAgent, Tracked> _tracked = new();
        readonly List<AnimalAgent> _stale = new();

        public int TrackedCount => _tracked.Count;

        public AnimalNetSync(NetWorldServer server, Node root)
        {
            _server = server;
            _root = root;
        }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            if (tick % PublishDivisorTicks != 0) return;

            foreach (var n in _root.GetTree().GetNodesInGroup("animals"))
            {
                if (n is not AnimalAgent a || !GodotObject.IsInstanceValid(a)) continue;
                if (!_tracked.TryGetValue(a, out var t))
                {
                    var id = _server.Ids.Mint();
                    t = new Tracked { NetId = id.Value, Brain = a };
                    _tracked[a] = t;
                    _server.Animals.ServerSpawn(id, a.Species, ToU(a.GlobalPosition), tick);
                }
                _server.Animals.ServerPublish(new NetId(t.NetId), ToU(a.GlobalPosition), a.RotationDegrees.Y, a.NetAnim, tick);
            }

            // agents freed (streamed out of range by AnimalField) -> retire the entity
            _stale.Clear();
            foreach (var kv in _tracked)
                if (!GodotObject.IsInstanceValid(kv.Key) || !kv.Key.IsInsideTree()) _stale.Add(kv.Key);
            foreach (var a in _stale)
            {
                _server.Animals.ServerRemove(new NetId(_tracked[a].NetId), tick);
                _tracked.Remove(a);
            }
        }

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
    }
}
