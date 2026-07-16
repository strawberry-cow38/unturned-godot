using Godot;

namespace UnturnedGodot.Testing
{
    // Shared scene bits for the L1 tests: the infinite ground plane every old Build*Test used, and the standard
    // demo player (no mouse capture; _Ready registers the item catalog + builds the FP camera used to aim).
    static class Rigs
    {
        public static StaticBody3D Ground(Node3D world)
        {
            var ground = new StaticBody3D { CollisionLayer = 1 << 0 };
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            world.AddChild(ground);
            return ground;
        }

        public static PlayerController Player(Node3D world, Vector3 pos, string gunPath = null)
        {
            var p = new PlayerController { CaptureMouse = false };
            p.LoadGun(gunPath ?? "res://content/eaglefire.dat");
            world.AddChild(p);
            p.GlobalPosition = pos;
            return p;
        }
    }
}
