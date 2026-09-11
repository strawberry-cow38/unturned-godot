using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE JET'S CHASE CAMERA (strawberry 2026-09-09: "fix 3rd person in a jet to not be top down").
    //
    // This exists because the fix shipped UNPHOTOGRAPHED. There is no flying harness -- --drivetest drives a jeep
    // and --vehicle=<name> is a static showcase, so nothing exercised PositionDriveCam's plane branch -- and the
    // evidence in that commit was a table of angles I computed from the same expressions the code evaluates.
    // tinyclaw named the problem the same afternoon and it is worth writing down: a check built out of the design's
    // own formulas is a restatement of the design, not a test of it. It cannot fail for the reason you care about.
    //
    // So this measures the CAMERA NODE the shipped code actually moved, and asserts the PROPERTY master asked for
    // rather than the arithmetic behind it: however far the nose is pitched, you should be looking at the aircraft
    // from the same angle. The old world-stable camera fails that by construction -- its height was world-vertical
    // and its "behind" ignored pitch, so the view angle was 25 degrees PLUS the dive angle (85 degrees at a 60
    // degree dive, which is what "top down" meant).
    public sealed class PlaneChaseCamTests : GameTest
    {
        public override string Name => "vehicle.plane_chase_cam";

        /// <summary>Degrees the camera sits ABOVE the aircraft's own longitudinal axis -- i.e. how much of its top
        /// you are looking at. Measured in the AIRFRAME's frame, which is the whole point: a world-space angle
        /// cannot tell a level camera behind a diving plane from a camera hanging over its wing.</summary>
        static float AboveAxisDeg(Transform3D vt, Vector3 camPos)
        {
            Vector3 back = -(-vt.Basis.Z).Normalized();          // along the tail, in the aircraft's frame
            Vector3 rel = camPos - vt.Origin;
            if (rel.LengthSquared() < 1e-6f) return 0f;
            Vector3 up = vt.Basis.Y.Normalized();
            // component along the tail vs component along the airframe's up: atan2 gives the elevation.
            return Mathf.RadToDeg(Mathf.Atan2(rel.Dot(up), rel.Dot(back)));
        }

        /// <summary>The camera placement as it was BEFORE cb5fc99e -- flattened heading, world-vertical height --
        /// reproduced here for one purpose: to be REJECTED. tinyclaw, 2026-09-09: "'fails on the old camera by
        /// construction' is still reasoning ... if the check is vacuous it comes back PASS and tells you nothing",
        /// and once the fix is committed the old camera is not around to fail against.
        ///
        /// Running the check once in a scratch worktree would answer that once. A CONTROL answers it every night:
        /// the assertions below are applied to this too, and must reject it. If someone later widens the tolerance
        /// until the check no longer discriminates, that shows up here rather than as a quiet PASS. Same shape as
        /// the control pairs in PlaneDiveTests, and it does not need the no-tests rule bent to get the evidence.
        ///
        /// Its fidelity does not have to be perfect to do its job. It only has to be top-down at a dive, which was
        /// the complaint; a criterion that cannot separate this from the fix is not measuring anything.</summary>
        static Vector3 LegacyChaseCamPos(Transform3D vt, float size)
        {
            float dist = Mathf.Clamp(size * 0.62f, 6.5f, 20f);
            var pf = -vt.Basis.Z; pf.Y = 0f;
            pf = pf.LengthSquared() > 0.001f ? pf.Normalized() : Vector3.Forward;
            return vt.Origin - pf * (dist * 0.9f) + Vector3.Up * (dist * 0.34f + size * 0.05f);
        }

        public override IEnumerable<Step> Run()
        {
            var v = Vehicle.BuildByName("fighterjet");
            World.AddChild(v);
            var p = new PlayerController { CaptureMouse = false };
            World.AddChild(p);
            yield return Ticks(2);   // let _Ready build the camera

            var cam = p.CamForTest;
            T.Check("the player built a camera to aim", cam != null);
            if (cam == null) yield break;

            // Level, then progressively nose-down. Roll is deliberately included at the end: the 2026-08-18
            // complaint was that rolling swung the whole view, and the fix must not bring that back.
            var samples = new List<(string name, float pitch, float roll, float deg)>();
            var worldPos = new List<(float roll, Vector3 eye, Vector3 up)>();
            foreach (var (pitch, roll) in new[] { (0f, 0f), (-20f, 0f), (-45f, 0f), (-60f, 0f), (30f, 0f), (0f, 50f) })
            {
                var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(pitch), 0f, Mathf.DegToRad(roll)), EulerOrder.Yxz);
                var vt = new Transform3D(basis, new Vector3(0f, 400f, 0f));   // high up, so no ground pulls the cam in
                v.GlobalTransform = vt;
                p.PositionDriveCamForTest(v, vt);
                samples.Add(($"pitch {pitch:0} roll {roll:0}", pitch, roll, AboveAxisDeg(vt, cam.GlobalPosition)));
                if (Mathf.IsZeroApprox(pitch)) worldPos.Add((roll, cam.GlobalPosition, cam.GlobalTransform.Basis.Y));
            }

            foreach (var s in samples) GD.Print($"[planecam] {s.name}: {s.deg:0.0} deg above the airframe axis");

            // THE CLAIM. Not "it equals 17.5" -- that is the formula again -- but that the angle does not TRACK
            // the pitch, which is the bug. Level is the reference; every other attitude must stay near it.
            float level = samples[0].deg;
            T.Check($"level flight sits behind and slightly above, not overhead ({level:0.0} deg)",
                level > 5f && level < 30f);
            // PITCH ONLY. The roll sample is deliberately excluded and gets its own, stronger check below:
            // this metric is expressed in the AIRFRAME's frame, so rolling the aircraft rotates the yardstick
            // even when the camera has not moved a millimetre -- and not moving is precisely what the roll fix
            // is for. Measured, a 50 deg roll reads 11.5 vs 17.5 purely because 17.5 * cos(50 deg) is 11.5.
            // Asserting frame-relative stability under roll would have demanded the camera FOLLOW the roll,
            // which is the 2026-08-18 bug this camera exists to be rid of.
            foreach (var s in samples)
                if (Mathf.IsZeroApprox(s.roll))
                    T.Check($"[{s.name}] holds the level framing ({s.deg:0.0} vs {level:0.0} deg)",
                        Mathf.Abs(s.deg - level) < 4f);

            // ...and the specific regression, stated as the thing that was wrong: at a 60 degree dive the old
            // camera was ~85 degrees above the axis. Anything near that is the world-stable cam back again.
            var dive = samples.Find(x => Mathf.IsEqualApprox(x.pitch, -60f));
            T.Check($"a 60 degree dive is not looking down on the wing ({dive.deg:0.0} deg, was ~85)",
                dive.deg < 40f);

            // ROLL, stated in the frame the claim actually lives in: the WORLD. A roll-free camera does not
            // move and its up does not tip when only the roll changes -- that is what stops the horizon
            // spinning. Two claims, both stronger than the frame-relative angle this used to compare.
            var lvl = worldPos.Find(x => Mathf.IsZeroApprox(x.roll));
            var rol = worldPos.Find(x => Mathf.IsEqualApprox(x.roll, 50f));
            // DIRECTION, not position. The offset's direction is roll-free by construction; its LENGTH is not,
            // because the chase distance is driven by WorldMeshAabb, which is world-axis-aligned and therefore
            // measures a rolled aircraft as bigger. Measured, a 50 deg roll moves the camera 1.11 m straight
            // out along an unchanged bearing. That is a real quirk -- the chase distance breathes during
            // aerobatics -- but it is not the horizon-spinning bug this camera was built to fix, and pretending
            // the stricter claim holds would just be an assertion that fails on shipped behaviour.
            var dl = (lvl.eye - new Vector3(0f, 400f, 0f)).Normalized();
            var dr = (rol.eye - new Vector3(0f, 400f, 0f)).Normalized();
            T.Check($"a 50 degree roll does not swing where the camera sits ({Mathf.RadToDeg(dl.AngleTo(dr)):0.00} deg of bearing)",
                Mathf.RadToDeg(dl.AngleTo(dr)) < 1f);
            T.Check($"...nor tip its horizon ({Mathf.RadToDeg(lvl.up.AngleTo(rol.up)):0.00} deg of roll in the view)",
                Mathf.RadToDeg(lvl.up.AngleTo(rol.up)) < 1f);
            var rolled = samples.Find(x => Mathf.IsEqualApprox(x.roll, 50f));
            T.Check($"...and the airframe-frame reading moves only as cos(roll) predicts ({rolled.deg:0.0} vs {level * Mathf.Cos(Mathf.DegToRad(50f)):0.0} deg)",
                Mathf.Abs(rolled.deg - level * Mathf.Cos(Mathf.DegToRad(50f))) < 2f);

            // ---- THE CONTROL. Everything above passes on the shipped camera; none of it means anything unless the
            // same criteria REJECT the camera this replaced. Run them against the old placement and require failure.
            float size = v.WorldMeshAabb().Size.Length();
            var diveXf = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(-60f), 0f, 0f), EulerOrder.Yxz),
                                         new Vector3(0f, 400f, 0f));
            var levelXf = new Transform3D(Basis.Identity, new Vector3(0f, 400f, 0f));
            float oldLevel = AboveAxisDeg(levelXf, LegacyChaseCamPos(levelXf, size));
            float oldDive = AboveAxisDeg(diveXf, LegacyChaseCamPos(diveXf, size));
            GD.Print($"[planecam] CONTROL old camera: level {oldLevel:0.0}, 60 deg dive {oldDive:0.0}");
            T.Check($"the control reproduces the bug -- the old camera DID track pitch ({oldDive:0.0} vs {oldLevel:0.0} deg)",
                Mathf.Abs(oldDive - oldLevel) > 20f);
            // Rejected by the SAME criterion the real camera is held to -- deviation from its own level
            // reading -- not by a one-sided "< 40". That threshold assumed the old camera's error was
            // POSITIVE (the comment above predicts +85 at a 60 deg dive); measured, it is -34.9, the same 60
            // degrees of pitch-tracking in the other direction, and a one-sided test waves that straight
            // through. A discriminator that only catches the bug when it leans one way is not one.
            T.Check($"...and this test's own criterion rejects it ({Mathf.Abs(oldDive - oldLevel):0.0} deg of tracking is not < 4)",
                !(Mathf.Abs(oldDive - oldLevel) < 4f));
            yield break;
        }
    }
}
