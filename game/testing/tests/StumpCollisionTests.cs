using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>A felled tree leaves something solid (master 2026-09-07: "give tree stumps collision").
    ///
    /// Felling zeroes the TRUNK body's collision layer -- it must, an 8 m cylinder cannot keep blocking a tree
    /// that is lying down -- and SpawnStump only ever added meshes. So the stump you were left standing next to
    /// was scenery you walked straight through.
    ///
    /// The teeth here are the SECOND and THIRD checks, not the first. "Something blocks a ray at stump height"
    /// would also pass if the old full-height trunk collider had simply been left switched on, which is the
    /// obvious wrong fix; a ray well above the stump has to MISS for the collider to be stump-shaped rather
    /// than tree-shaped. A trunk built here has no CollisionShape3D of its own (production adds it separately
    /// in LoadResources), so a hit cannot come from anything but the stump.</summary>
    public sealed class StumpCollisionTests : GameTest
    {
        public override string Name => "tree.stump_collision";
        public override double TimeoutSimSeconds => 20;

        static bool RayHits(Node3D ctx, Vector3 from, Vector3 to, out GodotObject hit)
        {
            hit = null;
            var q = PhysicsRayQueryParameters3D.Create(from, to, 1u << 0);
            var r = ctx.GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (r == null || r.Count == 0 || !r.ContainsKey("collider")) return false;
            hit = r["collider"].AsGodotObject();
            return true;
        }

        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            // Field null skips SetAlive, and a trunk built this way carries NO collision shape -- so every hit
            // below is the stump's or nothing's.
            var trunk = new TreeTrunk { Field = null, Index = 1, LogItem = 37, Health = 10f, RewardMin = 1, RewardMax = 1,
                                        TreeXf = Transform3D.Identity };
            World.AddChild(trunk);
            trunk.GlobalPosition = Vector3.Zero;
            yield return Ticks(1);

            T.Check("standing tree: nothing at stump height yet",
                    !RayHits(trunk, new Vector3(-4f, 1.0f, 0f), new Vector3(4f, 1.0f, 0f), out _));

            trunk.Chop(999f, Vector3.Zero, Vector3.Forward);
            T.Check("the tree felled", trunk.Felled);
            yield return Ticks(2);   // the stump + its body attach

            // 1) the stump blocks you at stump height
            bool hitLow = RayHits(trunk, new Vector3(-4f, 1.0f, 0f), new Vector3(4f, 1.0f, 0f), out var lowCol);
            T.Check($"a felled tree's stump blocks a ray at 1.0 m (hit={(lowCol as Node)?.Name})", hitLow);

            // 2) TEETH: stump-shaped, not tree-shaped. The old trunk cylinder reached 6.5 m; if this hits,
            //    the "fix" was just leaving the trunk collider on.
            T.Check("...but NOT at 3 m -- the collider is a stump, not the whole trunk",
                    !RayHits(trunk, new Vector3(-4f, 3.0f, 0f), new Vector3(4f, 3.0f, 0f), out _));

            // 3) TEETH: it is the trunk's slim radius, not the mesh's flared one -- you should not bump into a
            //    stump from a metre and a half away.
            T.Check("...and not 1.5 m to the side of it",
                    !RayHits(trunk, new Vector3(-4f, 1.0f, 1.5f), new Vector3(4f, 1.0f, 1.5f), out _));

            // 4) it is WOOD underfoot, like the tree it came from
            var body = lowCol as Node;
            T.Check("the stump reads as wood for footsteps",
                    body != null && body.HasMeta(PlayerController.SurfMeta)
                    && (int)body.GetMeta(PlayerController.SurfMeta) == (int)PlayerController.Surf.Wood);

            // 5) it is streamed like the trunk it replaces rather than living outside the collider budget
            T.Check("the stump joins the collider budget group",
                    body != null && body.IsInGroup(ColliderBudget.Group));

            trunk.QueueFree();
            yield return Ticks(1);

            // ---- and again with a REAL asset, so the mesh path is covered rather than just the fallback.
            // The synthetic trunk above loads no meshes, so its height comes from StumpTopFallback. Every
            // shipped stump happens to top at exactly that same 1.60, which is convenient and also means the
            // AABB branch would go untested if this case did not exist -- the two agreeing is the thing worth
            // checking, not something to assume.
            string dir = ProjectSettings.GlobalizePath($"res://content/{ResourceField.MapDir}/");
            var real = new TreeTrunk { Field = null, Index = 2, LogItem = 41, Health = 10f, RewardMin = 1, RewardMax = 1,
                                       TreeName = "Pine_0", ResDir = dir, TreeXf = Transform3D.Identity };
            World.AddChild(real);
            real.GlobalPosition = new Vector3(60f, 0f, 0f);   // clear of the first trunk's debris
            yield return Ticks(1);
            real.Chop(999f, Vector3.Zero, Vector3.Forward);
            yield return Ticks(2);

            bool realLow = RayHits(real, new Vector3(56f, 1.0f, 0f), new Vector3(64f, 1.0f, 0f), out var realCol);
            T.Check($"a real Pine_0 stump blocks at 1.0 m (hit={(realCol as Node)?.Name})", realLow);
            T.Check("...still nothing at 3 m", !RayHits(real, new Vector3(56f, 3.0f, 0f), new Vector3(64f, 3.0f, 0f), out _));

            // The stump goes when the tree comes back, or a regrown trunk keeps an invisible one at its foot.
            real.DebugRegrowNow();
            yield return Ticks(2);
            T.Check("regrowing clears the stump collider",
                    !RayHits(real, new Vector3(56f, 1.0f, 0f), new Vector3(64f, 1.0f, 0f), out _));
            real.QueueFree();
        }
    }
}
