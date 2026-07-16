using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // L1 physics test guarding commit 5bbe90d: a dropped item must LAND on a thin trimesh surface, not tunnel through it
    // (the real terrain collider is a trimesh; without ContinuousCd the item falls straight through). This is the
    // UG_TRIMESH render harness promoted to an assertion.
    public class ItemTrimeshNoTunnel : GameTest
    {
        public override string Name => "item.trimesh_no_tunnel";
        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            var ground = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };   // thin trimesh, like the real terrain
            ground.AddChild(new CollisionShape3D { Shape = new PlaneMesh { Size = new Vector2(24f, 8f) }.CreateTrimeshShape() });
            World.AddChild(ground);

            var item = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(0f, 1.2f, 0f));   // metal scrap, dropped from 1.2m
            yield return Until(() => item.Settled, maxSimSeconds: 4);

            T.Check("item settled before timeout", item.Settled);
            T.Check($"item rests on the surface, didn't tunnel (y={item.GlobalPosition.Y:0.00})", item.GlobalPosition.Y > -0.1f);
        }
    }
}
