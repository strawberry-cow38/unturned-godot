using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE TOASTER (strawberry: "make the toaster take 2 shots to break. the first shot has a chance to eject a piece of
    // bread or two out the top, launch with velocity").
    //
    // Two claims, and the second only exists because of the first: retail ships Toaster_0 at 25 hp, which is EXACTLY one
    // Eaglefire Object_Damage, so it burst on the first bullet and there was no surviving first shot for anything to
    // happen on. The bread would have been unreachable code that still compiled and still "worked".
    //
    // The pop policy is pinned as a pure function because none of its interesting rules are visible from bread on the
    // floor: once per intact toaster, never while broken, and latched even on a FAILED roll -- a chance on the first
    // shot, not a chance on every shot until it finally happens, which is a bread fountain and looks deliberate.
    public sealed class ToasterTests : GameTest
    {
        public override string Name => "props.toaster_pops_bread";

        public override IEnumerable<Step> Run()
        {
            // ---- TWO SHOTS. Stated in Eaglefire rounds, because that is the unit the health was wrong in.
            var cat = DestructibleField.LoadCatalog();
            const string guid = "2d1daa0412b94503aa57a5b422187d48";   // Toaster_0
            T.Check("the toaster is in the rubble catalog at all", cat.ContainsKey(guid));
            if (cat.TryGetValue(guid, out var rub))
            {
                const float shot = 25f;   // eaglefire Object_Damage
                int shots = Mathf.CeilToInt(rub.Health / shot);
                T.Check($"it takes TWO shots ({rub.Health:0} hp / {shot:0} per round = {shots})", shots == 2);
                T.Check($"...i.e. the first round does NOT finish it ({rub.Health:0} > {shot:0})", rub.Health > shot);
                T.Check($"...and the second does ({rub.Health:0} <= {2 * shot:0})", rub.Health <= 2 * shot);
            }

            // ---- THE POP POLICY.
            // Only while intact: a smashed toaster has nothing to throw.
            T.Check("a broken toaster pops nothing", Toaster.SlicesFor(intact: false, alreadyPopped: false, roll: 0f) == 0);
            // Only once: the latch is what stops it being a fountain.
            T.Check("an already-popped toaster pops nothing", Toaster.SlicesFor(true, alreadyPopped: true, roll: 0f) == 0);
            // A roll inside the band throws one or two; outside it, none.
            T.Check($"a low roll throws two ({Toaster.SlicesFor(true, false, 0.05f)})", Toaster.SlicesFor(true, false, 0.05f) == 2);
            T.Check($"a mid roll throws one ({Toaster.SlicesFor(true, false, 0.45f)})", Toaster.SlicesFor(true, false, 0.45f) == 1);
            T.Check($"a high roll throws none ({Toaster.SlicesFor(true, false, 0.95f)})", Toaster.SlicesFor(true, false, 0.95f) == 0);
            // ...and it really is a CHANCE, not a certainty -- swept, so "chance" is not just an adjective in a comment.
            int popped = 0, two = 0;
            for (int i = 0; i <= 1000; i++)
            {
                int n = Toaster.SlicesFor(true, false, i / 1000f);
                if (n > 0) popped++;
                if (n == 2) two++;
            }
            T.Check($"...it fires on some rolls and not others ({popped}/1001)", popped > 100 && popped < 900);
            T.Check($"...and two slices are rarer than one ({two} of {popped})", two > 0 && two < popped);

            // ---- THE LATCH SURVIVES A FAILED ROLL. This is the rule that a screenshot cannot show and that reads as
            // correct either way: with it, an unlucky toaster simply never pops; without it, every subsequent bullet
            // re-rolls until it does, so the "chance" is really "eventually, guaranteed".
            var t = new Toaster();
            World.AddChild(t);
            yield return Ticks(1);
            T.Check("a fresh toaster has not popped", !t.DebugPopped);
            t.OnShot();
            T.Check("...and one shot latches it whether or not bread came out", t.DebugPopped);
            t.OnShot();
            T.Check("...a second shot changes nothing", t.DebugPopped);

            // ---- A RUBBLE RESET GIVES THE BREAD BACK. A reset prop is a NEW toaster, same reasoning as a reset
            // television coming back whole and switched on.
            t.SetBroken(true);
            T.Check("a smashed toaster reports broken", t.DebugBroken);
            t.SetBroken(false);
            T.Check("...and a reset one can pop again", !t.DebugBroken && !t.DebugPopped);

            yield break;
        }
    }
}
