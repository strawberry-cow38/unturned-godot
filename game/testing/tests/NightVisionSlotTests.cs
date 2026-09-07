using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Night vision and the headlamp, both dead (master 2026-09-07: "night vision isnt working,
    /// neither is headlamp").
    ///
    /// They are ONE bug or none: both read the same `Inventory.wornGlasses`, and all three items -- Military
    /// Nightvision (334), Civilian Nightvision (1044), Headlamp (1199) -- are GLASSES-slot. So an empty slot
    /// kills both at once, which is exactly why it presents as two failures.
    ///
    /// This test exists to BISECT that rather than reason about it. It drives the sim half only -- wear the
    /// item, read the predicate, hit the toggle -- with no rendering and no input. If it passes, the slot,
    /// the predicates and the toggles are all fine and the fault is downstream in the screen pass or in the
    /// key never arriving; if it fails, the fault is here and the render is irrelevant. Either way it halves
    /// the search, which reading the call chain had not managed to do.</summary>
    public sealed class NightVisionSlotTests : GameTest
    {
        public override string Name => "player.vision_slot";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var p = Rigs.Player(World, new Vector3(0f, 2f, 0f));
            yield return Ticks(2);

            T.Check("the player has an inventory at all", p.Inventory != null);
            T.Check("nothing worn to start: no nightvision", !p.WearingNightvision);
            T.Check("nothing worn to start: no headlamp", !p.WearingHeadlamp);

            // ---- MILITARY NVG (334)
            p.WearClothing(new Item(334));
            yield return Ticks(1);
            T.Check($"military NVG lands in the glasses slot (worn={p.Inventory?.wornGlasses?.id})",
                    p.Inventory?.wornGlasses?.id == 334);
            T.Check("...and the predicate sees it", p.WearingNightvision);
            T.Check("...as the MILITARY variant", p.NightvisionMilitary);
            T.Check("the goggles start switched OFF", !p.NightVisionOn);
            p.ToggleNightVision();
            T.Check("N switches them on", p.NightVisionOn);
            p.ToggleNightVision();
            T.Check("...and off again", !p.NightVisionOn);

            // ---- CIVILIAN NVG (1044): the other id the predicate accepts
            p.ToggleNightVision();                       // leave the switch ON, to prove the swap keeps it
            p.WearClothing(new Item(1044));
            yield return Ticks(1);
            T.Check("civilian NVG is also nightvision", p.WearingNightvision && !p.NightvisionMilitary);
            T.Check("the switch survives swapping goggles", p.NightVisionOn);

            // ---- HEADLAMP (1199): same slot, so wearing it must END the nightvision
            p.WearClothing(new Item(1199));
            yield return Ticks(1);
            T.Check($"headlamp lands in the same glasses slot (worn={p.Inventory?.wornGlasses?.id})",
                    p.Inventory?.wornGlasses?.id == 1199);
            T.Check("the headlamp is not nightvision", !p.WearingNightvision);
            T.Check("...so the goggle view is off even with the switch left on", !p.NightVisionOn);
            T.Check("the headlamp IS seen as worn", p.WearingHeadlamp);
            T.Check("the lamp starts off", !p.HeadlampOn);
            p.ToggleHeadlamp();
            T.Check("N switches the lamp on", p.HeadlampOn);

            // ---- taking it off must kill the beam, or you keep a light welded to your face
            p.UnwearClothing(EItemType.GLASSES);
            yield return Ticks(1);
            T.Check("unworn: the glasses slot is empty", p.Inventory?.wornGlasses == null);
            T.Check("...and the lamp is off with it", !p.HeadlampOn);

            // ---- THROUGH THE KEY, which is the half that was actually broken.
            // Everything above calls the toggles directly and passes even with N unreachable -- that is exactly
            // how this survived. The real fault was a vehicle-ignition branch earlier in the same else-chain
            // matching ANY N press (its driver check sat in the body, not the condition), swallowing the key
            // before the flashlight branch could see it. So drive the EVENT, not the method.
            p.WearClothing(new Item(334));
            yield return Ticks(1);
            // The SWITCH deliberately survives taking the goggles off, so it may still be on from the swap
            // checks above -- put it back to a known-off start rather than assuming one.
            if (p.NightVisionOn) p.ToggleNightVision();
            T.Check("re-worn, switch known-off for the input pass", p.WearingNightvision && !p.NightVisionOn);

            p._UnhandledInput(NKey());
            yield return Ticks(1);
            T.Check("pressing N actually reaches the goggles", p.NightVisionOn);
            p._UnhandledInput(NKey());
            yield return Ticks(1);
            T.Check("...and toggles them off again", !p.NightVisionOn);

            p.WearClothing(new Item(1199));
            yield return Ticks(1);
            if (p.HeadlampOn) p.ToggleHeadlamp();   // its switch persists too -- start from a known off
            T.Check("headlamp worn, switch known-off", p.WearingHeadlamp && !p.HeadlampOn);
            p._UnhandledInput(NKey());
            yield return Ticks(1);
            T.Check("the same key reaches the headlamp", p.HeadlampOn);

            p.QueueFree();
        }

        /// <summary>An N keypress as the engine delivers it -- Echo:false, matching the ignition branch's own
        /// pattern, so this is the exact event that used to be eaten.</summary>
        static InputEventKey NKey() => new InputEventKey { Keycode = Key.N, PhysicalKeycode = Key.N, Pressed = true, Echo = false };
    }
}
