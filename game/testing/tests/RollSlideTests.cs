using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>A sharp turn should SPIN YOU OUT, not put you on the roof (strawberry 2026-10-11: "rework
    /// handling to not flip you on sharp turns, rather spin you out horizontally").
    ///
    /// ⚠⚠ THIS TEST IS WORTHLESS WITHOUT ITS CONTROL, so it runs the identical manoeuvre twice: once with the
    /// anti-flip bleed off (UG_ROLLSLIDE / Vehicle.RollSlideEnabled) and once with it on. "The car does not flip"
    /// proves nothing on its own -- it would pass just as happily on a build where the car never flipped, or
    /// where it was too slow to reach the limit. The control has to roll for the result to mean anything.</summary>
    public sealed class RollSlideTests : GameTest
    {
        public override string Name => "vehicle.slide_not_flip";
        public override double TimeoutSimSeconds => 240;

        /// <summary>Flat-out, then full lock. Returns the worst roll angle seen (deg) and the peak lateral g.</summary>
        IEnumerable<Step> Corner(string car, bool slide, System.Action<(float roll, float latG, float yaw, float grip)> report)
        {
            Vehicle.RollSlideEnabled = slide;
            var v = Vehicle.BuildByName(car);
            World.AddChild(v);
            v.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            v.EngineOn = true; v.Wake(); v.Brake = 0f;
            yield return Ticks(40);
            for (int i = 0; i < 700; i++) { v.Drive(1f, 0f, false); yield return Ticks(1); }   // get it up to speed in a straight line
            float roll = 0f, lat = 0f, yaw = 0f;
            float entrySpeed = v.LinearVelocity.Length();
            float minGrip = 1f; int tipTick = -1; float latAtTip = 0f, airAtTip = 0f;
            for (int i = 0; i < 260; i++)
            {
                v.Drive(1f, 1f, false);
                yield return Ticks(1);
                // Roll = how far the car's own up-axis has tipped away from world up. 90 deg is on its side.
                float r = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(v.GlobalTransform.Basis.Y.Dot(Vector3.Up), -1f, 1f)));
                if (tipTick < 0 && r > 25f) { tipTick = i; latAtTip = v.LateralG; airAtTip = v.WheelsOnGroundForTest; }
                roll = Mathf.Max(roll, r);
                lat = Mathf.Max(lat, v.LateralG);
                yaw = Mathf.Max(yaw, Mathf.Abs(v.AngularVelocity.Y));
                minGrip = Mathf.Min(minGrip, v.RollSlideGrip);
            }
            // ⚠ INSTRUMENTED AFTER TWO FAILED FIXES. Both attempts assumed the car tips because cornering
            // force beats the rollover threshold; neither moved the number, which means the DIAGNOSIS is what
            // is wrong. These are the quantities that distinguish the candidate causes: if latAtTip is well
            // under the threshold, cornering force is not what is turning it over.
            GD.Print($"[rollslide] {car} slide={slide}: entry {entrySpeed:0.0} m/s, ssf {v.RolloverThresholdForTest:0.00} g, "
                   + $"peak {lat:0.00} g, minGrip {minGrip:0.00}, tipped at tick {tipTick} with {latAtTip:0.00} g "
                   + $"and {airAtTip:0} wheels down, final roll {roll:0.0} deg");
            report((roll, lat, yaw, minGrip));
            World.RemoveChild(v); v.QueueFree();
            Vehicle.RollSlideEnabled = true;
            yield return Ticks(5);
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(5);

            foreach (var car in new[] { "roadster", "jeep" })
            {
                (float roll, float latG, float yaw, float grip) off = default, on = default;
                foreach (var st in Corner(car, false, r => off = r)) yield return st;
                foreach (var st in Corner(car, true, r => on = r)) yield return st;
                GD.Print($"[rollslide] {car}: bleed OFF roll {off.roll:0.0} deg, {off.latG:0.00} g, yaw {off.yaw:0.00} | ON roll {on.roll:0.0} deg, {on.latG:0.00} g, yaw {on.yaw:0.00}");

                // THE CONTROL. If the car stays flat with the bleed OFF then this manoeuvre never tested
                // anything and the pass below would be free.
                T.Check($"{car}: CONTROL -- without the bleed the corner really does tip it ({off.roll:0.0} deg)",
                        off.roll > 20f);
                // ...and with it, the car stays on its wheels.
                T.Check($"{car}: with the bleed it stays on its wheels ({on.roll:0.0} deg < {off.roll:0.0})",
                        on.roll < 20f && on.roll < off.roll);
                // ⭐ AND IT IS STILL TURNING. The cheap way to stop a car flipping is to stop it cornering;
                // master asked for a SPIN, so the yaw must survive.
                T.Check($"{car}: it still spins rather than just stopping ({on.yaw:0.00} rad/s)", on.yaw > 0.25f);
                // ⭐ THE TYRES ACTUALLY LET GO. Without this the test passes on a build that stops the flip by
                // simply refusing to turn -- "does not roll" and "slides" are different claims, and master
                // asked for the second. minGrip below 1 means the bleed engaged and the back stepped out.
                // ⚠ NOT asserted against off.latG: LateralG is zeroed on the disabled path, so comparing the
                // two arms compares a measurement with a switched-off instrument. (Caught by this test failing
                // while every behavioural number was already right.)
                T.Check($"{car}: the tyres gave up grip rather than the car just refusing to turn (minGrip {on.grip:0.00})",
                        on.grip < 0.95f);
            }
        }
    }
}
