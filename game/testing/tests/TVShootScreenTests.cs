using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SHOOT THE SCREEN OUT (master: "make the tvs take 1 shot to destroy the visual screen +cone and a few to destroy
    // the actual prop").
    //
    // Two halves that fail in completely different ways, so they are asserted separately:
    //
    //  1. The STATE MACHINE -- one shot kills the picture, the second does NOT get eaten, and a rubble reset brings the
    //     glass back. The middle one is the load-bearing assertion: ShootOutScreen has to report false once the screen
    //     is already gone, or PlayerController swallows every subsequent bullet and the cabinet becomes INDESTRUCTIBLE
    //     after one hit. That is not a subtle visual regression, it is the feature inverted -- and the only way to
    //     notice it in game is to stand there emptying a magazine into a TV wondering why nothing happens.
    //
    //  2. The HIT TEST -- glass vs cabinet. The prop's collider is one trimesh over the whole set, so nothing but the
    //     screen's own bounds separates a shot at the picture from a shot at the plastic.
    //
    // Television_0/1 ship with Unturned and this box has no install, so TVDevice.Make cannot run and a bare TVDevice
    // has no screen mesh -- which is exactly why the hit test's geometry lives in a pure static (PointOnScreen) rather
    // than only inside the instance method. Without that split this half of the feature could not be tested at all
    // here, and "untestable" quietly becomes "untested".
    public sealed class TVShootScreenTests : GameTest
    {
        public override string Name => "tv.shoot_screen_out";

        public override IEnumerable<Step> Run()
        {
            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);

            var tv = new TVDevice { PropName = "Television_1" };
            World.AddChild(tv);
            yield return Ticks(2);

            tv.DebugForceOn();
            yield return Ticks(2);
            T.Check($"a powered TV is lit to start ({tv.DebugLit})", tv.DebugLit);

            T.Check("the first shot on the glass is CONSUMED", tv.ShootOutScreen());
            yield return Ticks(2);
            T.Check($"...and the picture is gone ({tv.DebugLit}, shot={tv.DebugScreenShot})", !tv.DebugLit && tv.DebugScreenShot);
            // The cabinet is NOT rubble -- that is the whole point of the split. If this ever reads true, the screen
            // shot is taking the prop with it and "a few to destroy the actual prop" never happens.
            T.Check($"...but the set itself is still standing ({tv.DebugBroken})", !tv.DebugBroken);

            // THE ONE THAT MATTERS. A second shot must NOT be consumed -- PlayerController only falls through to the
            // destructible's health when this returns false, so a `true` here makes the cabinet bulletproof.
            T.Check("a second shot is NOT consumed -- it falls through to the cabinet", !tv.ShootOutScreen());

            // Same STATE-not-one-shot trap the broken suite documents: the day/night grid sweep re-derives the lit
            // state on every TV, so a screen killed by a one-shot switch-off would light back up at the next mains
            // edge -- a smashed tube glowing away in an intact cabinet.
            PowerNet.SetGlobalPower(false);
            tv.Refresh();
            PowerNet.SetGlobalPower(true);
            tv.Refresh();
            yield return Ticks(2);
            T.Check($"a grid sweep does NOT relight a shot-out screen ({tv.DebugLit})", !tv.DebugLit);

            // Pressing F on a dead set must not ARM it. Refresh keeps it dark either way, so the failure is invisible
            // until the rubble resets and the TV switches itself on -- the identical bug _broken already had.
            tv.Toggle();
            yield return Ticks(2);
            T.Check($"F on a shot-out set does nothing ({tv.DebugLit})", !tv.DebugLit);

            // Rubble reset rebuilds the prop WHOLE: new cabinet, new glass. It comes back off, and the arming check
            // above is what makes that meaningful rather than luck.
            tv.SetBroken(true);
            yield return Ticks(2);
            tv.SetBroken(false);
            yield return Ticks(2);
            T.Check($"a rubble reset restores the glass ({tv.DebugScreenShot})", !tv.DebugScreenShot);
            T.Check($"...and it comes back OFF, not mid-programme ({tv.DebugLit})", !tv.DebugLit);
            tv.DebugForceOn();
            yield return Ticks(2);
            T.Check($"...and works again ({tv.DebugLit})", tv.DebugLit);
            T.Check("...and can be shot out a second time", tv.ShootOutScreen());

            // ---- HIT TEST: glass vs cabinet -----------------------------------------------------------------------
            // A CRT-shaped screen: 0.85 x 0.79, 0.02 thick, face-up in prop-local space (both Television screens are
            // authored that way), sitting 0.4m up the cabinet.
            var screen = new Aabb(new Vector3(-0.425f, 0.39f, -0.395f), new Vector3(0.85f, 0.02f, 0.79f));
            var atOrigin = Transform3D.Identity;

            T.Check("a shot at the centre of the glass is a screen hit",
                TVDevice.PointOnScreen(screen, atOrigin, new Vector3(0f, 0.40f, 0f)));
            T.Check("...just inside a corner too",
                TVDevice.PointOnScreen(screen, atOrigin, new Vector3(0.40f, 0.40f, 0.37f)));
            // The cabinet. These are the ones that decide whether you can shoot a TV to death at all: if the bounds
            // are too generous every hit becomes a screen hit, ShootOutScreen keeps consuming, and the prop's health
            // is never touched.
            T.Check("a shot on the cabinet BELOW the screen is not",
                !TVDevice.PointOnScreen(screen, atOrigin, new Vector3(0f, 0.10f, 0f)));
            T.Check("...on the bezel beside it is not",
                !TVDevice.PointOnScreen(screen, atOrigin, new Vector3(0.70f, 0.40f, 0f)));
            T.Check("...and the back of the set is not",
                !TVDevice.PointOnScreen(screen, atOrigin, new Vector3(0f, 0.40f, -0.70f)));

            // WORLD-SPACE, not prop-space. The bounds are stored local and the placement basis stands the prop up, so
            // testing the raw world point against them would work at the origin and quietly fail on every TV in the
            // map that is not at (0,0,0) facing default -- which is all of them.
            var placed = new Transform3D(new Basis(Vector3.Up, Mathf.Pi * 0.5f), new Vector3(12f, 3f, -40f));
            T.Check("the same centre hit still lands once the prop is moved and turned",
                TVDevice.PointOnScreen(screen, placed, placed * new Vector3(0f, 0.40f, 0f)));
            T.Check("...and the same cabinet miss still misses",
                !TVDevice.PointOnScreen(screen, placed, placed * new Vector3(0f, 0.10f, 0f)));
            // The point that USED to work: feeding the world point in raw. If PointOnScreen ever stops inverting the
            // prop transform this goes true and every check above still passes, because they all live at the origin.
            T.Check("a raw world point ignoring the placement is NOT a hit",
                !TVDevice.PointOnScreen(screen, placed, new Vector3(0f, 0.40f, 0f)));

            // A device with no screen mesh (the split matched nothing) must not report hits on a zero-size box.
            T.Check("a TV with no screen never reports a screen hit",
                !TVDevice.PointOnScreen(new Aabb(), atOrigin, Vector3.Zero));

            PowerNet.SetGlobalPower(gridWas);
        }
    }
}
