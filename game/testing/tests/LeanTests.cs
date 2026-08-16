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

            // ---- HOW LENIENT THE OBSTRUCTION CHECK IS (strawberry: "make leaning snap-out colliders a little more
            // lenient"). Reach is the whole knob: the capsule is (Reach - Radius) long with Radius caps, so Reach IS
            // the distance from the eye at which something starts refusing the lean.
            T.Check($"the reach is looser than retail's ({PlayerController.LeanReach:0.##} m vs {PlayerController.LeanReachRetail:0.##} m)",
                PlayerController.LeanReach < PlayerController.LeanReachRetail);
            // ...and NOT loose enough to lie. The head really does travel LeanPeek metres; a reach under that plus the
            // head itself would wave you through into geometry, which is a worse failure than the strictness it was
            // loosened from. This is the check that stops "a little more lenient" being applied twice.
            float floorReach = PlayerController.LeanPeek(1.75f) + PlayerController.LeanHeadRadius;
            T.Check($"...but still clears the peek it permits ({PlayerController.LeanReach:0.##} m > {floorReach:0.##} m = {PlayerController.LeanPeek(1.75f):0.##} peek + {PlayerController.LeanHeadRadius:0.##} head)",
                PlayerController.LeanReach > floorReach);

            // ---- THE QUERY ONLY LOOKS AT THE SIDE YOU ARE LEANING TOWARDS (strawberry, in game: "before my left side
            // was against a wall, and it prevented me from leaning right").
            //
            // The source sweeps from the eye with a hemisphere still hanging BACK off it, reaching PlayerStance.RADIUS
            // behind. Retail gets away with that because the player's own body is exactly that wide, so a wall flush
            // against your shoulder sits tangent to the overhang. Our body is 0.35 -- narrower than the source's 0.4 --
            // so the overhang stuck out past our own shoulder and a wall you were touching blocked the lean AWAY from
            // it. The per-side branch in LeanFrom was correct the whole time; the shape was looking the wrong way.
            var span = PlayerController.LeanCapsuleSpan();
            T.Check($"the obstruction capsule starts AT the eye, not behind it (near edge {span.Mid - span.Height * 0.5f:0.###} m)",
                Mathf.Abs(span.Mid - span.Height * 0.5f) < 1e-4f);
            T.Check($"...and ends exactly at the reach ({span.Mid + span.Height * 0.5f:0.###} m vs {PlayerController.LeanReach:0.##})",
                Mathf.Abs((span.Mid + span.Height * 0.5f) - PlayerController.LeanReach) < 1e-4f);

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

            // ---- AND THE VIEW TILTS WITH IT. Retail never rolls the main camera itself (PlayerLook.cs:1643 pins its
            // local Z to 0) -- the roll is inherited from the pivot, so the horizon comes over by the full lean angle.
            // Worth its own check because everything above measures POSITION: a lean re-implemented as a sideways
            // camera offset would peek correctly, keep the horizon dead level, and pass every one of them.
            var camUp = p.Camera.GlobalBasis.Y;
            float rollDeg = Mathf.RadToDeg(camUp.AngleTo(Vector3.Up));
            T.Check($"the horizon rolls by the lean angle ({rollDeg:0.##} deg vs {PlayerController.LeanDegrees:0.##})",
                Mathf.Abs(rollDeg - PlayerController.LeanDegrees) < 1.5f);
            // ...and rolls the correct WAY: leaning left tips the top of your head left, same side as the peek. A roll
            // that went the other way would still be 20 degrees off level and would look like falling over.
            T.Check($"...toward the same side as the peek ({(p.GlobalTransform.Basis.Inverse() * camUp).X:0.###})",
                (p.GlobalTransform.Basis.Inverse() * camUp).X < -0.2f);

            p.ScriptedLean = -1;   // E
            yield return Until(() => p.DebugLeanAngle < -19f, 6);
            var right = EyeLocal(p);
            T.Check($"E moves the eyes to the player's RIGHT ({right.X:0.###} m)", right.X > 0.3f);
            T.Check($"...symmetrically ({left.X:0.###} vs {right.X:0.###})", Mathf.Abs(left.X + right.X) < 0.05f);

            p.ScriptedLean = 0;
            yield return Until(() => Mathf.Abs(p.DebugLeanAngle) < 0.5f, 6);
            T.Check($"releasing returns to the centreline ({EyeLocal(p).X:0.###} m)", Mathf.Abs(EyeLocal(p).X) < 0.05f);
            T.Check($"...and the horizon comes back level ({Mathf.RadToDeg(p.Camera.GlobalBasis.Y.AngleTo(Vector3.Up)):0.##} deg)",
                Mathf.RadToDeg(p.Camera.GlobalBasis.Y.AngleTo(Vector3.Up)) < 1f);

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
            // Start it in the GAP the leniency opened up: a face between the new reach and retail's. Retail refused a
            // lean here; we allow it. Testing the constants alone would not catch a query that ignores them.
            const float halfThick = 0.2f;
            float lenientFace = 0.5f * (PlayerController.LeanReach + PlayerController.LeanReachRetail);   // 1.075 m out
            wall.GlobalPosition = stood + w.GlobalTransform.Basis.X * -(lenientFace + halfThick) + Vector3.Up * 2f;
            yield return Ticks(10);
            T.Check($"the wall did not shove the player ({(w.GlobalPosition - stood)} of drift)",
                w.GlobalPosition.DistanceTo(stood) < 0.05f);

            w.ScriptedLean = 1;
            yield return Until(() => Mathf.Abs(w.DebugLeanAngle) > 19f, 6);
            T.Check($"a wall {lenientFace:0.##} m out no longer blocks -- retail's 1.2 m reach did ({EyeLocal(w).X:0.###} m of peek)",
                !w.DebugLeanObstructed && EyeLocal(w).X < -0.3f);
            T.Check($"...and the head still stops short of it ({lenientFace - -EyeLocal(w).X:0.###} m of clearance left)",
                -EyeLocal(w).X < lenientFace - 0.15f);

            // Now bring it inside the new reach: still blocked, so the loosening did not simply switch the check off.
            w.ScriptedLean = 0;
            yield return Until(() => Mathf.Abs(w.DebugLeanAngle) < 0.5f, 6);
            wall.GlobalPosition = stood + w.GlobalTransform.Basis.X * -(0.65f + halfThick) + Vector3.Up * 2f;
            yield return Ticks(10);
            T.Check($"...the wall still did not shove the player ({(w.GlobalPosition - stood)} of drift)",
                w.GlobalPosition.DistanceTo(stood) < 0.05f);

            w.ScriptedLean = 1;   // into the wall
            yield return Ticks(10);
            T.Check("a wall 0.65 m out at head height still blocks the lean into it", w.DebugLeanObstructed && w.DebugLean == 0);
            T.Check($"...so the eyes stay put ({EyeLocal(w).X:0.###} m)", Mathf.Abs(EyeLocal(w).X) < 0.05f);

            w.ScriptedLean = -1;   // away from it
            yield return Until(() => w.DebugLeanAngle < -19f, 6);
            T.Check($"...and leaning AWAY from that same wall still works ({EyeLocal(w).X:0.###} m)",
                !w.DebugLeanObstructed && EyeLocal(w).X > 0.3f);

            // ---- AND THE REPORTED CASE, END TO END: a wall pressed against the shoulder. Placed as close as the body
            // physically allows, which is the only place it can be for the symptom to show -- the wall above sits
            // 0.65 m out, past where the overhang reached, which is how 39 green checks missed this.
            w.ScriptedLean = 0;
            yield return Until(() => Mathf.Abs(w.DebugLeanAngle) < 0.5f, 6);
            wall.GlobalPosition = stood + w.GlobalTransform.Basis.X * -(0.36f + halfThick) + Vector3.Up * 2f;
            yield return Ticks(10);
            T.Check($"a wall flush to the shoulder did not shove the player ({(w.GlobalPosition - stood)})",
                w.GlobalPosition.DistanceTo(stood) < 0.05f);
            w.ScriptedLean = 1;   // into it
            yield return Ticks(10);
            T.Check("...leaning INTO the shoulder wall is blocked", w.DebugLeanObstructed && w.DebugLean == 0);
            w.ScriptedLean = -1;  // away from it -- the reported bug
            yield return Ticks(40);   // fixed wait, not Until: a broken build should report THIS check by name with its
                                      //  numbers, not an anonymous "UNTIL timed out" that says nothing about the case
            T.Check($"...and leaning AWAY from it is NOT ({EyeLocal(w).X:0.###} m of peek, obstructed {w.DebugLeanObstructed})",
                !w.DebugLeanObstructed && EyeLocal(w).X > 0.3f);

            // Blocked mid-lean EASES back upright rather than snapping. This is master's deliberate override of the
            // source's instant snap (PlayerLook.cs:738-741), made in 12ae0365; the ease travels OUT of the
            // obstruction, so the head-in-wall clip the snap existed to avoid is brief.
            //
            // This check asserted the old snap for a day after the behaviour changed under it, and failed every
            // full sweep in between while reporting the code as broken. It was not: the code was doing exactly what
            // it had been asked to do, and the test was the stale half. Left here as the reason the assertion is
            // written against the CURRENT rule rather than the one the port started from.
            //
            // What each half actually covers, MEASURED rather than asserted from reading the code:
            //   - "still tilted 3 ticks in" is the one with teeth for this rule. Restoring the instant snap makes
            //     it fail at 0.00 deg. It is the whole difference between the old behaviour and the new one.
            //   - "upright by 3 s" pins that the ease COMPLETES, not that obstruction drives it. It does not
            //     isolate the obstructed path at all: LeanFrom returns lean 0 on exactly the branches where it
            //     raises `obstructed`, so `target` is already 0 whenever `_leanObstructed` is set, and the
            //     `_leanObstructed ? 0f : target` in ApplyLean is belt-and-braces. Deleting that branch entirely
            //     leaves this suite green -- checked, not assumed. Worth knowing before anyone reads a pass here
            //     as evidence that the obstruction wiring works; the check above it is what proves that.
            w.ScriptedLean = 1;
            yield return Ticks(3);
            T.Check($"...and turning back INTO it is obstructed again (lean {w.DebugLean}, obstructed {w.DebugLeanObstructed}, drift {w.GlobalPosition.DistanceTo(stood):0.###} m)",
                w.DebugLeanObstructed && w.DebugLean == 0);
            float easing = w.DebugLeanAngle;
            T.Check($"a blocked lean EASES out instead of snapping -- still tilted 3 ticks in ({easing:0.##} deg)",
                Mathf.Abs(easing) > 1f);
            yield return Ticks(150);   // LeanLerp is 4/s, so ~20 deg decays to far under a tenth of one over 3 s
            T.Check($"...and is upright again by 3 s ({easing:0.##} -> {w.DebugLeanAngle:0.###} deg)",
                Mathf.Abs(w.DebugLeanAngle) < 0.5f);

            yield break;
        }
    }
}
