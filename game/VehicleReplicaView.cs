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
        // Dead-reckoning horizon: past this a starved replica visibly FREEZES instead of gliding along its
        // last velocity forever (docs/EXIT_POSITION_ROOTCAUSE.md §5 hardening -- the unbounded glide masked
        // a total snapshot blackout as a normal-looking drive). Healthy 25 Hz flow resets SinceSnap every
        // ~0.04 s, so this only ever bites during a stall/hold.
        const float MaxExtrapolationSeconds = 0.5f;

        sealed class Tracked
        {
            public VehiclePuppet Node;
            public UnityEngine.Vector3 LastSnapPos;
            public float SinceSnap;   // seconds since the replicated transform last changed -> extrapolation horizon
        }
        readonly Dictionary<uint, Tracked> _puppets = new();

        // A6: cosmetic tow ropes, keyed by TOWER NetId (the entity carrying TowedNetId). Diff-driven like the
        // puppet mirror -- a rope appears when a tower entity echoes TowedNetId!=0 and both puppets exist,
        // is re-pointed every frame between the two puppets' tow nodes, and is retired the moment the tower
        // detaches (TowedNetId->0), either puppet vanishes, or the tower entity leaves the replica. Pure
        // render: the pull physics stays host-authoritative in Vehicle.UpdateTow on the real bodies.
        readonly Dictionary<uint, TowRope> _ropes = new();
        public int RopeCount => _ropes.Count;

        /// <summary>Part A (CLIENT_PREDICTION_PLAN §5.2 A1): NetIds whose puppet this VIEW must not render
        /// -- the driver's own vehicle, replaced by the session's client-local real Vehicle. VIEW-only by
        /// design: the replica STORE keeps mirroring snapshots verbatim (hash parity + "replicas mirror
        /// snapshots" stay intact); this is the port's retail tellState-early-return
        /// (U3 InteractableVehicle.cs:2113-2116). Owned by ClientWorldSession.</summary>
        public readonly HashSet<uint> Suppressed = new();

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
                if (Suppressed.Contains(e.NetIdValue)) continue;   // driver's own predicted vehicle: no puppet (the gone-sweep below retires an existing one)
                seen.Add(e.NetIdValue);
                if (!_puppets.TryGetValue(e.NetIdValue, out var t) || !IsInstanceValid(t.Node))
                {
                    string key = e.TypeId < Vehicle.SpecNames.Length ? Vehicle.SpecNames[e.TypeId] : "jeep";
                    var pup = Vehicle.BuildPuppetByName(key, e.Variant);
                    pup.NetId = e.NetIdValue;               // C6: the shell's interact seam targets puppets by group + NetId
                    pup.AddToGroup("vehicle_puppets");
                    parent.AddChild(pup);
                    pup.GlobalPosition = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                    t = new Tracked { Node = pup, LastSnapPos = e.Pos };
                    _puppets[e.NetIdValue] = t;
                }

                if (e.Pos != t.LastSnapPos) { t.LastSnapPos = e.Pos; t.SinceSnap = 0f; }
                else t.SinceSnap += dt;

                t.Node.Exploded = e.Exploded;   // review #10: mirror the wreck state so TowScannable excludes wrecks (SP parity)

                var vel = new Vector3(e.LinVel.x, e.LinVel.y, e.LinVel.z);
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z) + vel * Mathf.Min(t.SinceSnap, MaxExtrapolationSeconds);   // dead-reckoned between snapshots, bounded horizon
                var pos = t.Node.GlobalPosition;
                t.Node.GlobalPosition = pos.DistanceTo(target) > SnapDistance ? target : pos.Lerp(target, a);

                // orientation: slerp toward the replicated euler (angles are periodic -- the wire's [0,360) wrap is the same rotation)
                var targetBasis = Basis.FromEuler(new Vector3(
                    Mathf.DegToRad(e.PitchDegrees), Mathf.DegToRad(e.YawDegrees), Mathf.DegToRad(e.RollDegrees)));
                t.Node.Basis = t.Node.Basis.Orthonormalized().Slerp(targetBasis, a);

                // signed forward speed -> wheel roll rate. Past the dead-reckon horizon the target position
                // is FROZEN (line above caps SinceSnap), so the puppet has stopped translating -- freeze the
                // wheels too, else a stalled/desynced car does a "burnout in place" (wheels spinning on the
                // stale velocity while the body sits still). Only trips on a real snapshot stall (>0.5 s),
                // never on the 40 ms 25 Hz cadence.
                float fwdSpeed = t.SinceSnap < MaxExtrapolationSeconds ? vel.Dot(-targetBasis.Z) : 0f;
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

            // A6: the cosmetic tow-rope layer -- runs AFTER the puppet transforms are updated this frame, so
            // the rope reads the just-glided tow-node world positions. A tower entity (TowedNetId != 0) draws
            // one TowRope from its own puppet's REAR node to the towed puppet's FRONT node; "am I towed" is
            // never on the wire, it's derived here from being someone's TowedNetId. A rope needs BOTH puppets
            // present (a suppressed/not-yet-built end skips it), and is retired when the tower detaches or
            // either puppet is gone.
            var ropesSeen = new HashSet<uint>();
            foreach (var e in Client.Vehicles.All)
            {
                if (e.TowedNetId == 0) continue;
                if (!TryGetPuppet(e.NetIdValue, out var tower)) continue;   // tower puppet suppressed/gone
                if (!TryGetPuppet(e.TowedNetId, out var towed)) continue;   // towed puppet not built yet
                ropesSeen.Add(e.NetIdValue);
                if (!_ropes.TryGetValue(e.NetIdValue, out var rope) || !IsInstanceValid(rope))
                {
                    rope = new TowRope();
                    parent.AddChild(rope);
                    _ropes[e.NetIdValue] = rope;
                }
                rope.SetEndpoints(tower.RearTowWorld, towed.FrontTowWorld, e.TowRestLen);
            }
            List<uint> goneRopes = null;
            foreach (var kv in _ropes)
                if (!ropesSeen.Contains(kv.Key)) (goneRopes ??= new List<uint>()).Add(kv.Key);
            if (goneRopes != null)
                foreach (uint id in goneRopes)
                {
                    if (IsInstanceValid(_ropes[id])) _ropes[id].QueueFree();
                    _ropes.Remove(id);
                }
        }
    }
}
