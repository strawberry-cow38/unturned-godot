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
        // HARD CEILING ON VISIBLE BODIES (strawberry 2026-09-17: "zombies infinitely spawn. add a zombie cap").
        // Budget already bounds how many zombies are SIMULATED, but nothing bounded how many got a ZombieBody:
        // Move() gave one to every zombie inside HotBodyDist, so a dense corner simply built as many rigs as it
        // had zombies. That is the cost that actually shows up on screen -- a rig, a skeleton and a MoveAndSlide
        // each -- and it now stops at a number instead of at whatever the map happens to contain.
        public const int MaxHotBodies = 48;
        // How long a killed zombie's slot stays empty before the chunk may refill it. Without this, clearing an
        // area empties it PERMANENTLY, which is its own bug; with it, kills mean something for a while.
        public const double RespawnSeconds = 300.0;

        // Tier thresholds: distance (m, XZ) from a chunk's CENTRE to the nearest player. ENTER a hotter tier at the
        // inner radius, LEAVE it only past the outer -- the gap is the hysteresis band.
        const float HotIn = 48f, HotOut = 80f;
        const float WarmIn = 128f, WarmOut = 168f;
        const float ColdIn = 256f, ColdOut = 304f;

        // ---- phase 2: the flow field + drift ----
        const float FieldRadius = 160f;    // the flow field covers ±this around the anchor (comfortably past WARM)
        // PLAYER WALK SPEED (strawberry 2026-09-17: "change the zombie walk speed to be the same as our
        // player walk speed"). Was 1.3, which was tuned DOWN to stop the feet skating -- the wrong end of that
        // trade, and it made a horde something you could stroll away from. The skate is fixed at the ANIMATION
        // end now (ZombieBody slows the clip to the ground), so the speed no longer has to be what gives way.
        public const float ZombieSpeed = SDG.Unturned.PlayerMovementDef.SPEED_STAND;   // 4.5 m/s -- public so the --zface diagnostic drives at the REAL speed instead of its own copy of it (master: don't speed up the anim, slow the zombie). HOT+WARM; COLD takes ONE coarse step every ColdStep seconds
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
        // RAISED 45 -> 90 (strawberry 2026-09-17: "their render distance is super short"). This was the only
        // thing bounding the cost of visible zombies, so it had to be small. MaxHotBodies bounds it directly now,
        // and 48 rigs cost 48 rigs whatever radius they were chosen from -- so the radius is free to describe how
        // far you can SEE a zombie rather than how many the machine can afford.
        const float HotBodyDist = 90f;    // a zombie within this of a player gets a visible ZombieBody...
        const float HotBodyDrop = 110f;   // ...and loses it past this (hysteresis, so the edge doesn't flicker)
        const float SepR = 2.8f;          // separation radius -- HOT bodies steer apart (boids) so a horde SPREADS instead of stacking into one blob (master: "make them aware of eachother")
        const float SepStrength = 1.7f;
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
        // Table is the spawn point's own table index (Police/Farm/...); Outfit is a per-zombie seed, held HERE
        // rather than on the body because the body is destroyed on demote and rebuilt on re-promote -- rolling the
        // clothes in the constructor would change what a zombie is wearing every time you walked away and back.
        public class Zombie { public Vector3 Home; public Vector3 Pos; public Vector2 Vel; public ZombieBody Body; public byte Table = 255; public uint Outfit = 1; }

        public class Chunk
        {
            public int Cx, Cz;
            public Vector3 Center;                          // world centre (Y = 0; XZ is what tiers test)
            public readonly List<Vector3> SpawnPts = new(); // Animals.dat points that fell in this chunk
            public readonly List<byte> SpawnTables = new();  // each point's zombie TABLE index, parallel to SpawnPts
            public int Cap;                                 // how many zombies this chunk holds when awake
            // ⚠ KILLS HAVE TO OUTLIVE Live. A chunk drops its Live list when it FREEZES and rebuilds a full Cap
            // when you come back, so before this the only record that anything died went away with the list --
            // clear a town, walk far enough for the chunk to freeze, return, and the whole population is back.
            // That is the "infinitely spawn": not a spawner running away, but kills that never persisted.
            public int Killed;                              // slots emptied by death, not yet eligible to refill
            public double RefillAt;                         // clock at which one killed slot may come back
            public Tier Tier = Tier.Frozen;
            public List<Zombie> Live;                       // null while FROZEN; materialized (Cap zombies) once COLD+
            public uint Seed;                               // deterministic per-chunk spawn-point pick
            public int Population => Live?.Count ?? Mathf.Max(0, Cap - Killed);   // FROZEN reports its POTENTIAL, minus what died there
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
            ZombieTables.Load(peiRoot);   // the wardrobe that goes with these points
            string path = System.IO.Path.Combine(peiRoot, "Spawns", "Animals.dat");
            if (!System.IO.File.Exists(path)) { Log.Print("[zchunk] no Animals.dat -- no zombie spawns"); return; }
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
                        byte table = b[o++];   // the point's ZOMBIE TABLE index -- Police, Farm, Civilian...
                        // ⚠ This was skipped as "PEI = one NORMAL zombie table". PEI's 1456 points carry 18
                        // DISTINCT values here, so that comment cost us the entire per-region wardrobe.
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
                        c.SpawnTables.Add(table);
                        kept++;
                    }
                }
            int capSum = 0;
            foreach (var c in _chunks.Values)
            {
                c.Cap = Mathf.Min(ChunkMaxLive, Mathf.CeilToInt(c.SpawnPts.Count * SpawnChance));
                capSum += c.Cap;
            }
            Log.Print($"[zchunk] {kept}/{total} Animals.dat pts ({water} water dropped) -> {_chunks.Count} chunks @ {ChunkSize}m; " +
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

        /// <summary>Can a zombie at this position HEAR the live alert? ⚠ The loudness has always been a RADIUS IN
        /// METRES -- Walk 10, Sprint 18, CrouchWalk 5, Gunshot 48 -- and GetStealthDetectionRadius is documented as
        /// "the radius within which a zombie can sense this player". Nothing used it that way: it only decided which
        /// noise WON, and then every non-frozen zombie in the ±160 m field walked at the anchor regardless.
        ///
        /// So a footstep reached exactly as far as a gunshot, and since a moving player emits every 0.4 s the 8 s
        /// alert never lapsed -- the whole map aggroed permanently the moment you walked (strawberry 2026-09-17:
        /// "zombies seem to agro on me no matter what"). Sneaking, crouching and suppressors all had a number that
        /// went nowhere. This is what makes standing-around-until-they-hear-you the default rather than the
        /// exception: no alert in earshot, and the WARM/COLD drift and the HOT sound-follow both decline.</summary>
        bool Hears(Vector3 pos)
        {
            if (_clock >= _alertExpiry) return false;
            float dx = pos.X - _alertPos.X, dz = pos.Z - _alertPos.Z;
            return dx * dx + dz * dz <= _alertLoud * _alertLoud;
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
                // Record the POINT too, not just the live zombie. Materialize refuses a chunk with no spawn points,
                // so without this a seeded chunk that froze came back permanently EMPTY -- the debug path quietly
                // behaving unlike the real one, which is exactly where a harness stops proving anything.
                c.SpawnPts.Add(p); c.SpawnTables.Add(255);
                c.Live.Add(new Zombie { Home = p, Pos = p, Table = 255, Outfit = s | 1u });
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
                // One killed slot becomes eligible again every RespawnSeconds, so a cleared area stays cleared for
                // a while and then refills -- rather than refilling the instant you look away, or never.
                if (c.Killed > 0 && c.RefillAt > 0 && _clock >= c.RefillAt)
                { c.Killed--; c.RefillAt = c.Killed > 0 ? _clock + RespawnSeconds : 0; }
                if (c.Tier >= Tier.Cold && c.Live == null) Materialize(c);
                else if (c.Tier == Tier.Frozen && c.Live != null) c.Live = null;   // back to pure data
                TierChunks[(int)c.Tier]++;
                TierZombies[(int)c.Tier] += c.Population;
            }
        }

        int _hotBodies;                    // bodies alive RIGHT NOW, recounted every Move (see the note there)
        readonly List<(float d, Zombie z)> _promote = new();   // this frame's body candidates, promoted nearest-first
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
            int want = Mathf.Max(0, c.Cap - c.Killed);   // the dead do not come back with the thaw
            c.Live = new List<Zombie>(want);
            if (c.SpawnPts.Count == 0) return;
            uint s = c.Seed | 1u;
            for (int i = 0; i < want; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;             // xorshift32 -- deterministic pick
                int pi = (int)(s % (uint)c.SpawnPts.Count);
                var p = c.SpawnPts[pi];
                byte tbl = pi < c.SpawnTables.Count ? c.SpawnTables[pi] : (byte)255;
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                c.Live.Add(new Zombie { Home = p, Pos = p, Table = tbl, Outfit = s | 1u });
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
            // ONE query object for the whole sweep, and the result DISPOSED each time. Both of these are native
            // handles: a fresh PhysicsRayQueryParameters3D per candidate anchor plus an abandoned result
            // Dictionary per ray meant two tracked objects per anchor per call, and this runs over every anchor
            // of every chunk. WorldItem's LOS scan already reuses a single query object for exactly this reason.
            var q = PhysicsRayQueryParameters3D.Create(eye, eye, WorldLayers.World);   // a wall between = can't see
            q.From = eye;
            foreach (var a in _anchors)
            {
                float dx = a.X - from.X, dz = a.Z - from.Z;
                if (dx * dx + dz * dz > SightRange * SightRange) continue;
                q.To = a + Vector3.Up * 1.0f;
                using var hit = space.IntersectRay(q);
                if (hit.Count == 0) { seen = a; return true; }
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
            _promote.Clear();
            // ⚠ RECOUNTED, never accumulated. A body is dropped on demote AND on death AND by QueueFree, so an
            // increment/decrement pair has three places to get out of step -- and a counter that drifts upward
            // silently stops every future spawn, which looks exactly like the bug this cap was added to fix.
            // Counting what actually exists is O(live zombies), a few hundred at worst, and cannot drift.
            _hotBodies = 0;
            foreach (var cc in _chunks.Values)
            {
                if (cc.Live == null) continue;
                foreach (var zz in cc.Live) if (zz.Body != null) _hotBodies++;
            }
            // pass 1: body promote/demote + death cleanup; drift the data-only (WARM/COLD) ones TOWARD THE SOUND; gather HOT
            foreach (var c in _chunks.Values)
            {
                if (c.Live == null) continue;                       // FROZEN
                bool cold = c.Tier == Tier.Cold;
                for (int i = c.Live.Count - 1; i >= 0; i--)
                {
                    var z = c.Live[i];
                    if (z.Body != null && (!GodotObject.IsInstanceValid(z.Body) || z.Body.Dead))
                    {   // killed -> gone, and REMEMBERED, so the chunk does not hand the slot back on the next thaw
                        z.Body = null; c.Live.RemoveAt(i);
                        if (c.Killed < c.Cap) { c.Killed++; if (c.RefillAt <= 0) c.RefillAt = _clock + RespawnSeconds; }
                        continue;
                    }
                    float d = NearestAnchorDist(z.Pos);             // XZ distance to the nearest player
                    // ⚠ CANDIDATE, not a promotion. Taking the cap's slots in dictionary order hands bodies to
                    // whichever chunk the map happened to enumerate first, so in a crowd the ones you are looking
                    // AT could stay invisible while something 80 m behind you got a rig. Harmless while the radius
                    // was 45 m and nothing capped the count; both of those just changed. Sorted by distance below.
                    if (z.Body == null && d < HotBodyDist) _promote.Add((d, z));
                    else if (z.Body != null && d > HotBodyDrop) { z.Body.QueueFree(); z.Body = null; }

                    if (z.Body != null) { z.Pos = z.Body.GlobalPosition; _hotList.Add(z); continue; }   // HOT -> steered in pass 2

                    // WARM/COLD drift toward the last SOUND -- only when there's a live alert (else they stay put / wander)
                    if (!_hasField || !Hears(z.Pos)) continue;   // out of earshot -> stay where you are
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
            // NEAREST FIRST, up to the cap. A frame's newly-promoted bodies steer from the next frame, which is
            // a tick of latency on something that just came into view and cheaper than re-walking every chunk.
            if (_promote.Count > 0)
            {
                _promote.Sort((a, b) => a.d.CompareTo(b.d));
                foreach (var (_, z) in _promote)
                {
                    if (_hotBodies >= MaxHotBodies) break;
                    z.Body = new ZombieBody(z.Table, z.Outfit);
                    AddChild(z.Body);
                    z.Body.GlobalPosition = z.Pos;
                    _hotBodies++;
                }
            }

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
                else if (_hasField && Hears(z.Pos))          // can't see, but CAN hear -> path to the last SOUND
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
