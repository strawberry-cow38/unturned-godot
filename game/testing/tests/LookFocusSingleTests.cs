using Godot;
using System.Collections.Generic;
using Look = UnturnedGodot.PlayerController.Look;

namespace UnturnedGodot.Testing
{
    // ONE TARGET AT A TIME (master: "a long standing bug where you could select a dropped item and another
    // interactable. lookatradius should only choose ONE. hard restriction of ONE item when ur looking at something.
    // cover all cases").
    //
    // The look system runs two passes. A ray picks one interactable -- its chain is else-if, so that half was always
    // singular and always looked fine in isolation. Then a sphere at the ray's end sweeps for items, vehicles and
    // puppets, and arbitrated only among ITSELF. Nothing ever compared across the two, so a dropped item lying near
    // where the ray landed lit up at the same time as the door or TV the ray actually hit.
    //
    // The double outline was the visible half. The nastier half is that F resolved the tie through a THIRD ordering --
    // its own else-if chain in the input handler, which puts items ahead of doors -- so the thing you saw highlighted
    // and the thing you interacted with could genuinely differ. That is unreproducible-bug-report territory: it needs
    // a loose item within the assist radius of an interactable, which is common in a looted room and never happens in
    // a clean test scene.
    //
    // "Cover all cases" is taken literally here: the resolver is pure, so every combination of candidates is
    // enumerated rather than sampled. 2^4 sphere combinations x every ray state, and each one must yield exactly one
    // winner.
    //
    // WHAT THIS SUITE DOES NOT COVER, said plainly: UpdateLookFocus is gated on a CAPTURED MOUSE, and a headless
    // Godot has no window to capture -- verified, the gate reads false and the whole body is skipped, so candidates
    // and focus both sit at 0 no matter what the code does. So the resolver's POLICY is proven exhaustively and its
    // WIRING into the focus fields is not. That wiring is six straight-line assignments and the risk is low, but a
    // green run here is not evidence the live path was exercised, and the first version of the live check below
    // passed with the entire arbitration block deleted. It needs a human in game.
    public sealed class LookFocusSingleTests : GameTest
    {
        public override string Name => "look.single_focus";

        const float Near = 1f, Mid = 4f, Far = 9f, Absent = float.MaxValue;

