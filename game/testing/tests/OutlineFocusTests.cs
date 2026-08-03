using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The look-at outline's rim colour comes from WorldItem.FocusColor -- ONE shared static that the outline shader
    // samples every frame. Every focusable sets it when it gains focus; nothing clears it when focus is lost. That is
    // workable only while everything that lights the outline layer also claims the colour, and two things didn't:
    // containers and standalone doored props, which therefore wore the rarity of whatever item you looked at last
    // (strawberry: "containers inherit the rarity color of the last rarity item you looked at").
    //
    // This is a nasty shape to test for and an easy one to regress:
    //  - it is ORDER-DEPENDENT. Focus a container first and it is white by luck, because nothing has dirtied the
    //    static yet. The bug only appears after something else has been focused, so a test that focuses one object in
    //    a clean sandbox passes against the broken code. Every case below deliberately dirties the static first.
    //  - it is SILENT. Nothing errors, nothing is missing, the silhouette mesh is still unshaded white -- only the rim
    //    tint is wrong, and only sometimes.
    public sealed class OutlineFocusColorTests : GameTest
    {
        public override string Name => "ui.outline_focus_color";

        static readonly Color Rare = new Color(1f, 0.45f, 0.05f);   // stand-in for a high-rarity item's colour

        public override IEnumerable<Step> Run()
        {
            var saved = WorldItem.FocusColor;

            // A CONTAINER, after a rare item has already claimed the static.
            var shelf = new StoreShelf();
            World.AddChild(shelf);
            yield return Ticks(1);
            WorldItem.FocusColor = Rare;                       // ...what looking at a rare gun leaves behind
            shelf.SetShelfFocused(true);
            T.Check($"a focused container forces a WHITE rim, not the last rarity ({WorldItem.FocusColor})",
                WorldItem.FocusColor == Colors.White);

            // Losing focus must not smear white over the next thing that DOES own a colour -- the fix is "claim it on
            // gain", not "reset it on loss", and an over-eager clear would break every item outline instead.
            WorldItem.FocusColor = Rare;
            shelf.SetShelfFocused(false);
            T.Check("...and unfocusing does not stomp a colour someone else owns",
                WorldItem.FocusColor == Rare);

            // A STANDALONE DOORED PROP (a wardrobe outside the container path) had the identical gap.
            var door = new ObjectDoor();
            World.AddChild(door);
            yield return Ticks(1);
            WorldItem.FocusColor = Rare;
            door.SetLookFocused(true);
            T.Check($"a focused doored prop forces a WHITE rim too ({WorldItem.FocusColor})",
                WorldItem.FocusColor == Colors.White);
            WorldItem.FocusColor = Rare;
            door.SetLookFocused(false);
            T.Check("...and it too leaves an unfocus alone", WorldItem.FocusColor == Rare);

            // A THIRD site, added when TVs landed. Not distrust of the author -- the point is that this static is a
            // shared trap and EVERY new focusable is a fresh chance to forget it, so the test guards the pattern
            // rather than the two places that happened to be broken first.
            var tv = new TVDevice();
            World.AddChild(tv);
            yield return Ticks(1);
            WorldItem.FocusColor = Rare;
            tv.SetLookFocused(true);
            T.Check($"a focused TV forces a WHITE rim ({WorldItem.FocusColor})", WorldItem.FocusColor == Colors.White);
            WorldItem.FocusColor = Rare;
            tv.SetLookFocused(false);
            T.Check("...and leaves an unfocus alone", WorldItem.FocusColor == Rare);

            WorldItem.FocusColor = saved;
        }
    }
}
