using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;
using SDG.Unturned;

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
        // The rewrite's zombies are sim ROWS, so they can never appear in the node group above and were
        // never replicated at all -- a second player saw an empty world under --newzombies. Tracked
        // separately, keyed by ZombieId (stable across the sim's swap-removes, unlike a row index),
        // publishing through the SAME wire calls so the protocol is unchanged.
        readonly Dictionary<ZombieId, Tracked> _simTracked = new();
        readonly List<ZombieId> _simStale = new();
        bool _announcedSimRows;
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

            PublishSimRows(tick);

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

        /// <summary>Publish the rewrite's sim rows over the same wire the node-backed zombies use.
        /// Position, yaw and the anim byte carry identical meaning; only where the data comes from
        /// changes, so no client-side or protocol change is needed.</summary>
        void PublishSimRows(long tick)
        {
            var sim = ZombieDirector.Instance?.Sim;
            if (sim == null) return;

            for (int row = 0; row < sim.Count; row++)
            {
                var id = sim.IdOf(row);
                var pos = sim.PositionOf(row);
                if (!_simTracked.TryGetValue(id, out var t))
                {
                    var netId = _server.Ids.Mint();
                    t = new Tracked { NetId = netId.Value, Brain = null, LastSwingSeq = 0, DeadAnnounced = sim.IsDead(row) };
                    _simTracked[id] = t;
                    _byId[t.NetId] = t;
                    _server.Zombies.ServerSpawn(netId, 0, pos, tick);   // kind 0: the rewrite has one kind so far
                    // One line, once, so "are the new zombies actually on the wire?" is answerable from a
                    // log instead of by reading code. Silence here is what let four whole gameplay
                    // surfaces address an empty node group for days without anyone noticing.
                    if (!_announcedSimRows)
                    {
                        _announcedSimRows = true;
                        GD.Print($"[zombienet] publishing SIM ROWS to the wire (--newzombies); first netId {t.NetId}");
                    }
                }

                byte anim;
                if (sim.IsDead(row)) anim = (byte)ZombieNetAnim.Dead;
                else if (sim.IsSwinging(row)) anim = (byte)ZombieNetAnim.Attack;
                else
                {
                    _server.Zombies.TryGet(new NetId(t.NetId), out var prev);
                    float speed = prev != null ? (pos - prev.Pos).magnitude / (PublishDivisorTicks * 0.02f) : 0f;
                    anim = speed > 0.5f ? (byte)ZombieNetAnim.Walk : (byte)ZombieNetAnim.Idle;
                }

                var face = sim.FacingOf(row);
                float yaw = Mathf.RadToDeg(Mathf.Atan2(face.x, face.z));
                _server.Zombies.ServerPublish(new NetId(t.NetId), pos, yaw, anim, tick);

                if (sim.IsDead(row) && !t.DeadAnnounced)
                {
                    t.DeadAnnounced = true;
                    var evt = new ZombieDiedEvent { NetId = t.NetId, Killer = 0 };
                    _server.BroadcastEvent(NetMessagePak.Pack(ReplicationIds.EventZombieDied, evt.Write));
                }
            }

            // Rows the sim has dropped (corpse recycle, despawn) -> retire the entity. IsAlive is the
            // authority: a ZombieId's generation is bumped on removal, so a recycled slot never matches.
            _simStale.Clear();
            foreach (var kv in _simTracked)
                if (!sim.IsAlive(kv.Key)) _simStale.Add(kv.Key);
            foreach (var id in _simStale)
            {
                var t = _simTracked[id];
                _server.Zombies.ServerRemove(new NetId(t.NetId), tick);
                _byId.Remove(t.NetId);
                _simTracked.Remove(id);
            }
        }

        // ---- IZombieHost: the wire's damage lands on the real brain (the same DamageHit path SP combat uses) ----
        public bool DamageZombie(uint zombieNetId, float damage, UnityEngine.Vector3 point, UnityEngine.Vector3 dir, ushort attackerPlayerId, bool headshot)
        {
            if (!_byId.TryGetValue(zombieNetId, out var t)) return false;
            if (t.Brain == null)
            {
                // A sim row: no node to damage. Find it by the ZombieId this NetId was minted for.
                var sim = ZombieDirector.Instance?.Sim;
                if (sim == null) return false;
                foreach (var kv in _simTracked)
                {
                    if (kv.Value != t) continue;
                    if (!sim.TryGetRow(kv.Key, out int r) || sim.IsDead(r)) return false;
                    bool killed = sim.Damage(kv.Key, damage, headshot ? ZombieLimb.Skull : ZombieLimb.Spine);
                    if (killed) t.DeadAnnounced = true;   // ServerCombat broadcasts the death with kill credit
                    return killed;
                }
                return false;
            }
            if (!GodotObject.IsInstanceValid(t.Brain) || t.Brain.Dead) return false;
            t.Brain.DamageHit(damage, ToG(point), ToG(dir));
            if (!t.Brain.Dead) return false;
            t.DeadAnnounced = true;   // ServerCombat broadcasts the ZombieDied (with kill credit) for this path
            return true;
        }

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
        static Vector3 ToG(UnityEngine.Vector3 v) => new Vector3(v.x, v.y, v.z);
    }
}
