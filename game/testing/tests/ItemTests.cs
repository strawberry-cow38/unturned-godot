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

            // deterministic drop pose: the normal spawn applies a GD.RandRange tilt (global RNG, unseeded), and an
            // awkward edge-landing can wobble past the settle window -- this test is about CCD-vs-trimesh, not
            // landing dynamics. ResetGlobals restores the flag after the test.
            WorldItem.NoDropRotation = true;
            var item = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(0f, 1.2f, 0f));   // metal scrap, dropped from 1.2m
            yield return Until(() => item.Settled, maxSimSeconds: 5);

            T.Check("item settled before timeout", item.Settled);
            T.Check($"item rests on the surface, didn't tunnel (y={item.GlobalPosition.Y:0.00})", item.GlobalPosition.Y > -0.1f);
        }
    }

    // The Nearby/AREA page rule (strawberry): a radius around the player, LOS-checked so loot through a wall
    // doesn't list. Source is PlayerDashboardInventoryUI -- onItemDropAdded rejects on sqrMagnitude > 16 from
    // GetEyesPositionWithoutLeaning (4 m, from the EYES), then a Physics.Linecast with RayMasks.BLOCK_PICKUP
    // culls anything obstructed. The subtle half is WHICH geometry blocks: WorldBuilder puts large opaque
    // structures on bit0 and every small prop + all glass/alpha-cutout on bit6 exactly so the latter DOESN'T
    // break item LOS, mirroring BLOCK_PICKUP omitting GROUND/ENVIRONMENT. A mask that blocked on everything
    // would hide loot behind a pane of glass and look identical to "the feature works".
    public class ItemNearbyLineOfSight : GameTest
    {
        public override string Name => "item.nearby_los";
        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            WorldItem.NoDropRotation = true;

            var ground = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40f, 0.2f, 40f) } });
            ground.Position = new Vector3(0f, -0.1f, 0f);
            World.AddChild(ground);

            // an OPAQUE STRUCTURE (bit0) at z=+2, and a small prop / glass (bit6) at z=-2
            var wall = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0, Position = new Vector3(0f, 1.5f, 2f) };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3f, 3f, 0.3f) } });
            World.AddChild(wall);
            var glass = new StaticBody3D { CollisionLayer = 1u << 6, CollisionMask = 0, Position = new Vector3(0f, 1.5f, -2f) };
            glass.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3f, 3f, 0.3f) } });
            World.AddChild(glass);

            var clear  = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(2f, 0.3f, 0f));
            var walled = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(0f, 0.3f, 3.5f));
            var behindGlass = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(0f, 0.3f, -3.5f));
            yield return Ticks(6);   // let the bodies register with the physics space

            Vector3 eye = new Vector3(0f, 1.6f, 0f);

            T.Check($"radius constant is the source's 4 m (sq={PlayerController.NearbyRadiusSq})",
                    Mathf.IsEqualApprox(PlayerController.NearbyRadiusSq, 16f));
            T.Check($"all three fixtures are inside that radius (clear={eye.DistanceTo(clear.GlobalPosition):0.00}m, " +
                    $"walled={eye.DistanceTo(walled.GlobalPosition):0.00}m, glass={eye.DistanceTo(behindGlass.GlobalPosition):0.00}m)",
                    eye.DistanceSquaredTo(clear.GlobalPosition) < 16f
                    && eye.DistanceSquaredTo(walled.GlobalPosition) < 16f
                    && eye.DistanceSquaredTo(behindGlass.GlobalPosition) < 16f);

            T.Check("an unobstructed item HAS line of sight", clear.HasLineOfSightFrom(eye));
            T.Check("an item behind an opaque structure (bit0) does NOT", !walled.HasLineOfSightFrom(eye));
            T.Check("an item behind glass / a small prop (bit6) still DOES -- bit6 must not block",
                    behindGlass.HasLineOfSightFrom(eye));
        }
    }

    // Regression, reported live by strawberry ("nearby loot is not working... it wasn't working before btw"):
    // the Nearby/AREA page was permanently EMPTY in-game. Not the radius and not the LOS -- the scan simply was
    // never on the path a player uses. There are four ways to open the bag (the keybind via Toggle, OpenInventory,
    // crate-open, and the replicated storage fact) and only OpenInventory scanned; the keybind goes Toggle -> Open
    // and populated nothing. Main.cs's demo called OpenInventory, so every harness looked fine while the actual
    // game never worked -- a green-on-the-wrong-caller bug.
    //
    // So this test drives the REAL entry point (Toggle, what G does) rather than the convenient one.
    public class ItemNearbyPopulatesOnRealOpenPath : GameTest
    {
        public override string Name => "item.nearby_open_path";
        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            WorldItem.NoDropRotation = true;

            var ground = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40f, 0.2f, 40f) } });
            ground.Position = new Vector3(0f, -0.1f, 0f);
            World.AddChild(ground);

            var player = new PlayerController();
            World.AddChild(player);
            yield return Ticks(4);                       // _Ready builds Inventory + the dashboard

            T.Check("player built an inventory", player.Inventory != null);
            if (player.Inventory == null) yield break;

            var a = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(1.5f, 0.3f, 0f));
            var b = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(-1.5f, 0.3f, 0f));
            yield return Ticks(6);

            var area = player.Inventory.items[SDG.Unturned.PlayerInventory.AREA];
            T.Check($"AREA starts empty before any open (count={area.getItemCount()})", area.getItemCount() == 0);

            // the actual keybind path: Toggle, not OpenInventory
            player.DebugToggleInventory();
            yield return Ticks(2);

            area = player.Inventory.items[SDG.Unturned.PlayerInventory.AREA];
            T.Check($"the KEYBIND open path populated Nearby (count={area.getItemCount()}, expected 2)",
                    area.getItemCount() == 2);
            T.Check($"...and gave the page a non-zero grid so the bar can draw ({area.width}x{area.height})",
                    area.width > 0 && area.height > 0);

            // A live PlayerController left in the shared World leaks into later tests (L1 is ONE engine boot) --
            // this test originally took three power.* tests down with it. Same convention as GunTests/InventoryTests.
            player.QueueFree(); a.QueueFree(); b.QueueFree();
        }
    }
}
