using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Client side of the animal brain/puppet split (A5, mirrors ZombiePuppets §3.5): watches a NetWorldClient's
    // animal replicas and keeps one puppet (a RiggedCharacter built from the replicated species) per replicated
    // animal -- spawned on first sight, glided toward the replicated transform each frame, the anim clip mapped
    // from the anim byte, freed when the server retires the entity. The RemotePlayers/ZombiePuppets pattern, for
    // wildlife. Only attached where the world does NOT own the animals (a remote client's view); the
    // loopback/listen-server world renders its real AnimalAgents directly and never puppets them.
    //
    // Matches the SP look: AnimalAgent wanders but never ticks its rig (frozen clip pose -- "static rest pose"),
    // so the puppet likewise sets the clip on the anim byte + interpolates the transform, without ticking the rig.
    public partial class AnimalPuppets : Node3D
    {
        public NetWorldClient Client;

        sealed class Puppet { public Node3D Holder; public RiggedCharacter Rig; public byte Anim = 255; public Vector3 DispPos; public float DispYaw; public bool Init; }
        readonly Dictionary<uint, Puppet> _puppets = new();

        const float PosLerp = 10f, YawLerp = 10f;   // glide toward the 12.5 Hz snapshots

        static string ClipFor(byte anim) => anim switch
        {
            (byte)AnimalNetAnim.Walk => "Walk",
            (byte)AnimalNetAnim.Eat => "Eat",
            (byte)AnimalNetAnim.Glance => "Glance_0",
            _ => "Idle",
        };

        public int PuppetCount => _puppets.Count;
        public bool TryGetPuppet(uint netId, out Node3D holder)
        {
            if (_puppets.TryGetValue(netId, out var p) && IsInstanceValid(p.Holder)) { holder = p.Holder; return true; }
            holder = null; return false;
        }

        public override void _Process(double delta)
        {
            if (Client == null) return;
            float dt = (float)delta;

            foreach (var e in Client.Animals.All)
            {
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                if (!_puppets.TryGetValue(e.NetIdValue, out var p) || !IsInstanceValid(p.Holder))
                {
                    var kind = AnimalCatalog.Get(e.Species);
                    var rig = RiggedCharacter.Build($"res://content/{kind.Rig}_rig.json", Colors.White, false, $"res://content/objects/{kind.Tex}", null);
                    if (rig == null) continue;
                    var holder = new Node3D();
                    AddChild(holder);
                    holder.AddChild(rig);
                    holder.GlobalPosition = target;
                    p = new Puppet { Holder = holder, Rig = rig, DispPos = target, DispYaw = e.YawDegrees, Init = true };
                    _puppets[e.NetIdValue] = p;
                }

                // glide toward the snapshot; snap on the first frame so it doesn't streak in from (0,0,0)
                p.DispPos = p.Init ? target : p.DispPos.Lerp(target, Mathf.Min(1f, PosLerp * dt));
                p.DispYaw = p.Init ? e.YawDegrees
                    : Mathf.RadToDeg(Mathf.LerpAngle(Mathf.DegToRad(p.DispYaw), Mathf.DegToRad(e.YawDegrees), Mathf.Min(1f, YawLerp * dt)));
                p.Init = false;
                p.Holder.GlobalPosition = p.DispPos;
                p.Holder.RotationDegrees = new Vector3(0f, p.DispYaw, 0f);

                if (p.Anim != e.AnimState)
                {
                    p.Anim = e.AnimState;
                    p.Rig?.Play(ClipFor(e.AnimState));
                }
            }

            if (_puppets.Count > 0)   // server retired an entity -> free the stale puppet
            {
                List<uint> stale = null;
                foreach (var kv in _puppets)
                    if (!Client.Animals.TryGet(new NetId(kv.Key), out _)) (stale ??= new List<uint>()).Add(kv.Key);
                if (stale != null)
                    foreach (var id in stale)
                    {
                        if (IsInstanceValid(_puppets[id].Holder)) _puppets[id].Holder.QueueFree();
                        _puppets.Remove(id);
                    }
            }
        }
    }
}
