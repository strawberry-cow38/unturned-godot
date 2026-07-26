using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>
    /// The one thing the zombie sim cannot answer by itself: where the walkable surface is. The engine
    /// implements this; the sim only ever sees the interface, which is what keeps the whole simulation
    /// L0-testable (a test supplies a flat plane, or a maze, and no engine is involved).
    ///
    /// Deliberately a corridor of waypoints, not a "move me" call. The sim owns movement; the navmesh is
    /// asked for a route, once, and then followed for as long as it stays valid. That is what makes path
    /// queries budgetable -- a horde that all hears one gunshot queues 60 requests and drains them over
    /// several ticks instead of issuing 60 in one.
    /// </summary>
    public interface IZombieNavQuery
    {
        /// <summary>Fill <paramref name="corridor"/> with waypoints leading from <paramref name="from"/>
        /// toward <paramref name="to"/>. Returns the number written; 0 means no route exists.</summary>
        int QueryPath(Vector3 from, Vector3 to, Vector3[] corridor);

        /// <summary>Put a position on the walkable surface -- the sim integrates in XZ and takes Y from
        /// here rather than simulating gravity into a collider.</summary>
        Vector3 SnapToSurface(Vector3 p);
    }

    /// <summary>A flat plane at y = 0 that routes straight to the target. The default when no engine is
    /// attached, and what the L0 movement tests run against.</summary>
    public sealed class FlatGroundNav : IZombieNavQuery
    {
        public float Height;
        public int QueryPath(Vector3 from, Vector3 to, Vector3[] corridor)
        {
            if (corridor == null || corridor.Length == 0) return 0;
            corridor[0] = new Vector3(to.x, Height, to.z);
            return 1;
        }
        public Vector3 SnapToSurface(Vector3 p) => new Vector3(p.x, Height, p.z);
    }
}
