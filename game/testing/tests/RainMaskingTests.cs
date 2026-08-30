using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // Rain masks the player from zombie hearing. bitvox: "approve the masking but make sure it is pretty
    // limited, not a free pass."
    //
    // The RULE is L0 (NoiseMaskingTests) and is not repeated here. What needs an engine is the WIRING: that a
    // real SoundBus.Emit, with a real WeatherManager raining, actually arrives at the listener quieter. Both
    // halves can be individually correct and not joined up -- a pure function nothing calls is the shape of
    // half the bugs found on this project today.
    public class RainMasksEmittedNoise : GameTest
    {
        public override string Name => "combat.rain_masks_emitted_noise";

        public override IEnumerable<Step> Run()
        {
            float heard = -1f;
            System.Action<Vector3, float> listener = (_, l) => heard = l;
            SoundBus.OnNoise += listener;

            // NO WEATHER AT ALL FIRST. This is the check that matters most: every player on a clear day must
            // be exactly as loud as they were before this feature existed.
            heard = -1f;
            SoundBus.Emit(World.GetTree(), Vector3.Zero, SoundBus.Gunshot);
            T.Check($"with no WeatherManager a gunshot is untouched ({heard})", Mathf.IsEqualApprox(heard, SoundBus.Gunshot));

            var dn = new DayNightCycle { DayLength = 120f, VisualsEnabled = false };
            World.AddChild(dn);
            var wm = WeatherManager.Attach(World, null, dn, seed: 4242);
            yield return Step.Ticks(2);

            // Dry weather, manager present: still untouched. A manager existing must not by itself change
            // anything -- the masking has to come from actual rain.
            heard = -1f;
            SoundBus.Emit(World.GetTree(), Vector3.Zero, SoundBus.Gunshot);
            T.Check($"a dry WeatherManager changes nothing ({heard})", Mathf.IsEqualApprox(heard, SoundBus.Gunshot));

            // Now make it rain, hard, and let the blend run up.
            // SetPerpetual, not ForecastImmediately: the scheduled path fades in over a window shorter than
            // the asset's own fade, so the blend stalls part-way and the test would be asserting against
            // whatever fraction it happened to reach (0.40 on the first run). Perpetual publishes a
            // committed storm immediately -- the same call the `weather heavy` console command uses, so this
            // exercises a real path rather than a test-only one.
            wm.Sim.SetPerpetual(1);   // Heavy Rain
            yield return Step.Ticks(4);
            float rint = wm.RainIntensity * wm.Severity;
            T.Check($"the storm actually committed (rint {rint:0.00})", rint > 0.5f);

            // BREAK IT: drop the NoiseMasking call from SoundBus.Emit -> these two are unchanged and the
            // whole feature is a pure function nobody calls.
            heard = -1f;
            SoundBus.Emit(World.GetTree(), Vector3.Zero, SoundBus.Walk);
            float walk = heard;
            heard = -1f;
            SoundBus.Emit(World.GetTree(), Vector3.Zero, SoundBus.Gunshot);
            float shot = heard;

            T.Check($"footsteps carry less in a storm ({walk:0.0} of {SoundBus.Walk})", walk < SoundBus.Walk);
            T.Check($"so does a gunshot ({shot:0.0} of {SoundBus.Gunshot})", shot < SoundBus.Gunshot);

            // THE LIMIT, which is the half bitvox actually asked for. Moving must benefit clearly more than
            // shooting, and nothing may lose more than a quarter.
            float moveLoss = 1f - walk / SoundBus.Walk;
            float shootLoss = 1f - shot / SoundBus.Gunshot;
            T.Check($"moving benefits more than shooting ({moveLoss:P0} vs {shootLoss:P0})",
                    moveLoss > shootLoss * 1.5f);
            T.Check($"and a storm never takes more than a quarter ({moveLoss:P0})",
                    moveLoss <= 0.25f + 1e-3f);

            // THE TIERS MUST DIFFER, and this is the only fixture that can see it. Heavy Rain has Severity
            // exactly 1.0, so rint == RainIntensity there and dropping Severity from the calculation changes
            // NOTHING -- a mutation that removed it survived a green suite against the heavy-only fixture.
            // Default Rain is 0.7, so a lighter storm must mask visibly less.
            wm.Sim.SetPerpetual(0);   // Default Rain
            yield return Step.Ticks(4);
            float lightRint = wm.RainIntensity * wm.Severity;
            T.Check($"default rain is a weaker storm than heavy ({lightRint:0.00} vs {rint:0.00})",
                    lightRint < rint - 0.05f);

            heard = -1f;
            SoundBus.Emit(World.GetTree(), Vector3.Zero, SoundBus.Walk);
            float lightWalk = heard;
            heard = -1f;
            SoundBus.Emit(World.GetTree(), Vector3.Zero, SoundBus.Gunshot);
            float lightShot = heard;
            T.Check($"footsteps: default rain masks less than heavy ({lightWalk:0.0} vs {walk:0.0})",
                    lightWalk > walk);
            T.Check($"gunshots too ({lightShot:0.0} vs {shot:0.0})", lightShot > shot);
            T.Check($"but it still masks something ({lightWalk:0.0} of {SoundBus.Walk})",
                    lightWalk < SoundBus.Walk);

            wm.Sim.SetPerpetual(1);   // back to heavy for the remaining checks
            yield return Step.Ticks(4);

            // A silent emission stays silent -- a suppressed shot must not become an audible tiny one.
            heard = -1f;
            SoundBus.Emit(World.GetTree(), Vector3.Zero, 0f);
            T.Check($"silence is still silence ({heard})", heard < 0f);

            SoundBus.OnNoise -= listener;
            wm.QueueFree(); dn.QueueFree();
        }
    }
}
