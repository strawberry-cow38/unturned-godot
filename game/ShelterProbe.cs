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

            // SMALL PROPS ARE NOT COVER (strawberry 2026-09-05 "prevent the muffle effect when standing under small
            // props"). Standing under a sign or a lamp head was muffling the rain like a roof. The rule is NOT a new
            // one: RainRoofMap.IsSmallProp already decides this for the visual mask (fence height or post-thin gets
            // cast through), and this file's own header insists the cover implementations share one rule -- so it
            // calls that predicate rather than defining "small" a second time and letting the two drift.
            //
            // THIS LOOP IS NOT THE LOOP THE HEADER BURIED. That one stepped past near-vertical hits, a measure-zero
            // case no fixture could reach, which is why its mutations survived a green suite. Skipping a small prop
            // is an ordinary, common hit -- a sign really is between you and the roof -- so the loop has real work
            // and a real bound.
            var exclude = new Godot.Collections.Array<Rid>();
            for (int i = 0; i < 8; i++)   // bounded: a stack of eight small props above one point is not a real world
            {
                var hit = space.IntersectRay(new PhysicsRayQueryParameters3D
                {
                    From = at,
                    To = at + new Vector3(0f, maxUp, 0f),
                    CollisionMask = 1u << 0,
                    Exclude = exclude,
                });
                if (hit.Count == 0) return null;                          // open sky

                // FOOTPRINT, not thickness -- IsTooSmallForCover, NOT IsSmallProp. See that method: IsSmallProp
                // treats "thin in Y" as small, which is a roof, so this probe cast through every roof in the game.
                if (hit.TryGetValue("collider", out var col) && RainRoofMap.IsTooSmallForCover(col.As<GodotObject>()))
                {
                    if (hit.TryGetValue("rid", out var rid)) { exclude.Add(rid.As<Rid>()); continue; }
                    return null;   // no rid to exclude -> cannot advance past it; call the sky open rather than spin
                }
                var n = (Vector3)hit["normal"];
                if (Mathf.Abs(n.Y) <= RainShelter.MinFacing) return null; // edge-on: keeps no rain off
                return ((Vector3)hit["position"]).Y;
            }
            return null;   // eight small props deep and still nothing solid -- treat as open sky
        }

        public static bool IsSheltered(World3D world, Vector3 at, float maxUp = 60f)
            => CoverAbove(world, at, maxUp).HasValue;
    }
}
