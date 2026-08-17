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

        /// <summary>Grant the demo kit into the joiner's SERVER grid.
        ///
        /// The dedicated server used to hand this to every peer on join, so these suites silently depended on
        /// what a REAL player spawns with -- and emptying the starter kit (strawberry 2026-08-16) broke four
        /// netcode tests that have nothing to do with loadouts. A fixture belongs at the point that needs it,
        /// not in production seeding. Returns false if the server has not made the inventory yet, so a caller
        /// that seeds too early fails loudly instead of asserting against an empty bag.</summary>
        public static bool SeedServerKit(DedicatedServer ded, ClientWorldSession sess)
        {
            if (!ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var inv)) return false;
            PlayerController.PopulateDemoKit(inv.Inventory);
            return true;
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
