using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A GENERATED ISLAND'S PLAYER SPAWN POINTS (strawberry 2026-09-16: procgen "missing features", spawns first).
    //
    // ⚠ THE COUNT IS NOT THE TEST. Running the generator prints "placed 24 player spawn(s) of 24 wanted" on
    // every seed, and that line is equally true of twenty-four points stacked in the sea on a cliff edge. The
    // invariants are the claim; the count only says the search did not give up. So this asserts what each
    // point has to BE, against the island it was chosen from.
    //
    // Headless by construction: ChoosePlayerSpawns is the pure half (no nodes), which is the same split
    // ProcIsland already keeps and the reason this can be checked at all without an editor and a camera.
    public sealed class ProcIslandSpawnPointTests : GameTest
    {
        public override string Name => "island.spawn_points";

        public override IEnumerable<Step> Run()
        {
            var terr = new Terrain();
            World.AddChild(terr);
            yield return Ticks(2);

            var pois = terr.GenerateIsland(4242);
            T.Check("the island generated something to stand on", pois != null && terr.IslandTiles.Count > 0);
            if (pois == null || terr.IslandTiles.Count == 0) yield break;

            var picks = ProcIslandSpawn.ChoosePlayerSpawns(terr, 4242);
            T.Check($"spawns were chosen ({picks.Count})", picks.Count > 0);
            if (picks.Count == 0) yield break;

            // ---- dry. a spawn at the waterline is a spawn in the surf --------------------------------------
            int wet = 0;
            foreach (var p in picks) if (Terrain.HasWater && p.Pos.Y < Terrain.SeaLevelY) wet++;
            T.Check($"no spawn is below sea level ({wet} wet)", wet == 0);

            // ---- flat. sampled the same way the chooser does, so a bug that skips the check shows here -----
            int steep = 0;
            foreach (var p in picks)
            {
                float lo = p.Pos.Y, hi = p.Pos.Y;
                for (int k = 0; k < 4; k++)
                {
                    float sx = p.Pos.X + (k == 0 ? 2.5f : k == 1 ? -2.5f : 0f);
                    float sz = p.Pos.Z + (k == 2 ? 2.5f : k == 3 ? -2.5f : 0f);
                    float h = terr.SampleHeight(sx, sz);
                    if (h < lo) lo = h; if (h > hi) hi = h;
                }
                if (hi - lo > 1.6f) steep++;   // the chooser's tolerance is 1.1; a little slack for the frame change
            }
            T.Check($"no spawn is on a slope ({steep} steep)", steep == 0);

            // ---- spread. THE one that catches "24 points, all in one car park" ----------------------------
            float worst = float.MaxValue;
            for (int i = 0; i < picks.Count; i++)
                for (int j = i + 1; j < picks.Count; j++)
                {
                    float d = picks[i].Pos.DistanceTo(picks[j].Pos);
                    if (d < worst) worst = d;
                }
            T.Check($"spawns are spread out (closest pair {worst:0.0} m, want >= 25)", worst >= 25f);

            // ---- clear of buildings. spawning inside a wall is worse than spawning on a roof --------------
            int inside = 0;
            foreach (var p in picks)
                foreach (var b in terr.IslandBuildings)
                {
                    var bw = ProcIslandSpawn.PosFor(terr, b.X, b.Z);
                    if (new Vector2(p.Pos.X - bw.X, p.Pos.Z - bw.Z).Length() < 7f) { inside++; break; }
                }
            T.Check($"no spawn is inside a building's footprint ({inside})", inside == 0);

            // ---- determinism. the same seed must give the same start points, or two machines generating
            // "the same" world disagree about where people begin -- which is a desync you cannot see.
            var again = ProcIslandSpawn.ChoosePlayerSpawns(terr, 4242);
            bool same = again.Count == picks.Count;
            if (same)
                for (int i = 0; i < picks.Count; i++)
                    if (picks[i].Pos.DistanceTo(again[i].Pos) > 0.001f) { same = false; break; }
            T.Check("the same seed chooses the same spawns", same);

            // ...and a DIFFERENT seed does not, or "seeded" is decorative and every island shares one layout.
            var other = ProcIslandSpawn.ChoosePlayerSpawns(terr, 99);
            bool differs = other.Count != picks.Count;
            if (!differs)
                for (int i = 0; i < picks.Count; i++)
                    if (picks[i].Pos.DistanceTo(other[i].Pos) > 0.001f) { differs = true; break; }
            T.Check("a different seed chooses differently", differs);
        }
    }
}
