using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The mote LOOK is taste and needs an eyeball, but "the dust is gone before the lamp cuts out" is a property
    // and can be asserted. StreetLight.MoteFadeFor is a pure function of time of day precisely so this half is
    // checkable rather than left to a screenshot.
    //
    // Lamps switch at DayNightCycle.IsNightTime's thresholds (off at dawn 0.26, on at dusk 0.74). Motes must
    // reach zero BEFORE dawn, not with it, and come back after dusk lights the lamp.
    public sealed class StreetLightMoteFadeTests : GameTest
    {
        public override string Name => "props.streetlight_motes_fade_with_the_clock";

        public override IEnumerable<Step> Run()
        {
            const float dawn = 0.26f, dusk = 0.74f;

            T.Check("deep night: motes at full", Mathf.IsEqualApprox(StreetLight.MoteFadeFor(0.05f), 1f));
            T.Check("midday: no motes", Mathf.IsEqualApprox(StreetLight.MoteFadeFor(0.5f), 0f));

            // The actual ask: gone BEFORE the lamp goes out, not at the same moment.
            float atDawn = StreetLight.MoteFadeFor(dawn);
            float justBefore = StreetLight.MoteFadeFor(dawn - StreetLight.MoteFadeGap * 0.5f);
            T.Check($"motes are already out when the lamp cuts at dawn (fade={atDawn:0.###})", Mathf.IsZeroApprox(atDawn));
            T.Check($"...and out slightly earlier too, so the two events are separated ({justBefore:0.###})", Mathf.IsZeroApprox(justBefore));

            // Monotone ramp down through the dawn window -- no jumps, no reversals.
            float prev = 1.01f; bool monotone = true;
            for (float t = dawn - StreetLight.MoteFadeLead - StreetLight.MoteFadeGap; t <= dawn; t += 0.002f)
            {
                float v = StreetLight.MoteFadeFor(t);
                if (v > prev + 0.001f) { monotone = false; break; }
                prev = v;
            }
            T.Check("the dawn fade only ever decreases", monotone);

            // Dusk: lamp lights at 0.74, motes come back just after -- not before, or dust appears in an unlit beam.
            T.Check("no motes just before dusk lights the lamp", Mathf.IsZeroApprox(StreetLight.MoteFadeFor(dusk - 0.01f)));
            T.Check("motes are back at full a full lead after dusk",
                    Mathf.IsEqualApprox(StreetLight.MoteFadeFor(dusk + StreetLight.MoteFadeGap + StreetLight.MoteFadeLead + 0.01f), 1f));
            float mid = StreetLight.MoteFadeFor(dusk + StreetLight.MoteFadeGap + StreetLight.MoteFadeLead * 0.5f);
            T.Check($"and ramp through the middle of the dusk window rather than snapping ({mid:0.##})", mid > 0.2f && mid < 0.8f);

            // Wraparound: the curve is a function of a cyclic clock, so t=1.05 must read as t=0.05.
            T.Check("the curve wraps with the clock", Mathf.IsEqualApprox(StreetLight.MoteFadeFor(1.05f), StreetLight.MoteFadeFor(0.05f)));

            // And an unlit lamp shows nothing regardless of what the clock says.
            var lamp = StreetLight.Make(new Vector3(0f, 5f, 0f), 5f);
            World.AddChild(lamp);
            yield return Ticks(2);
            lamp.SetNight(true); lamp.SetPowered(true); lamp.SetMoteFade(1f);
            yield return Ticks(1);
            T.Check("a lit lamp at full fade emits motes", lamp.LitMotesForTest);
            lamp.SetPowered(false);
            yield return Ticks(1);
            T.Check("cutting the grid stops the motes even at full fade", !lamp.LitMotesForTest);
        }
    }
}
