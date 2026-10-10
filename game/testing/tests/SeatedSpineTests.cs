using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The 3P body's spine while SEATED (strawberry 2026-10-10: "in 3p in vehicle seats it looks like
    /// my model is leaning forward where they sit").
    ///
    /// ⭐⭐ THE BUG WAS STALE STATE, NOT A WRONG ANGLE. The vehicle-seat branch of UpdateBody set neither
    /// LeanDeg nor SpinePitchScale, so both kept whatever the ON-FOOT branch last wrote -- the look pitch you
    /// held while climbing in, which is downward because you were looking at the door prompt. That angle then
    /// sat on the spine for the whole drive, tracking nothing.
    ///
    /// ⚠ WHAT THIS TEST CANNOT DO, stated rather than faked: measure the posed spine. The angle only exists
    /// inside the SkeletonModifier3D pass, and this harness never drives the per-frame chain that poses the
    /// body -- every basis reads identity and a broken fix would look the same as a working one. I tried
    /// (including calling UpdateBody directly) and it stays unposed, so the geometry is verified by RENDER
    /// instead and this holds the CONTRACT: the scale is taken out when seated and put back when not.</summary>
    public sealed class SeatedSpine : GameTest
    {
        public override string Name => "vehicle.seated_spine";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            p.DebugSetThirdPerson(true);
            yield return Ticks(4);
            var body = p.DebugBodyRig;
            T.Check("the 3P body exists", body != null);
            if (body == null) yield break;

            // A rig with no scale applied behaves exactly as it always did.
            T.Check($"the spine takes its full share by default ({body.SpinePitchScale:0.##})",
                    Mathf.IsEqualApprox(body.SpinePitchScale, 1f));

            // ---- ENTER WHILE LOOKING DOWN -- the pose that caused the bug ---------------------------------
            var car = Vehicle.BuildByName("jeep");
            World.AddChild(car);
            yield return Ticks(3);
            p.DebugSetLookPitch(-35f);
            p.EnterVehicle(car, 0);
            yield return Ticks(3);
            p.DebugUpdateBody(0.05);
            T.Check("seated", p.DebugIsDriving);

            T.Check($"seated takes the spine's share OUT ({body.SpinePitchScale:0.##})",
                    Mathf.IsZeroApprox(body.SpinePitchScale));
            T.Check($"...and clears the carried-in lean ({body.LeanDeg:0.##})", Mathf.IsZeroApprox(body.LeanDeg));
            // ⭐ THE HEAD MUST STILL GET THE LOOK. Zeroing PitchDeg outright -- which is what the BED path does
            // -- would satisfy the two checks above while leaving a driver who cannot look around. This is the
            // control that separates the fix from the blunt version of it.
            T.Check($"...but the look still reaches the skull (PitchDeg {body.PitchDeg:0.#})",
                    Mathf.Abs(body.PitchDeg) > 1f);

            // ---- AND IT COMES BACK ON FOOT ----------------------------------------------------------------
            // ⚠ The mirror of the original bug: a scale left at 0 after stepping out is the same stale-state
            // failure pointing the other way, and the checks above would not notice.
            p.TryExitVehicle();
            yield return Ticks(3);
            p.DebugUpdateBody(0.05);
            T.Check($"stepping out restores the spine's share ({body.SpinePitchScale:0.##})",
                    Mathf.IsEqualApprox(body.SpinePitchScale, 1f));

            car.QueueFree();
        }
    }
}
