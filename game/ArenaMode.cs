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
        public const float SpawnRadius = 60f;      // fallback half-extent when a POI has no clustered buildings
        public const float ClusterRadius = 140f;   // buildings within this of the POI node point define its real extent

        // The POI's REAL extent: the XZ bounding box of the placed buildings (the "editor_loaded_object" group) within
        // clusterRadius of the node point. Falls back to a default box centred on the node if the POI has no buildings.
        public static void PoiBounds(Vector3 node, IEnumerable<Node3D> buildings, float clusterRadius,
                                     out Vector3 centre, out float halfX, out float halfZ, out int count)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            count = 0; float cr2 = clusterRadius * clusterRadius;
            foreach (var b in buildings)
            {
                var p = b.GlobalPosition;
                float dx = p.X - node.X, dz = p.Z - node.Z;
                if (dx * dx + dz * dz > cr2) continue;
                minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
                minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
                count++;
            }
            if (count == 0) { centre = node; halfX = halfZ = SpawnRadius; return; }   // no buildings -> default box
            centre = new Vector3((minX + maxX) * 0.5f, node.Y, (minZ + maxZ) * 0.5f);
            halfX = Mathf.Max((maxX - minX) * 0.5f + 6f, 22f);   // pad the footprint slightly; a floor for a one-building POI
            halfZ = Mathf.Max((maxZ - minZ) * 0.5f + 6f, 22f);
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
