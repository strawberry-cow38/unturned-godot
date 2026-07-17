using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Client side of the zombie brain/puppet split (MP_PLAN §3.5): watches a NetWorldClient's zombie
    // replicas and keeps one IsPuppet ZombieController per replicated zombie -- spawned on first sight
    // (with the replicated speciality, so tint/clips/hitbox match), driven every render frame through
    // PuppetFrame (glide-interpolated position + yaw + anim byte, death ragdoll once), freed when the
    // server retires the entity. The RemotePlayers pattern, for zombies. Only attached where the world
    // does NOT own the brains (a remote client's view) -- the loopback/listen-server world renders its
    // real brains directly and never puppets them.
    public partial class ZombiePuppets : Node3D
    {
        public NetWorldClient Client;

        readonly Dictionary<uint, ZombieController> _puppets = new();

        public int PuppetCount => _puppets.Count;
        public bool TryGetPuppet(uint netId, out ZombieController puppet) => _puppets.TryGetValue(netId, out puppet);

        public override void _Process(double delta)
        {
            if (Client == null) return;

            foreach (var e in Client.Zombies.All)
            {
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                if (!_puppets.TryGetValue(e.NetIdValue, out var pup) || !IsInstanceValid(pup))
                {
                    pup = new ZombieController { IsPuppet = true, Speciality = (ZombieController.ESpeciality)e.Speciality };
                    AddChild(pup);
                    pup.GlobalPosition = target;
                    _puppets[e.NetIdValue] = pup;
                }
                pup.PuppetFrame(delta, target, e.YawDegrees, e.AnimState);
            }

            if (_puppets.Count > 0)   // server retired an entity -> free the stale puppet (corpse cleanup)
            {
                List<uint> stale = null;
                foreach (var kv in _puppets)
                    if (!Client.Zombies.TryGet(new NetId(kv.Key), out _)) (stale ??= new List<uint>()).Add(kv.Key);
                if (stale != null)
                    foreach (var id in stale)
                    {
                        if (IsInstanceValid(_puppets[id])) _puppets[id].QueueFree();
                        _puppets.Remove(id);
                    }
            }
        }
    }
}
