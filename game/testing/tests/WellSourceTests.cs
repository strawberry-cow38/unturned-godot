using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WELLS (master: "get the well props as water containers/producers. unlimited tainted water, hose output,
    // requires a pump to extract via hose. always producing no matter what, globalwater has no effect").
    //
    // Two of those clauses are the whole feature and both fail SILENTLY if they regress:
    //
    //  - NoHead is what "requires a pump" means in this solver. Without it the well gravity-feeds anything downhill
    //    exactly like a hydrant, which LOOKS like a working well -- water comes out, hoses fill tanks -- and is only
    //    wrong in the case nobody tests: a tank below it, where it should have needed a pump and didn't.
    //  - SupplyEnabled must NOT follow FluidNet.GlobalWater. The base class does, so this is an override that could be
    //    deleted by anyone tidying up "redundant" code, and the symptom is that wells die with the mains -- which is
    //    precisely the situation they exist for and precisely when nobody is around to notice they stopped.
    //
    // Asserted against the tower and the hydrant rather than in isolation, because "a well behaves differently" is a
    // claim about a contrast, and pinning the well alone would pass just as happily if all three drifted together.
    public sealed class WellSourceTests : GameTest
    {
        public override string Name => "fluid.well_source";

        public override IEnumerable<Step> Run()
        {
            var well = WellSource.Make();
            var tower = WaterTowerSource.Make();
            var hydrant = FireHydrantSource.Make();
            World.AddChild(well); World.AddChild(tower); World.AddChild(hydrant);
            yield return Ticks(2);

            // ---- UNLIMITED TAINTED WATER.
            T.Check($"a well is a SOURCE ({well.Role})", well.Role == FluidRole.Source);
            T.Check($"...of water ({well.Tank.Type})", well.Tank.Type == FluidType.Water);
            T.Check($"...tainted, like every unbottled water in this world ({well.Tank.Quality})",
                well.Tank.Quality == WaterQuality.Tainted);
            T.Check("...and infinite -- an aquifer, not a tank", well.Infinite);

            // ---- REQUIRES A PUMP. This is the clause with no visible symptom when it breaks.
            T.Check("a well has NO HEAD, so only a powered pump can draw from it", well.NoHead);
            // The contrast that makes it meaningful: the mains sources DO have head and gravity-feed downhill. If
            // NoHead ever became the default, this pair would still pass one at a time.
            T.Check("...unlike the water tower, which gravity-feeds downhill", !tower.NoHead);
            T.Check("...and unlike a fire hydrant, which is a pressurised main", !hydrant.NoHead);

            // ---- ALWAYS PRODUCING. Drive the real global flag rather than reading the property in a vacuum: the
            // whole failure mode is "the override got removed and the base class's mains gate took over".
            bool waterWas = FluidNet.GlobalWater;
            FluidNet.SetGlobalWater(true);
            T.Check("with the mains UP, everything supplies", well.SupplyEnabled && tower.SupplyEnabled && hydrant.SupplyEnabled);

            FluidNet.SetGlobalWater(false);
            T.Check($"with the mains DOWN the well keeps producing ({well.SupplyEnabled})", well.SupplyEnabled);
            // ...and the others must go dead, which is what makes the line above a distinction rather than a coincidence.
            // If this ever passes with the mains off, GlobalWater has stopped gating anything and the well's
            // independence is meaningless.
            T.Check($"...while the tower and hydrant go dead ({tower.SupplyEnabled}, {hydrant.SupplyEnabled})",
                !tower.SupplyEnabled && !hydrant.SupplyEnabled);
            FluidNet.SetGlobalWater(waterWas);

            // ---- HOSE OUTPUT, placed on the real prop. Measured off Well_0.obj: node-space wall runs 0..1.25m at
            // radius ~0.99, roof 2.25..2.62m at radius 1.22. A port outside the roof or inside the stonework is not a
            // visual nitpick -- it is a hose anchor the player cannot reach or that hangs in mid-air.
            var p = well.PortLocalPos;
            float radial = Mathf.Sqrt(p.X * p.X + p.Z * p.Z);
            T.Check($"the spigot clears the well wall ({radial:0.00} > 0.99)", radial > 0.99f);
            T.Check($"...but stays under the roof overhang ({radial:0.00} < 1.22)", radial < 1.22f);
            T.Check($"...at a reachable height, not down the shaft or up in the eaves ({p.Y:0.00})",
                p.Y > 0.3f && p.Y < 1.6f);

            // Flow is slower than the pressure-fed sources -- a pumped shaft, not a municipal main. Pinned as an
            // ordering rather than a magic number so retuning any of them cannot silently invert the relationship.
            T.Check($"a pumped well is slower than the mains sources ({well.FlowRate} < {tower.FlowRate})",
                well.FlowRate < tower.FlowRate && well.FlowRate > 0f);
            T.Check($"...and names itself distinctly in the port HUD (\"{well.DisplayName}\")",
                well.DisplayName == "Well" && well.DisplayName != tower.DisplayName);

            well.QueueFree(); tower.QueueFree(); hydrant.QueueFree();
            yield return Ticks(1);
        }
    }
}
