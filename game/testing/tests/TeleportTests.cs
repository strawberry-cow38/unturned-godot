using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // TELEPORT ACTUALLY MOVES YOU (strawberry: "teleporting doesnt work. console says 'teleported to x' but it doesnt
    // move me. on SP").
    //
    // The reported symptom IS the interesting part: the console prints success either way. `Player?.TeleportTo(...)`
    // followed by an unconditional `Log("teleported to ...")` reports the same thing whether the move happened, whether
    // it was undone a tick later, or whether Player was null and nothing was called at all. Every other verb in
    // DevConsole guards with `if (Player == null) { Log("no player"); return; }`; teleport is the one that does not.
    //
    // So the check that matters is not "did TeleportTo get called" but "is the player still there SEVERAL TICKS
    // LATER". PlayerController keeps render-interpolation snapshots and its 50 Hz tick RESTORES GlobalPosition from
    // _interpCurr before moving -- so a positional write that does not also shift those snapshots is silently undone
    // on the very next tick, which is exactly a teleport that reports success and does nothing. TeleportTo carries a
    // comment saying this was fixed once already; this suite is what would notice it coming back.
    public sealed class TeleportTests : GameTest
    {
        public override string Name => "player.teleport_sticks";

        static bool SameXZ(Vector3 a, Vector3 b, float tol = 0.5f)
            => Mathf.Abs(a.X - b.X) < tol && Mathf.Abs(a.Z - b.Z) < tol;

        public override IEnumerable<Step> Run()
        {
            var p = new PlayerController { CaptureMouse = false };
            World.AddChild(p);
            yield return Ticks(3);   // let the interp snapshots become ready -- the bug only exists once they are

            var before = p.GlobalPosition;
            var target = before + new Vector3(120f, 0f, -85f);
            p.TeleportTo(target);

            // 1. It moves AT ALL. If this fails the write never happened.
            T.Check($"teleport moves the player immediately ({p.GlobalPosition} -> want {target})",
                SameXZ(p.GlobalPosition, target));

            // 2. IT STICKS. This is the one that catches the reported bug: the 50 Hz tick restores GlobalPosition from
            //    _interpCurr, so a teleport that did not reset those snapshots is dragged straight back to where it
            //    started -- after the console has already said "teleported to X".
            yield return Ticks(1);
            T.Check($"...and is still there after ONE physics tick ({p.GlobalPosition})",
                SameXZ(p.GlobalPosition, target));
            yield return Ticks(12);
            T.Check($"...and after twelve ({p.GlobalPosition})", SameXZ(p.GlobalPosition, target));

            // 3. ...and it did not merely fail to snap back by landing where it started. A test whose target is near
            //    the origin would pass while doing nothing, so the move has to be big enough to be unambiguous.
            T.Check($"...having genuinely gone somewhere ({before.DistanceTo(p.GlobalPosition):0} m from the start)",
                before.DistanceTo(p.GlobalPosition) > 50f);

            // 4. The render-interp snapshots moved WITH it. This is the actual mechanism, and the reason a naive
            //    `GlobalPosition = x` looks correct for exactly one frame before being undone.
            T.Check($"...with the physics-truth position agreeing, not just the visual one ({p.TruePhysicsPosition})",
                SameXZ(p.TruePhysicsPosition, target));

            // 5. TEETH: prove the harness would actually catch a snap-back, by doing the broken thing on purpose --
            //    a bare position write that leaves the interp snapshots pointing at the old spot.
            var p2 = new PlayerController { CaptureMouse = false };
            World.AddChild(p2);
            yield return Ticks(3);
            var start2 = p2.GlobalPosition;
            p2.GlobalPosition = start2 + new Vector3(140f, 0f, 60f);   // the naive teleport, no snapshot reset
            yield return Ticks(3);
            T.Check($"a bare position write really IS undone by the tick ({p2.GlobalPosition} back at {start2}) -- so check 2 has teeth",
                SameXZ(p2.GlobalPosition, start2));

            yield break;
        }
    }
}
