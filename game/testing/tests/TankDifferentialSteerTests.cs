using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // TANK DIFFERENTIAL / SKID STEER (master "actual tank controls"). The --vehicle rig harness advances its
    // _frame counter far faster than the physics sim actually steps (a rig capture at "frame 340" has only ~15
    // physics ticks behind it), so a driving TURN never simulates in a screenshot -- yaw reads 0 the whole
    // capture even when the wiring is right. This steps physics DETERMINISTICALLY instead: forward drive
    // translates and holds heading (both tracks equal); A on its own counter-rotates the two tracks and swings
    // the hull in place. The gap between those two is the whole feature, and it fails if Drive()'s _tracked
    // branch is removed (the tank would then steer by a wheel angle it does not have -> no turn at all).
    public sealed class TankDifferentialSteerTests : GameTest
    {
        public override string Name => "tank.differential_steer";

        public override IEnumerable<Step> Run()
        {
            var ground = new StaticBody3D();
            ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
            World.AddChild(ground);

            var tank = Vehicle.BuildTank(0);
            World.AddChild(tank);
            tank.Position = new Vector3(0f, 1.0f, 0f);
            tank.EngineOn = true;
            yield return Step.Ticks(45);   // drop + settle onto the plane -> a true standstill
            // it must settle UPRIGHT on its wheels, not tipped over or riding on a scraping collision box.
            T.Check($"settles upright on its wheels ({tank.GlobalTransform.Basis.Y.Dot(Vector3.Up):0.00})", tank.GlobalTransform.Basis.Y.Dot(Vector3.Up) > 0.95f);

            // PIVOT FROM REST: A on its own (throttle 0, steer -1) -> left track reverses + right track drives, a
            // couple that spins the hull with ~no net translation. Tested from a STANDSTILL on purpose: a tank
            // that is already rolling forward can't spin on the spot (the reverse track just brakes the forward
            // roll and it curves instead), which is correct behaviour but hides the couple, so the pivot has to be
            // measured from rest.
            var pivStart = tank.GlobalPosition;
            float yawP0 = tank.RotationDegrees.Y;
            for (int i = 0; i < 90; i++) { tank.Drive(0f, -1f, false); yield return Step.Ticks(1); }
            float yawSwing = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(Mathf.DegToRad(yawP0), Mathf.DegToRad(tank.RotationDegrees.Y))));
            float movedPiv = new Vector2(tank.GlobalPosition.X - pivStart.X, tank.GlobalPosition.Z - pivStart.Z).Length();
            T.Check($"A-alone spins the hull in place ({yawSwing:0.0} deg)", yawSwing > 15f);
            T.Check($"...with little translation ({movedPiv:0.0} m)", movedPiv < 8f);

            // brake back to a standstill before the straight run (clear the pivot's spin momentum)
            for (int i = 0; i < 45; i++) { tank.Drive(0f, 0f, true); yield return Step.Ticks(1); }

            // FORWARD FROM REST: both tracks get equal torque -> the hull drives ahead and keeps its heading.
            var fwdStart = tank.GlobalPosition;
            float yawF0 = tank.RotationDegrees.Y;
            for (int i = 0; i < 90; i++) { tank.Drive(1f, 0f, false); yield return Step.Ticks(1); }
            float movedFwd = new Vector2(tank.GlobalPosition.X - fwdStart.X, tank.GlobalPosition.Z - fwdStart.Z).Length();
            float yawDrift = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(Mathf.DegToRad(yawF0), Mathf.DegToRad(tank.RotationDegrees.Y))));
            T.Check($"forward drive moves the tank ({movedFwd:0.0} m)", movedFwd > 2f);
            T.Check($"forward drive holds heading (drift {yawDrift:0.0} deg)", yawDrift < 25f);

            // brake, then W+A: must DRIVE at speed AND turn -- NOT crawl (master: stopping the inside track halved the power)
            for (int i = 0; i < 40; i++) { tank.Drive(0f, 0f, true); yield return Step.Ticks(1); }
            var arcStart = tank.GlobalPosition; float arcYaw0 = tank.RotationDegrees.Y;
            for (int i = 0; i < 90; i++) { tank.Drive(1f, -1f, false); yield return Step.Ticks(1); }
            float arcMoved = new Vector2(tank.GlobalPosition.X - arcStart.X, tank.GlobalPosition.Z - arcStart.Z).Length();
            float arcYaw = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(Mathf.DegToRad(arcYaw0), Mathf.DegToRad(tank.RotationDegrees.Y))));
            T.Check($"W+A keeps speed vs straight, no crawl (arc {arcMoved:0.1} m vs fwd {movedFwd:0.1} m = {(movedFwd > 0.1f ? arcMoved / movedFwd : 0f):0.00}x, turned {arcYaw:0.0} deg)", arcMoved > movedFwd * 0.55f && arcYaw > 15f);

            // never flipped through the pivot + drive (master: "easily flipped")
            T.Check($"stays upright through the whole drive ({tank.GlobalTransform.Basis.Y.Dot(Vector3.Up):0.00})", tank.GlobalTransform.Basis.Y.Dot(Vector3.Up) > 0.9f);

            tank.QueueFree();
        }
    }
}
