using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>Sums the ThermalSources a point can actually FEEL: in range, and with line of sight.
    ///
    /// This is the only engine-shaped part of the temperature work. The curve lives in ThermalFalloff and the
    /// body model in PlayerTemperatureSim, both engine-free and unit-tested; what needs Godot is "which nodes
    /// are near me" and "is there a wall in the way", and that is all this does.</summary>
    public static class ThermalField
    {
        /// <summary>World geometry only (layer bit 0). Deliberately NOT the player layer: a second player
        /// standing between you and the fire does not block its heat, and including bodies would make the
        /// reading flicker as people walk past. Deployables sit on the world layer, so a wall of crates
        /// genuinely does shade you.</summary>
        public const uint BlockerMask = 1u << 0;

        /// <summary>Net degrees C at `at`, clamped. Returns 0 when there is nothing around, which is the
        /// correct neutral -- callers add it to the world ambient.</summary>
        public static float NetC(Node context, Vector3 at)
        {
            var tree = context?.GetTree();
            if (tree == null) return 0f;
            var space = (context as Node3D)?.GetWorld3D()?.DirectSpaceState;

            float sum = 0f;
            foreach (var n in tree.GetNodesInGroup(ThermalSource.Group))
            {
                if (n is not ThermalSource s || !IsInstanceValid(s) || !s.Active) continue;
                if (s.Radius <= 0f || Mathf.IsZeroApprox(s.DeltaC)) continue;

                Vector3 p = s.GlobalPosition;
                float d = at.DistanceTo(p);
                // RANGE FIRST, then the ray. The ray is the expensive half and most sources on a map are not
                // near you; testing it the other way round would cast against every campfire in the world.
                if (d >= s.Radius) continue;
                if (space != null && Blocked(space, at, p, s.SelfRadius)) continue;

                sum += ThermalFalloff.Contribution(s.DeltaC, d, s.Radius);
            }
            return ThermalFalloff.Clamp(sum);
        }

        /// <summary>LOS from the player to the source. Cast FROM the player so a source buried inside its own
        /// collider (a stove inside a cabinet, a fire pit sunk into the ground) is not self-occluded at the
        /// first millimetre -- the query excludes nothing, so a ray starting inside geometry would report an
        /// immediate hit and silently switch the source off.</summary>
        static bool Blocked(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, float selfRadius)
        {
            var q = PhysicsRayQueryParameters3D.Create(from, to, BlockerMask);
            q.CollideWithAreas = false;
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return false;
            // A hit within the source's OWN body is the source, not a wall between you and it. The tolerance
            // is declared per source (ThermalSource.SelfRadius) rather than guessed here: a flat 0.35 m looked
            // reasonable and failed on the first sunk fire pit, because the field cannot know how big any
            // given emitter is.
            var point = (Vector3)hit["position"];
            return point.DistanceTo(to) > Mathf.Max(selfRadius, 0.05f);
        }

        static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);
    }
}
