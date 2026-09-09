using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The toaster's lever rides its container. strawberry 2026-09-07: "make the toaster's lever move
    /// up when looking in the container, and down when not. with a smooth glide down and a spring up."
    ///
    /// Three separable claims, and they fail for different reasons, so they are checked apart:
    ///   1. the lever EXISTS as its own instance -- the split off Toaster_0.obj's second connected component
    ///   2. it follows the container's open/close signal, which is the same SetDoorsOpen a fridge leaf rides
    ///   3. the two motions are actually DIFFERENT motions, which is the part a screenshot cannot tell you
    ///
    /// (3) is the one worth writing carefully. "Glide down, spring up" is one integrator with two damping
    /// ratios, and the failure mode of getting that wrong is not a crash or a wrong position -- it is both
    /// directions moving identically, which settles at the right place and looks fine in a still. So the
    /// assertions are about the SHAPE of each trace: down must never pass its mark, up must.</summary>
    public class ToasterLeverTests : GameTest
    {
        public override string Name => "props.toaster_lever";
        public override double TimeoutSimSeconds => 60;

        const double Dt = 0.02;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            // No ItemCatalog needed: MinItems/MaxItems are 0, so the shelf rolls nothing and this test is about
            // the lever's motion rather than what is in the toaster.
            var shelf = new StoreShelf { MeshName = "Toaster_0", MinItems = 0, MaxItems = 0, ShowItems = false, TableIndex = 6 };
            World.AddChild(shelf);
            shelf.GlobalPosition = new Vector3(2f, 0f, 0f);
            yield return Ticks(2);

            var t = shelf.DebugToaster;
            T.Check("the container built its Toaster device", t != null);
            if (t == null) yield break;
            T.Check("...and the lever split off the body as its own instance", t.DebugHasLever);
            T.Check($"it starts DOWN, as modelled ({t.DebugLeverOffset:0.000})", Mathf.IsZeroApprox(t.DebugLeverOffset));

            // ---- (2) THE CONTAINER SIGNAL. Not a direct SetLeverUp call: the point is that the lever rides the
            // same open/close the doored containers do, so this goes through SetDoorsOpen.
            shelf.SetDoorsOpen(true);
            T.Check("opening the container asks the lever up", t.DebugLeverUp);

            // ---- (3a) THE SPRING UP. Traced tick by tick, because the claim is about the path and not the
            // destination -- a lever that teleported to the top would pass any end-state check.
            float upPeak = 0f; int upSettleTicks = -1;
            for (int i = 0; i < 200; i++)
            {
                shelf.TickDoorsForTest(Dt);
                upPeak = Mathf.Max(upPeak, t.DebugLeverOffset);
                if (upSettleTicks < 0 && Mathf.Abs(t.DebugLeverOffset - Toaster.DebugLeverTravel) < 0.0005f) upSettleTicks = i;
            }
            GD.Print($"[toaster] UP: peak {upPeak:0.0000} of a {Toaster.DebugLeverTravel:0.0000} travel ({upPeak / Toaster.DebugLeverTravel:0.00}x), first within 0.5mm at tick {upSettleTicks} ({upSettleTicks * Dt:0.00}s)");
            T.Check($"the lever ends UP ({t.DebugLeverOffset:0.0000} vs travel {Toaster.DebugLeverTravel:0.0000})",
                    Mathf.Abs(t.DebugLeverOffset - Toaster.DebugLeverTravel) < 0.001f);
            // THE DISCRIMINATING CHECK. An overdamped or critically damped rise approaches its target from below
            // and never exceeds it, so a peak above travel is only reachable by an actually-springy spring. This
            // is what fails if both directions get the same constants.
            T.Check($"...and it SPRINGS -- overshoots its mark ({upPeak / Toaster.DebugLeverTravel:0.00}x travel)",
                    upPeak > Toaster.DebugLeverTravel * 1.05f);
            T.Check($"...but does not fly off the toaster ({upPeak:0.0000}, body is 0.625 tall)", upPeak < 0.30f);
            T.Check($"...and it is a SNAP, not a drift (settled by {upSettleTicks * Dt:0.00}s)", upSettleTicks >= 0 && upSettleTicks * Dt < 0.60);

            // ---- (3b) THE GLIDE DOWN.
            shelf.SetDoorsOpen(false);
            T.Check("closing it asks the lever down", !t.DebugLeverUp);
            float downMin = 99f; int downSettleTicks = -1;
            for (int i = 0; i < 200; i++)
            {
                shelf.TickDoorsForTest(Dt);
                downMin = Mathf.Min(downMin, t.DebugLeverOffset);
                if (downSettleTicks < 0 && Mathf.Abs(t.DebugLeverOffset) < 0.0005f) downSettleTicks = i;
            }
            GD.Print($"[toaster] DOWN: undershoot {downMin:0.0000} (0 = the mark), first within 0.5mm at tick {downSettleTicks} ({downSettleTicks * Dt:0.00}s)");
            T.Check($"the lever ends DOWN ({t.DebugLeverOffset:0.0000})", Mathf.IsZeroApprox(t.DebugLeverOffset));
            // The mirror of the overshoot check, and the reason the down leg is not simply the up leg reversed:
            // a lever that sprang DOWN would punch through the counter the toaster stands on. -0.5mm of slack is
            // the same settle tolerance the loop above uses, not a tolerance for bouncing.
            T.Check($"...by GLIDING -- it never passes its mark ({downMin:0.0000})", downMin > -0.0006f);
            T.Check($"...and takes its time doing it ({downSettleTicks * Dt:0.00}s)", downSettleTicks >= 0 && downSettleTicks * Dt > 0.10);

            // The two motions must be measurably different in the way claimed, not merely both present.
            GD.Print($"[toaster] shapes: up overshoots to {upPeak / Toaster.DebugLeverTravel:0.00}x and settles in {upSettleTicks * Dt:0.00}s; down overshoots 0.00x and settles in {downSettleTicks * Dt:0.00}s");
            T.Check($"the spring is quicker than the glide ({upSettleTicks * Dt:0.00}s vs {downSettleTicks * Dt:0.00}s)",
                    upSettleTicks < downSettleTicks);
        }
    }
}
