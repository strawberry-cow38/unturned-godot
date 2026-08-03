using Godot;
using SDG.Unturned;   // FluidPortKind lives with the engine-free solver

namespace UnturnedGodot
{
    // Fluid IO on the map's FIRE HYDRANT props (strawberry). PEI places 46 Fire_Hydrant_0; WorldBuilder attaches one of
    // these at each in Playable mode, turning every hydrant into a mains tap.
    //
    // FOUR hose ports, not one (strawberry's spec) -- a real hydrant has multiple outlets and this is the difference
    // between a hydrant and the water tower: the tower is a single elevated spigot, a hydrant is where you run several
    // lines at once. They are spread around the barrel so four hoses do not stack on one face.
    //
    // TAINTED, like the tower and every other non-bottled water: mains water in this world is not drinking water, you
    // purify or bottle it. INFINITE while the mains are up (a municipal main, not a tank) -- and dead the moment they
    // are not, which is what SupplyEnabled is for.
    //
    // It HAS head -- deliberately NOT the NoHead flag, which means "never flows passively" and belongs to the
    // submersible river inlet. A hydrant is a pressurised main, so it gravity-feeds anything below it exactly like the
    // tower does. The practical difference is elevation, not plumbing: the tower is up in the air and reaches most of
    // the map downhill, while a hydrant sits at street level, so a tank on a rise needs a pump. That falls out of the
    // solver from the node's world Y with nothing to configure -- worth knowing before filing it as a bug.
    public partial class FireHydrantSource : FluidContainer
    {
        /// <summary>Per-outlet flow. Lower than the tower's 125: a street-level hydrant tap, and four of them on one
        /// prop, so the mast total is comfortably above a single tower spigot without any one line being faster.</summary>
        public const float OutletRate = 80f;
        public const int Outlets = 4;

        public static FireHydrantSource Make() => new FireHydrantSource
        {
            Role = FluidRole.Source,
            Tank = new FluidTank(FluidType.Water, 200000f, 200000f, WaterQuality.Tainted),
            FlowRate = OutletRate,
            Infinite = true,                      // a main, not a tank -- never depletes
            DisplayName = "Fire Hydrant",         // reads distinctly from "Water Tower" in the hose-tool port HUD
        };

        // Municipal supply: dead when the mains are off. Kept as the SOLVER's notion of supplying (FluidContainer.
        // SupplyEnabled) rather than, say, emptying the tank -- a source that merely reads empty still advertises a
        // port, and every pump on its line would stay awake waiting for water that is never coming.
        public override bool SupplyEnabled => FluidNet.GlobalWater;

        // FOUR outlets around the barrel. The base Source case adds exactly one port at PortLocalPos, so this replaces
        // it rather than extending it -- calling base would leave a fifth port buried in the middle of the prop.
        protected override void BuildPorts()
        {
            const float r = 0.42f;     // just clear of the hydrant barrel
            const float h = 0.62f;     // outlet height: the cap band, not the base flange
            for (int i = 0; i < Outlets; i++)
            {
                float a = Mathf.Tau * i / Outlets;
                AddPort(FluidPortKind.Source, OutletRate, new Vector3(Mathf.Sin(a) * r, h, Mathf.Cos(a) * r));
            }
        }

        protected override void BuildVisuals() { }   // rides the Fire_Hydrant_0 prop mesh -- no tank body / fill bar of its own

        /// <summary>Status billboard: says WHY it is dead, rather than reading as a broken fixture. The whole point of
        /// the machine-status line is that a dead device explains itself.</summary>
        public override (string text, Color color) StatusLine()
            => FluidNet.GlobalWater ? ("MAINS", new Color(0.45f, 0.85f, 1f))
                                    : ("NO WATER", new Color(1f, 0.55f, 0.2f));
    }
}
