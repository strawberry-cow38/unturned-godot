using Godot;

namespace UnturnedGodot
{
    // Single-player horde: keeps ~MaxAlive zombies chasing the target, spawning replacements around it.
    public partial class HordeSpawner : Node3D
    {
        public Node3D Target;
        [Export] public int MaxAlive = 8;
        float _cd;

        public override void _PhysicsProcess(double delta)
        {
            if (Target == null) return;
            int alive = 0;
            foreach (var n in GetTree().GetNodesInGroup("zombies"))
                if (n is ZombieController z && !z.Dead) alive++;

            _cd -= (float)delta;
            if (alive < MaxAlive && _cd <= 0f)
            {
                // speed comes from the speciality now; a horde is mostly normals with some flankers + a few
                // sprinters/crawlers for variety (so the LEFT/RIGHT/FLANK approach mix is visible).
                var z = new ZombieController { Target = Target, Speciality = RollSpeciality() };
                AddChild(z);
                // ring them around the player -- some land inside standing detection (12 m), the rest wait to be
                // sensed until the player moves/sprints toward them (faithful: zombies don't magically know you).
                float a = GD.Randf() * Mathf.Pi * 2f, r = 10f + GD.Randf() * 10f;
                z.GlobalPosition = Target.GlobalPosition + new Vector3(Mathf.Sin(a) * r, 0.2f, Mathf.Cos(a) * r);
                _cd = 0.6f;
            }
        }

        ZombieController.ESpeciality RollSpeciality()
        {
            float roll = GD.Randf();
            if (roll < 0.60f) return ZombieController.ESpeciality.NORMAL;
            if (roll < 0.80f) return ZombieController.ESpeciality.FLANKER;
            if (roll < 0.93f) return ZombieController.ESpeciality.SPRINTER;
            return ZombieController.ESpeciality.CRAWLER;
        }
    }
}
