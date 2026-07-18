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

        // Perf (strawberry: POIs packed with zombies tank 280->56 fps). Every puppet ran its full skeletal
        // rig (RiggedCharacter.Tick) EVERY render frame regardless of distance or visibility, so N zombies
        // in a POI cost N x an expensive bone-pose update x the render rate. Cull the ANIMATION (not the
        // entity -- the server stays authoritative over every zombie; this is purely what the CLIENT draws):
        // past CullRadius, or beyond NearAlways AND off-screen, freeze the rig + hide the node; resume +
        // snap when it comes back near/on-screen. A shambling zombie's 1-frame catch-up on re-entry is
        // invisible; a nearby one (NearAlways) always animates so turning around never pops a close threat.
        const float CullRadius = 90f;   const float CullRadiusSq = CullRadius * CullRadius;
        const float NearAlways = 20f;   const float NearAlwaysSq = NearAlways * NearAlways;

        public int PuppetCount => _puppets.Count;
        public bool TryGetPuppet(uint netId, out ZombieController puppet) => _puppets.TryGetValue(netId, out puppet);

        public override void _Process(double delta)
        {
            if (Client == null) return;
            var cam = GetViewport()?.GetCamera3D();
            Vector3 eye = cam != null ? cam.GlobalPosition : Vector3.Zero;

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

                // animate only if near enough AND (very close OR on-screen) -- test the CURRENT target so a
                // frozen zombie the server walks into view re-activates on its real position
                bool active = cam == null;
                if (cam != null)
                {
                    float dSq = eye.DistanceSquaredTo(target);
                    active = dSq <= CullRadiusSq && (dSq <= NearAlwaysSq || cam.IsPositionInFrustum(target));
                }
                if (active)
                {
                    if (!pup.Visible) { pup.Visible = true; pup.GlobalPosition = target; }   // re-entry: show + snap to current pos
                    pup.PuppetFrame(delta, target, e.YawDegrees, e.AnimState);
                }
                else if (pup.Visible) pup.Visible = false;   // far/off-screen: hide + freeze the rig (skip Tick)
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
