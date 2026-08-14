using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHERE A BULLET STARTS (strawberry: "a bigger thing is making our bullet raycasts come from the PM's eyes, not the
    // camera middle").
    //
    // In FIRST person the camera sits at the eyes, so the two are the same point and the bug is invisible. It only
    // exists in third person -- and it only became reachable at all once the third-person camera moved to its real
    // over-the-shoulder position, 2 m back and a metre to one side. Firing from there means the shot leaves from behind
    // your own shoulder, through anything standing between the camera and you.
    //
    // Source keeps the two jobs apart and this suite checks both halves of that split: the CAMERA decides what you are
    // pointing at, the EYES are where the bullet comes from (UseableGun.cs:962-977, then 1001). Aim from the eyes
    // straight down the look axis instead and the shot misses the crosshair by however far the camera is offset -- so
    // "starts at the eyes" on its own is only half a fix, and the convergence checks below are the other half.
    public sealed class ShotOriginTests : GameTest
    {
        public override string Name => "combat.shot_origin";
        public override double TimeoutSimSeconds => 40;

        static float AngleBetween(Vector3 a, Vector3 b) => Mathf.RadToDeg(a.Normalized().AngleTo(b.Normalized()));

        public override IEnumerable<Step> Run()
        {
            var floor = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0, Position = new Vector3(0f, -0.5f, 0f) };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(400f, 1f, 400f) } });
            World.AddChild(floor);

            var p = new PlayerController { CaptureMouse = false, Position = new Vector3(0f, 0.2f, 0f) };
            World.AddChild(p);
            yield return Ticks(60);
            p.EquipHeldGun("eaglefire");
            p.Ammo = 60;
            yield return Ticks(60);   // let the equip animation finish -- Fire() refuses until IsEquipComplete

            // ---- THE EYES ARE NOT THE CAMERA. First person is where that is easy to miss.
            p.DriveFP = true;
            yield return Ticks(40);
            T.Check($"in FIRST person the eyes and the camera are the same point ({p.EyesWorld.DistanceTo(p.Camera.GlobalPosition):0.###} m apart)",
                p.EyesWorld.DistanceTo(p.Camera.GlobalPosition) < 0.02f);
            T.Check($"...and the eyes are up at head height ({p.EyesWorld.Y - p.GlobalPosition.Y:0.##} m above the feet)",
                p.EyesWorld.Y - p.GlobalPosition.Y > 1.5f);

            p.DriveFP = false;
            yield return Ticks(60);
            float split = p.EyesWorld.DistanceTo(p.Camera.GlobalPosition);
            T.Check($"in THIRD person they are metres apart ({split:0.##} m) -- which is the whole bug", split > 1.5f);

            // ---- A REAL SHOT STARTS AT THE EYES. Read back off the production fire path, not recomputed here.
            p.DebugSetPitch(0f);
            yield return Ticks(10);
            bool fired = p.Fire();
            T.Check("the gun actually fired (otherwise everything below is vacuous)", fired);
            yield return Ticks(1);
            // THE BULLET, not the eye basis. This block asserted `DebugLastShotOrigin` (= the eyes) was within 5cm
            // of the eyes -- trivially true, and silent about the projectile, which spawns 0.43 m away at the muzzle.
            // Measured with a probe before rewriting: asserting the real origin against the old 5cm bound fails at
            // 0.429 m, so the old check could never have caught a wrong bullet origin. It is now split in two:
            // where the AIM MATHS is based (eyes) and where the PROJECTILE leaves (muzzle), which are different
            // points on purpose and were being conflated.
            T.Check($"the aim basis is the eyes ({p.DebugLastShotOrigin.DistanceTo(p.EyesWorld):0.###} m off)",
                p.DebugLastShotOrigin.DistanceTo(p.EyesWorld) < 0.05f);
            // The bullet leaves from the muzzle: forward of the eyes, close to the player, never behind them. The
            // bound is a metre because the muzzle offset is deliberately gun-relative (0.4 m forward, 0.12 m right
            // at the hip lerping to centre on ADS) -- what matters is that it is ON the player, not that it is at
            // one exact point.
            float bulletOff = p.DebugLastBulletOrigin.DistanceTo(p.EyesWorld);
            T.Check($"the BULLET leaves from the muzzle, forward of the eyes ({bulletOff:0.###} m)",
                bulletOff > 0.15f && bulletOff < 1.0f);
            T.Check($"...and in FRONT of the player, not behind ({(p.GlobalTransform.AffineInverse() * p.DebugLastBulletOrigin).Z:0.##} m)",
                (p.GlobalTransform.AffineInverse() * p.DebugLastBulletOrigin).Z < 0f);
            // ...and emphatically NOT at the camera. Stated separately: "near the eyes" and "not at the camera" are the
            // same claim only while the two are far apart, and this is the assertion that names the reported bug.
            T.Check($"...and NOT at the camera ({p.DebugLastBulletOrigin.DistanceTo(p.Camera.GlobalPosition):0.##} m from it)",
                p.DebugLastBulletOrigin.DistanceTo(p.Camera.GlobalPosition) > 1.5f);
            T.Check($"...nor behind the player ({(p.GlobalTransform.AffineInverse() * p.DebugLastBulletOrigin).Z:0.##} m back)",
                (p.GlobalTransform.AffineInverse() * p.DebugLastBulletOrigin).Z < 0.3f);

            // ---- IT STILL HITS WHAT THE CROSSHAIR IS OVER. A pillar placed on the CAMERA's axis, which -- because the
            // camera is offset and toed in 5 degrees -- is NOT on the player's straight-ahead. Firing from the eyes
            // down the raw look axis would sail past it, so this is what makes "start at the eyes" a fix and not a
            // different bug.
            var camPos = p.Camera.GlobalPosition;
            var camFwd = -p.Camera.GlobalBasis.Z;
            var aimPoint = camPos + camFwd * 25f;
            var pillar = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0 };
            pillar.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f, 6f, 0.5f) } });
            pillar.Position = new Vector3(aimPoint.X, 2f, aimPoint.Z);   // positioned BEFORE entering the tree
            World.AddChild(pillar);
            yield return Ticks(20);

            var eyes = p.EyesWorld;
            var wantDir = (pillar.GlobalPosition + Vector3.Up * (aimPoint.Y - 2f) - eyes).Normalized();
            var rawLook = -(new Basis(Vector3.Up, p.Rotation.Y) * new Basis(Vector3.Right, Mathf.DegToRad(p.DebugPitch))).Z;
            float missBy = AngleBetween(rawLook, wantDir);
            T.Check($"the crosshair target is genuinely off the raw look axis ({missBy:0.##} deg) -- so convergence has something to do",
                missBy > 0.5f);

            p.Ammo = 60;
            yield return Ticks(30);   // fire-rate cooldown
            T.Check("the second shot fired", p.Fire());
            yield return Ticks(1);
            float aimErr = AngleBetween(p.DebugLastShotDir, wantDir);
            // THE CAP IS SET FROM THE MEASURED RESIDUAL, not from a round number. It was `aimErr < 1.5f`, and the
            // residual actually runs 1.24 / 1.30 / 1.43 / 1.60 across runs -- so the threshold sat INSIDE its own
            // distribution and the test failed maybe a quarter of the time, in the full suite only, which reads as a
            // regression somewhere else entirely.
            //
            // The residual is not error, it is geometry plus state: the pillar is 0.5 m thick and 25 m away, so the
            // camera ray converges on its FRONT FACE while wantDir points at the centre -- 0.57 deg of the total
            // before anything else -- and the earlier shot in this test leaves accumulated recoil on the aim, whose
            // yaw component is random-signed by design. Neither is a bug and neither is going to zero.
            //
            // So the scale-free RATIO carries the claim (convergence must remove most of an 8 deg miss) and the
            // absolute cap only guards against it drifting off entirely. Both sit clear of the measured spread.
            T.Check($"...and the shot converges on it from the eyes ({aimErr:0.##} deg off, vs {missBy:0.##} without)",
                aimErr < missBy * 0.35f && aimErr < 2.5f);

            // ---- FIRST PERSON IS UNTOUCHED. Source only redirects in third person, and a first-person shot that
            // quietly went through the convergence path would be a regression nothing else here would catch.
            p.DriveFP = true;
            yield return Ticks(60);
            p.Ammo = 60;
            // Sampled BEFORE the shot: recoil drains into _pitchDeg over the following ticks, so a look axis read
            // afterwards is half a degree away from the one the bullet actually left on -- and that is the whole
            // tolerance this check has.
            var fpLook = -(new Basis(Vector3.Up, p.Rotation.Y) * new Basis(Vector3.Right, Mathf.DegToRad(p.DebugPitch))).Z;
            T.Check("a first-person shot fired", p.Fire());
            yield return Ticks(1);
            T.Check($"a first-person shot goes straight down the look axis ({AngleBetween(p.DebugLastShotDir, fpLook):0.###} deg off)",
                AngleBetween(p.DebugLastShotDir, fpLook) < 0.2f);
            T.Check($"...from the camera, which IS the eyes there ({p.DebugLastShotOrigin.DistanceTo(p.Camera.GlobalPosition):0.###} m)",
                p.DebugLastShotOrigin.DistanceTo(p.Camera.GlobalPosition) < 0.05f);

            yield break;
        }
    }
}
