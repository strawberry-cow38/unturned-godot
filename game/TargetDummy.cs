using Godot;

namespace UnturnedGodot
{
    // A standing "player" target for the gun playground (strawberry: "simulated player entities that stand as
    // targets, respawning on a timer. shows floating damage numbers with each hit").
    //
    // WHY THIS EXISTS: player damage was unreachable. The client fire path damages zombies with Zombie_Damage
    // (PlayerController.StepBullets), and the only other consumer of a gun's player damage is ServerCombat --
    // which is multiplayer-only AND eaglefire-only, with its numbers hardcoded rather than read from the .dat.
    // So there was no way to feel a per-cartridge player-damage change anywhere in the game. This dummy is the
    // missing target: it takes damage through the SAME zone model the server uses, so what you measure here is
    // what PvP will do once per-gun stats reach it.
    //
    // Deliberately NOT a ZombieController with the AI switched off: zombies resolve damage through their own
    // limb model (ZombieCombat, skull at 82% height) with different bands and a different HP pool, so a dummy
    // built on one would report zombie numbers wearing a player's shape -- exactly the confusion this is meant
    // to remove.
    public partial class TargetDummy : StaticBody3D
    {
        [Export] public float MaxHealth = 100f;
        [Export] public float RespawnSeconds = 3f;
        [Export] public string Label = "";        // shown under the dummy, e.g. its range in metres

        public float Health { get; private set; }
        public bool Down => Health <= 0f;
        public float LastDamage { get; private set; }        // test hooks -- the last hit resolved on this dummy
        public HitZone LastZone { get; private set; }
        public int TimesDowned { get; private set; }

        public enum HitZone { Legs, Torso, Head }

        Node3D _visual;
        float _respawnT;

        public override void _Ready()
        {
            Health = MaxHealth;
            _visual = Humanoid.Build(new Color(0.86f, 0.70f, 0.55f), new Color(0.25f, 0.45f, 0.75f), new Color(0.20f, 0.22f, 0.28f));
            AddChild(_visual);
            // Box, not a capsule: the zone bands are pure height cuts, and a capsule's rounded cap would make the
            // top of the head a curved surface where a grazing shot reads as a torso hit at head height.
            AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(Humanoid.Radius * 2f, Humanoid.TopY, 0.5f) },
                Position = new Vector3(0f, Humanoid.TopY * 0.5f, 0f),
            });
        }

        /// <summary>Resolve a bullet hit. <paramref name="baseDamage"/> is the gun's PLAYER damage; the zone
        /// multiplier is applied here so the dummy and the server agree. Returns the damage actually dealt.</summary>
        public float TakeHit(float baseDamage, Vector3 worldPoint)
        {
            if (Down) return 0f;
            float relY = ToLocal(worldPoint).Y;   // feet = 0, same frame the server measures in
            HitZone zone = relY >= Humanoid.HeadMinY ? HitZone.Head : (relY >= Humanoid.TorsoMinY ? HitZone.Torso : HitZone.Legs);
            float dmg = baseDamage * (zone == HitZone.Head ? Humanoid.HeadMult
                                    : zone == HitZone.Torso ? Humanoid.TorsoMult : Humanoid.LegMult);
            Health -= dmg;
            LastDamage = dmg; LastZone = zone;
            DamageNumbers.Instance?.Show(worldPoint, dmg, zone);
            if (Health <= 0f)
            {
                Health = 0f;
                TimesDowned++;
                _respawnT = RespawnSeconds;
                if (_visual != null) _visual.Visible = false;
                SetCollisionLayerValue(1, false);   // a downed dummy stops eating bullets meant for the one behind it
            }
            return dmg;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (!Down) return;
            _respawnT -= (float)delta;
            if (_respawnT > 0f) return;
            Health = MaxHealth;
            if (_visual != null) _visual.Visible = true;
            SetCollisionLayerValue(1, true);
        }

        /// <summary>Test hook: drop it now rather than shooting it 5 times.</summary>
        public void DebugKill() { Health = 0.01f; TakeHit(1f, GlobalPosition + Vector3.Up * Humanoid.TorsoMinY); }
    }
}
