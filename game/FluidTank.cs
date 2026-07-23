using Godot;

namespace UnturnedGodot
{
    // The fluid types (the fluidID). None=0 / Fuel=1 kept stable (gas pumps); new fluids APPENDED so ids don't shift.
    // (DirtyWater is GONE -- dirtiness is now a WaterQuality FLAG on water, not its own type.) The DRINK fluids
    // (Soda/Cola/OrangeJuice/Milk/CoconutWater/EnergyDrink) all act like clean water when drunk -- see FluidDef.IsBeverage.
    public enum FluidType { None, Fuel, Water, Oil, Gas, Soda, Cola, OrangeJuice, Milk, CoconutWater, EnergyDrink, AppleJuice, GrapeJuice }

    // Water QUALITY (strawberry): clean < tainted < dirty. Bottled water = clean; natural water (river/rain/ocean/inlet) =
    // tainted; the sluice makes dirty. A container takes the WORST quality that enters it (one drop of dirty -> all dirty).
    // Ordered so Mathf.Max on the (int) value = the worse of two. Only meaningful when the fluid is Water/Soda/Cola.
    public enum WaterQuality { Clean, Tainted, Dirty }

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
            [FluidType.Soda]       = new Def("Soda",        new Color(0.55f, 0.30f, 0.85f)),   // purple fizz (acts like water)
            [FluidType.Cola]       = new Def("Cola",        new Color(0.28f, 0.16f, 0.10f)),   // dark cola brown
            [FluidType.OrangeJuice]  = new Def("Orange Juice",  new Color(0.95f, 0.55f, 0.10f)),   // orange
            [FluidType.Milk]         = new Def("Milk",          new Color(0.96f, 0.95f, 0.90f)),   // cream white
            [FluidType.CoconutWater] = new Def("Coconut Water", new Color(0.90f, 0.92f, 0.84f)),   // pale cloudy
            [FluidType.EnergyDrink]  = new Def("Energy Drink",  new Color(0.35f, 0.85f, 0.30f)),   // lurid green
            [FluidType.AppleJuice]   = new Def("Apple Juice",   new Color(0.85f, 0.70f, 0.20f)),   // golden
            [FluidType.GrapeJuice]   = new Def("Grape Juice",   new Color(0.42f, 0.16f, 0.42f)),   // deep purple
        };
        public static Def Get(FluidType id) => _defs.TryGetValue(id, out var d) ? d : _defs[FluidType.None];
        public static string Name(FluidType id) => Get(id).Name;
        public static Color Color(FluidType id) => Get(id).Color;

        // WATER is the only type that carries a quality flag; its display name folds the quality in (dirty water reads murky).
        public static string WaterName(FluidType id, WaterQuality q) => id == FluidType.Water
            ? q switch { WaterQuality.Dirty => "Dirty Water", WaterQuality.Tainted => "Tainted Water", _ => "Clean Water" }
            : Name(id);
        public static Color WaterColor(FluidType id, WaterQuality q) => id == FluidType.Water
            ? q switch { WaterQuality.Dirty => new Color(0.45f, 0.40f, 0.25f), WaterQuality.Tainted => new Color(0.45f, 0.60f, 0.65f), _ => Color(FluidType.Water) }
            : Color(id);
        // A BEVERAGE fluid (soda/cola/juice/milk/etc.) -- always drinkable, no water-quality flag. Fuel/oil/gas/plain water are not.
        public static bool IsBeverage(FluidType id) => id == FluidType.Soda || id == FluidType.Cola || id == FluidType.OrangeJuice
                                                    || id == FluidType.Milk || id == FluidType.CoconutWater || id == FluidType.EnergyDrink
                                                    || id == FluidType.AppleJuice || id == FluidType.GrapeJuice;
        public static bool Drinkable(FluidType id, WaterQuality q) => id == FluidType.Water ? q == WaterQuality.Clean : IsBeverage(id);   // only CLEAN water, or a beverage, can be drunk

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
        public WaterQuality Quality;   // water only: clean/tainted/dirty. A container takes the WORST that enters it.

        public FluidTank(FluidType type, float capacity, float amount = -1f, WaterQuality quality = WaterQuality.Clean)
        {
            Type = type; Capacity = capacity; Quality = quality;
            Amount = amount < 0f ? capacity : Mathf.Clamp(amount, 0f, capacity);   // -1 = start full
        }

        // taking on `q` water can only ever make this container WORSE (one drop of dirty dirties it all) -- strawberry
        public void Contaminate(WaterQuality q) { if ((int)q > (int)Quality) Quality = q; }

        public bool IsEmpty => Amount <= 0.001f;
        public float Space => Mathf.Max(0f, Capacity - Amount);
        public float Drain(float requested) { float d = Mathf.Clamp(requested, 0f, Amount); Amount -= d; return d; }   // returns how much came out
        public float Fill(float requested)  { float f = Mathf.Clamp(requested, 0f, Space);  Amount += f; return f; }   // returns how much went in
    }
}
