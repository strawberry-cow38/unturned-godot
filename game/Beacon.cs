using Godot;
using SDG.Unturned;   // Assets.makeLoot, Item

namespace UnturnedGodot
{
    // HORDE BEACON -- source InteractableBeacon + BeaconManager + ItemBeaconAsset. A placed barricade that summons a
    // BOUNDED zombie horde: activate it and it spawns Wave zombies TOTAL (id1194 = Wave 100), keeping up to MaxAlive up
    // at once around the beacon and sending them at the defender. As each horde zombie dies the tally climbs; once the
    // whole wave is spawned AND cleared, the beacon SELF-DESTRUCTS and drops Rewards loot (id1194 = 7). The source also
    // self-destructs if no player is in its nav region for a few seconds (abandoned) -- here: if the defender is gone.
    //
    // Adapted to SP: retail spawns via the zombie region/nav system and scales the wave to the participant count; here a
    // single defender = 1 participant, we ring the horde around the beacon and set each zombie's Target to the defender
    // so they converge (movement needs the live navmesh; in a flat test they hold at spawn and get mown down in place).
    // Rewards: retail resolves Reward_ID through a spawn table; the port has no spawn-table resolver, so we roll from a
    // curated rare-gun pool (flagged) x Rewards.
    public partial class Beacon : Node3D
    {
        public int Wave = 100;          // ItemBeaconAsset.wave: total horde budget (Beacon id1194)
        public int MaxAlive = 12;       // concurrent cap (SP-tuned; source scales with participants)
        public int Rewards = 7;         // reward drops on completion (id1194)
        public float Health = 80f;      // beacon hp -- destroy it to cancel the horde early (Beacon.dat Health 80)
        public Node3D Defender;         // what the horde converges on (the player / a sentry)

        // stand-in for Reward_ID's spawn table: eaglefire/maplestrike/ace/augewehr/avenger/nykorev/grizzly/zubeknakov
        static readonly int[] RewardPool = { 4, 363, 107, 1362, 1021, 126, 297, 122 };

        int _remaining;                 // zombies left to spawn (budget)
        int _killed;
        bool _active, _done;
        double _spawnCd, _abandonT, _freeT;
        MeshInstance3D _glow; OmniLight3D _light;
        readonly System.Collections.Generic.List<ZombieController> _horde = new();

        public override void _Ready() => BuildVisual();

        public void Activate(Node3D defender)
        {
            if (_active) return;
            Defender = defender;
            _remaining = Wave;
            _active = true;
            int burst = Mathf.Min(MaxAlive, Wave);   // source init sets 'alive' up front -> spawn the opening swarm at once
            for (int i = 0; i < burst; i++) SpawnOne();
            GD.Print($"[beacon] HORDE STARTED -- wave {Wave}, max {MaxAlive} alive (opening swarm {burst}), defender {(defender?.Name ?? "(none)")}");
        }

        void SpawnOne()
        {
            if (_remaining <= 0) return;
            var z = new ZombieController { Target = Defender, Speciality = RollSpeciality() };
            AddChild(z);
            float a = GD.Randf() * Mathf.Tau, r = 9f + GD.Randf() * 6f;   // ring them around the beacon
            z.GlobalPosition = GlobalPosition + new Vector3(Mathf.Sin(a) * r, 0.2f, Mathf.Cos(a) * r);
            _horde.Add(z); _remaining--;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_done) { _freeT -= delta; if (_freeT <= 0.0) QueueFree(); return; }   // brief "cleared" beat, then self-destruct
            if (!_active) return;

            // count our living horde + tally kills as they fall
            _horde.RemoveAll(z => {
                if (z == null || !GodotObject.IsInstanceValid(z)) return true;
                if (z.Dead) { _killed++; return true; }
                return false;
            });
            int alive = _horde.Count;

            // abandoned: no valid defender for 5 s -> cancel without loot (source: no player in nav for 3 s)
            if (Defender == null || !GodotObject.IsInstanceValid(Defender))
            {
                _abandonT += delta;
                if (_abandonT > 5.0) { GD.Print("[beacon] abandoned -> self-destruct (no loot)"); Complete(dropLoot: false); }
                return;
            }
            _abandonT = 0.0;

            // trickle the wave in up to the concurrent cap
            _spawnCd -= delta;
            if (_remaining > 0 && alive < MaxAlive && _spawnCd <= 0.0)
            {
                var z = new ZombieController { Target = Defender, Speciality = RollSpeciality() };
                AddChild(z);
                float a = GD.Randf() * Mathf.Tau, r = 9f + GD.Randf() * 6f;
                z.GlobalPosition = GlobalPosition + new Vector3(Mathf.Sin(a) * r, 0.2f, Mathf.Cos(a) * r);
                _horde.Add(z); _remaining--;
                _spawnCd = 0.6;
            }

            // cleared: the whole wave has spawned AND everything's dead -> complete + reward
            if (_remaining == 0 && alive == 0) Complete(dropLoot: true);
        }

        void Complete(bool dropLoot)
        {
            if (_done) return;
            _done = true; _freeT = 1.5;
            if (_glow != null) _glow.Visible = false;
            if (_light != null) _light.Visible = false;
            if (dropLoot)
            {
                for (int i = 0; i < Rewards; i++)
                {
                    int id = RewardPool[(int)(GD.Randi() % (uint)RewardPool.Length)];
                    float a = GD.Randf() * Mathf.Tau, r = 0.3f + GD.Randf() * 1.1f;
                    WorldItem.Spawn(GetParent(), Assets.makeLoot((ushort)id), GlobalPosition + new Vector3(Mathf.Sin(a) * r, 0.5f, Mathf.Cos(a) * r));
                }
                GD.Print($"[beacon] HORDE CLEARED -- killed {_killed}/{Wave}, dropped {Rewards} rewards, self-destructing");
            }
        }

        // read-only status for the harness / HUD
        public bool Active => _active && !_done;
        public bool Done => _done;
        public int Killed => _killed;
        public int Remaining => _remaining;
        public int Alive => _horde.Count;

        ZombieController.ESpeciality RollSpeciality()
        {
            float roll = GD.Randf();
            if (roll < 0.5f) return ZombieController.ESpeciality.NORMAL;
            if (roll < 0.66f) return ZombieController.ESpeciality.FLANKER;
            if (roll < 0.78f) return ZombieController.ESpeciality.SPRINTER;
            if (roll < 0.88f) return ZombieController.ESpeciality.CRAWLER;
            if (roll < 0.95f) return ZombieController.ESpeciality.BURNER;
            return ZombieController.ESpeciality.ACID;
        }

        void BuildVisual()
        {
            // a stout metal obelisk with a glowing red "engine" head (the part the source activates on placement)
            var metal = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.21f, 0.24f), Metallic = 0.5f, Roughness = 0.6f };
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.7f, 1.4f, 0.7f) }, Position = new Vector3(0f, 0.7f, 0f), MaterialOverride = metal });
            _glow = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.35f, 0.5f) }, Position = new Vector3(0f, 1.6f, 0f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.15f, 0.1f), EmissionEnabled = true, Emission = new Color(1f, 0.2f, 0.1f), EmissionEnergyMultiplier = 3f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
            AddChild(_glow);
            _light = new OmniLight3D { Position = new Vector3(0f, 1.7f, 0f), LightColor = new Color(1f, 0.3f, 0.2f), LightEnergy = 2.5f, OmniRange = 12f };
            AddChild(_light);
        }
    }
}
