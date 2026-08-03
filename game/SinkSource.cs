using Godot;

namespace UnturnedGodot
{
    // Fluid IO on the map's KITCHEN SINKS (strawberry: "sinks supply clean water").
    //
    // WHICH PROPS ARE SINKS was read off the meshes, not guessed. PEI has four counter variants and no prop named
    // "sink": Counter_0 and Counter_2 are all-wood (their whole 2x2 palette is the two brown tones, geometry topping
    // out at the counter surface Z 1.35), while Counter_1 and Counter_3 carry two extra GREY metal texels --
    // (157,157,157) over Z 0.97..1.35 and (174,174,174) over Z 1.09..1.83 -- i.e. a metal basin recessed into the top
    // plus a fitting standing above it. That is a sink unit. 22 Counter_1 + 6 Counter_3 = 28 sinks, against 126 plain
    // counters that stay ordinary props.
    //
    // CLEAN water, and it is the only clean source in the world: everything else (rain, river, tower, hydrant) is
    // tainted and has to be purified or bottled. That is deliberately generous and deliberately temporary -- it is
    // gated on the mains, so the day the water shuts off, clean water goes back to being something you work for.
    public partial class SinkSource : FluidContainer
    {
        /// <summary>A tap, not a main: slow enough that filling a big tank off a kitchen sink is a chore rather than
        /// the obvious play, which is what keeps the hydrants and towers worth hosing.</summary>
        public const float TapRate = 30f;

        public static SinkSource Make() => new SinkSource
        {
            Role = FluidRole.Source,
            Tank = new FluidTank(FluidType.Water, 200000f, 200000f, WaterQuality.Clean),
            FlowRate = TapRate,
            Infinite = true,                              // mains-fed, not a tank
            DisplayName = "Sink",
            // The basin sits in the counter top (metal texels span Z 0.97..1.83); put the spout port just above the
            // rim and forward of centre so the hose cube is reachable standing at the counter rather than inside it.
            PortLocalPos = new Vector3(0f, 1.42f, 0.22f),
        };

        public override bool SupplyEnabled => FluidNet.GlobalWater;

        protected override void BuildVisuals() { }   // rides the Counter_1 / Counter_3 prop mesh

        public override (string text, Color color) StatusLine()
            => FluidNet.GlobalWater ? ("CLEAN", new Color(0.5f, 1f, 0.6f))
                                    : ("NO WATER", new Color(1f, 0.55f, 0.2f));
    }
}
