using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Map music was wired into the singleplayer path and nowhere else, so joining a server came up silent.
    // Two things can make this feature mute and they fail identically from the outside -- no sound -- so both
    // are pinned here: the SELECTION rule (which map plays what), and the CLIP actually existing on disk.
    //
    // ⚠ THE SECOND ONE IS THE ONE THAT BITES. GameAudio.Clip returns null for a missing/unimported resource
    // rather than throwing, and PlayLoop on a null clip is a no-op, so a wrong name ships as silence with no
    // error anywhere -- exactly how the geiger counter shipped mute. Asserting that pei_loop RESOLVES is what
    // separates "wired correctly" from "wired to nothing".
    public class MapMusicSelection : GameTest
    {
        public override string Name => "audio.map_music";
        public override IEnumerable<Step> Run()
        {
            T.Check("PEI maps to the pei loop", Main.MapMusicKey("/x/Maps/PEI") == "pei");
            T.Check("a trailing slash does not become part of the key", Main.MapMusicKey("/x/Maps/PEI/") == "pei");
            T.Check("spaces are stripped (Germany 2.0-style folder names)", Main.MapMusicKey("/x/Maps/Hawaii Island") == "hawaiiisland");
            // The one map that must play NOTHING -- not a fallback, nothing. A null here is the whole rule.
            T.Check("Washington plays no music and gets no PEI fallback", Main.MapMusicKey("/x/Maps/Washington") == null);
            T.Check("case does not matter", Main.MapMusicKey("/x/Maps/washington") == null);
            T.Check("a null map root is not a crash", Main.MapMusicKey(null) == null);

            // The resource half. Without this the five checks above can all pass while the game is silent.
            T.Check("the pei_loop clip actually resolves (a missing one plays as silence, not an error)",
                    GameAudio.Clip("music", "pei_loop") != null);
            yield break;
        }
    }
}
