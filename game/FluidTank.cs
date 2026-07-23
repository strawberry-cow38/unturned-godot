using Godot;

namespace UnturnedGodot
{
    // The fluid types (the fluidID). None=0 / Fuel=1 kept stable (gas pumps); new fluids APPENDED so ids don't shift.
    public enum FluidType { None, Fuel, Water, Oil, Gas, DirtyWater }

    // fluidID -> display name + bar colour (master's "fluidID:name:amount per container with bars"). The fill bars and
    // the "cannot mix fluids" tooltip read these. Extend as new fluids land.
    public static class FluidDef
    {
        public readonly struct Def { public readonly string Name; public readonly Color Color; public Def(string n, Color c) { Name = n; Color = c; } }
        static readonly System.Collections.Generic.Dictionary<FluidType, Def> _defs = new()
        {
            [FluidType.None]       = new Def("Empty",       new Color(0.40f, 0.40f, 0.45f)),
            [FluidType.Fuel]       = new Def("Fuel",        new Color(0.86f, 0.55f, 0.15f)),   // amber
            [FluidType.Water]      = new Def("Water",       new Color(0.25f, 0.55f, 0.95f)),   // blue
            [FluidType.Oil]        = new Def("Oil",         new Color(0.15f, 0.13f, 0.10f)),   // near-black
            [FluidType.Gas]        = new Def("Gasoline",    new Color(0.95f, 0.85f, 0.30f)),   // pale yellow
            [FluidType.DirtyWater] = new Def("Dirty Water", new Color(0.45f, 0.40f, 0.25f)),   // murky brown
        };
        public static Def Get(FluidType id) => _defs.TryGetValue(id, out var d) ? d : _defs[FluidType.None];
        public static string Name(FluidType id) => Get(id).Name;
        public static Color Color(FluidType id) => Get(id).Color;

        // Display a volume in litres (1 unit = 1 mL, strawberry 2026-07-22). Sub-litre reads in mL so a near-empty can
        // isn't just "0.0 L"; >= 1 L shows one decimal. e.g. 20000 -> "20.0 L", 450 -> "450 mL".
        public static string Litres(float mL) => mL < 1000f ? $"{mL:0} mL" : $"{mL / 1000f:0.0} L";
    }

    // A quantity of fluid a prop can hold (master's fluids system). A gas pump is a Fuel tank; the concept is general
    // so any prop CAN carry one. Fill/Drain clamp to [0, Capacity] and return the amount actually moved. Once drained
    // it just sits at 0 (a pump doesn't respawn its fuel).
    public class FluidTank
    {
        public FluidType Type;
        public float Amount;     // current contents
        public float Capacity;   // max

        public FluidTank(FluidType type, float capacity, float amount = -1f)
        {
            Type = type; Capacity = capacity;
            Amount = amount < 0f ? capacity : Mathf.Clamp(amount, 0f, capacity);   // -1 = start full
        }

        public bool IsEmpty => Amount <= 0.001f;
        public float Space => Mathf.Max(0f, Capacity - Amount);
        public float Drain(float requested) { float d = Mathf.Clamp(requested, 0f, Amount); Amount -= d; return d; }   // returns how much came out
        public float Fill(float requested)  { float f = Mathf.Clamp(requested, 0f, Space);  Amount += f; return f; }   // returns how much went in
    }
}
