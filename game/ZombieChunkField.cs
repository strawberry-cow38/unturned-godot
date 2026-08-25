using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Zombie AI rewrite -- PHASE 1: the chunked grid + tier classification + FROZEN store (docs/ZOMBIE_REDESIGN.md).
    //
    // The map is a grid of 64 m CHUNKS. Each chunk's TIER is a function of the nearest player's distance:
    //   HOT    -- a player is on/next to this chunk (in view/reach): full zombies (mesh+anim+collision) -- LATER phase.
    //   WARM   -- ~1 chunk out: "ghost" zombies that drift on the flow field, no collision/anim, low Hz -- LATER phase.
    //   COLD   -- 2-4 chunks out: still active, one coarse step every few seconds -- LATER phase.
    //   FROZEN -- beyond: pure DATA (a count + spawn points), NEVER ticked.
    // Only HOT+WARM count against the per-player BUDGET (64, retail's pocket cap); FROZEN is unbounded -> a whole-map
    // population costs ~nothing because we only pay for the chunks near a player.
    //
    // PHASE 1 does NOT move zombies -- it stands up the grid, classifies tiers (with hysteresis so a player pacing a
    // chunk edge doesn't thrash), and materializes/drops each chunk's zombie list as it wakes/sleeps. Movement (the flow
    // field), HOT mesh/anim/collision, sight/sound targeting and spawn budgeting are phases 2-5.
    public partial class ZombieChunkField : Node3D
    {
        // Streaming anchor (the LootField/AnimalField precedent): an explicitly-set Player is the SP path, honored
        // exactly. Player == null (server worlds) streams on EVERY registered player via PlayerRegistry.
        public PlayerController Player;
        public Terrain Terr;
        public Vector3? DebugAnchor;   // --zombietier verify: drive the streaming off a bare position, no full player needed

        public const float ChunkSize = 64f;      // metres per chunk (master 2026-08-25)
        public const int Budget = 64;            // max SIMULATED (HOT+WARM) zombies per player (retail pocket maxZombies)
        const float SpawnChance = 0.25f;         // NORMAL survival (retail Provider Zombies.Spawn_Chance)
        const int ChunkMaxLive = 24;             // per-chunk materialized cap (keeps one dense chunk from eating the whole budget)

        // Tier thresholds: distance (m, XZ) from a chunk's CENTRE to the nearest player. ENTER a hotter tier at the
        // inner radius, LEAVE it only past the outer -- the gap is the hysteresis band.
        const float HotIn = 48f, HotOut = 80f;
        const float WarmIn = 128f, WarmOut = 168f;
        const float ColdIn = 256f, ColdOut = 304f;

        public enum Tier { Frozen = 0, Cold = 1, Warm = 2, Hot = 3 }

        // A zombie. PHASE 1: just where it stands (Home == Pos, no movement). Later phases add velocity/target + a node
        // when HOT. Kept a struct so a FROZEN chunk's would-be zombies cost nothing but the count.
        public struct Zombie { public Vector3 Home; public Vector3 Pos; }

        public class Chunk
        {
            public int Cx, Cz;
            public Vector3 Center;                          // world centre (Y = 0; XZ is what tiers test)
            public readonly List<Vector3> SpawnPts = new(); // Animals.dat points that fell in this chunk
            public int Cap;                                 // how many zombies this chunk holds when awake
            public Tier Tier = Tier.Frozen;
            public List<Zombie> Live;                       // null while FROZEN; materialized (Cap zombies) once COLD+
            public uint Seed;                               // deterministic per-chunk spawn-point pick
            public int Population => Live?.Count ?? Cap;     // FROZEN reports its POTENTIAL, so map totals stay honest
        }

        readonly Dictionary<(int, int), Chunk> _chunks = new();
        double _acc = 1;                                    // force a classify on the first tick
        const double Interval = 0.25;                       // tiering runs at ~4 Hz -- it does not need the physics rate

        // ---- debug snapshot (for the --zombietier verify render/log) ----
        public IReadOnlyDictionary<(int, int), Chunk> Chunks => _chunks;
        public readonly int[] TierChunks = new int[4];      // chunk counts per tier
        public readonly int[] TierZombies = new int[4];     // zombie counts per tier (FROZEN = potential)

        static (int, int) Key(float x, float z) => (Mathf.FloorToInt(x / ChunkSize), Mathf.FloorToInt(z / ChunkSize));

        public void LoadFromPei(string peiRoot)
        {
            string path = System.IO.Path.Combine(peiRoot, "Spawns", "Animals.dat");
            if (!System.IO.File.Exists(path)) { GD.Print("[zchunk] no Animals.dat -- no zombie spawns"); return; }
            var b = System.IO.File.ReadAllBytes(path); int o = 0;
            byte version = b[o++];
            if (version == 0) return;
            int total = 0, kept = 0, water = 0;
            for (int rx = 0; rx < 64; rx++)
                for (int ry = 0; ry < 64; ry++)
                {
                    ushort count = System.BitConverter.ToUInt16(b, o); o += 2;
                    for (int i = 0; i < count; i++)
                    {
                        o++;                                                 // byte type (PEI = one NORMAL zombie table)
                        float px = System.BitConverter.ToSingle(b, o); o += 4;
                        o += 4;                                              // skip point.y -- zombies stand on our terrain
                        float pz = System.BitConverter.ToSingle(b, o); o += 4;
                        total++;
                        float gx = px, gz = -pz;                             // negate-Z into Godot space
                        if (Terr != null && Terrain.IsWater(Terr.SampleDominantLayer(gx, gz))) { water++; continue; }
                        var k = Key(gx, gz);
                        if (!_chunks.TryGetValue(k, out var c))
                        {
                            c = new Chunk
                            {
                                Cx = k.Item1, Cz = k.Item2,
                                Center = new Vector3((k.Item1 + 0.5f) * ChunkSize, 0f, (k.Item2 + 0.5f) * ChunkSize),
                                Seed = (uint)(k.Item1 * 73856093) ^ (uint)(k.Item2 * 19349663) ^ 0x9E3779B9u,
                            };
                            _chunks[k] = c;
                        }
                        float gy = Terr != null ? Terr.SampleHeight(gx, gz) : 0f;
                        c.SpawnPts.Add(new Vector3(gx, gy, gz));
                        kept++;
                    }
                }
            int capSum = 0;
            foreach (var c in _chunks.Values)
            {
                c.Cap = Mathf.Min(ChunkMaxLive, Mathf.CeilToInt(c.SpawnPts.Count * SpawnChance));
                capSum += c.Cap;
            }
            GD.Print($"[zchunk] {kept}/{total} Animals.dat pts ({water} water dropped) -> {_chunks.Count} chunks @ {ChunkSize}m; " +
                     $"map population potential = {capSum} zombies (Σ min({ChunkMaxLive}, ceil(pts*{SpawnChance})))");
        }

        public override void _PhysicsProcess(double delta)
        {
            _acc += delta;
            if (_acc < Interval) return;
            _acc = 0;
            Reclassify();
        }

        // Verify hook (--zombietier): drive a classify pass synchronously (headless), no physics ticks needed.
        public void ForceReclassify() => Reclassify();

        // Densest chunk centre -- the harness anchors here so the verify frames a populated town, not empty grass.
        public Vector3 DensestChunkCenter()
        {
            Chunk best = null;
            foreach (var c in _chunks.Values) if (best == null || c.SpawnPts.Count > best.SpawnPts.Count) best = c;
            return best?.Center ?? Vector3.Zero;
        }

        // Gather anchors, tier every chunk (hysteresis), enforce the per-player HOT+WARM budget, materialize/drop.
        void Reclassify()
        {
            // 1) anchor player positions
            _anchors.Clear();
            if (DebugAnchor.HasValue) _anchors.Add(DebugAnchor.Value);
            else if (Player != null) _anchors.Add(Player.GlobalPosition);
            else foreach (var p in PlayerRegistry.All) if (GodotObject.IsInstanceValid(p)) _anchors.Add(p.GlobalPosition);

            for (int t = 0; t < 4; t++) { TierChunks[t] = 0; TierZombies[t] = 0; }

            // 2) tier each chunk by nearest-anchor distance, with hysteresis off its CURRENT tier
            _active.Clear();
            foreach (var c in _chunks.Values)
            {
                float d = NearestAnchorDist(c.Center);
                c.Tier = ClassifyHysteretic(c.Tier, d);
                if (c.Tier >= Tier.Warm) _active.Add(c);   // HOT+WARM are the ones that count against the budget
            }

            // 3) BUDGET: per anchor, at most 64 HOT+WARM zombies. Nearest chunks win; demote the overflow to COLD.
            // (Single global pass sorted by distance -- with anchors usually 1 and 64m chunks this is a handful.)
            _active.Sort((a, bb) => NearestAnchorDist(a.Center).CompareTo(NearestAnchorDist(bb.Center)));
            int simBudget = Mathf.Max(1, _anchors.Count) * Budget;
            int sim = 0;
            foreach (var c in _active)
            {
                if (sim + c.Cap > simBudget && c.Tier == Tier.Warm) { c.Tier = Tier.Cold; continue; }   // shed the far WARM first
                sim += c.Cap;
            }

            // 4) wake (materialize the zombie list) COLD+; sleep (drop it, keep the count) when FROZEN
            foreach (var c in _chunks.Values)
            {
                if (c.Tier >= Tier.Cold && c.Live == null) Materialize(c);
                else if (c.Tier == Tier.Frozen && c.Live != null) c.Live = null;   // back to pure data
                TierChunks[(int)c.Tier]++;
                TierZombies[(int)c.Tier] += c.Population;
            }
        }

        readonly List<Vector3> _anchors = new();
        readonly List<Chunk> _active = new();

        float NearestAnchorDist(Vector3 center)
        {
            if (_anchors.Count == 0) return float.MaxValue;
            float best = float.MaxValue;
            foreach (var a in _anchors)
            {
                float dx = a.X - center.X, dz = a.Z - center.Z;   // XZ only
                float d2 = dx * dx + dz * dz;
                if (d2 < best) best = d2;
            }
            return Mathf.Sqrt(best);
        }

        static Tier ClassifyHysteretic(Tier cur, float d)
        {
            // Target tier from the ENTER radii...
            Tier want = d <= HotIn ? Tier.Hot : d <= WarmIn ? Tier.Warm : d <= ColdIn ? Tier.Cold : Tier.Frozen;
            if (want >= cur) return want;   // getting closer -> promote immediately
            // ...but only DEMOTE once past the wider LEAVE radius, so the boundary doesn't thrash.
            return cur switch
            {
                Tier.Hot => d > HotOut ? (d <= WarmIn ? Tier.Warm : d <= ColdIn ? Tier.Cold : Tier.Frozen) : Tier.Hot,
                Tier.Warm => d > WarmOut ? (d <= ColdIn ? Tier.Cold : Tier.Frozen) : Tier.Warm,
                Tier.Cold => d > ColdOut ? Tier.Frozen : Tier.Cold,
                _ => want,
            };
        }

        // Spawn Cap zombies at deterministically-chosen spawn points (xorshift off the chunk seed). PHASE 1: they just
        // stand there (Home == Pos); a later phase gives them the flow field. No node, no mesh, no physics yet.
        void Materialize(Chunk c)
        {
            c.Live = new List<Zombie>(c.Cap);
            if (c.SpawnPts.Count == 0) return;
            uint s = c.Seed | 1u;
            for (int i = 0; i < c.Cap; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;             // xorshift32 -- deterministic pick
                var p = c.SpawnPts[(int)(s % (uint)c.SpawnPts.Count)];
                c.Live.Add(new Zombie { Home = p, Pos = p });
            }
        }
    }
}
