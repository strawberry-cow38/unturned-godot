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
        public uint NetId;              // MP: the server entity this view mirrors (0 = the SP/host authoritative node)
        public bool IsReplica;          // MP: a client-side VIEW-ONLY replica -- renders the obelisk only; the SERVER (ServerBeacon) owns the horde spawn/track/reward (its zombies replicate via the normal ZombieReplication)

        // stand-in for Reward_ID's spawn table: eaglefire/maplestrike/ace/augewehr/avenger/nykorev/grizzly/zubeknakov
        static readonly int[] RewardPool = { 4, 363, 107, 1362, 1021, 126, 297, 122 };

        int _remaining;                 // zombies left to spawn (budget)
        int _killed;
        bool _active, _done;
        double _spawnCd, _abandonT, _freeT;
        MeshInstance3D _glow; OmniLight3D _light;
        readonly System.Collections.Generic.List<ZombieController> _horde = new();

        public override void _Ready() => BuildVisual();

        // MP: the client's DeployableReplicaView calls this for a FixtureKind.Beacon entity -> a VIEW-ONLY obelisk. The
        // SERVER (ServerBeacon) owns Activate/horde/reward; the summoned zombies replicate through the normal
        // ZombieReplication (rendered as puppets), so the replica needs no horde logic. Mirrors OilPump.Materialize.
        public static Beacon Materialize(Node parent, Vector3 pos, float yawDegrees, uint netId)
        {
            var b = new Beacon { Position = pos, RotationDegrees = new Vector3(0f, yawDegrees, 0f), NetId = netId, IsReplica = true };
            parent.AddChild(b);
            return b;
        }

        // Shoot the beacon to cancel the horde early (source: the beacon is a Health-80 barricade; BarricadeManager.damage
        // -> ManualOnDestroy self-destructs it and despawns the horde with NO loot). Bullets route here via StepBullets.
        public void TakeDamage(float amount)
        {
            if (IsReplica || _done) return;   // a replica's beacon HP is server-owned -- shooting routes to the server (like a zombie), never a local cancel
            Health -= amount;
            if (Health <= 0f)
            {
                GD.Print($"[beacon] DESTROYED (shot down) -> horde cancelled, no loot");
                if (_active) Complete(dropLoot: false);   // despawns the horde (children of the beacon) + self-destructs
                else QueueFree();
            }
        }

        public void Activate(Node3D defender)
        {
            if (IsReplica || _active) return;   // the horde is server-authoritative -- ServerBeacon activates it, never a client replica
            Defender = defender;
            _remaining = Wave;
            _active = true;
            int burst = Mathf.Min(MaxAlive, Wave);   // opening swarm. (Source init(amount) seeds alive = the participant count already in the nav region, NOT MaxAlive; SP has no pre-existing zombies, so we open with a full cap-sized swarm as an SP tuning.)
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
            if (IsReplica) return;   // a client REPLICA only renders the obelisk -- ServerBeacon owns the horde spawn/track/reward; removal comes from the ReplicaView when the server retires the entity
            if (_done) { _freeT -= delta; if (_freeT <= 0.0) QueueFree(); return; }   // brief "cleared" beat, then self-destruct
            if (!_active) return;

            // count our horde as it falls -- a zombie removed for ANY reason is gone, so tally it either way (a node freed
            // before its Dead flag latched would otherwise silently drop from the count and skip the kill tally).
            _horde.RemoveAll(z => {
                if (z == null || !GodotObject.IsInstanceValid(z)) { _killed++; return true; }
                if (z.Dead) { _killed++; return true; }
                return false;
            });
            int alive = _horde.Count;

            // CLEARED, checked FIRST: the whole wave has spawned AND everything's dead -> complete + reward. Before the
            // abandon test so a wave cleared on the same frame the defender dies still pays out (source drops loot in
            // ManualOnDestroy when remaining+alive == 0, independent of the abandon path).
            if (_remaining == 0 && alive == 0) { Complete(dropLoot: true); return; }

            // abandoned -> cancel without loot. NB source self-destructs when no player is in the beacon's nav REGION
            // (proximity, after a 3 s startup grace); our SP adaptation is LIVENESS-based (the defender node is gone --
            // e.g. the player died/despawned) with a short grace, since the port has no nav-region participant tracking.
            if (Defender == null || !GodotObject.IsInstanceValid(Defender))
            {
                _abandonT += delta;
                if (_abandonT > 3.0) { GD.Print("[beacon] abandoned -> self-destruct (no loot)"); Complete(dropLoot: false); }
                return;
            }
            _abandonT = 0.0;

            // trickle the wave in up to the concurrent cap
            _spawnCd -= delta;
            if (_remaining > 0 && alive < MaxAlive && _spawnCd <= 0.0) { SpawnOne(); _spawnCd = 0.6; }
        }

        void Complete(bool dropLoot)
        {
            if (_done) return;
            _done = true; _freeT = 1.5;
            if (_glow != null) _glow.Visible = false;
            if (_light != null) _light.Visible = false;
            if (dropLoot)
            {
                // source scales the drop count by sqrt(participants) then host Beacon_Rewards_Multiplier / _Max_Rewards;
                // in SP (1 participant) sqrt(1)=1 and there's no host config, so a flat Rewards count is source-correct here.
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
            // bullet-hittable collider (props layer bit 6, like the gas-pump/grid-power fixtures) so gunfire can destroy
            // the beacon to cancel the horde. Mask 0 -> it never blocks movement, and it's off the sentry LOS layer (bit 0).
            var hit = new StaticBody3D { CollisionLayer = 1u << 6, CollisionMask = 0 };
            hit.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.7f, 1.4f, 0.7f) }, Position = new Vector3(0f, 0.7f, 0f) });
            hit.SetMeta("beacon", this);   // StepBullets resolves this -> Beacon.TakeDamage
            AddChild(hit);
            _glow = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.35f, 0.5f) }, Position = new Vector3(0f, 1.6f, 0f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.15f, 0.1f), EmissionEnabled = true, Emission = new Color(1f, 0.2f, 0.1f), EmissionEnergyMultiplier = 3f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
            AddChild(_glow);
            _light = new OmniLight3D { Position = new Vector3(0f, 1.7f, 0f), LightColor = new Color(1f, 0.3f, 0.2f), LightEnergy = 2.5f, OmniRange = 12f };
            AddChild(_light);
        }
    }
}
