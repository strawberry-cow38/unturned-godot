using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A StoreShelf's collider is built by MeshInstance3D.CreateTrimeshCollision(), which defaults
    // backface_collision = false -- ONE-SIDED. A body shoved into the shelf from the wrong face (a vehicle pushing
    // the player against it) tunnels straight through instead of being stopped, the same class of bug as the
    // one-sided river-chunk terrain collider (tinyclaw 2026-08-31). StoreShelf two-sides the generated shape; this
    // spawns a real shelf and proves the flip actually took -- which also pins that the child-navigation used to
    // reach the generated shape still finds it if Godot ever changes what CreateTrimeshCollision emits.
    public sealed class ShelfColliderTests : GameTest
    {
        public override string Name => "shelf.collider_two_sided";
        public override double TimeoutSimSeconds => 30;

        static ConcavePolygonShape3D FindConcave(Node root)
        {
            if (root is CollisionShape3D cs && cs.Shape is ConcavePolygonShape3D cps) return cps;
            foreach (var c in root.GetChildren()) { var f = FindConcave(c); if (f != null) return f; }
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var shelf = StoreShelf.Spawn(World, Vector3.Zero, "Shelf_1", 0, 0f, false, "Shelf");
            yield return Ticks(3);

            var shape = shelf != null ? FindConcave(shelf) : null;
            T.Check("the shelf built a reachable ConcavePolygonShape3D collider", shape != null);
            if (shape != null)
                T.Check("...and it is TWO-SIDED (backface on) so a vehicle can't shove the player through the shelf", shape.BackfaceCollision);

            if (shelf != null && GodotObject.IsInstanceValid(shelf)) shelf.QueueFree();
        }
    }
}
