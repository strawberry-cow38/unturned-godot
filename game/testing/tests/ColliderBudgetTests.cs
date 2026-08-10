using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    // Distance-streamed prop collision (ColliderBudget). The contract that matters is not "far things turn
    // off" -- it is that turning them off cannot break the two systems already sharing those colliders:
    // destructible breakage (which drives CollisionLayer) and the player standing on the ground.
    //
    // NOTE this test builds its own bodies, so it CANNOT prove the budget is wired to the real map. That is
    // the failure the shadow budget actually shipped -- correct code, nothing in the group, silently managing
    // nothing. Reachability is checked from a real boot's `[collbudget] N collision shapes in M cells` line,
    // not from here.
    public class ColliderBudgetContract : GameTest
    {
        public override string Name => "collbudget.streams_by_distance";

        static StaticBody3D MakeProp(Node parent, Vector3 at)
        {
            var b = new StaticBody3D { Position = at, CollisionLayer = 1u << 0 };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            parent.AddChild(b);
            b.AddToGroup(ColliderBudget.Group);
            return b;
        }

        static CollisionShape3D ShapeOf(StaticBody3D b)
        {
            foreach (var c in b.GetChildren()) if (c is CollisionShape3D cs) return cs;
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var near = MakeProp(World, new Vector3(2f, 0f, 0f));
            var far = MakeProp(World, new Vector3(4000f, 0f, 0f));      // far outside any sane radius
            // A camera at the origin is the focus point; the budget prefers it over the players group.
            var cam = new Camera3D { Position = Vector3.Zero, Current = true };
            World.AddChild(cam);
            var budget = new ColliderBudget();
            World.AddChild(budget);
            yield return Ticks(1);

            budget.Build();
            T.Check("the budget found both props", budget.ChunkCountForTest >= 2);
            T.Check("everything starts collidable (the world is built solid)", !ShapeOf(near).Disabled && !ShapeOf(far).Disabled);

            budget.Rebalance();
            yield return Ticks(1);
            T.Check("near prop stays collidable", !ShapeOf(near).Disabled);
            T.Check("far prop is streamed OUT", ShapeOf(far).Disabled);

            // THE ONE THAT PROTECTS DESTRUCTIBLES. Breakage drives CollisionLayer; the budget must use a
            // different axis or a smashed prop goes solid again when you walk away and back.
            T.Check("streaming out did NOT touch CollisionLayer", far.CollisionLayer == 1u << 0);
            T.Check("...nor the near prop's layer", near.CollisionLayer == 1u << 0);

            // Walk to the far prop: it must come back.
            cam.Position = new Vector3(4000f, 0f, 0f);
            yield return Ticks(1);
            budget.Rebalance();
            yield return Ticks(1);
            T.Check("walking there streams the far prop back IN", !ShapeOf(far).Disabled);
            T.Check("...and the now-distant one OUT", ShapeOf(near).Disabled);

            // Hysteresis: sitting still must not thrash. Two rebalances with no movement change nothing.
            int before = budget.EnabledChunksForTest;
            budget.Rebalance(); budget.Rebalance();
            T.Check("standing still changes nothing (no boundary thrash)", budget.EnabledChunksForTest == before);

            // COLLISION FOLLOWS VISIBILITY. Two props at the SAME distance with different render cull
            // distances must not share a fate: the small one (64 m, invisible out there) may drop its
            // collider, the big one (512 m, still drawn and therefore still aimable) may not. A flat radius
            // cannot express this, which is exactly why the first cut would have eaten sniper shots.
            cam.Position = Vector3.Zero;
            var small = MakeProp(World, new Vector3(200f, 0f, 0f));
            small.SetMeta(ColliderBudget.RadiusMeta, 64f);
            var big = MakeProp(World, new Vector3(202f, 0f, 0f));
            big.SetMeta(ColliderBudget.RadiusMeta, 512f);
            var b3 = new ColliderBudget();
            World.AddChild(b3);
            yield return Ticks(1);
            b3.Build();
            b3.Rebalance();
            T.Check("at 200m the SMALL-cull prop drops collision", ShapeOf(small).Disabled);
            T.Check("...and the LARGE-cull prop beside it keeps it (still visible = still shootable)", !ShapeOf(big).Disabled);

            // UG_COLLDIST=0 is the A/B control -- it must be a true no-op, or the "batching off" comparison
            // it exists for would be measuring the budget instead of the thing under test.
            float saved = ColliderBudget.Flat;
            ColliderBudget.Flat = 0f;
            var b2 = MakeProp(World, new Vector3(9000f, 0f, 0f));
            var budget2 = new ColliderBudget();
            World.AddChild(budget2);
            yield return Ticks(1);
            budget2.Build();
            budget2.Rebalance();
            T.Check("radius 0 disables the budget entirely (control path is a no-op)", !ShapeOf(b2).Disabled);
            ColliderBudget.Flat = saved;
        }
    }
}
