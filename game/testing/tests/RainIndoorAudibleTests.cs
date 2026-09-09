using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Rain stays audible indoors (strawberry 2026-09-05 "change the muffle effect in buildings to not
    /// mute rain, but make it still audible", then again 2026-09-07 "when inside a building dont mute the rain
    /// sounds").
    ///
    /// Asked twice, because the first pass fixed the wrong amount. The shelter muffle is a 24 dB/oct low-pass,
    /// so what matters is how far the knee sits BELOW rain's own band -- rain is hiss and lives at 4-8 kHz:
    ///
    ///     900 Hz  -> 8 kHz is 3.15 octaves up = ~76 dB down   (the original: indoors was silence)
    ///     2200 Hz -> 8 kHz is 1.86 octaves up = ~45 dB down   (the first fix: still silence)
    ///     6000 Hz -> 8 kHz is 0.42 octaves up = ~10 dB down   (dulled, still plainly rain)
    ///
    /// A knee "raised" from 900 to 2200 looks like a 2.4x improvement written down and is nearly none at all
    /// once the slope is applied, which is exactly how the same complaint came back unchanged two days later.
    ///
    /// So this pins the AUDIBLE outcome rather than the constant: indoors the knee must stay inside the band
    /// rain actually occupies, and the level must not collapse. It is deliberately a floor and not an equality
    /// -- tuning the muffle further is fine, re-muting the rain is not. Both legs are asserted, because a
    /// filter check alone would pass happily while the dB trim did the swallowing instead, which is precisely
    /// the mistake the first pass made when it left the trim alone on the grounds that the filter was to
    /// blame.</summary>
    public sealed class RainIndoorAudibleTests : GameTest
    {
        public override string Name => "rain.indoor_audible";
        public override double TimeoutSimSeconds => 30;

        /// <summary>Rain's audible band. Below this the clip's character is gone and it reads as silence
        /// rather than as shelter.</summary>
        const float RainBandHz = 5000f;

        static void Settle(RainAudio ra)
        {
            for (int i = 0; i < 120; i++) ra.HubProcess(0.05);   // the level SLEWS at 180 dB/s; give it time to arrive
        }

        public override IEnumerable<Step> Run()
        {
            var ra = new RainAudio();
            World.AddChild(ra);
            yield return Ticks(2);

            // ---- OUTDOORS, heavy rain. The reference the indoor case is judged against.
            ra.Intensity = 1f;
            ra.Shelter = 1f;
            Settle(ra);
            yield return Ticks(1);
            float openCut = ra.DebugCutoffHz, openDb = ra.DebugLightDb;
            T.Check($"open sky leaves the mix unfiltered ({openCut:0} Hz)", openCut > 15000f);
            T.Check($"open sky rain is audible ({openDb:0.0} dB)", openDb > -30f);

            // ---- FULLY INDOORS. Shelter 0 is what ShelterFactor reports inside a building.
            ra.Shelter = 0f;
            Settle(ra);
            yield return Ticks(1);
            float inCut = ra.DebugCutoffHz, inDb = ra.DebugLightDb;

            T.Check($"indoors the knee stays inside rain's own band ({inCut:0} Hz >= {RainBandHz:0})",
                    inCut >= RainBandHz);
            T.Check($"indoors rain is still audible, not muted ({inDb:0.0} dB)", inDb > -30f);
            T.Check($"...and within a few dB of open sky ({inDb - openDb:0.0} dB of trim)",
                    openDb - inDb <= 5f);

            // ---- BUT IT IS STILL SHELTER. If indoors and outdoors were identical the muffle would be gone
            // rather than corrected, and that is a different bug with the same test result unless it is named.
            T.Check($"indoors is still dulled relative to open sky ({inCut:0} Hz < {openCut:0} Hz)",
                    inCut < openCut * 0.75f);

            // ---- DRY. No rain, no bed, whatever the shelter says.
            ra.Intensity = 0f;
            Settle(ra);
            yield return Ticks(1);
            T.Check($"dry weather silences the bed ({ra.DebugLightDb:0.0} dB)", ra.DebugLightDb < -60f);
        }
    }
}
