using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // ARENA gamemode (master 2026-09-02): PEI, a POI per round, 8 spawns within its ACTUAL footprint (the real cluster
    // of buildings, NOT a circle round a Nodes.dat label), on land, and NEVER inside a wall; no zombies, no normal loot
    // (guns drop instead), last-player-standing -> new POI, infinite reserve ammo but you still reload.
    // This pass: POI-extent-from-buildings + spawn generation with a water AND in-wall reject + a --arenaspawns debug shot.
    public static class ArenaMode
    {
        public const int SpawnCount = 8;
        public const float SpawnRadius = 60f;   // fallback half-extent when a POI has no clustered buildings
        public const float LinkDist = 55f;         // buildings within this of the cluster join it -> the CONNECTED town
        public const float MaxTownRadius = 320f;   // the flood-fill never leaves this radius of the node (safety vs prop chains)

        // The POI's REAL extent = the tight TOWN CORE: (1) density-filter to the PACKED buildings (each core one has
        // >= MinNeighbours within DenseRadius, so the roadside sprawl + outlying farms drop out), then (2) flood-fill the
        // core cluster nearest the node + bound it. Tracks the town's actual shape/centre -- ignoring the node LABEL being
        // offset from the buildings, and ignoring disconnected districts across town.
        public static void PoiBounds(Vector3 node, IReadOnlyList<Node3D> buildings, float linkDist,
                                     out Vector3 centre, out float halfX, out float halfZ, out int count)
        {
            // buildings in the POI's neighbourhood
            var near = new List<Vector3>();
            float max2 = MaxTownRadius * MaxTownRadius;
            foreach (var b in buildings) { var p = b.GlobalPosition; float dx = p.X - node.X, dz = p.Z - node.Z; if (dx * dx + dz * dz <= max2) near.Add(p); }
            // 1) DENSITY: a building is CORE only if it has >= MinNeighbours others within DenseRadius. The town CENTRE
            //    packs buildings far tighter than the roadside sprawl / outlying farms, so this isolates the real core.
            const int MinNeighbours = 5; const float DenseRadius = 28f;
            float dense2 = DenseRadius * DenseRadius;
            var core = new List<Vector3>();
            for (int i = 0; i < near.Count; i++)
            {
                int nb = 0;
                for (int j = 0; j < near.Count && nb < MinNeighbours; j++)
                {
                    if (i == j) continue;
                    float dx = near[i].X - near[j].X, dz = near[i].Z - near[j].Z;
                    if (dx * dx + dz * dz <= dense2) nb++;
                }
                if (nb >= MinNeighbours) core.Add(near[i]);
            }
            if (core.Count == 0) { count = 0; centre = node; halfX = halfZ = SpawnRadius; return; }   // sparse POI -> default box
            // 2) CONNECTED: flood-fill the core cluster containing the node's nearest core building (core buildings within
            //    linkDist link), so a SECOND dense district across town doesn't stretch the box onto empty ground between them.
            int seed = 0; float bestD = float.MaxValue;
            for (int i = 0; i < core.Count; i++) { float dx = core[i].X - node.X, dz = core[i].Z - node.Z, d = dx * dx + dz * dz; if (d < bestD) { bestD = d; seed = i; } }
            float link2 = linkDist * linkDist;
            var inC = new bool[core.Count]; var stack = new Stack<int>(); stack.Push(seed); inC[seed] = true;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            count = 0;
            while (stack.Count > 0)
            {
                var pi = core[stack.Pop()]; count++;
                minX = Mathf.Min(minX, pi.X); maxX = Mathf.Max(maxX, pi.X); minZ = Mathf.Min(minZ, pi.Z); maxZ = Mathf.Max(maxZ, pi.Z);
                for (int j = 0; j < core.Count; j++)
                {
                    if (inC[j]) continue;
                    float dx = pi.X - core[j].X, dz = pi.Z - core[j].Z;
                    if (dx * dx + dz * dz <= link2) { inC[j] = true; stack.Push(j); }
                }
            }
            centre = new Vector3((minX + maxX) * 0.5f, node.Y, (minZ + maxZ) * 0.5f);
            halfX = Mathf.Max((maxX - minX) * 0.5f + 12f, 25f);   // small margin so the border sits just outside the core buildings
            halfZ = Mathf.Max((maxZ - minZ) * 0.5f + 12f, 25f);
        }

        // `count` spawns spread across the POI footprint [centre ± half], on the ground, NEVER on water or inside a wall
        // (inWall = a caller-supplied physics test vs the building colliders). Grid the box + reject, then farthest-point
        // sample for an even spread. Deterministic; yaw faces the centre so players look inward.
        public static List<(Vector3 Pos, float Yaw)> GenerateSpawns(Vector3 centre, float halfX, float halfZ, Terrain terr,
                                                                    System.Func<Vector3, bool> inWall, int count = SpawnCount)
        {
            var cand = new List<Vector3>();
            const int steps = 22;   // fine enough to find clear road/gap cells in a dense town (a coarse grid starved it to 7)
            for (int gx = -steps; gx <= steps; gx++)
                for (int gz = -steps; gz <= steps; gz++)
                {
                    float x = centre.X + (gx / (float)steps) * halfX;
                    float z = centre.Z + (gz / (float)steps) * halfZ;
                    if (terr != null && Terrain.IsWater(terr.SampleDominantLayer(x, z))) continue;   // no ocean
                    float y = terr != null ? terr.SampleHeight(x, z) : centre.Y;
                    var p = new Vector3(x, y, z);
                    if (inWall != null && inWall(p)) continue;                                       // no buildings/walls
                    cand.Add(p);
                }
            var picked = new List<Vector3>();
            if (cand.Count == 0) return new List<(Vector3, float)>();
            // seed with the land+clear cell nearest the centre, then farthest-point sample the rest (max-min distance).
            int seed = 0; float bestD = float.MaxValue;
            for (int i = 0; i < cand.Count; i++) { float d = HSqr(cand[i], centre); if (d < bestD) { bestD = d; seed = i; } }
            picked.Add(cand[seed]);
            while (picked.Count < count && picked.Count < cand.Count)
            {
                int far = -1; float farD = -1f;
                for (int i = 0; i < cand.Count; i++)
                {
                    float md = float.MaxValue;
                    for (int j = 0; j < picked.Count; j++) { float d = HSqr(cand[i], picked[j]); if (d < md) md = d; }
                    if (md > farD) { farD = md; far = i; }
                }
                if (far < 0) break;
                picked.Add(cand[far]);
            }
            var outp = new List<(Vector3, float)>(picked.Count);
            foreach (var p in picked) outp.Add((p, Mathf.Atan2(centre.X - p.X, centre.Z - p.Z)));
            return outp;
        }

        static float HSqr(Vector3 a, Vector3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz; }
    }
}
