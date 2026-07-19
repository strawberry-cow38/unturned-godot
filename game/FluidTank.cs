using Godot;

namespace UnturnedGodot
{
    public enum FluidType { None, Fuel }   // extensible later (water, oil, ...) -- master's "fluids system"

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
