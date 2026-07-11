using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // PEI's REAL zombie spawns, region/distance-streamed. Source: Spawns/Animals.dat (legacy name -- LevelZombies.cs reads
    // THIS file for zombies): byte version, then a 64x64 region grid each [u16 count, count x (byte type + Vector3)], each a
    // ZombieSpawnpoint(type,point). 1456 points on PEI (vs the old 52-zombie Bounds.dat navmesh approximation). Streamed like
    // Unturned's real region loader -- only points within ~85m are live (cap 64), so the horde density/placement is right
    // without 1456 live AI. Killed zombies respawn after a cooldown; walking away despawns far ones, returning re-streams.
    public partial class ZombieField : Node3D
    {
        public PlayerController Player;
        public Terrain Terr;

        struct Pt { public float X, Z; }                            // world X, Z (negate-Z'd)
        readonly List<Pt> _pts = new();
        readonly Dictionary<int, ZombieController> _live = new();
        readonly Dictionary<int, double> _cooldown = new();         // idx -> earliest respawn clock (killed zombies)

        double _acc = 999, _clock;                                  // _acc large -> first _Process streams immediately (for the shot)
        const float SpawnR = 85f, DespawnR = 115f, RespawnDelay = 60f;
        const int MaxLive = 64;

        public void LoadFromPei(string peiRoot)
        {
            string path = System.IO.Path.Combine(peiRoot, "Spawns", "Animals.dat");
            if (!System.IO.File.Exists(path)) return;
            var b = System.IO.File.ReadAllBytes(path); int o = 0;
            byte version = b[o++];
            if (version == 0) return;
            for (int x = 0; x < 64; x++)
                for (int y = 0; y < 64; y++)
                {
                    ushort count = System.BitConverter.ToUInt16(b, o); o += 2;
                    for (int i = 0; i < count; i++)
                    {
                        o++;                                        // byte type (zombie region -- all spawn the same NORMAL zombie here)
                        float px = System.BitConverter.ToSingle(b, o); o += 4;
                        o += 4;                                     // skip point.y (zombies stand on the port's terrain)
                        float pz = System.BitConverter.ToSingle(b, o); o += 4;
                        _pts.Add(new Pt { X = px, Z = -pz });       // negate-Z
                    }
                }
            GD.Print($"[zombies] {_pts.Count} real spawn points loaded (Spawns/Animals.dat), region-streamed (cap {MaxLive})");
        }

        public override void _Process(double delta)
        {
            _clock += delta;
            _acc += delta;
            if (_acc < 0.4 || Player == null || Terr == null) return;
            _acc = 0;
            var pp = Player.GlobalPosition;

            var drop = new List<int>();                             // detect kills + despawn far
            foreach (var kv in _live)
            {
                if (!IsInstanceValid(kv.Value)) { _cooldown[kv.Key] = _clock + RespawnDelay; drop.Add(kv.Key); continue; }   // killed -> respawn timer
                float dx = kv.Value.GlobalPosition.X - pp.X, dz = kv.Value.GlobalPosition.Z - pp.Z;
                if (dx * dx + dz * dz > DespawnR * DespawnR) { kv.Value.QueueFree(); drop.Add(kv.Key); }                       // wandered out of range
            }
            foreach (var k in drop) _live.Remove(k);

            for (int idx = 0; idx < _pts.Count; idx++)               // stream in near points
            {
                if (_live.ContainsKey(idx)) continue;
                if (_cooldown.TryGetValue(idx, out var t) && _clock < t) continue;
                var p = _pts[idx];
                float dx = p.X - pp.X, dz = p.Z - pp.Z;
                if (dx * dx + dz * dz > SpawnR * SpawnR) continue;
                if (_live.Count >= MaxLive) break;
                if (Terrain.IsWater(Terr.SampleDominantLayer(p.X, p.Z))) continue;   // no zombies in the ocean
                var z = new ZombieController { Target = Player, Speciality = ZombieController.ESpeciality.NORMAL };
                AddChild(z);
                z.GlobalPosition = new Vector3(p.X, Terr.SampleHeight(p.X, p.Z) + 1f, p.Z);
                _live[idx] = z;
            }
        }
    }
}
