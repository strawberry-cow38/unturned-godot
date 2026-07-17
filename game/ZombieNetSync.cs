using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server side of the zombie brain/puppet split (MP_PLAN §3.5): the REAL ZombieController brains stay
    // authoritative (sensing/flanking/specialities on the server world's NavigationServer); this sync
    // publishes each brain's transform + anim byte + speciality into ZombieReplication at 12.5 Hz (§2.5
    // cadence -- the publisher's choice, no framing change) and turns brain facts into events
    // (AttackSwing on each new SwingSeq, ZombieDied once per corpse). It is also the ServerCombat
    // IZombieHost: server-stepped bullets/melee/grenades resolve against replicated positions, then land
    // here as authoritative DamageHit calls on the brain -- the same damage path SP uses.
    //
    // Ticked between "net.server.sim" and "net.server.replicate" on the world's SimRoot (registered by
    // DedicatedServer/MpLoopback) so published state is this tick's, and replication still sends LAST.
    public sealed class ZombieNetSync : IZombieHost
    {
        public const int PublishDivisorTicks = 4;   // 12.5 Hz (MP_PLAN §2.5: zombies/animals every 4th tick)

        readonly NetWorldServer _server;
        readonly Node _root;   // any in-tree node -> "zombies" group queries

        sealed class Tracked
        {
            public uint NetId;
            public ZombieController Brain;
            public int LastSwingSeq;
            public bool DeadAnnounced;
        }
        readonly Dictionary<ZombieController, Tracked> _tracked = new();
        readonly Dictionary<uint, Tracked> _byId = new();
        readonly List<ZombieController> _stale = new();

        public int TrackedCount => _tracked.Count;

        public ZombieNetSync(NetWorldServer server, Node root)
        {
            _server = server;
            _root = root;
            server.Combat.ZombieHost = this;
        }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            if (tick % PublishDivisorTicks != 0) return;

            foreach (var n in _root.GetTree().GetNodesInGroup("zombies"))
            {
                if (n is not ZombieController z || z.IsPuppet || !GodotObject.IsInstanceValid(z)) continue;   // puppets never join the group, but stay defensive
                if (!_tracked.TryGetValue(z, out var t))
                {
                    var id = _server.Ids.Mint();
                    t = new Tracked { NetId = id.Value, Brain = z, LastSwingSeq = z.SwingSeq, DeadAnnounced = z.Dead };
                    _tracked[z] = t;
                    _byId[t.NetId] = t;
                    _server.Zombies.ServerSpawn(id, (byte)z.Speciality, ToU(z.GlobalPosition), tick);
                }

                // anim byte: dead > attacking > walk-vs-idle from the movement since the last publish
                byte anim;
                if (z.Dead) anim = (byte)ZombieNetAnim.Dead;
                else if (z.IsAttackSwinging) anim = (byte)ZombieNetAnim.Attack;
                else
                {
                    _server.Zombies.TryGet(new NetId(t.NetId), out var prev);
                    float speed = prev != null
                        ? (ToU(z.GlobalPosition) - prev.Pos).magnitude / (PublishDivisorTicks * 0.02f)
                        : 0f;
                    anim = speed > 0.5f ? (byte)ZombieNetAnim.Walk : (byte)ZombieNetAnim.Idle;
                }
                _server.Zombies.ServerPublish(new NetId(t.NetId), ToU(z.GlobalPosition), z.RotationDegrees.Y, anim, tick);

                if (z.SwingSeq != t.LastSwingSeq)
                {
                    t.LastSwingSeq = z.SwingSeq;
                    var evt = new AttackSwingEvent { NetId = t.NetId };
                    _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventAttackSwing, evt.Write));
                }
                if (z.Dead && !t.DeadAnnounced)
                {
                    // killed OUTSIDE the server combat path (the listen-server local player's direct SP
                    // combat) -- announce with no networked killer; wire-path kills announce in ServerCombat
                    t.DeadAnnounced = true;
                    var evt = new ZombieDiedEvent { NetId = t.NetId, Killer = 0 };
                    _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventZombieDied, evt.Write));
                }
            }

            // brains freed (pocket despawn / corpse cleanup) -> retire the entity
            _stale.Clear();
            foreach (var kv in _tracked)
                if (!GodotObject.IsInstanceValid(kv.Key) || !kv.Key.IsInsideTree()) _stale.Add(kv.Key);
            foreach (var z in _stale)
            {
                var t = _tracked[z];
                _server.Zombies.ServerRemove(new NetId(t.NetId), tick);
                _byId.Remove(t.NetId);
                _tracked.Remove(z);
            }
        }

        // ---- IZombieHost: the wire's damage lands on the real brain (the same DamageHit path SP combat uses) ----
        public bool DamageZombie(uint zombieNetId, float damage, UnityEngine.Vector3 point, UnityEngine.Vector3 dir, ushort attackerPlayerId, bool headshot)
        {
            if (!_byId.TryGetValue(zombieNetId, out var t) || !GodotObject.IsInstanceValid(t.Brain) || t.Brain.Dead) return false;
            t.Brain.DamageHit(damage, ToG(point), ToG(dir));
            if (!t.Brain.Dead) return false;
            t.DeadAnnounced = true;   // ServerCombat broadcasts the ZombieDied (with kill credit) for this path
            return true;
        }

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
        static Vector3 ToG(UnityEngine.Vector3 v) => new Vector3(v.x, v.y, v.z);
    }
}
