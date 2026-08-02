using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Reported: toggling global power did not turn the streetlights off. The lamps are auto-grid municipal
    // consumers -- DayNightCycle.DriveStreetlights sweeps the "streetlights" group with SetNight/SetPowered --
    // so this asserts the grid path end to end rather than calling SetPowered directly, which would test the
    // setter and skip the sweep that actually has to notice the flip.
    public sealed class StreetLightFollowsGlobalPowerTests : GameTest
    {
        public override string Name => "props.streetlight_follows_global_power";

        public override IEnumerable<Step> Run()
        {
            float savedReact = StreetLight.ReactionMax; StreetLight.ReactionMax = 0.1f;   // the grid-follow ANIMATES now -> keep the reaction short so the polls settle fast (restored at the end)
            var sun = new DirectionalLight3D();
            World.AddChild(sun);
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color };
            var cyc = new DayNightCycle { Sun = sun, Env = env, DayLength = 100000f, VisualsEnabled = true, Time = 0.90f };   // deep night, effectively frozen
            World.AddChild(cyc);

            var lamp = StreetLight.Make(new Vector3(0f, 5f, 0f), 5f);
            World.AddChild(lamp);
            PowerNet.SetGlobalPower(true);
            yield return Until(() => lamp.LitSpotForTest && !lamp.TransitioningForTest, 3);   // sweep animates the light-on (reaction + flicker) -> wait for it to settle

            T.Check("a lamp is lit at night while the grid is live", lamp.LitSpotForTest);

            // The sweep throttles its mains check (~4Hz), so poll rather than assuming a fixed tick count --
            // a hardcoded wait here silently encodes the throttle period and breaks when it is tuned.
            PowerNet.SetGlobalPower(false);
            yield return Until(() => !lamp.LitSpotForTest && !lamp.TransitioningForTest, 3);
            T.Check("toggling global power OFF darkens the lamp", !lamp.LitSpotForTest);
            T.Check("...and its cone", !lamp.LitConeForTest);

            PowerNet.SetGlobalPower(true);
            yield return Until(() => lamp.LitSpotForTest && !lamp.TransitioningForTest, 3);
            T.Check("toggling it back ON relights the lamp", lamp.LitSpotForTest);
            StreetLight.ReactionMax = savedReact;
        }
    }
}
