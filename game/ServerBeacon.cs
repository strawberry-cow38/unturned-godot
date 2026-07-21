using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Server-authoritative half of the HORDE BEACON (SP/MP-unify §3.1). A FixtureKind.Beacon deployable places as a plain
    // ENTITY + a VIEW-ONLY client replica (Beacon.Materialize renders the obelisk). This system is where the horde
    // actually SPAWNS: on placement it activates + spawns real server-side ZombieController brains around the beacon
    // (opening burst then trickling up to MaxAlive until the Wave budget is spent). Those zombies are ordinary
    // server-authoritative brains -> ZombieNetSync auto-mints their NetId + publishes them, so a joined client sees the
    // whole horde as puppets with ZERO beacon-specific sync. Clear the wave and the beacon self-destructs + drops rewards
    // (WorldItem nodes -> WorldItemNetSync auto-publishes them, same free-ride). Mirrors ServerSentries/ServerTraps; the
    // SP Beacon node's logic, over server entities, spawning into the shared world.
    //
    // CUT 1 scope (documented): the horde spawns with NO Target -> the zombies hold near the beacon (they replicate + can
    // be fought/cleared, but don't yet CONVERGE on a defender -- that needs a server player-target seam; the SP node sets
    // Target = the placing player). Shoot-to-cancel (destroy the beacon mid-horde) is deferred with the server barricade-
    // damage seam (bullets don't damage a deployable server-side yet); completion (clear the wave) is the win path here.
    public sealed class ServerBeacon
    {
        readonly ZombieReplication _zombies;
        readonly DeployableReplication _deployables;
        readonly Node _spawnParent;    // where the horde + reward nodes are added (tree-wide, so ZombieNetSync/WorldItemNetSync find them)
        readonly RandomNumberGenerator _rng = new() { Seed = 0xB4C0 };   // LOCAL rng -- don't perturb the global GD.Rand shared-determinism (server-only rolls, replicated results)

        sealed class BeaconState
        {
            public bool Activated;
            public int Remaining;      // horde budget left to spawn
            public double SpawnCd;
            public readonly List<ZombieController> Horde = new();
        }
        readonly Dictionary<uint, BeaconState> _beacons = new();
        readonly HashSet<uint> _seen = new();

        const int Wave = 100;      // ItemBeaconAsset.wave (Beacon id1194): total horde budget
        const int MaxAlive = 12;   // concurrent cap (SP-tuned)
        const int Rewards = 7;     // reward drops on completion (id1194)
        // stand-in for Reward_ID's spawn table (matches the SP Beacon): eaglefire/maplestrike/ace/augewehr/avenger/nykorev/grizzly/zubeknakov
        static readonly int[] RewardPool = { 4, 363, 107, 1362, 1021, 126, 297, 122 };

        public ServerBeacon(ZombieReplication zombies, DeployableReplication deployables, Node spawnParent)
        { _zombies = zombies; _deployables = deployables; _spawnParent = spawnParent; }

        static Vector3 ToG(UnityEngine.Vector3 v) => new(v.x, v.y, v.z);

        // 50 Hz server tick. Drive every placed beacon: activate on first sight, spawn/trickle the horde, complete when cleared.
        public void Tick(long tick, float dt)
        {
            _seen.Clear();
            foreach (var e in _deployables.All)
            {
                if (e == null) continue;
                var def = DeployableDef.ById(e.DefId);
                if (def == null || def.Fixture != FixtureKind.Beacon) continue;
                _seen.Add(e.NetIdValue);
                if (!_beacons.TryGetValue(e.NetIdValue, out var st)) { st = new BeaconState(); _beacons[e.NetIdValue] = st; }

                if (!st.Activated)
                {
                    st.Activated = true; st.Remaining = Wave;
                    int burst = Mathf.Min(MaxAlive, Wave);          // opening swarm
                    for (int i = 0; i < burst; i++) SpawnOne(st, e.Pos);
                    continue;
                }

                // count the horde as it falls (freed OR Dead == gone)
                st.Horde.RemoveAll(z => z == null || !GodotObject.IsInstanceValid(z) || z.Dead);
                int alive = st.Horde.Count;

                // CLEARED -> the whole wave spawned AND everything's dead: complete + reward + self-destruct
                if (st.Remaining == 0 && alive == 0) { Complete(e, tick); _beacons.Remove(e.NetIdValue); continue; }

                // trickle the wave in up to the concurrent cap
                st.SpawnCd -= dt;
                if (st.Remaining > 0 && alive < MaxAlive && st.SpawnCd <= 0.0) { SpawnOne(st, e.Pos); st.SpawnCd = 0.6; }
            }

            // retire gone beacons (salvaged / destroyed) -> despawn their horde with them
            if (_beacons.Count > _seen.Count)
            {
                List<uint> gone = null;
                foreach (var kv in _beacons) if (!_seen.Contains(kv.Key)) (gone ??= new List<uint>()).Add(kv.Key);
                if (gone != null) foreach (var id in gone) { DespawnHorde(_beacons[id]); _beacons.Remove(id); }
            }
        }

        void SpawnOne(BeaconState st, UnityEngine.Vector3 beaconPos)
        {
            if (st.Remaining <= 0) return;
            var z = new ZombieController { Speciality = RollSpeciality() };   // Target unset (cut 1): the horde holds near the beacon, replicates, can be cleared
            _spawnParent.AddChild(z);                                          // joins "zombies" in _Ready -> ZombieNetSync auto-mints its NetId + publishes it
            float a = _rng.Randf() * Mathf.Tau, r = 9f + _rng.Randf() * 6f;    // ring them around the beacon
            z.GlobalPosition = ToG(beaconPos) + new Vector3(Mathf.Sin(a) * r, 0.2f, Mathf.Cos(a) * r);
            st.Horde.Add(z); st.Remaining--;
        }

        // clear payout: drop Rewards loot as server WorldItem nodes (WorldItemNetSync auto-publishes them), then remove
        // the beacon entity (its client obelisk view retires). SP scales by sqrt(participants); 1 participant -> flat count.
        void Complete(DeployableReplication.DeployableEntity e, long tick)
        {
            for (int i = 0; i < Rewards; i++)
            {
                int id = RewardPool[(int)(_rng.Randi() % (uint)RewardPool.Length)];
                float a = _rng.Randf() * Mathf.Tau, r = 0.3f + _rng.Randf() * 1.1f;
                WorldItem.Spawn(_spawnParent, Assets.makeLoot((ushort)id), ToG(e.Pos) + new Vector3(Mathf.Sin(a) * r, 0.5f, Mathf.Cos(a) * r));
            }
            _deployables.ServerRemove(e.NetIdValue, tick);
        }

        void DespawnHorde(BeaconState st)
        {
            foreach (var z in st.Horde) if (GodotObject.IsInstanceValid(z)) z.QueueFree();
        }

        // weighted speciality mix (matches the SP Beacon.RollSpeciality)
        ZombieController.ESpeciality RollSpeciality()
        {
            float roll = _rng.Randf();
            if (roll < 0.5f) return ZombieController.ESpeciality.NORMAL;
            if (roll < 0.66f) return ZombieController.ESpeciality.FLANKER;
            if (roll < 0.78f) return ZombieController.ESpeciality.SPRINTER;
            if (roll < 0.88f) return ZombieController.ESpeciality.CRAWLER;
            if (roll < 0.95f) return ZombieController.ESpeciality.BURNER;
            return ZombieController.ESpeciality.ACID;
        }

        // test seam: how many beacons the server is currently driving + the current live horde across all of them
        public int TrackedCount => _beacons.Count;
        public int LiveHorde { get { int n = 0; foreach (var b in _beacons.Values) n += b.Horde.Count; return n; } }
    }
}
