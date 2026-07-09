using Godot;

namespace UnturnedGodot
{
    // Scripts the playable scene for a recorded demo (no live input over SSH): keep zombies chasing, aim
    // the player at the nearest live one, fire on a cadence, respawn after kills. Drives the ACTUAL
    // gameplay code (PlayerController.Fire, ZombieController chase) -- it just supplies the "input".
    public partial class DemoDirector : Node
    {
        public PlayerController Player;
        public Node SpawnRoot;

        float _spawnCd = 0.3f;
        int _spawned;

        public override void _PhysicsProcess(double delta)
        {
            if (Player == null) return;

            int alive = 0;
            ZombieController nearest = null;
            float best = float.MaxValue;
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead)
                {
                    alive++;
                    float d = z.GlobalPosition.DistanceTo(Player.GlobalPosition);
                    if (d < best) { best = d; nearest = z; }
                }

            // maintain a dense horde so some break through to melee (shows the HP-drop / survival stakes)
            _spawnCd -= (float)delta;
            if (alive < 9 && _spawned < 60 && _spawnCd <= 0f) { SpawnZombie(); _spawnCd = 0.12f; }

            if (nearest == null) return;

            // aim: yaw the body at the zombie (same-Y target = yaw only), pitch the camera at its chest.
            var flat = new Vector3(nearest.GlobalPosition.X, Player.GlobalPosition.Y, nearest.GlobalPosition.Z);
            if (flat.DistanceTo(Player.GlobalPosition) > 0.2f)
                Player.LookAt(flat, Vector3.Up);
            Vector3 camTo = nearest.GlobalPosition + new Vector3(0, 1.0f, 0) - Player.Camera.GlobalPosition;
            float pitch = Mathf.RadToDeg(Mathf.Atan2(camTo.Y, new Vector2(camTo.X, camTo.Z).Length()));
            Player.Camera.RotationDegrees = new Vector3(pitch, 0f, 0f);

            // advance toward the horde (aimed at the nearest, so forward = into them) until melee range: this
            // closes distance so the zombies SENSE the player by sight and commit to their flank paths -- the
            // faithful trigger, not a magic aggro. Stop inside 3.5 m and let them swarm (shows melee + HP loss).
            Player.ScriptedInput = new UnityEngine.Vector2(0f, best > 3.5f ? 1f : 0f);

            // fire at approaching zombies (gun self-limits to the real .dat firerate); deliberately let ones
            // inside 2.5 m close to melee so the player TAKES damage -- demonstrates the survival loop.
            if (best > 2.5f && best < 45f)
            {
                if (Player.Ammo <= 0) Player.Ammo = Player.Gun?.AmmoMax ?? 30;
                Player.Fire();
            }
        }

        void SpawnZombie()
        {
            _spawned++;
            var z = new ZombieController { Target = Player, Speciality = RollSpeciality() };
            SpawnRoot.AddChild(z);
            float spread = (GD.Randf() - 0.5f) * 2.2f;   // front arc (around -Z)
            float r = 8f + GD.Randf() * 6f;
            z.GlobalPosition = Player.GlobalPosition + new Vector3(Mathf.Sin(spread) * r, 0.2f, -Mathf.Cos(spread) * r);
        }

        ZombieController.ESpeciality RollSpeciality()
        {
            float roll = GD.Randf();
            if (roll < 0.58f) return ZombieController.ESpeciality.NORMAL;
            if (roll < 0.82f) return ZombieController.ESpeciality.FLANKER;   // extra flankers so the demo shows flanks
            if (roll < 0.94f) return ZombieController.ESpeciality.SPRINTER;
            return ZombieController.ESpeciality.CRAWLER;
        }
    }
}
