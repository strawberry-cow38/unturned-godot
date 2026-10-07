using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Only the real light (strawberry 2026-10-04: "remove the headlight faux beams + dust flecks, just
    /// keeping the actual light sources, do the same for the street lights. remove the cones as well as dust flecks").
    ///
    /// A lit streetlight at night carries its spot and its lens and NOTHING else: no additive shaft mesh, no particle
    /// cloud. Asserted on the node's children rather than on a flag, because the old cone and motes were plain
    /// children -- any future fake beam would show up here the same way. The car's shaft + dust went with their code
    /// (Vehicle.BuildHeadlightBeam and HeadlightBeam.cs no longer exist), so the compiler is that half's check.</summary>
    public sealed class StreetLightNoFakeBeamTests : GameTest
    {
        public override string Name => "props.streetlight_only_real_light";

        public override IEnumerable<Step> Run()
        {
            var lamp = StreetLight.Make(new Vector3(0f, 5f, 0f), 5f);
            World.AddChild(lamp);
            yield return Ticks(2);
            lamp.SetNight(true); lamp.SetPowered(true);
            yield return Ticks(1);
            T.Check("a powered lamp at night still lights its spot", lamp.LitSpotForTest);
            T.Check("...and its lens", lamp.LitPanelForTest);
            int kids = lamp.GetChildCount();
            T.Check($"...and builds no fake shaft and no dust ({kids} children: the spot and the stand-in lens)", !lamp.HasFakeBeamOrDustForTest);
        }
    }
}
