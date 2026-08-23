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
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();   // WorldItem.Spawn resolves the dropped log's asset
            var trunk = new TreeTrunk { Field = null, Index = 3, LogItem = 37, Health = 100f, RewardMin = 2, RewardMax = 3 };   // Field null skips SetAlive; small hp/reward for a fast test
            World.AddChild(trunk);
            trunk.GlobalPosition = Vector3.Zero;
            yield return Ticks(1);   // _Ready caches the max health

            T.Check("a standing tree is not felled", !trunk.Felled);
            trunk.Chop(50f, Vector3.Zero, Vector3.Forward);
            T.Check("a partial chop does not fell it (100 hp)", !trunk.Felled);
            trunk.Chop(200f, Vector3.Zero, Vector3.Forward);
            T.Check("felled once its health reaches 0", trunk.Felled);
            yield return Ticks(1);   // let the dropped WorldItems attach

            int items = 0;
            foreach (var c in World.GetChildren()) if (c is WorldItem) items++;
            T.Check($"felling dropped Reward_Min..Max items (2-3, logs+sticks), got {items}", items >= 2 && items <= 3);

            float before = trunk.Health;
            trunk.Chop(50f, Vector3.Zero, Vector3.Forward);
            T.Check("a swing at a felled tree is a no-op", trunk.Felled && Mathf.IsEqualApprox(trunk.Health, before));
            trunk.QueueFree();
        }
    }
}
