using Godot;

namespace UnturnedGodot
{
    // Fluid IO on the map's WELL props (strawberry: "get the well props as water containers/producers. unlimited
    // tainted water, hose output, requires a pump to extract via hose. always producing no matter what, globalwater
    // has no effect"). WorldBuilder attaches one at each Well_0 in Playable mode, exactly like the tower and hydrants.
    //
    // Two things make a well different from every other map water source, and both are the point rather than details:
    //
    //  - IT NEEDS A PUMP. NoHead, the flag the submersible river inlet uses: the water is down a shaft, so it has no
    //    head pressure and will never flow passively no matter what the terrain does. That is NOT the same as the
    //    hydrant's "sits at street level so a tank on a rise needs a pump" -- that one gravity-feeds anything below
    //    it and only needs help going up. A well needs help going ANYWHERE.
    //  - IT IS OFF THE MAINS. SupplyEnabled is hardcoded true rather than following FluidNet.GlobalWater, because a
    //    well is a hole in the ground with groundwater at the bottom; there is no municipal valve to shut. Kill the
    //    town's water and the tower, hydrants and sinks all go dead -- the wells keep producing, which is exactly what
    //    makes them worth finding once the mains are down.
    //
    // Tainted like every other unbottled water in this world (strawberry's water-quality rule): groundwater is not
    // drinking water, you purify or bottle it. Infinite because an aquifer is not a tank.
    public partial class WellSource : FluidContainer
    {
        /// <summary>Drawn through a pump rather than pressure-fed, so it is slower than the tower's elevated 125 and
        /// the hydrant's mains-pressure 80. The rate a hand-worked shaft gives you, not a municipal one.</summary>
        public const float DrawRate = 60f;

        public static WellSource Make() => new WellSource
        {
            Role = FluidRole.Source,
            Tank = new FluidTank(FluidType.Water, 200000f, 200000f, WaterQuality.Tainted),
            FlowRate = DrawRate,
            Infinite = true,        // an aquifer, not a filled tank -- never depletes
            NoHead = true,          // down a shaft: no head pressure, so ONLY a powered pump can draw from it
            DisplayName = "Well",   // reads distinctly from "Water Tower" / "Fire Hydrant" in the hose-tool port HUD
            // Measured off Well_0.obj rather than eyeballed. In node space (the mesh is Z-up and the node is yaw-only,
            // so mesh(x,y,z) -> node(x,z,-y)) the stone wall runs 0 -> 1.25m at radius ~0.99 and the roof sits at
            // 2.25 -> 2.62m at radius 1.22. So this spigot at 1.0m up and 1.1m out is just clear of the wall, under
            // the roof overhang, and at a height a hose reaches -- not buried in the stonework or floating past the eaves.
            PortLocalPos = new Vector3(0f, 1.0f, 1.1f),
        };

        /// <summary>ALWAYS producing (master: "always producing no matter what, globalwater has no effect"). The base
        /// returns FluidNet.GlobalWater like the tower/hydrant/sink do; a well has no mains to be cut off from, so it
        /// deliberately does not consult it. If this ever starts following the global flag, shutting the town's water
        /// off silently kills every well too -- and the whole reason wells matter is that it does not.</summary>
        public override bool SupplyEnabled => true;

        protected override void BuildVisuals() { }   // rides the Well_0 prop mesh -- no tank body or fill bar of its own
    }
}
