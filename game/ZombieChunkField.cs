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

        // ---- phase 2: the flow field + drift ----
        const float FieldRadius = 160f;    // the flow field covers ±this around the anchor (comfortably past WARM)
        const float ZombieSpeed = 1.3f;    // m/s shamble -- tuned DOWN to the Move_N clip's natural stride so the 1x anim doesn't foot-slide (master: don't speed up the anim, slow the zombie). HOT+WARM; COLD takes ONE coarse step every ColdStep seconds
        const float ColdStep = 2f;
        const float StopDist = 1.5f;       // pile at the player rather than oscillate through them
        readonly ZombieFlowField _field = new();
        readonly Dictionary<(int, int), bool> _walkCache = new();   // per-cell walkability (buildings are static -> query once)
        (int, int) _fieldCell = (int.MinValue, int.MinValue);
        Vector3 _fieldAnchor;
        bool _hasField;
        double _coldAcc;
        BoxShape3D _probe;
        public bool HasField => _hasField;
        public ZombieFlowField Field => _field;   // --zflow verify render reads the baked arrows

        // ---- phase 3: HOT (visible body) promotion + separation ----
        const float HotBodyDist = 45f;    // a zombie within this of a player gets a visible ZombieBody...
        const float HotBodyDrop = 60f;    // ...and loses it past this (hysteresis, so the edge doesn't flicker)
        const float SepR = 1.7f;          // separation radius -- HOT bodies steer apart (boids), so a horde spreads
        const float SepStrength = 1.3f;
        readonly List<Zombie> _hotList = new();

        // ---- phase 4: sound alert + sight targeting ----
        const float SightRange = 24f;      // a HOT zombie that SEES a player within this (clear line of sight) chases it directly
        const float AlertSeconds = 8f;     // a heard noise stays the field's target this long, then fades -> they lose the trail
        Vector3 _alertPos; float _alertLoud; double _alertExpiry = -1; double _clock;
        System.Action<Vector3, float> _noiseHandler;
        public bool HasAlert => _clock < _alertExpiry;

        public enum Tier { Frozen = 0, Cold = 1, Warm = 2, Hot = 3 }

        // A zombie. Home = spawn point; Pos = current position. Body != null once it's HOT (within ~45 m of a player):
        // the visible/collidable/killable node, which then owns its transform (Pos syncs from it). A class (not a struct)
        // so it can hold the Body ref and be mutated in place. FROZEN chunks allocate none of these -- they stay a count.
        public class Zombie { public Vector3 Home; public Vector3 Pos; public Vector2 Vel; public ZombieBody Body; }

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

        public override void _EnterTree() { _noiseHandler = HearNoise; SoundBus.OnNoise += _noiseHandler; }
        public override void _ExitTree() { if (_noiseHandler != null) { SoundBus.OnNoise -= _noiseHandler; _noiseHandler = null; } }

        public override void _PhysicsProcess(double delta)
        {
            _clock += delta;
            _acc += delta;
            if (_acc >= Interval) { _acc = 0; Reclassify(); RebuildFieldForAlert(); }
            Move(delta);   // sight-chase / sound-drift every frame; COLD steps coarsely inside
        }

        // A sound was emitted (footstep/gunshot/horn/door). Make it the field's target if it's LOUDER than the current
        // still-live alert (a gunshot beats footsteps), or the old one has faded. Footsteps keep it fresh near a moving player.
        void HearNoise(Vector3 pos, float loudness)
        {
            if (_clock < _alertExpiry && loudness < _alertLoud) return;   // a louder, still-live alert stands
            _alertPos = pos; _alertLoud = loudness; _alertExpiry = _clock + AlertSeconds;
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

        // --zflow verify: drop `count` zombies around `at` directly (no Animals.dat), pre-materialized so they drift
        // as soon as the anchor tiers their chunk active.
        public void DebugSeed(Vector3 at, int count, float spread)
        {
            var k = Key(at.X, at.Z);
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
            c.Cap = count;
            c.Live = new List<Zombie>(count);
            uint s = c.Seed | 1u;
            for (int i = 0; i < count; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5; float ox = ((s % 1000u) / 1000f - 0.5f) * spread;
                s ^= s << 13; s ^= s >> 17; s ^= s << 5; float oz = ((s % 1000u) / 1000f - 0.5f) * spread;
                var p = new Vector3(at.X + ox, at.Y, at.Z + oz);
                c.Live.Add(new Zombie { Home = p, Pos = p });
            }
        }

        public IEnumerable<Zombie> DebugZombies()
        {
            foreach (var c in _chunks.Values) if (c.Live != null) foreach (var z in c.Live) yield return z;
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

        // Rebuild the flow field from the last heard SOUND (the field's target now, not the player -- phase 4). Amortised:
        // a BFS only when the alert crosses a cell. No live alert -> no field, and the zombies wander / rely on sight.
        void RebuildFieldForAlert()
        {
            if (_clock >= _alertExpiry) { _hasField = false; return; }   // the noise faded -> nothing to path to
            _fieldAnchor = _alertPos;
            var cell = (Mathf.FloorToInt(_alertPos.X / ZombieFlowField.Cell), Mathf.FloorToInt(_alertPos.Z / ZombieFlowField.Cell));
            if (_hasField && cell == _fieldCell) return;
            _fieldCell = cell;
            var min = new Vector3(_alertPos.X - FieldRadius, 0f, _alertPos.Z - FieldRadius);
            var max = new Vector3(_alertPos.X + FieldRadius, 0f, _alertPos.Z + FieldRadius);
            _field.Build(min, max, _alertPos, Walkable);
            _hasField = true;
        }

        // Can this zombie SEE a player? Nearest anchor within SightRange with a clear line of sight (walls block it).
        // Returns the seen player's position, or Vector3.Zero if none. Only HOT zombies pay for it (they're few).
        bool SeePlayer(PhysicsDirectSpaceState3D space, Vector3 from, out Vector3 seen)
        {
            seen = default;
            if (space == null) return false;
            Vector3 eye = from + Vector3.Up * 1.5f;
            foreach (var a in _anchors)
            {
                float dx = a.X - from.X, dz = a.Z - from.Z;
                if (dx * dx + dz * dz > SightRange * SightRange) continue;
                var q = PhysicsRayQueryParameters3D.Create(eye, a + Vector3.Up * 1.0f, WorldLayers.World);   // a wall between = can't see
                if (space.IntersectRay(q).Count == 0) { seen = a; return true; }
            }
            return false;
        }

        // A cell is walkable unless a building occupies its walking-height band. Probe a box ~0.5-2.2 m above the
        // sampled ground -- ABOVE flat terrain (open ground reads clear) but through any wall. Cached, since buildings
        // are static. (Phase-2 approximation: steep slopes can over-block; a building-only collision layer would tighten
        // it for the HOT routing later.)
        bool Walkable(int cx, int cz)
        {
            var key = (cx, cz);
            if (_walkCache.TryGetValue(key, out bool cached)) return cached;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return true;   // physics not up yet -> assume open, don't cache
            float wx = (cx + 0.5f) * ZombieFlowField.Cell, wz = (cz + 0.5f) * ZombieFlowField.Cell;
            float gy = Terr != null ? Terr.SampleHeight(wx, wz) : 0f;
            _probe ??= new BoxShape3D { Size = new Vector3(ZombieFlowField.Cell * 0.9f, 1.7f, ZombieFlowField.Cell * 0.9f) };
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = _probe,
                Transform = new Transform3D(Basis.Identity, new Vector3(wx, gy + 1.35f, wz)),
                CollisionMask = WorldLayers.World,
                CollideWithBodies = true, CollideWithAreas = false,
            };
            bool walk = space.IntersectShape(q, 1).Count == 0;
            _walkCache[key] = walk;
            return walk;
        }

        // PHASE 3: promote zombies within ~45 m of a player to a visible ZombieBody (mesh + collision), demote past 60 m,
        // retire dead ones. HOT bodies steer by the field + separation (their own MoveAndSlide moves them); WARM/COLD
        // zombies keep drifting as pure DATA (COLD on the coarse step). FROZEN chunks aren't touched.
        void Move(double delta)
        {
            float dt = (float)delta;
            _coldAcc += delta;
            bool coldStep = _coldAcc >= ColdStep;
            if (coldStep) _coldAcc = 0;

            _hotList.Clear();
            // pass 1: body promote/demote + death cleanup; drift the data-only (WARM/COLD) ones TOWARD THE SOUND; gather HOT
            foreach (var c in _chunks.Values)
            {
                if (c.Live == null) continue;                       // FROZEN
                bool cold = c.Tier == Tier.Cold;
                for (int i = c.Live.Count - 1; i >= 0; i--)
                {
                    var z = c.Live[i];
                    if (z.Body != null && (!GodotObject.IsInstanceValid(z.Body) || z.Body.Dead)) { z.Body = null; c.Live.RemoveAt(i); continue; }   // killed -> gone
                    float d = NearestAnchorDist(z.Pos);             // XZ distance to the nearest player
                    if (z.Body == null && d < HotBodyDist) { z.Body = new ZombieBody(); AddChild(z.Body); z.Body.GlobalPosition = z.Pos; }
                    else if (z.Body != null && d > HotBodyDrop) { z.Body.QueueFree(); z.Body = null; }

                    if (z.Body != null) { z.Pos = z.Body.GlobalPosition; _hotList.Add(z); continue; }   // HOT -> steered in pass 2

                    // WARM/COLD drift toward the last SOUND -- only when there's a live alert (else they stay put / wander)
                    if (!_hasField) continue;
                    if (cold && !coldStep) continue;
                    float step = cold ? ZombieSpeed * ColdStep : ZombieSpeed * dt;
                    float ddx = _fieldAnchor.X - z.Pos.X, ddz = _fieldAnchor.Z - z.Pos.Z;
                    if (ddx * ddx + ddz * ddz <= StopDist * StopDist) continue;
                    var wdir = _field.Sample(z.Pos);
                    if (wdir == Vector2.Zero) continue;
                    float nx = z.Pos.X + wdir.X * step, nz = z.Pos.Z + wdir.Y * step;
                    z.Pos = new Vector3(nx, Terr != null ? Terr.SampleHeight(nx, nz) : z.Pos.Y, nz);
                }
            }

            // pass 2: HOT bodies -- SIGHT overrides the sound (a zombie that can SEE a player chases it directly; else it
            // paths to the last sound), plus boids separation so a horde surrounds rather than stacks.
            var space = GetWorld3D()?.DirectSpaceState;
            for (int i = 0; i < _hotList.Count; i++)
            {
                var z = _hotList[i];
                if (z.Body == null || !GodotObject.IsInstanceValid(z.Body)) continue;
                Vector2 sep = Vector2.Zero;
                for (int j = 0; j < _hotList.Count; j++)
                {
                    if (j == i) continue;
                    var o = _hotList[j];
                    float ax = z.Pos.X - o.Pos.X, az = z.Pos.Z - o.Pos.Z, d2 = ax * ax + az * az;
                    if (d2 > 1e-4f && d2 < SepR * SepR) { float dd = Mathf.Sqrt(d2); sep += new Vector2(ax / dd, az / dd) * (1f - dd / SepR); }
                }
                Vector2 want;
                if (SeePlayer(space, z.Pos, out var seen))   // I can SEE a player -> chase it directly (sight beats sound)
                {
                    float sx = seen.X - z.Pos.X, sz = seen.Z - z.Pos.Z;
                    want = (sx * sx + sz * sz <= StopDist * StopDist) ? Vector2.Zero : new Vector2(sx, sz).Normalized();
                    HearNoise(seen, 6f);   // seeing a player also refreshes the alert, so nearby unseen zombies get pulled in
                }
                else if (_hasField)                          // can't see -> path to the last SOUND
                {
                    float dx = _fieldAnchor.X - z.Pos.X, dz = _fieldAnchor.Z - z.Pos.Z;
                    want = (dx * dx + dz * dz <= StopDist * StopDist) ? Vector2.Zero : _field.Sample(z.Pos);
                }
                else want = Vector2.Zero;                    // no sight, no sound -> hold (wander can slot in here later)
                z.Body.DesiredVel = (want + sep * SepStrength).LimitLength(1f) * ZombieSpeed;
            }
        }
    }
}
