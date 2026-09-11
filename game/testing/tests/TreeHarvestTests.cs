using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Tree harvesting (strawberry 2026-08-22): "deal enough damage and the tree breaks like a destructible + drops
    // 1-3 logs of the tree's wood type." Verifies the TreeTrunk damage -> fell -> drop path at runtime. The felling
    // STRUCTURE follows retail ResourceManager.damage (drop log x reward, then a stick); the damage ROUTING is ours
    // (gun/melee -> Chop); item spawning is our WorldItem.Spawn.
    public sealed class TreeHarvestTests : GameTest
    {
        public override string Name => "tree.harvest";
        public override double TimeoutSimSeconds => 20;   // DebrisLife is shortened to 0.2 s for this test

        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();   // WorldItem.Spawn resolves the dropped log's asset
            double debrisWas = TreeTrunk.DebrisLife;
            TreeTrunk.DebrisLife = 0.2;   // real timer, test pace (restored below)
            var trunk = new TreeTrunk { Field = null, Index = 3, LogItem = 37, Health = 100f, RewardMin = 2, RewardMax = 3 };   // Field null skips SetAlive; small hp/reward for a fast test
            World.AddChild(trunk);
            trunk.GlobalPosition = Vector3.Zero;
            yield return Ticks(1);   // _Ready caches the max health

            T.Check("a standing tree is not felled", !trunk.Felled);
            trunk.Chop(50f, Vector3.Zero, Vector3.Forward);
            T.Check("a partial chop does not fell it (100 hp)", !trunk.Felled);
            trunk.Chop(200f, Vector3.Zero, Vector3.Forward);
            T.Check("felled once its health reaches 0", trunk.Felled);
            yield return Ticks(1);

            // ⚠ THE WOOD ARRIVES WITH THE CLEANUP, NOT AT THE CHOP. Since 2026-09-09 the rewards drop on the
            // debris timer (strawberry: "only produce logs once theyve despawned" -- they scatter along the
            // FALLEN trunk, and there is no fall direction to scatter along until it has landed). This asserted
            // an immediate drop and had been red ever since, unseen because the nightly had not run -- a test
            // nobody edited going red can mean the REQUIREMENT moved, not that the code regressed.
            //
            // Shortened rather than waited out (0.2 s of a 30 s budget via the real timer, restored below) and
            // NOT bypassed: the timer wiring is the part that broke, so a seam that called DropRewards directly
            // would pass with it deleted.
            //
            // The empty window is asserted FIRST, which the shortened-timer version alone does not cover: with
            // DebrisLife trimmed to 0.2 s, an instant-drop regression and a correctly-delayed drop both read as
            // "items appeared very soon", and a bare `Until(count > 0)` cannot tell them apart. Checking count is
            // still 0 in the same frame as Felled becoming true is what makes "at the chop" and "at the cleanup"
            // two different, distinguishable claims.
            T.Check($"nothing drops at the moment of felling (got {CountItems()})", CountItems() == 0);

            int items = 0;
            yield return Until(() => { items = CountItems(); return items > 0; }, 6);
            T.Check($"the debris cleanup drops Reward_Min..Max items (2-3, logs+sticks), got {items}",
                    items >= 2 && items <= 3);

            float before = trunk.Health;
            trunk.Chop(50f, Vector3.Zero, Vector3.Forward);
            T.Check("a swing at a felled tree is a no-op", trunk.Felled && Mathf.IsEqualApprox(trunk.Health, before));
            trunk.QueueFree();
            TreeTrunk.DebrisLife = debrisWas;   // other tests (and any render after this) get the real dwell back
        }

        int CountItems()
        {
            int n = 0;
            foreach (var c in World.GetChildren()) if (c is WorldItem) n++;
            return n;
        }
    }
}
