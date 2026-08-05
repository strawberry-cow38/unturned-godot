using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE THIRD-PERSON CAMERA (strawberry: "fix the 3rd person camera to be source accurate. Q & E switch which
    // shoulder of OTS cam", "3p just.. sucks", "we tilt the cam as if our cam is our head, not focusing on the
    // playermodel").
    //
    // That last sentence names the old bug precisely, and it is the one this suite is built around. The chase cam sat
    // at a FIXED offset behind the player and pitched in place, so looking up or down rotated the view about a
    // stationary point and slid the character out of frame. Source computes the offset in the CAMERA's own frame
    // (PlayerLook.cs:1799), so pitching down swings the camera up and back and the player stays in shot. The camera
    // ORBITS; it does not merely tilt.
    //
    // "Does the camera sit behind the player" passes under both. So the checks here sweep the PITCH and ask whether the
    // player is still in view at the ends of it -- the only formulation that separates an orbit from a tilt.
    public sealed class ThirdPersonCamTests : GameTest
    {
        public override string Name => "player.third_person_cam";
        public override double TimeoutSimSeconds => 40;

        // Is the player actually visible? Asked of the real frustum rather than an angle threshold: this camera sits
        // 2 m away and over a metre to one side, so the chest is legitimately ~40 degrees off the middle of the shot
        // and any hand-picked angle cap is a guess about the FOV rather than a statement about being in view.
        static bool PlayerInShot(PlayerController p)
            => p.Camera.IsPositionInFrustum(p.GlobalPosition + Vector3.Up * 1.0f);

        // Kept for the failure text -- a number to read when the frustum check goes red.
        static float OffAxisDeg(PlayerController p)
        {
            var chest = p.GlobalPosition + Vector3.Up * 1.0f;
            var toChest = chest - p.Camera.GlobalPosition;
            if (toChest.LengthSquared() < 1e-6f) return 0f;
            return Mathf.RadToDeg((-p.Camera.GlobalBasis.Z).AngleTo(toChest.Normalized()));
        }

        public override IEnumerable<Step> Run()
        {
            // ---- THE OFFSET SHAPE, engine-free. Normalised, so the three weights choose the ANGLE the camera sits at
            // and TpLength alone sets the distance -- change one weight and the camera swings without getting nearer.
            var off = PlayerController.ThirdPersonOffsetLocal(1f);
            T.Check($"the offset direction is a unit vector ({off.Length():0.###})", Mathf.Abs(off.Length() - 1f) < 1e-3f);
            T.Check($"...mostly behind ({off.Z:0.##} back vs {off.Y:0.##} up)", off.Z > 0.6f && off.Y > 0.05f);
            T.Check($"...and offset to the RIGHT at shoulder +1 ({off.X:0.##})", off.X > 0.3f);
            T.Check($"...mirrored at shoulder -1 ({PlayerController.ThirdPersonOffsetLocal(-1f).X:0.##})",
                Mathf.IsEqualApprox(PlayerController.ThirdPersonOffsetLocal(-1f).X, -off.X));
            T.Check($"...and centred at shoulder 0 ({PlayerController.ThirdPersonOffsetLocal(0f).X:0.##})",
                Mathf.Abs(PlayerController.ThirdPersonOffsetLocal(0f).X) < 1e-4f);

            // The pivot is CLAMPED into the collision capsule so the sweep sphere cannot start poking out of the top of
            // you (PlayerLook.cs:1238-1240). Worth pinning because it makes the standing 3P pivot 1.605 -- NOT the 1.75
            // the first-person eye sits at, which is the number you would reach for if you were guessing.
            float stand = PlayerController.ThirdPersonPivot(1.75f, PlayerMovementDef.HeightForStance(EPlayerStance.STAND));
            T.Check($"a standing 3P pivot clamps below the eye ({stand:0.###} m, eye 1.75)", stand < 1.75f && stand > 1.4f);
            T.Check($"...specifically to capsule - sweep radius ({stand:0.###} vs {2f - 0.39f - 0.005f:0.###})",
                Mathf.Abs(stand - (2f - PlayerController.TpSweepRadius - 0.005f)) < 1e-3f);
            float prone = PlayerController.ThirdPersonPivot(0.35f, PlayerMovementDef.HeightForStance(EPlayerStance.PRONE));
            T.Check($"a prone pivot is lifted OFF the floor rather than left at the eye ({prone:0.###} m, eye 0.35)",
                prone > 0.35f);

            // ---- LIVE. Floor, so nobody falls; open sky around, so the sweep is not what is being measured yet.
            var floor = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0, Position = new Vector3(0f, -0.5f, 0f) };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200f, 1f, 200f) } });
            World.AddChild(floor);

            var p = new PlayerController { CaptureMouse = false, Position = new Vector3(0f, 0.2f, 0f) };
            World.AddChild(p);
            p.DriveFP = false;   // third person
            yield return Ticks(80);

            T.Check($"the camera sits {PlayerController.TpLength:0.#} m off the PIVOT in the open ({p.DebugTpOrigin.DistanceTo(p.Camera.GlobalPosition):0.##} m)",
                Mathf.Abs(p.DebugTpOrigin.DistanceTo(p.Camera.GlobalPosition) - PlayerController.TpLength) < 0.1f);
            T.Check($"...behind them ({(p.GlobalTransform.AffineInverse() * p.Camera.GlobalPosition).Z:0.##} m back)",
                (p.GlobalTransform.AffineInverse() * p.Camera.GlobalPosition).Z > 0.8f);
            T.Check($"...over the RIGHT shoulder by default ({(p.GlobalTransform.AffineInverse() * p.Camera.GlobalPosition).X:0.##} m)",
                (p.GlobalTransform.AffineInverse() * p.Camera.GlobalPosition).X > 0.3f);
            T.Check($"...and the player is in shot ({OffAxisDeg(p):0.#} deg off the middle)", PlayerInShot(p));

            // ---- THE ORBIT. Sweep the pitch and check the player stays in frame at both ends. A camera that pitches
            // in place keeps its POSITION and swings its aim off the player; an orbiting one moves instead.
            var levelCam = p.Camera.GlobalPosition;
            p.DebugSetPitch(-55f);   // looking down
            yield return Ticks(10);
            float downOff = OffAxisDeg(p);
            var downCam = p.Camera.GlobalPosition;
            T.Check($"looking DOWN keeps the player in shot ({downOff:0.#} deg off the middle)", PlayerInShot(p));
            T.Check($"...because the camera MOVED rather than just tilting ({levelCam.DistanceTo(downCam):0.##} m)",
                levelCam.DistanceTo(downCam) > 0.4f);
            T.Check($"...specifically upward, over the shoulder ({downCam.Y - levelCam.Y:0.##} m higher)",
                downCam.Y > levelCam.Y + 0.15f);

            p.DebugSetPitch(55f);   // looking up
            yield return Ticks(10);
            var upCam = p.Camera.GlobalPosition;
            T.Check($"...with the camera dropped instead ({upCam.Y - levelCam.Y:0.##} m)", upCam.Y < levelCam.Y - 0.15f);

            // How far you can pitch before the character genuinely leaves the frame. MEASURED rather than asserted at a
            // hand-picked angle: source's offset is the same formula at every pitch, so at a steep enough look-up the
            // camera has dropped below you and is aiming over your head -- retail does that too and it is not a bug to
            // "fix". What matters is that the usable range is wide, so the range is what gets pinned.
            float lo = 0f, hi = 0f;
            for (float a = 0f; a >= -85f; a -= 5f) { p.DebugSetPitch(a); yield return Ticks(6); if (!PlayerInShot(p)) break; lo = a; }
            for (float a = 0f; a <= 85f; a += 5f) { p.DebugSetPitch(a); yield return Ticks(6); if (!PlayerInShot(p)) break; hi = a; }
            // Asymmetric, and correctly so: looking DOWN swings the camera up and back, which keeps you framed all the
            // way to the pitch limit, while looking UP drops the camera below you and eventually aims it over your
            // head. Source's offset is the same formula at every pitch, so retail does this too -- it is not a bug to
            // "fix". The exact angles depend on the camera FOV, so the margins here are loose on purpose.
            T.Check($"looking DOWN keeps you framed to the pitch limit ({lo:0} deg)", lo <= -60f);
            T.Check($"...and looking up holds for a usable range too (+{hi:0} deg)", hi >= 15f);
            p.DebugSetPitch(0f);
            yield return Ticks(10);

            // ---- Q AND E SWAP SHOULDERS, WITH SOURCE'S TAP SUPPRESSION (strawberry: "do it the src way, tap
            // supression. not tap/hold state"). There is no tap-vs-hold MODE and nothing latches: the key is polled
            // every tick as always, and the window merely withholds the lean for 75 ms after a press in third person.
            // So a quick tap resolves as a shoulder swap alone, and simply keeping the key down leans with no second
            // input. Both halves are checked, because a build that only ever swaps would pass the first on its own.
            T.Check("source's tap suppression is on", PlayerController.ShoulderTapSuppressesLean);
            T.Check("the camera starts on the right shoulder", !p.DebugCamOnLeftSide);
            float rightX = (p.GlobalTransform.AffineInverse() * p.Camera.GlobalPosition).X;

            p.ScriptedLean = 1;             // TAP Q -- two ticks is 0.04 s, inside the 0.075 s window
            yield return Ticks(2);
            p.ScriptedLean = 0;
            T.Check("a TAP of Q puts the camera on the left shoulder", p.DebugCamOnLeftSide);
            T.Check($"...without leaning ({p.DebugLeanAngle:0.##} deg)", Mathf.Abs(p.DebugLeanAngle) < 1f);
            yield return Ticks(40);
            float leftX = (p.GlobalTransform.AffineInverse() * p.Camera.GlobalPosition).X;
            T.Check($"...and the camera really crossed over ({rightX:0.##} -> {leftX:0.##})", leftX < -0.3f && rightX > 0.3f);
            T.Check($"...symmetrically ({rightX + leftX:0.###} of asymmetry)", Mathf.Abs(rightX + leftX) < 0.15f);
            T.Check($"...with the player still in shot ({OffAxisDeg(p):0.#} deg)", PlayerInShot(p));

            p.ScriptedLean = -1;            // TAP E -- back to the right
            yield return Ticks(2);
            p.ScriptedLean = 0;
            T.Check("a TAP of E puts it back on the right", !p.DebugCamOnLeftSide);
            T.Check($"...also without leaning ({p.DebugLeanAngle:0.##} deg)", Mathf.Abs(p.DebugLeanAngle) < 1f);
            yield return Ticks(40);

            // ...and simply KEEPING the key down leans, with no second input. This is the half that stops "tap
            // suppression" from silently becoming "the lean key does nothing in third person".
            p.ScriptedLean = 1;
            yield return Ticks(30);
            T.Check($"holding Q swaps AND leans ({p.DebugLeanAngle:0.##} deg, left side {p.DebugCamOnLeftSide})",
                p.DebugCamOnLeftSide && Mathf.Abs(p.DebugLeanAngle) > 5f);
            p.ScriptedLean = 0;
            yield return Ticks(40);

            // ---- FIRST PERSON SKIPS THE WINDOW ENTIRELY. Source gates it on perspective (`perspective == FIRST ||`),
            // so a tap in first person leans at once -- a first-person lean that waited 75 ms every time would feel
            // broken, and it is the same key.
            p.DriveFP = true;
            yield return Ticks(20);
            p.ScriptedLean = 1;
            yield return Ticks(2);
            T.Check($"in FIRST person a tap leans straight away (lean {p.DebugLean})", p.DebugLean == 1);
            p.ScriptedLean = 0;
            p.DriveFP = false;
            yield return Ticks(40);

            // ---- A WALL PULLS THE CAMERA IN rather than letting it clip through (sphereCastCamera takes the CLOSEST
            // hit). Without this the third-person camera happily sits inside geometry and you see through walls.
            // Positioned BEFORE it enters the tree. Added first and moved after, a body spends one physics frame at the
            // sandbox origin -- which is exactly where the player is standing -- and shoves them half a metre before the
            // move lands. The wall then looks innocent at its final spot while the player is somewhere else.
            var basePos = p.GlobalPosition;
            var wall = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(12f, 8f, 0.4f) } });
            // Far enough back not to touch the BODY (radius 0.35) but inside the camera's 1.65 m of travel.
            wall.Position = basePos + p.GlobalTransform.Basis.Z * 1.6f + Vector3.Up * 2f;   // +Z is BEHIND
            World.AddChild(wall);
            yield return Ticks(20);
            T.Check($"the wall did not shove the player ({(p.GlobalPosition - basePos)} of drift)",
                p.GlobalPosition.DistanceTo(basePos) < 0.05f);
            // Diagnostic: prove the wall is actually in the camera's path before believing anything about the sweep.
            var org = p.DebugTpOrigin;
            var toCam = (p.Camera.GlobalPosition - org).Normalized();
            var rq = PhysicsRayQueryParameters3D.Create(org, org + toCam * 2f);
            rq.CollisionMask = (1u << 0) | (1u << 5) | (1u << 6);
            rq.Exclude = new Godot.Collections.Array<Rid> { p.GetRid() };
            var rayHit = p.GetWorld3D().DirectSpaceState.IntersectRay(rq);
            T.Check($"the wall is genuinely in the camera's path (ray hit: {rayHit.Count > 0}, wall at {wall.GlobalPosition}, cam at {p.Camera.GlobalPosition})",
                rayHit.Count > 0);

            float pinched = p.DebugTpOrigin.DistanceTo(p.Camera.GlobalPosition);
            T.Check($"a wall behind pulls the camera in ({pinched:0.##} m, was ~{PlayerController.TpLength:0.#}; sweep fraction {p.DebugTpSweepFraction:0.###})",
                pinched < PlayerController.TpLength - 0.3f);
            T.Check($"...but not through the player ({pinched:0.##} m)", pinched > 0.2f);

            yield break;
        }
    }
}
