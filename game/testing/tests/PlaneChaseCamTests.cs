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
            foreach (var (pitch, roll) in new[] { (0f, 0f), (-20f, 0f), (-45f, 0f), (-60f, 0f), (30f, 0f), (0f, 50f) })
            {
                var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(pitch), 0f, Mathf.DegToRad(roll)), EulerOrder.Yxz);
                var vt = new Transform3D(basis, new Vector3(0f, 400f, 0f));   // high up, so no ground pulls the cam in
                v.GlobalTransform = vt;
                p.PositionDriveCamForTest(v, vt);
                samples.Add(($"pitch {pitch:0} roll {roll:0}", pitch, roll, AboveAxisDeg(vt, cam.GlobalPosition)));
            }

            foreach (var s in samples) GD.Print($"[planecam] {s.name}: {s.deg:0.0} deg above the airframe axis");

            // THE CLAIM. Not "it equals 17.5" -- that is the formula again -- but that the angle does not TRACK
            // the pitch, which is the bug. Level is the reference; every other attitude must stay near it.
            float level = samples[0].deg;
            T.Check($"level flight sits behind and slightly above, not overhead ({level:0.0} deg)",
                level > 5f && level < 30f);
            foreach (var s in samples)
                T.Check($"[{s.name}] holds the level framing ({s.deg:0.0} vs {level:0.0} deg)",
                    Mathf.Abs(s.deg - level) < 4f);

            // ...and the specific regression, stated as the thing that was wrong: at a 60 degree dive the old
            // camera was ~85 degrees above the axis. Anything near that is the world-stable cam back again.
            var dive = samples.Find(x => Mathf.IsEqualApprox(x.pitch, -60f));
            T.Check($"a 60 degree dive is not looking down on the wing ({dive.deg:0.0} deg, was ~85)",
                dive.deg < 40f);

            // ROLL must not tip the view: the roll-free up is what keeps the horizon from spinning.
            var rolled = samples.Find(x => Mathf.IsEqualApprox(x.roll, 50f));
            T.Check($"a 50 degree roll does not swing the chase angle ({rolled.deg:0.0} vs {level:0.0} deg)",
                Mathf.Abs(rolled.deg - level) < 4f);
            yield break;
        }
    }
}
