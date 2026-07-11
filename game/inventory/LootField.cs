using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    // PEI loot, region/distance-streamed like Unturned's real region loader. Source: Spawns/Jars.dat (misnamed -- it's the
    // item spawn points: byte version, then 64x64 regions each [u16 count, count x (byte type + Vector3)] where `type`
    // indexes Spawns/Items.dat's spawn tables) + Spawns/Items.dat (u8 ver, u8 tableCount, per table [color 3B, name str,
    // u16 tableID if v>3, u8 tierCount, per tier: name str, f32 chance, u8 spawnCount, spawnCount x u16 id]). 2470 points on
    // PEI. Only points near the player are live (LevelItems streams per-region); each rolls its table ONCE (deterministic per
    // index) into a real item id. Unknown ids fall back to the table's editor colour + name so the map still reads
    // (Police=blue, Military=green, Food=brown...). Picked-up points stay taken; walking away/back re-streams the rest.
    public partial class LootField : Node3D
    {
        public Node3D Player;
        public Terrain Terr;

        struct Pt { public byte Type; public float X, Z; }              // world X, Z already negate-Z'd
        readonly List<Pt> _pts = new();
        readonly Dictionary<int, WorldItem> _live = new();             // point index -> live drop
        readonly HashSet<int> _taken = new();                          // picked up: don't respawn

        Color[] _tblColor;                                            // per table: editor RGB (fallback tint)
        string[] _tblName;                                           // per table: readable name (fallback label)
        (float chance, ushort[] ids)[][] _tiers;                      // per table: tiers

        double _acc = 999;                                          // large -> first _Process streams immediately (for the shot)
        const float SpawnR = 95f, DespawnR = 125f;
        const int MaxLive = 600;

        public void LoadFromPei(string peiRoot)
        {
            LoadTables(System.IO.Path.Combine(peiRoot, "Spawns", "Items.dat"));
            LoadPoints(System.IO.Path.Combine(peiRoot, "Spawns", "Jars.dat"));
            GD.Print($"[loot] {_pts.Count} item spawn points loaded, {(_tblName?.Length ?? 0)} tables");
        }

        void LoadTables(string path)
        {
            if (!System.IO.File.Exists(path)) return;
            var b = System.IO.File.ReadAllBytes(path); int o = 0;
            byte U8() => b[o++];
            ushort U16() { var v = System.BitConverter.ToUInt16(b, o); o += 2; return v; }
            float F32() { var v = System.BitConverter.ToSingle(b, o); o += 4; return v; }
            string RStr() { int n = U8(); var s = System.Text.Encoding.UTF8.GetString(b, o, n); o += n; return s; }
            byte ver = U8();
            if (ver > 1 && ver < 3) o += 8;                          // SteamID
            byte tcount = U8();
            _tblColor = new Color[tcount]; _tblName = new string[tcount]; _tiers = new (float, ushort[])[tcount][];
            for (int t = 0; t < tcount; t++)
            {
                byte r = U8(), g = U8(), bl = U8();
                _tblColor[t] = new Color(r / 255f, g / 255f, bl / 255f);
                _tblName[t] = RStr().Replace('_', ' ');
                if (ver > 3) o += 2;                                 // tableID
                byte tiers = U8();
                _tiers[t] = new (float, ushort[])[tiers];
                for (int ti = 0; ti < tiers; ti++)
                {
                    RStr();                                         // tier name
                    float chance = F32();
                    byte sc = U8();
                    var ids = new ushort[sc];
                    for (int s = 0; s < sc; s++) ids[s] = U16();
                    _tiers[t][ti] = (chance, ids);
                }
            }
        }

        void LoadPoints(string path)
        {
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
                        byte type = b[o++];
                        float px = System.BitConverter.ToSingle(b, o); o += 4;
                        o += 4;                                     // skip point.y (we sit on the port's terrain)
                        float pz = System.BitConverter.ToSingle(b, o); o += 4;
                        _pts.Add(new Pt { Type = type, X = px, Z = -pz });   // negate-Z
                    }
                }
        }

        static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }

        int Roll(int idx, byte type)                                // deterministic per point; weighted tier pick (chance), uniform id
        {
            if (_tiers == null || type >= _tiers.Length) return -1;
            var tiers = _tiers[type];
            if (tiers == null || tiers.Length == 0) return -1;
            uint h = Hash((uint)idx + 0x9e3779b9u);
            float r0 = (h & 0xFFFF) / 65536f, r1 = ((h >> 16) & 0xFFFF) / 65536f;
            float total = 0f; foreach (var t in tiers) total += t.chance;
            int pick = tiers.Length - 1;
            if (total > 0f) { float acc = r0 * total; for (int i = 0; i < tiers.Length; i++) { acc -= tiers[i].chance; if (acc <= 0f) { pick = i; break; } } }
            else pick = (int)(r0 * tiers.Length) % tiers.Length;
            var ids = tiers[pick].ids;
            if (ids == null || ids.Length == 0) return -1;
            return ids[(int)(r1 * ids.Length) % ids.Length];
        }

        public override void _Process(double delta)
        {
            _acc += delta;
            if (_acc < 0.35 || Player == null || Terr == null) return;
            _acc = 0;
            var pp = Player.GlobalPosition;

            var drop = new List<int>();                             // detect pickups + despawn far
            foreach (var kv in _live)
            {
                if (!IsInstanceValid(kv.Value)) { _taken.Add(kv.Key); drop.Add(kv.Key); continue; }
                float dx = kv.Value.GlobalPosition.X - pp.X, dz = kv.Value.GlobalPosition.Z - pp.Z;
                if (dx * dx + dz * dz > DespawnR * DespawnR) { kv.Value.QueueFree(); drop.Add(kv.Key); }
            }
            foreach (var k in drop) _live.Remove(k);

            for (int idx = 0; idx < _pts.Count; idx++)               // spawn near
            {
                if (_live.ContainsKey(idx) || _taken.Contains(idx)) continue;
                var p = _pts[idx];
                float dx = p.X - pp.X, dz = p.Z - pp.Z;
                if (dx * dx + dz * dz > SpawnR * SpawnR) continue;
                if (_live.Count >= MaxLive) break;
                int id = Roll(idx, p.Type);
                var item = id >= 0 ? new Item((ushort)id) : null;
                var pos = new Vector3(p.X, Terr.SampleHeight(p.X, p.Z) + 0.05f, p.Z);
                _live[idx] = WorldItem.Spawn(this, item, pos, _tblColor[p.Type], _tblName[p.Type]);
            }
        }
    }
}
