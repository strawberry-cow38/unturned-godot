using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Step-up behaviour (strawberry 2026-08-27: "i dont want an instant zoop upwards ... its a smooth 'ramp'
    // type thing. not a sharp step"). Three defects, one test each:
    //   1. the rise was quantised to StepHeight/4, so the SMALLEST possible step was 0.125 m -- a 2 cm lip
    //      lifted you 12.5 cm and FloorSnapLength dragged the rest back, which is half the felt jolt. The
    //      `need < MinStepHeight` guard under it could never fire, because 0.125 > 0.07: dead code.
    //   2. the slope guard read the FIRST CONTACT normal, which on a capsule is usually an edge -- steep on
    //      ground you could have walked. That is the "erroneously triggered on slightly steep terrain" report.
    //   3. nothing checked there was anything to LAND on, so you could step up over a gap into mid-air.
    static class StepRig
    {
        public static StaticBody3D Box(Node3D world, Vector3 center, Vector3 size, float pitchDeg = 0f)
        {
            var b = new StaticBody3D { CollisionLayer = 1 << 0 };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            world.AddChild(b);
            b.GlobalTransform = new Transform3D(
                Basis.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(pitchDeg))), center);
            return b;
        }
        /// Point the player at +X and hold forward. Caller yields the ticks.
        public static void WalkForward(PlayerController p)
        {
            p.GlobalRotation = new Vector3(0f, -Mathf.Pi / 2f, 0f);   // face +X
            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
        }
    }

    // A real curb must lift by the curb's height, not by a quantised 0.125 / 0.25 / 0.5.
    public class StepUpRisesOnlyWhatItNeeds : GameTest
    {
        public override string Name => "step.rise_matches_the_obstacle";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            // 0.30, chosen for two reasons: a 0.3-radius capsule rolls over anything much smaller on its
            // own curvature (a 0.15 curb never invoked StepUp at all), and the old StepHeight/4 sampler
            // would have answered 0.375 here -- far enough outside the tolerance to fail loudly.
            const float curb = 0.30f;
            // Wide on purpose: a 4 m slab gets walked clean off the far side inside the test window, and the
            // final position then measures the ground beyond rather than the step.
            StepRig.Box(World, new Vector3(20f, curb / 2f, 0f), new Vector3(36f, curb, 8f));
            var p = Rigs.Player(World, new Vector3(0f, 1.2f, 0f));
            yield return Ticks(5);
            p.StepUpCount = 0;
            StepRig.WalkForward(p);
            yield return Ticks(90);   // ~4 m: onto the slab and no further -- 180 walks clean off its far side
            p.ScriptedInput = null;

            T.Check($"stepped the curb at all (count={p.StepUpCount})", p.StepUpCount > 0);
            // The old sampler could only ever return 0.125, 0.25, 0.375 or 0.5. 0.15 is none of those, so a
            // regression to quantised rises fails here rather than passing on the nearest bucket.
            // `need` is the SMALLEST lift that clears, not the obstacle's height: a 0.35-radius capsule's
            // rounded bottom does part of the climb, so a 0.30 curb needs ~0.18. That is the whole point --
            // the old sampler could only answer in 0.125 steps and would have lifted 0.375 for this, more
            // than the curb is tall, with FloorSnapLength dragging the surplus back down as the felt jolt.
            T.Check($"rise {p.LastStepRise:0.###} m is well under the old 0.375 bucket", p.LastStepRise < 0.30f);
            T.Check($"and is not a StepHeight/4 multiple ({p.LastStepRise:0.###})",
                    Mathf.Abs(p.LastStepRise / 0.125f - Mathf.Round(p.LastStepRise / 0.125f)) > 0.05f);
            T.Check($"and actually got the player up (y={p.GlobalPosition.Y:0.##} > curb {curb})", p.GlobalPosition.Y > curb - 0.05f);
        }
    }

    // A slope move_and_slide can walk is not a step, however bumpy the first contact looks.
    public class StepUpIgnoresWalkableSlope : GameTest
    {
        public override string Name => "step.walkable_slope_is_not_a_step";
        public override IEnumerable<Step> Run()
        {
            // The ramp is the ONLY floor: an inclined slab the player starts standing on. Sinking a ramp
            // into a ground plane instead leaves a wedge where it emerges, and the player snags on that
            // rather than walking the slope -- which tests the wedge, not the slope.
            StepRig.Box(World, new Vector3(0f, 0f, 0f), new Vector3(40f, 1f, 12f), 20f);
            var p = Rigs.Player(World, new Vector3(0f, 1.2f, 0f));
            yield return Ticks(5);
            float y0 = p.GlobalPosition.Y;
            p.StepUpCount = 0;
            StepRig.WalkForward(p);
            yield return Ticks(240);
            p.ScriptedInput = null;

            T.Check($"climbed the ramp (y {y0:0.##} -> {p.GlobalPosition.Y:0.##})", p.GlobalPosition.Y > y0 + 0.2f);
            T.Check($"and did it by WALKING, not stepping (count={p.StepUpCount})", p.StepUpCount == 0);
        }
    }

    // Nothing to land on = not a step. Otherwise you rise over a lip into thin air and fall straight back.
    public class StepUpRefusesMidAir : GameTest
    {
        public override string Name => "step.gap_is_not_a_step";
        public override IEnumerable<Step> Run()
        {
            // A floor that STOPS, with a thin lip at its edge: raised clears the lip, but there is no ground
            // under the landing point. The infinite plane is deliberately absent here.
            StepRig.Box(World, new Vector3(0f, -0.5f, 0f), new Vector3(8f, 1f, 8f));         // floor, ends at x=4
            // The plate stands 0.2 m PAST the floor edge. On the edge it would be a legitimate step -- the
            // landing point is still over solid floor -- which is what the first version of this test got
            // wrong. Out here the capsule still reaches it (radius 0.3) but there is nothing underneath.
            StepRig.Box(World, new Vector3(4.2f, 0.15f, 0f), new Vector3(0.06f, 0.3f, 8f));
            var p = Rigs.Player(World, new Vector3(0f, 1.2f, 0f));
            yield return Ticks(5);
            p.StepUpCount = 0;
            StepRig.WalkForward(p);
            yield return Ticks(180);
            p.ScriptedInput = null;

            T.Check($"never stepped onto nothing (count={p.StepUpCount}, rise={p.LastStepRise:0.###})",
                    p.StepUpCount == 0);
        }
    }
}
