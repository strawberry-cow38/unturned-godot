using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>What a thrown thing sounds like (master 2026-09-10: "wire all the audio properly").
    ///
    /// Grenade.cs carried two comments asserting these clips did not exist -- "the canister popping (no
    /// dedicated retail clip in the rip)" and "no dedicated bounce clip in the rip" -- and played a bullet
    /// CASING pitched down in both places. The rip ships thirty throwable clips in content/audio/items, a
    /// folder no Bank/Pick call had ever named, because retail hangs throwable audio off each item's own
    /// bundle rather than beside the explosion banks. Nothing was broken; nobody had looked there.
    ///
    /// So the checks are aimed at the two ways this stays wrong: a clip that silently is not on disk (the
    /// fallback would quietly restore the casing), and the per-COLOUR clips collapsing onto one another --
    /// which is exactly what Pick would do here, since its `prefix_*` glob cannot separate
    /// throwables_smoke_red_use from throwables_smoke_red_smoke.
    ///
    /// ⚠ THIRTY FILES, SIX RECORDINGS. Measured 2026-09-13: every _use clip in the folder except the snowball,
    /// the C4/impact pair and the flashbang bang is the SAME bytes, and all eight smoke _smoke clips are one
    /// recording too. The id map below therefore proves the port asks for the right FILE per item -- it cannot
    /// prove two colours sound different, because in the rip they do not. That is retail's doing, not ours, and
    /// it is why throwables_grenade_bounce_use was never a bounce: it is the Grenade_Bounce ITEM's pin-pull,
    /// byte-identical to the frag's, and playing it at a bounce replayed the throw.</summary>
    public sealed class ThrowableAudioTests : GameTest
    {
        public override string Name => "audio.throwables";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            // ---- EVERY THROWABLE THE PORT HAS GETS ITS OWN ACTIVATION CLIP.
            var ids = new List<ushort> { 254, 1242 };
            for (ushort i = 255; i <= 268; i++) ids.Add(i);
            var seen = new Dictionary<string, ushort>();
            int distinct = 0;
            foreach (ushort id in ids)
            {
                string stem = GameAudio.ThrowableStem(id);
                T.Check($"{id} has a stem ({stem})", stem != null);
                var use = GameAudio.ThrowableUse(id);
                T.Check($"{id} ({stem}) has an activation clip on disk", use != null);
                if (use != null && !seen.ContainsKey(stem)) { seen[stem] = id; distinct++; }
            }
            T.Check($"all 16 stems are distinct (got {distinct})", distinct == 16);

            // ---- THE COLOURS DO NOT COLLAPSE. Two different smokes must not resolve to the same stream --
            // one clip for every colour is what a prefix glob would have produced.
            T.Check("red smoke and blue smoke are different clips",
                    !ReferenceEquals(GameAudio.ThrowableUse(266), GameAudio.ThrowableUse(262)));
            T.Check("red flare and red smoke are different clips",
                    !ReferenceEquals(GameAudio.ThrowableUse(259), GameAudio.ThrowableUse(266)));
            T.Check("a frag and a makeshift grenade are different clips",
                    !ReferenceEquals(GameAudio.ThrowableUse(254), GameAudio.ThrowableUse(1242)));

            // ---- AND THE PIN IS NOT THE CANISTER. Same item, two clips: this is the pair a `prefix_*` glob
            // cannot tell apart, so it is the pair worth pinning.
            var vent = GameAudio.SmokeVent(266);
            T.Check("red smoke has a venting clip", vent != null);
            T.Check("...which is NOT its pin-pull", vent != null && !ReferenceEquals(vent, GameAudio.ThrowableUse(266)));
            T.Check("every smoke colour vents", GameAudio.SmokeVent(261) != null && GameAudio.SmokeVent(267) != null
                                             && GameAudio.SmokeVent(268) != null);
            T.Check("a frag does not vent", GameAudio.SmokeVent(254) == null);
            T.Check("a flare does not vent", GameAudio.SmokeVent(259) == null);

            // ---- THE BOUNCE IS THE SURFACE, NOT THE ITEM.
            // First the premise, in bytes, because the whole fix rests on it: the file called "bounce" is the
            // throw. If a later rip ever yields a genuinely different recording, THIS is the check that fails
            // and says so, instead of the wiring quietly staying wrong forever.
            byte[] bounceWav = null, useWav = null;
            try
            {
                string dir = ProjectSettings.GlobalizePath("res://content/audio/items");   // the path GameAudio.Clip itself resolves
                bounceWav = System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, "throwables_grenade_bounce_use.wav"));
                useWav    = System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, "throwables_grenade_use.wav"));
            }
            catch (System.Exception e) { T.Check($"reading both throwable clips ({e.Message})", false); }
            T.Check("both throwable clips are readable", bounceWav != null && bounceWav.Length > 0
                                                      && useWav != null && useWav.Length > 0);
            bool identical = bounceWav != null && useWav != null && bounceWav.Length == useWav.Length;
            if (identical)
                for (int i = 0; i < bounceWav.Length; i++) if (bounceWav[i] != useWav[i]) { identical = false; break; }
            T.Check("throwables_grenade_bounce_use IS throwables_grenade_use -- it is the BOUNCE GRENADE's "
                  + "pin-pull (item 1838), not a landing, and there is no bounce clip in the rip", identical);

            // So the bounce comes off the physics-impact bank, keyed by what was struck.
            var onConcrete = GameAudio.ThrowableBounce(PlayerController.Surf.Concrete);
            var onMetal    = GameAudio.ThrowableBounce(PlayerController.Surf.Metal);
            T.Check("a bounce on concrete has a clip", onConcrete != null);
            T.Check("a bounce on metal has a clip", onMetal != null);
            T.Check("...and concrete and metal are not the same clip", !ReferenceEquals(onConcrete, onMetal));
            T.Check("a bounce is never a throwable's pin-pull",
                    !ReferenceEquals(onConcrete, GameAudio.ThrowableUse(254))
                 && !ReferenceEquals(onMetal, GameAudio.ThrowableUse(254)));

            // ---- AN ITEM THAT IS NOT A THROWABLE ASKS FOR NOTHING.
            T.Check("a non-throwable id has no stem", GameAudio.ThrowableStem(13) == null);
            T.Check("...and no clip", GameAudio.ThrowableUse(13) == null);

            // ---- THE PLACEHOLDERS ARE GONE FROM THE SOURCE. Both call sites fell back to a casing behind a
            // comment claiming the rip had nothing; if either returns, the sound regresses with no test able
            // to see it -- the clip is still "on disk" and every check above still passes.
            string src = "";
            try
            {
                string p = ProjectSettings.GlobalizePath("res://Grenade.cs");
                if (!System.IO.File.Exists(p)) p = ProjectSettings.GlobalizePath("res://game/Grenade.cs");
                if (System.IO.File.Exists(p)) src = System.IO.File.ReadAllText(p);
            }
            catch { }
            if (src.Length > 0)
            {
                T.Check("Grenade.cs no longer claims the rip has no bounce clip", !src.Contains("no dedicated bounce clip"));
                T.Check("...nor that the canister has none", !src.Contains("no dedicated retail clip in the rip"));
                T.Check("the smoke vent is wired at the smoke branch", src.Contains("GameAudio.SmokeVent(ItemId)"));
                T.Check("the bounce is wired at the bounce, and takes the surface it struck",
                        src.Contains("GameAudio.ThrowableBounce(sf)"));
            }
        }
    }
}
