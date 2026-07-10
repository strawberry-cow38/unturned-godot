using Godot;

namespace UnturnedGodot
{
    // A thrown explosive (ItemThrowableAsset -- the Grenade): flies ballistically, bounces off the ground, and after
    // fuseLength seconds detonates, calling the thrower's Explode (DamageTool.explode). Values from the real
    // Grenade.dat: Fuse 2.5 s, Range 8, Zombie/Player_Damage 175. Uses real (1x) gravity, not the player's 3x arcade
    // gravity -- it's a physics object. Bounded: a simple ground bounce (assumes ground near y=0), no inventory
    // consumption, no impact effect / sticky / flash variants yet.
    public partial class Grenade : Node3D
    {
        public PlayerController Thrower;
        public Vector3 Vel;
        public float Fuse = 2.5f, Radius = 8f, ZombieDamage = 175f, PlayerDamage = 175f, VehicleDamage = 100f;   // Grenade.dat Vehicle_Damage 100
        const float Gravity = 9.81f;

        public override void _Ready()
        {
            AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.11f, Height = 0.22f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.2f, 0.13f), Metallic = 0.4f, Roughness = 0.6f },
            });
        }

        public override void _PhysicsProcess(double delta)
        {
            float dt = (float)delta;
            Fuse -= dt;
            Vel.Y -= Gravity * dt;
            Vector3 next = GlobalPosition + Vel * dt;
            if (next.Y < 0.11f) { next.Y = 0.11f; Vel = new Vector3(Vel.X * 0.4f, Mathf.Abs(Vel.Y) * 0.3f, Vel.Z * 0.4f); }   // bounce + damp
            GlobalPosition = next;
            if (Fuse <= 0f)
            {
                if (IsInstanceValid(Thrower)) Thrower.Explode(GlobalPosition, Radius, ZombieDamage, PlayerDamage, VehicleDamage);
                QueueFree();
            }
        }
    }
}
