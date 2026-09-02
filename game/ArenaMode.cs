using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // ARENA gamemode (master 2026-09-02): PEI, a random POI per round, 8 spawns within it, NO zombies, NO normal loot
    // (guns drop randomly instead), last-player-standing -> new POI + restart, infinite reserve ammo but you still reload.
    // THIS pass is just the POI + spawn-point generation + a --arenaspawns debug render, so spawn placement can be
    // eyeballed (spread / no water / no buildings) before the match loop is built.
    public static class ArenaMode
    {
        public const int SpawnCount = 8;
        public const float SpawnRadius = 60f;   // spawns spread within this of the POI centre (a PEI town is ~40-100 m across)

        // `count` spawn points spread across the LAND inside a POI disc, on the ground, never in ocean. A pure ring loses
        // radials to water on a COASTAL town (Charlottetown is half harbour), so instead: grid the disc, keep the land
        // cells, then FARTHEST-POINT sample -> the picks fan out to fill whatever land the POI actually has, still 8 of
        // them. Deterministic (same POI+radius -> same picks). Yaw faces the centre so players look inward (arena converges).
        public static List<(Vector3 Pos, float Yaw)> GenerateSpawns(Vector3 centre, Terrain terr, int count = SpawnCount, float radius = SpawnRadius)
        {
            var cand = new List<Vector3>();
            const int steps = 16;
            for (int gx = -steps; gx <= steps; gx++)
                for (int gz = -steps; gz <= steps; gz++)
                {
                    float fx = gx / (float)steps, fz = gz / (float)steps;
                    if (fx * fx + fz * fz > 1f) continue;                                    // inside the disc only
                    float x = centre.X + fx * radius, z = centre.Z + fz * radius;
                    if (terr != null && Terrain.IsWater(terr.SampleDominantLayer(x, z))) continue;   // skip ocean cells
                    float y = terr != null ? terr.SampleHeight(x, z) : centre.Y;
                    cand.Add(new Vector3(x, y, z));
                }
            var picked = new List<Vector3>();
            if (cand.Count == 0) return new List<(Vector3, float)>();
            // seed with the land cell nearest the centre, then repeatedly add the candidate whose nearest picked
            // neighbour is FARTHEST (max-min distance) -> an even, boundary-aware spread over exactly the land.
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
