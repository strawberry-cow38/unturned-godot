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
                var z = new ZombieController { Target = Target, Speed = 2.6f + GD.Randf() * 1.6f };
                AddChild(z);
                float a = GD.Randf() * Mathf.Pi * 2f, r = 14f + GD.Randf() * 9f;
                z.GlobalPosition = Target.GlobalPosition + new Vector3(Mathf.Sin(a) * r, 0.2f, Mathf.Cos(a) * r);
                _cd = 0.6f;
            }
        }
    }
}
