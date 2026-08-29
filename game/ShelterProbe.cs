using Godot;
using UnturnedSim;

namespace UnturnedGodot
{
    /// <summary>"Is this world point under cover?", for a BAKED world. strawberry_cow: "make floors/roofs
    /// occlude rain."
    ///
    /// TWO IMPLEMENTATIONS, ONE RULE, AND THAT IS DELIBERATE. RainShelter answers this exactly from wall
    /// plans, which is what the editor and any authoring tool have; a shipped map has baked meshes and no
    /// plans, so gameplay has to ask the physics world instead. What must NOT differ is the rule about what
    /// counts as cover, so both use RainShelter.MinFacing and both reject near-vertical surfaces. An
    /// in-engine test (buildtool.rain_shelter_matches_raycast) drives a real building through both and
    /// requires them to agree, which is the only thing keeping the pair honest.</summary>
    public static class ShelterProbe
    {
        /// <summary>The height of the lowest cover above <paramref name="at"/>, or null if the sky is open.
        ///
        /// ONE CAST. An earlier version looped, stepping past vertical hits on the theory that a ray fired
        /// beside a wall could clip the wall before reaching the ceiling. It cannot: a vertical ray and a
        /// vertical plane are parallel, so the hit is measure-zero, and two mutations to that loop survived
        /// a green suite because no fixture could reach them. Unreachable code that tests cannot cover is
        /// not safety, it is a place for a bug to live rent-free.
        ///
        /// The facing check stays, and it is HONESTLY UNTESTED: mutating it away leaves the whole suite
        /// green, because reaching it needs a surface within a twentieth of a degree of vertical that a
        /// vertical ray nonetheless hits, and that is the same measure-zero case the loop above died for.
        /// It is kept anyway, as a guard rather than a tested path, for two reasons worth more than the
        /// line costs: it is the RULE this shares with RainShelter (both reject surfaces too edge-on to
        /// keep rain off, and buildtool.rain_shelter_matches_raycast requires the pair to agree on a real
        /// building), and a degenerate collider CAN hand back a zero normal, which this rejects and a bare
        /// first-hit would call a roof.
        ///
        /// Do not read its survival in the mutation sweep as a gap someone forgot to close.</summary>
        public static float? CoverAbove(World3D world, Vector3 at, float maxUp = 60f)
        {
            if (world == null) return null;
            var space = world.DirectSpaceState;
            if (space == null) return null;

            var hit = space.IntersectRay(new PhysicsRayQueryParameters3D
            {
                From = at,
                To = at + new Vector3(0f, maxUp, 0f),
                CollisionMask = 1u << 0,
            });
            if (hit.Count == 0) return null;                          // open sky

            var n = (Vector3)hit["normal"];
            if (Mathf.Abs(n.Y) <= RainShelter.MinFacing) return null; // edge-on: keeps no rain off
            return ((Vector3)hit["position"]).Y;
        }

        public static bool IsSheltered(World3D world, Vector3 at, float maxUp = 60f)
            => CoverAbove(world, at, maxUp).HasValue;
    }
}
