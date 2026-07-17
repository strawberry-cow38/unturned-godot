using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // MP_PLAN §3.6, client side: materializes the replicated vehicle entities as mesh-only VehiclePuppet
    // nodes (the DeployableReplicaView node-mirror pattern -- diff-driven off the replica registry, so it
    // is idempotent against join snapshots and event/snapshot races). Motion is DEAD-RECKONED: between
    // 25 Hz snapshots the target keeps advancing along the replicated linear velocity, and the puppet
    // glides toward that moving target (exponential approach; snap across teleport-sized jumps). The
    // driver's own client renders the same puppet in v1 (input-latency driving -- driver-side prediction
    // is deferred per §3.6). No VehicleBody3D ever exists on this path.
    public partial class VehicleReplicaView : Node
    {
        public NetWorldClient Client;

        const float GlideRate = 12f;      // 1/s exponential approach to the dead-reckoned target
        const float SnapDistance = 8f;    // beyond this a glide would read as skating -> snap

        sealed class Tracked
        {
            public VehiclePuppet Node;
            public UnityEngine.Vector3 LastSnapPos;
            public float SinceSnap;   // seconds since the replicated transform last changed -> extrapolation horizon
        }
        readonly Dictionary<uint, Tracked> _puppets = new();

        public int PuppetCount => _puppets.Count;
        public bool TryGetPuppet(uint netId, out VehiclePuppet node)
        {
            node = _puppets.TryGetValue(netId, out var t) ? t.Node : null;
            return node != null && IsInstanceValid(node);
        }

        public override void _Process(double delta)
        {
            if (Client == null) return;
            var parent = GetParent();
            if (parent == null) return;
            float dt = (float)delta;
            float a = 1f - Mathf.Exp(-GlideRate * dt);

            var seen = new HashSet<uint>();
            foreach (var e in Client.Vehicles.All)
            {
                seen.Add(e.NetIdValue);
                if (!_puppets.TryGetValue(e.NetIdValue, out var t) || !IsInstanceValid(t.Node))
                {
                    string key = e.TypeId < Vehicle.SpecNames.Length ? Vehicle.SpecNames[e.TypeId] : "jeep";
                    var pup = Vehicle.BuildPuppetByName(key, e.Variant);
                    parent.AddChild(pup);
                    pup.GlobalPosition = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                    t = new Tracked { Node = pup, LastSnapPos = e.Pos };
                    _puppets[e.NetIdValue] = t;
                }

                if (e.Pos != t.LastSnapPos) { t.LastSnapPos = e.Pos; t.SinceSnap = 0f; }
                else t.SinceSnap += dt;

                var vel = new Vector3(e.LinVel.x, e.LinVel.y, e.LinVel.z);
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z) + vel * t.SinceSnap;   // dead-reckoned between snapshots
                var pos = t.Node.GlobalPosition;
                t.Node.GlobalPosition = pos.DistanceTo(target) > SnapDistance ? target : pos.Lerp(target, a);

                // orientation: slerp toward the replicated euler (angles are periodic -- the wire's [0,360) wrap is the same rotation)
                var targetBasis = Basis.FromEuler(new Vector3(
                    Mathf.DegToRad(e.PitchDegrees), Mathf.DegToRad(e.YawDegrees), Mathf.DegToRad(e.RollDegrees)));
                t.Node.Basis = t.Node.Basis.Orthonormalized().Slerp(targetBasis, a);

                float fwdSpeed = vel.Dot(-targetBasis.Z);   // signed forward speed -> wheel roll rate
                t.Node.DressWheels(e.SteerSigned, fwdSpeed, dt);
            }

            // entities gone from the replica -> retire their puppets
            List<uint> gone = null;
            foreach (var kv in _puppets)
                if (!seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
            if (gone != null)
                foreach (uint id in gone)
                {
                    if (IsInstanceValid(_puppets[id].Node)) _puppets[id].Node.QueueFree();
                    _puppets.Remove(id);
                }
        }
    }
}
