using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE IDLE FLUID TICK. With no hose connected anywhere, no fluid can move, so FluidNet skips the graph solve --
    // but it still has to run OnPostTick, which is per-device work (regen, decay, visuals) unrelated to the graph.
    //
    // The first version of that early-out walked ALL of `fluid_devices` to do it. Every frame that meant a fresh
    // node array marshalled across the C#/C++ boundary and an IsInstanceValid interop call per device, in order to
    // invoke a method that is EMPTY on every type except SinkSource and FluidFuelInlet. 2.5 ms/window on the real
    // map with nothing connected -- strawberry spotted it as "nothing fluid related is happening, yet 2.5ms".
    //
    // The fix routes the idle path through a second group holding only the types that actually implement the hook.
    // What makes that safe is WHO decides membership: FluidContainer asks the type whether it overrides OnPostTick,
    // rather than trusting a subclass to set a flag. A flag is one forgotten `override` away from a device that
    // silently stops post-ticking -- no error, no crash, just a sink that quietly never refills again.
    //
    // So the assertions below are about MEMBERSHIP being derived, not about speed. A timing test here would pass on
    // a build where the group is simply empty and nothing post-ticks at all, which is the failure that matters.
    // `partial` because the nested CountingDevice derives from GodotObject (GD0002).
    public sealed partial class FluidIdlePostTickTests : GameTest
    {
        public override string Name => "fluid.idle_posttick_group";

        // Overrides the hook, so it MUST end up in the group -- and counts calls, so "did the idle path still run
        // it" is answerable rather than assumed.
        public partial class CountingDevice : FluidContainer
        {
            public int Ticks;
            public override void OnPostTick(float dt) => Ticks++;
        }

        public override IEnumerable<Step> Run()
        {
            World.AddChild(new FluidManager());

            // Three plain containers (inherit the empty base) and one that implements the hook.
            var plain = new List<FluidContainer>();
            for (int i = 0; i < 3; i++)
            {
                var c = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 100f, 50f, WaterQuality.Clean), 1f);
                World.AddChild(c);
                plain.Add(c);
            }
            var counter = new CountingDevice { Role = FluidRole.Storage, Tank = new FluidTank(FluidType.Water, 100f, 50f, WaterQuality.Clean) };
            World.AddChild(counter);
            yield return Step.Ticks(2);

            var tree = World.GetTree();
            int inDevices = tree.GetNodeCountInGroup("fluid_devices");
            int inPostTick = tree.GetNodeCountInGroup(FluidContainer.PostTickGroup);

            T.Check($"every device is still in fluid_devices ({inDevices} >= 4)", inDevices >= 4);
            T.Check($"the overrider joined the post-tick group by ITSELF, no flag ({counter.IsInGroup(FluidContainer.PostTickGroup)})",
                    counter.IsInGroup(FluidContainer.PostTickGroup));

            // The teeth. If membership were "every FluidContainer" the optimisation buys nothing and this fails;
            // the plain three inherit an empty method and must not be visited.
            int plainInGroup = 0;
            foreach (var c in plain) if (c.IsInGroup(FluidContainer.PostTickGroup)) plainInGroup++;
            T.Check($"containers that do NOT override it stay out ({plainInGroup} of 3 wrongly joined)", plainInGroup == 0);
            T.Check($"so the idle path visits fewer nodes than the device list ({inPostTick} < {inDevices})", inPostTick < inDevices);

            // BEHAVIOUR EQUIVALENCE: narrowing the walk must not stop the hook firing. No hoses exist in this
            // scene, so Tick takes the idle branch every time.
            T.Check($"no hoses, so this is genuinely the idle path ({tree.GetNodeCountInGroup("hoses")})",
                    tree.GetNodeCountInGroup("hoses") == 0);
            counter.Ticks = 0;
            for (int i = 0; i < 5; i++) FluidNet.Tick(tree, 0.016f);
            T.Check($"an implementing device still post-ticks on the idle path ({counter.Ticks} of 5)", counter.Ticks == 5);

            // A smashed device drops out of the solver group; it has to leave this one too, or rubble keeps ticking.
            counter.SetBroken(true);
            T.Check("breaking the prop removes it from the post-tick group too",
                    !counter.IsInGroup(FluidContainer.PostTickGroup) && !counter.IsInGroup("fluid_devices"));
            counter.Ticks = 0;
            for (int i = 0; i < 5; i++) FluidNet.Tick(tree, 0.016f);
            T.Check($"and a broken device stops post-ticking ({counter.Ticks} calls while broken)", counter.Ticks == 0);

            counter.SetBroken(false);
            T.Check("un-breaking restores membership", counter.IsInGroup(FluidContainer.PostTickGroup));

            counter.QueueFree();
            foreach (var c in plain) c.QueueFree();
        }
    }
}
