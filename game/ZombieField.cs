using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // PEI's REAL zombie spawns -- SOURCE-ACCURATE POI-pocket model (source: LevelZombies.load server path + ZombieManager.generateZombies).
    // Zombies live ONLY in the 19 navmesh pockets, NEVER the wilderness. LevelZombies buckets each Spawns/Animals.dat point by
    // LevelNavigation.tryGetBounds (point in a pocket's EXPANDED box) + checkNavigation (in its NON-expanded size-64 box) -- both XZ-only
    // AABB tests -- and DISCARDS any point outside every pocket. Each pocket then spawns
    //   min( flagData.maxZombies[PEI 64], ceil(pocketPointCount * Spawn_Chance[0.25 NORMAL survival]) )
    // zombies drawn at random from THAT pocket's points, at point + 0.5y, all ON the baked navmesh so the Phase-2 agent can pathfind.
    // Pockets populate when the player enters range (listen-server onBoundUpdated model) and despawn when they leave; dead ones respawn
    // in-pocket on a cooldown. This replaces the old island-wide distance stream, which spawned wilderness zombies the real game never would.
    public partial class ZombieField : Node3D
    {
        // Streaming anchor (C4, the LootField precedent): an explicitly-set Player is the SP path and is
        // honored EXACTLY -- the single anchor, the registry never consulted. Player == null (server worlds,
        // where nobody wires one) streams on EVERY registered player via PlayerRegistry instead, mirroring
        // ZombieController's Target-less fallback. Spawned brains keep Target = Player verbatim, so SP
        // zombies chase the local player exactly as before and server brains (null) registry-hunt.
        public PlayerController Player;
        public Terrain Terr;

        const float SpawnChance = 0.25f;   // Provider.modeConfigData.Zombies.Spawn_Chance -- NORMAL survival (Easy .2 / Normal .25 / Hard .3)
        const float ActivateR = 90f;       // player distance to a pocket's box that POPULATES it (source populates the whole bound on entry)
        const float DeactivateR = 130f;    // ...and despawns it again -- 40m hysteresis so standing on the edge doesn't thrash
        const float RespawnDelay = 40f;    // in-pocket respawn cooldown when below cap (source: day 360 / night 30; port has no zombie day-night gate yet)
        const int GlobalMaxLive = 96;      // safety cap across all active pockets (player is normally inside 1, near ~1 more)

        class Pocket
        {
            public NavPocket Nav;
            public readonly List<Vector3> Pts = new();   // on-navmesh spawn points bucketed here (Godot space, terrain height)
            public int Cap;                              // min(MaxZombies, ceil(Pts.Count * SpawnChance))
            public bool Active;
            public readonly List<ZombieController> Live = new();
            public double NextRespawn;
        }
        readonly List<Pocket> _pockets = new();
        double _acc = 999, _clock;

        public void LoadFromPei(string peiRoot)
        {
            var navPockets = ZombieNav.LoadPockets(peiRoot);
            if (navPockets.Count == 0) { GD.Print("[zombies] no pockets -- no zombies (need Environment/Bounds.dat)"); return; }
            foreach (var np in navPockets) _pockets.Add(new Pocket { Nav = np });

            string path = System.IO.Path.Combine(peiRoot, "Spawns", "Animals.dat");
            if (!System.IO.File.Exists(path)) return;
            var b = System.IO.File.ReadAllBytes(path); int o = 0;
            byte version = b[o++];
            if (version == 0) return;
            int total = 0, kept = 0, water = 0;
            for (int x = 0; x < 64; x++)
                for (int y = 0; y < 64; y++)
                {
                    ushort count = System.BitConverter.ToUInt16(b, o); o += 2;
                    for (int i = 0; i < count; i++)
                    {
                        o++;                                          // byte type (PEI zombie region -- one NORMAL table)
                        float px = System.BitConverter.ToSingle(b, o); o += 4;
                        o += 4;                                       // skip point.y -- zombies stand on the port's terrain
                        float pz = System.BitConverter.ToSingle(b, o); o += 4;
                        total++;
                        float gx = px, gz = -pz;                      // negate-Z into Godot space
                        int pk = FindPocketXZ(gx, gz);                // source tryGetBounds + checkNavigation (XZ box)
                        if (pk < 0) continue;                         // wilderness point -> DISCARDED (source-accurate)
                        if (Terrain.IsWater(Terr.SampleDominantLayer(gx, gz))) { water++; continue; }   // no ocean spawns
                        _pockets[pk].Pts.Add(new Vector3(gx, Terr.SampleHeight(gx, gz), gz));
                        kept++;
                    }
                }
            int capSum = 0;
            foreach (var p in _pockets)
            {
                p.Cap = p.Nav.SpawnZombies ? Mathf.Min(p.Nav.MaxZombies, Mathf.CeilToInt(p.Pts.Count * SpawnChance)) : 0;
                capSum += p.Cap;
            }
            GD.Print($"[zombies] pocket-based: {kept}/{total} Animals.dat pts bucketed into {_pockets.Count} pockets " +
                     $"({total - kept - water} wilderness + {water} water discarded); pop cap sum={capSum} = Σ min(maxZombies, ceil(pts*{SpawnChance}))");
        }

        int FindPocketXZ(float x, float z)
        {
            for (int i = 0; i < _pockets.Count; i++)
            {
                var c = _pockets[i].Nav.Center; var h = _pockets[i].Nav.HalfExtent;
                if (x >= c.X - h.X && x <= c.X + h.X && z >= c.Z - h.Z && z <= c.Z + h.Z) return i;
            }
            return -1;
        }

        // The anchor set one streaming pass keys on: [Player.GlobalPosition] when Player is set (the SP
        // path, byte-identical to the old single-Player gate), else every valid PlayerRegistry entry.
        readonly List<Vector3> _anchors = new();

        List<Vector3> GatherAnchors()
        {
            _anchors.Clear();
            if (Player != null) { _anchors.Add(Player.GlobalPosition); return _anchors; }
            foreach (var p in PlayerRegistry.All)
                if (IsInstanceValid(p)) _anchors.Add(p.GlobalPosition);
            return _anchors;
        }

        // Nearest-any-anchor distance to a pocket's box -- with one anchor this IS the old DistToBoxXZ(pp).
        static float DistToBoxAnyXZ(List<Vector3> anchors, NavPocket np)
        {
            float best = float.MaxValue;
            foreach (var a in anchors) best = Mathf.Min(best, DistToBoxXZ(a, np));
            return best;
        }

        public override void _Process(double delta)
        {
            _clock += delta;
            _acc += delta;
            if (_acc < 0.4 || Terr == null) return;
            var anchors = GatherAnchors();
            if (anchors.Count == 0) return;   // nobody to stream around (pre-join server) -- same as the old Player == null gate
            _acc = 0;

            int liveTotal = 0;
            foreach (var p in _pockets) liveTotal += CountLive(p);

            foreach (var p in _pockets)
            {
                float d = DistToBoxAnyXZ(anchors, p.Nav);
                if (!p.Active && d <= ActivateR) { p.Active = true; Populate(p, ref liveTotal); }
                else if (p.Active && d >= DeactivateR) { Despawn(p); p.Active = false; }
                else if (p.Active) MaintainRespawn(p, ref liveTotal);
            }
        }

        int CountLive(Pocket p)
        {
            for (int i = p.Live.Count - 1; i >= 0; i--) if (!IsInstanceValid(p.Live[i])) p.Live.RemoveAt(i);
            return p.Live.Count;
        }

        // Populate a whole pocket at once, random distinct points until cap (source generateZombies eligibleSpawnpoints RemoveAt loop).
        void Populate(Pocket p, ref int liveTotal)
        {
            if (p.Cap <= 0 || p.Pts.Count == 0) return;
            var idx = new List<int>(p.Pts.Count);
            for (int i = 0; i < p.Pts.Count; i++) idx.Add(i);
            int want = p.Cap;
            while (want > 0 && idx.Count > 0 && liveTotal < GlobalMaxLive)
            {
                int r = (int)(GD.Randi() % (uint)idx.Count);
                var pt = p.Pts[idx[r]]; idx.RemoveAt(r);
                Spawn(p, pt); liveTotal++; want--;
            }
        }

        void MaintainRespawn(Pocket p, ref int liveTotal)
        {
            if (p.Cap <= 0 || p.Pts.Count == 0 || liveTotal >= GlobalMaxLive) return;
            if (CountLive(p) >= p.Cap || _clock < p.NextRespawn) return;
            var pt = p.Pts[(int)(GD.Randi() % (uint)p.Pts.Count)];
            Spawn(p, pt); liveTotal++;
            p.NextRespawn = _clock + RespawnDelay;
        }

        void Spawn(Pocket p, Vector3 pt)
        {
            var z = new ZombieController { Target = Player, Speciality = ZombieController.ESpeciality.NORMAL };
            AddChild(z);
            z.GlobalPosition = new Vector3(pt.X, pt.Y + 0.5f, pt.Z);   // source: spawn.point + Vector3.up*0.5
            p.Live.Add(z);
        }

        void Despawn(Pocket p)
        {
            foreach (var z in p.Live) if (IsInstanceValid(z)) z.QueueFree();
            p.Live.Clear();
        }

        static float DistToBoxXZ(Vector3 pos, NavPocket np)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(pos.X - np.Center.X) - np.HalfExtent.X);
            float dz = Mathf.Max(0f, Mathf.Abs(pos.Z - np.Center.Z) - np.HalfExtent.Z);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // OFFLINE VERIFY (--zombietest): the positions Populate() WOULD spawn for every pocket, no nodes created,
        // so a headless check can confirm each lands on the baked navmesh. Same random-pick logic as Populate.
        public List<(int pk, Vector3 pos)> DebugPlanSpawns()
        {
            var outp = new List<(int, Vector3)>();
            for (int pi = 0; pi < _pockets.Count; pi++)
            {
                var p = _pockets[pi];
                if (p.Cap <= 0 || p.Pts.Count == 0) continue;
                var idx = new List<int>(p.Pts.Count);
                for (int i = 0; i < p.Pts.Count; i++) idx.Add(i);
                int want = p.Cap;
                while (want > 0 && idx.Count > 0)
                {
                    int r = (int)(GD.Randi() % (uint)idx.Count);
                    var pt = p.Pts[idx[r]]; idx.RemoveAt(r);
                    outp.Add((pi, new Vector3(pt.X, pt.Y + 0.5f, pt.Z))); want--;
                }
            }
            return outp;
        }

        public int PocketCount => _pockets.Count;

        // TEST SEAMS (net.zombiefield_anyplayer, same style as DebugPlanSpawns): a synthetic pocket -- no
        // map data on CI -- plus probes for the exact anchor set / per-pocket distance / activation state
        // the streaming pass keys on. pts are used verbatim (the caller supplies terrain-height'd points).
        public int DebugAddPocket(Vector3 center, Vector3 halfExtent, Vector3[] pts, int cap)
        {
            var p = new Pocket { Nav = new NavPocket { Center = center, HalfExtent = halfExtent, MaxZombies = cap, SpawnZombies = true }, Cap = cap };
            p.Pts.AddRange(pts);
            _pockets.Add(p);
            return _pockets.Count - 1;
        }
        public List<Vector3> DebugAnchors() => GatherAnchors();
        public float DebugPocketDist(int pocket) => DistToBoxAnyXZ(GatherAnchors(), _pockets[pocket].Nav);
        public bool DebugPocketActive(int pocket) => _pockets[pocket].Active;
        public int DebugLiveCount(int pocket) => CountLive(_pockets[pocket]);
        public List<ZombieController> DebugLive(int pocket) { CountLive(_pockets[pocket]); return _pockets[pocket].Live; }
    }
}