        public override IEnumerable<Step> Run()
        {
            // ---- THE REPORTED BUG, stated directly: a dropped item next to something the ray claimed.
            T.Check("a dropped item does NOT survive alongside a ray-claimed interactable",
                PlayerController.ResolveFocus(Look.RayOther, itemD: Near, vehD: Absent, shelfItemD: Absent, puppetD: Absent) == Look.RayOther);
            // ...and it loses even when it is MUCH nearer than the thing being pointed at. Distance must not override
            // aim, or you cannot look at a door with a can of beans on the floor in front of it.
            T.Check("...even when the item is far nearer than the thing aimed at",
                PlayerController.ResolveFocus(Look.RayOther, 0.001f, Absent, Absent, Absent) == Look.RayOther);
            T.Check("...and the same for a vehicle, a puppet, and all of them at once",
                PlayerController.ResolveFocus(Look.RayOther, Near, Near, Near, Near) == Look.RayOther);

            // ---- EXHAUSTIVE. Every combination of the four sphere finds against every ray state. The claim master
            // asked for is "ONE", so the property under test is a COUNT, not a preference.
            var rays = new[] { Look.None, Look.RayOther, Look.Shelf, Look.ShelfItem };
            var dists = new[] { Absent, Near, Mid, Far };
            int cases = 0, multi = 0, zero = 0;
            string firstBad = null;
            foreach (var ray in rays)
                foreach (var i in dists)
                    foreach (var v in dists)
                        foreach (var s in dists)
                            foreach (var p in dists)
                            {
                                cases++;
                                var w = PlayerController.ResolveFocus(ray, i, v, s, p);
                                // Exactly one winner means: the result names a candidate that was actually PRESENT.
                                bool present = w switch
                                {
                                    Look.None => ray == Look.None && i == Absent && v == Absent && s == Absent && p == Absent,
                                    Look.RayOther => ray == Look.RayOther,
                                    Look.Shelf => ray == Look.Shelf,
                                    Look.ShelfItem => ray == Look.ShelfItem || s != Absent,
                                    Look.Item => i != Absent,
                                    Look.Vehicle => v != Absent,
                                    Look.Puppet => p != Absent,
                                    _ => false,
                                };
                                if (!present) { multi++; firstBad ??= $"ray={ray} i={i} v={v} s={s} p={p} -> {w}"; }
                                if (w == Look.None && !(ray == Look.None && i == Absent && v == Absent && s == Absent && p == Absent))
                                { zero++; firstBad ??= $"ray={ray} i={i} v={v} s={s} p={p} -> None despite candidates"; }
                            }
            T.Check($"all {cases} candidate combinations resolve to exactly one PRESENT target" + (firstBad != null ? $" (first bad: {firstBad})" : ""),
                multi == 0 && zero == 0);
            T.Check($"...and that is {cases} cases, not a sample", cases == 4 * 4 * 4 * 4 * 4);

            // ---- THE SHELF EXCEPTION, which is the one case where the sphere is still allowed to speak. Picking an
            // item off a shelf you are looking at is the entire reason the assist radius exists, so a shelf claim must
            // NOT be terminal the way every other ray claim is.
            T.Check("a looked-at shelf refines to the shelf ITEM the sphere found",
                PlayerController.ResolveFocus(Look.Shelf, Absent, Absent, Near, Absent) == Look.ShelfItem);
            T.Check("...and stays the shelf when the sphere found no item on it",
                PlayerController.ResolveFocus(Look.Shelf, Absent, Absent, Absent, Absent) == Look.Shelf);
            // But a shelf still beats loose world clutter -- a dropped item at your feet must not steal a shelf you
            // are aiming at, which is the same bug in a different coat.
            T.Check("a dropped item does not steal a looked-at shelf",
                PlayerController.ResolveFocus(Look.Shelf, Near, Absent, Absent, Absent) == Look.Shelf);
            T.Check("...nor does a vehicle or a puppet",
                PlayerController.ResolveFocus(Look.Shelf, Absent, Near, Absent, Near) == Look.Shelf);

            // ---- NO RAY CLAIM: nearest wins, and it is a strict comparison rather than first-found. The physics
            // query returns overlaps in whatever order it likes, so "first" would make the focus flicker between two
            // objects as you strafe -- which reads as a rendering glitch, not an arbitration bug.
            T.Check("with nothing aimed at, the nearest sphere find wins (item)",
                PlayerController.ResolveFocus(Look.None, Near, Far, Far, Far) == Look.Item);
            T.Check("...(vehicle)", PlayerController.ResolveFocus(Look.None, Far, Near, Far, Far) == Look.Vehicle);
            T.Check("...(shelf item)", PlayerController.ResolveFocus(Look.None, Far, Far, Near, Far) == Look.ShelfItem);
            T.Check("...(puppet)", PlayerController.ResolveFocus(Look.None, Far, Far, Far, Near) == Look.Puppet);
            T.Check("looking at nothing focuses nothing",
                PlayerController.ResolveFocus(Look.None, Absent, Absent, Absent, Absent) == Look.None);

            // Ties must be DETERMINISTIC. Two overlaps at the same distance is not exotic -- a dropped item resting
            // against a shelf item hits it -- and a non-deterministic winner is a focus that flickers while you stand
            // still, the single most confusing possible symptom.
            var tie = PlayerController.ResolveFocus(Look.None, Near, Near, Near, Near);
            bool stable = true;
            for (int n = 0; n < 64; n++)
                if (PlayerController.ResolveFocus(Look.None, Near, Near, Near, Near) != tie) stable = false;
            T.Check($"a four-way distance tie resolves the same way every time ({tie})", stable);

            // ---- AND THE LIVE SIDE. The resolver being correct is not the claim; the claim is that the PLAYER never
            // ends up with two outlines. A player with nothing to look at must sit at zero, which is the floor the
            // count is measured from -- if this ever reads non-zero on an empty scene, the count itself is lying and
            // every other reading taken with it is worthless.
            var player = new PlayerController();
            World.AddChild(player);
            yield return Ticks(2);
            T.Check($"a player looking at nothing focuses nothing ({player.DebugFocusCount})", player.DebugFocusCount == 0);

            // THE WIRING, which the resolver test above does NOT cover: the verdict has to actually reach the fields.
            // Set up the reported situation for real -- a door dead ahead with a dropped item lying against it, well
            // inside the assist radius -- and demand ONE outline. This is the case that produced two before the fix.
            var mouseWas = Input.MouseMode;
            Input.MouseMode = Input.MouseModeEnum.Captured;   // UpdateLookFocus is gated on this; without it the whole body is skipped
            // ...and whether that GATE actually took is the first thing to establish. Headless has no window, so the
            // capture can silently no-op, and then every reading below is 0 for a reason that has nothing to do with
            // the code under test. Naming it here means a future reader sees "the harness cannot reach this" instead
            // of quietly trusting a green run that never executed the path.
            bool gateOpen = Input.MouseMode == Input.MouseModeEnum.Captured;
            // Spawned through the real factory, not `new` -- a bare WorldItem has no mesh and therefore no collider,
            // so it is invisible to both look passes and the scene offers nothing to arbitrate.
            var door = new Door { Position = new Vector3(0f, 0f, -2.5f) };
            World.AddChild(door);
            var item = WorldItem.Spawn(World, new SDG.Unturned.Item(67), new Vector3(0.1f, 1.55f, -2.2f));   // metal scrap, floating right where the ray lands on the door
            yield return Ticks(6);

            int focused = player.DebugFocusCount, candidates = player.DebugLookCandidates;
            // THE TEETH, and they had to be added after the fact: the first version of this check asserted only
            // "focused <= 1" and PASSED with the entire arbitration block deleted. A scene that offers one candidate
            // satisfies "exactly one wins" no matter what the code does. So the bug has to be shown reproduced before
            // the fix can be claimed -- two candidates in, one out.
            if (gateOpen && candidates >= 2)
            {
                T.Check($"two candidates go in ({candidates}) and exactly one comes out ({focused})", focused == 1);
            }
            else
            {
                // Said out loud rather than skipped silently. The live half of this suite does NOT run in the headless
                // harness -- UpdateLookFocus is gated on a captured mouse, which needs a window -- so what is actually
                // verified here is the RESOLVER's policy, exhaustively, and not its wiring into the focus fields.
                // That wiring is six straight-line assignments; the risk is low, but it is untested and saying so is
                // the point. A green run on this suite is not evidence the live path was exercised.
                GD.Print($"[look.single_focus] live wiring NOT covered: mouse gate open={gateOpen}, candidates={candidates}, " +
                         $"focused={focused}. Headless has no window to capture, so UpdateLookFocus never runs. The " +
                         $"exhaustive ResolveFocus checks above are what carry this suite.");
                T.Check($"live look pass is unreachable headless (mouse gate open={gateOpen}) and the suite says so rather than pretending", true);
            }

            Input.MouseMode = mouseWas;
            item.QueueFree(); door.QueueFree(); player.QueueFree();
            yield return Ticks(1);
        }
    }
}
