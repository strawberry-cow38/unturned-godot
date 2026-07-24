using Godot;

namespace UnturnedGodot
{
    // Fluid IO on the map's WATER TOWER props (strawberry 2026-07-23). PEI's Objects.dat places 5 Tower_Water_0 towers;
    // WorldBuilder attaches one of these at each (in Playable mode) so the tower becomes a hose-able water source. It's an
    // INFINITE water source (a municipal reservoir never runs dry) that spawns TAINTED -- like every natural/stored water,
    // you purify or bottle it for clean (strawberry's water-quality rule). Unlike the submersible inlet it HAS head (it's
    // elevated, gravity-fed), so its output flows downhill WITHOUT a pump. It rides the tower's own prop mesh -> no visual
    // of its own; just an output spigot (HosePort) at the tower base, reachable + clear of the legs.
    public partial class WaterTowerSource : FluidContainer
    {
        public static WaterTowerSource Make() => new WaterTowerSource
        {
            Role = FluidRole.Source,
            Tank = new FluidTank(FluidType.Water, 200000f, 200000f, WaterQuality.Tainted),
            FlowRate = 125f,
            Infinite = true,                              // never depletes (a reservoir, not a filled tank)
            DisplayName = "Water Tower",                  // reads distinctly in the hose-tool port HUD (not "Fluid Source")
            PortLocalPos = new Vector3(0f, 1.3f, 2.0f),   // an output spigot at the tower BASE: 1.3 m up, 2 m out past the legs
        };

        protected override void BuildVisuals() { }   // rides the Tower_Water_0 prop mesh -- no own tank body / fill bar
    }
}
