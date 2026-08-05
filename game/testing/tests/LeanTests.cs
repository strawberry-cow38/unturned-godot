using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // LEANING (strawberry: "implement leaning from the source. Q leans ~45 deg left, E leans ~45 deg right (confirm
    // numbers vs src").
    //
    // Confirmed: it is TWENTY degrees. HumanAnimator.LEAN = 20, consumed at PlayerLook.cs:744.
    //
    // The trap this suite exists for: leaning has an obvious wrong implementation that is INDISTINGUISHABLE from the
    // right one in a screenshot and in any test that asks "does the view tilt". Roll the camera in place and the world
    // tilts exactly as it should -- and your head never leaves the doorway, so you cannot see round the corner and
    // nobody can see you. Retail does not do that. It rolls a pivot at the player's ORIGIN, at the feet, with the camera
    // riding at eye height above it, so the tilt DRAGS the head sideways. Source says so in as many words
    // (PlayerLook.GetEyesPositionWithoutLeaning: "child of another transform with zeroed position which gets rotated
    // according to the leaning angle").
    //
    // So nothing here asserts an angle sign or that a rotation happened. It asserts WHERE THE EYES END UP -- which is
    // also the only formulation that survives Unity/Godot handedness, where the raw Z sign is not portable and agreeing
    // with the source's literal `lean * LEAN` would be a coin flip.
    public sealed class LeanTests : GameTest
    {
        public override string Name => "player.lean";
        public override double TimeoutSimSeconds => 40;

        // Where the camera sits relative to the player, in the PLAYER's own frame: +X right, +Y up.
        static Vector3 EyeLocal(PlayerController p) => p.GlobalTransform.AffineInverse() * p.Camera.GlobalPosition;

        public override IEnumerable<Step> Run()
        {
            // ---- THE NUMBER. Stated against the source constant, because the ask carried a guess ("~45 deg") and the
            // port is 1:1 -- if this ever gets "fixed" to 45 it should be a deliberate edit that trips a test, not a drift.
            T.Check($"lean is 20 degrees, per HumanAnimator.LEAN ({PlayerController.LeanDegrees:0.##})",
                Mathf.IsEqualApprox(PlayerController.LeanDegrees, 20f));

            // ---- THE RULES, engine-free. These are the branches of PlayerAnimator.simulate.
            {
                T.Check("neither key -> upright", PlayerController.LeanFrom(false, false, EPlayerStance.STAND, true, true, out _) == 0);
                // Nelson, 2025-01-20: holding both stops the lean rather than preferring left. Worth pinning because
                // "prefer left" is the natural thing to write and looks fine until someone rolls their fingers.
                T.Check("BOTH keys -> upright, not a preferred side",
                    PlayerController.LeanFrom(true, true, EPlayerStance.STAND, true, true, out _) == 0);
                int l = PlayerController.LeanFrom(true, false, EPlayerStance.STAND, true, true, out _);
                int r = PlayerController.LeanFrom(false, true, EPlayerStance.STAND, true, true, out _);
                T.Check($"Q and E lean OPPOSITE ways ({l} vs {r})", l != 0 && r != 0 && l == -r);

                // Stance gate, from the source list. The interesting half is what is ABSENT: crouch and prone lean,
                // and leaning out of cover from a crouch is the entire point of the feature.
                foreach (var st in new[] { EPlayerStance.CLIMB, EPlayerStance.SPRINT, EPlayerStance.DRIVING, EPlayerStance.SITTING })
                    T.Check($"no leaning while {st}", PlayerController.LeanFrom(true, false, st, true, true, out _) == 0);
                foreach (var st in new[] { EPlayerStance.STAND, EPlayerStance.CROUCH, EPlayerStance.PRONE, EPlayerStance.SWIM })
                    T.Check($"...but {st} leans fine", PlayerController.LeanFrom(true, false, st, true, true, out _) != 0);

                // Obstruction is per-SIDE. A single "is anything near me" test passes every symmetric check and then
                // refuses to lean away from a wall you are standing against, which is the one time you want to.
                T.Check("a blocked left refuses to lean left",
                    PlayerController.LeanFrom(true, false, EPlayerStance.STAND, leftClear: false, rightClear: true, out bool ob) == 0 && ob);
                T.Check("...and still leans RIGHT with the same wall on the left",
                    PlayerController.LeanFrom(false, true, EPlayerStance.STAND, leftClear: false, rightClear: true, out _) != 0);
                T.Check("obstructed is distinct from simply-not-leaning",
                    PlayerController.LeanFrom(false, false, EPlayerStance.STAND, false, false, out bool ob2) == 0 && !ob2);
            }

            // ---- THE PEEK. This is the check the whole thing is for.
            var p = new PlayerController { CaptureMouse = false, Position = new Vector3(0f, 0.2f, 0f) };
            World.AddChild(p);
            // The eye height is ITSELF a 4/s lerp from 1.6 to the stance height, so anything measured a few ticks in is
            // a measurement of a number still moving -- and the arc-drop below is only 0.1 m, small enough for that
            // drift to swallow it and flip the sign.
            yield return Ticks(60);

            var upright = EyeLocal(p);
            T.Check($"upright, the eyes sit on the player's centreline ({upright.X:0.###} m off)", Mathf.Abs(upright.X) < 0.02f);
            float eyeY = upright.Y;

            p.ScriptedLean = 1;   // Q
            yield return Until(() => Mathf.Abs(p.DebugLeanAngle) > 19f, 6);
            var left = EyeLocal(p);
            // LEFT means -X in the player's own frame: Godot faces -Z, so +X is the right hand. Asserting the eye
            // POSITION rather than the roll sign is what makes this portable -- the source's `lean * LEAN` is written
            // for a left-handed engine and copying the sign across is a coin flip.
            T.Check($"Q moves the eyes to the player's LEFT ({left.X:0.###} m)", left.X < -0.3f);
            // ...and by the amount the geometry demands. A pivot at the feet swings the eyes by eyeHeight*sin(20).
            // Anything much smaller means the pivot is somewhere other than the floor; zero means the camera rolled in
            // place. Retail's own tilt-vs-peek relationship, expressed as a number.
            float want = eyeY * Mathf.Sin(Mathf.DegToRad(PlayerController.LeanDegrees));
            T.Check($"...by eyeHeight*sin(20) = {want:0.###} m (got {-left.X:0.###})", Mathf.Abs(-left.X - want) < 0.06f);
            T.Check($"...which also DROPS the head slightly ({eyeY - left.Y:0.###} m), because it swung on an arc",
                left.Y < eyeY - 0.05f);

            p.ScriptedLean = -1;   // E
            yield return Until(() => p.DebugLeanAngle < -19f, 6);
            var right = EyeLocal(p);
            T.Check($"E moves the eyes to the player's RIGHT ({right.X:0.###} m)", right.X > 0.3f);
            T.Check($"...symmetrically ({left.X:0.###} vs {right.X:0.###})", Mathf.Abs(left.X + right.X) < 0.05f);

            p.ScriptedLean = 0;
            yield return Until(() => Mathf.Abs(p.DebugLeanAngle) < 0.5f, 6);
            T.Check($"releasing returns to the centreline ({EyeLocal(p).X:0.###} m)", Mathf.Abs(EyeLocal(p).X) < 0.05f);

            // ---- TEETH. Prove the peek check above can actually fail, by doing the plausible wrong thing on purpose:
            // roll the CAMERA instead of the pivot. The view tilts by the same 20 degrees and the eyes do not move an
            // inch -- which is the version that ships, looks right in a video, and is useless in a firefight.
            {
                var q = new PlayerController { CaptureMouse = false, Position = new Vector3(20f, 0.2f, 0f) };
                World.AddChild(q);
                yield return Ticks(4);
                var before = EyeLocal(q);
                var rot = q.Camera.Rotation; rot.Z = Mathf.DegToRad(20f); q.Camera.Rotation = rot;
                yield return Ticks(2);
                var after = EyeLocal(q);
                T.Check($"rolling the camera in place tilts the view but moves the eyes {Mathf.Abs(after.X - before.X):0.###} m -- so the peek check has teeth",
                    Mathf.Abs(after.X - before.X) < 0.02f);
            }

            // ---- CROUCHING PEEKS LESS. Falls out of the pivot being on the FLOOR: a lower eye rides a shorter lever.
            // A camera-space offset implementation gets a constant peek at every stance and passes everything above.
            p.ScriptedStance = EPlayerStance.CROUCH;
            p.ScriptedLean = 0;
            yield return Until(() => p.Stance == EPlayerStance.CROUCH && Mathf.Abs(p.DebugLeanAngle) < 0.5f, 8);
            yield return Ticks(40);   // the eye height itself lerps at 4/s -- let it arrive before measuring the lever
            float crouchEye = EyeLocal(p).Y;
            p.ScriptedLean = 1;
            yield return Until(() => Mathf.Abs(p.DebugLeanAngle) > 19f, 6);
            float crouchPeek = -EyeLocal(p).X;
            T.Check($"a crouched lean peeks less than a standing one ({crouchPeek:0.###} m vs {-left.X:0.###} m)",
                crouchPeek > 0.05f && crouchPeek < -left.X - 0.1f);
            T.Check($"...in proportion to the eye height ({crouchEye:0.##} m vs {eyeY:0.##} m)",
                Mathf.Abs(crouchPeek - crouchEye * Mathf.Sin(Mathf.DegToRad(PlayerController.LeanDegrees))) < 0.08f);
            p.ScriptedStance = null;
            p.ScriptedLean = null;

            // ---- A WALL BLOCKS IT. The capsule is swept from the EYES, so the wall has to be up at head height --
            // which is also why the test puts it there rather than at the feet where a floor-level check would pass.
            var floor = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0, Position = new Vector3(0f, -0.5f, 0f) };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(120f, 1f, 120f) } });
            World.AddChild(floor);
            // Well away from the other two: players collide with each other, and a pile of them at the origin shoves
            // itself around, which looks exactly like the wall doing the shoving.
            var w = new PlayerController { CaptureMouse = false, Position = new Vector3(-20f, 0.2f, 0f) };
            World.AddChild(w);
            yield return Ticks(60);
            var stood = w.GlobalPosition;

            var wall = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.4f, 6f, 6f) } });
            World.AddChild(wall);
            // 1.0 m out, so its near face sits at 0.8 m: inside the lean capsule's 1.2 m reach and clear of the player's
            // own collision radius. A wall placed close enough to touch the BODY gets pushed off by the character
            // controller, drifts out of range, and quietly turns this into a test of an empty room.
            wall.GlobalPosition = stood + w.GlobalTransform.Basis.X * -1.0f + Vector3.Up * 2f;
            yield return Ticks(10);
            T.Check($"the wall did not shove the player ({(w.GlobalPosition - stood)} of drift)",
                w.GlobalPosition.DistanceTo(stood) < 0.05f);

            w.ScriptedLean = 1;   // into the wall
            yield return Ticks(10);
            T.Check("a wall at head height blocks the lean into it", w.DebugLeanObstructed && w.DebugLean == 0);
            T.Check($"...so the eyes stay put ({EyeLocal(w).X:0.###} m)", Mathf.Abs(EyeLocal(w).X) < 0.05f);

            w.ScriptedLean = -1;   // away from it
            yield return Until(() => w.DebugLeanAngle < -19f, 6);
            T.Check($"...and leaning AWAY from that same wall still works ({EyeLocal(w).X:0.###} m)",
                !w.DebugLeanObstructed && EyeLocal(w).X > 0.3f);

            // Blocked mid-lean SNAPS upright rather than lerping (PlayerLook.cs:738-741) -- lerping out of a wall means
            // spending a quarter second with your head inside it, which is the peek the check exists to deny.
            w.ScriptedLean = 1;
            yield return Ticks(3);
            T.Check($"...and turning back INTO it is obstructed again (lean {w.DebugLean}, obstructed {w.DebugLeanObstructed}, drift {w.GlobalPosition.DistanceTo(stood):0.###} m)",
                w.DebugLeanObstructed && w.DebugLean == 0);
            T.Check($"a blocked lean snaps to upright rather than easing ({w.DebugLeanAngle:0.##} deg)",
                Mathf.Abs(w.DebugLeanAngle) < 0.001f);

            yield break;
        }
    }
}
