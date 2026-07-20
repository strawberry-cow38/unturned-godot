using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // PEI wildlife: Spawns/Fauna.dat animal spawn points, distance-streamed like LootField/ZombieField. Fauna.dat = u8 ver,
    // u8 tableCount, per table [color3 + name str + u16 tableID(if ver>2) + u8 tierCount, per tier: name str + f32 chance +
    // u8 spawnCount + spawnCount x u16 animalID], u16 pointCount, per point [u8 type + Vector3]. PEI = 60 points, 1 "Wild"
    // table -> animal ids 1=Deer/4=Pig/6=Cow. Each point rolls its table deterministically into a rigged RiggedCharacter
    // (deer/pig/cow_rig.json). Static rest pose for now (reads as grazing wildlife); idle/walk clips + wander are next.
    public partial class AnimalField : Node3D
    {
        public PlayerController Player;
        public Terrain Terr;

        struct Pt { public byte Type; public float X, Z; }
        readonly List<Pt> _pts = new();
        readonly Dictionary<int, Node3D> _live = new();
        ushort[][] _tableIds;                                       // per Fauna table: the animal ids (all tiers merged)

        double _acc = 999;
        const float SpawnR = 130f, DespawnR = 165f;
        const int MaxLive = 36;

        // animal id -> (rig json name, real _MainTex from the bundle, foot offset so the feet sit on the terrain).
        // Cow = a 32x32 B&W Holstein texture; deer/pig = small palettes -> the accurate animal colours (white tint = show as-is).
        static readonly Dictionary<ushort, (string rig, string tex, float foot)> Kinds = new()
        {
            { 1, ("deer", "Animal_Deer_tex.png", 0.70f) },
            { 4, ("pig",  "Animal_Pig_tex.png",  0.22f) },
            { 6, ("cow",  "Animal_Cow_tex.png",  0.52f) },
        };

        public void LoadFromPei(string peiRoot)
        {
            string path = System.IO.Path.Combine(peiRoot, "Spawns", "Fauna.dat");
            if (!System.IO.File.Exists(path)) return;
            var b = System.IO.File.ReadAllBytes(path); int o = 0;
            byte U8() => b[o++];
            ushort U16() { var v = System.BitConverter.ToUInt16(b, o); o += 2; return v; }
            float F32() { var v = System.BitConverter.ToSingle(b, o); o += 4; return v; }
            void RStr() { int n = U8(); o += n; }
            byte ver = U8();
            byte tcount = U8();
            _tableIds = new ushort[tcount][];
            for (int t = 0; t < tcount; t++)
            {
                o += 3; RStr();                                     // color + table name
                if (ver > 2) o += 2;                                // tableID
                byte tiers = U8();
                var ids = new List<ushort>();
                for (int ti = 0; ti < tiers; ti++) { RStr(); o += 4; byte sc = U8(); for (int s = 0; s < sc; s++) ids.Add(U16()); }
                _tableIds[t] = ids.ToArray();
            }
            ushort pcount = U16();
            for (int i = 0; i < pcount; i++)
            {
                byte type = U8();
                float px = F32(); o += 4; float pz = F32();         // x, skip y, z
                _pts.Add(new Pt { Type = type, X = px, Z = -pz });   // negate-Z
            }
            GD.Print($"[animals] {_pts.Count} Fauna spawn points loaded, {tcount} tables");
        }

        static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }

        // Streaming anchors (C4, the ZombieField/LootField precedent): an explicitly-set Player is the SP path and is
        // honored EXACTLY (the single anchor, byte-identical to the old single-Player gate); Player == null (a dedicated
        // server, where nobody wires one) streams on EVERY registered player via PlayerRegistry -- so a joined client
        // gets wildlife around it too. Nearest-any-anchor distance, which with one anchor IS the old single-player gate.
        readonly List<Vector3> _anchors = new();
        List<Vector3> GatherAnchors()
        {
            _anchors.Clear();
            if (Player != null) { _anchors.Add(Player.GlobalPosition); return _anchors; }
            foreach (var p in PlayerRegistry.All)
                if (IsInstanceValid(p)) _anchors.Add(p.GlobalPosition);
            return _anchors;
        }
        static float NearestDistSq(List<Vector3> anchors, float x, float z)
        {
            float best = float.MaxValue;
            foreach (var a in anchors) { float dx = x - a.X, dz = z - a.Z; best = Mathf.Min(best, dx * dx + dz * dz); }
            return best;
        }

        public override void _Process(double delta)
        {
            _acc += delta;
            if (_acc < 0.5 || Terr == null) return;
            var anchors = GatherAnchors();
            if (anchors.Count == 0) return;   // pre-join server / no player -> nothing to stream around (was the Player==null gate)
            _acc = 0;

            var drop = new List<int>();
            foreach (var kv in _live)
            {
                if (!IsInstanceValid(kv.Value)) { drop.Add(kv.Key); continue; }
                var gp = kv.Value.GlobalPosition;
                if (NearestDistSq(anchors, gp.X, gp.Z) > DespawnR * DespawnR) { kv.Value.QueueFree(); drop.Add(kv.Key); }
            }
            foreach (var k in drop) _live.Remove(k);

            for (int idx = 0; idx < _pts.Count; idx++)
            {
                if (_live.ContainsKey(idx)) continue;
                var p = _pts[idx];
                if (NearestDistSq(anchors, p.X, p.Z) > SpawnR * SpawnR) continue;
                if (_live.Count >= MaxLive) break;
                if (Terrain.IsWater(Terr.SampleDominantLayer(p.X, p.Z))) continue;
                if (_tableIds == null || p.Type >= _tableIds.Length) continue;
                var ids = _tableIds[p.Type];
                if (ids == null || ids.Length == 0) continue;
                uint h = Hash((uint)idx + 0x51ed2701u);
                ushort id = ids[(int)(h % (uint)ids.Length)];        // deterministic deer/pig/cow pick
                if (!Kinds.TryGetValue(id, out var def)) continue;
                // build the visual rig only where it's actually rendered (SP/loopback host = Player set). A dedicated
                // server (Player null) streams RIG-LESS: the agent still wanders + AnimalNetSync publishes its
                // transform/anim/species, and remote clients render the puppet -- so no 36 wasted headless skeletons.
                RiggedCharacter rc = null;
                if (Player != null)
                {
                    rc = RiggedCharacter.Build($"res://content/{def.rig}_rig.json", Colors.White, false, $"res://content/objects/{def.tex}", null);
                    if (rc == null) continue;
                }
                var agent = new AnimalAgent { Terr = Terr, Foot = def.foot, Home = new Vector3(p.X, 0f, p.Z), Seed = h ^ 0xA53Cu, Species = AnimalCatalog.SpeciesForAnimalId(id) };
                AddChild(agent);
                agent.GlobalPosition = new Vector3(p.X, Terr.SampleHeight(p.X, p.Z) + def.foot, p.Z);
                if (rc != null) { agent.AddChild(rc); agent.Rig = rc; }
                agent.Begin();                                       // idle -> wander loop (see AnimalAgent)
                _live[idx] = agent;
            }
            if (!_animCam && _live.Count > 0 && System.Environment.GetEnvironmentVariable("UG_ANIMALSPAWN") == "1")   // demo: frame the first live animal
            {
                _animCam = true;
                Vector3 ap = Vector3.Zero; foreach (var v in _live.Values) { ap = ((Node3D)v).GlobalPosition; break; }
                var acam = new Camera3D { Current = true, Fov = 60f, Far = 10000f };
                AddChild(acam); acam.Position = ap + new Vector3(9f, 7f, 9f);   // wide enough to keep the wander range in frame
                acam.LookAt(ap + new Vector3(0f, 0.5f, 0f), Vector3.Up);
            }
        }
        bool _animCam;   // UG_ANIMALSPAWN demo cam fired once
    }
}
