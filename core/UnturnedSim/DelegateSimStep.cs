using System;

namespace SDG.Unturned
{
    // Adapter so a host (Godot node, net pump, test) can register a plain callback on SimRoot without
    // declaring a class per step. The Name is for diagnostics/tick-order tests only.
    public sealed class DelegateSimStep : ISimStepped
    {
        readonly Action<long, double> _step;

        public string Name { get; }

        public DelegateSimStep(Action<long, double> step, string name = null)
        {
            _step = step ?? throw new ArgumentNullException(nameof(step));
            Name = name ?? "anonymous";
        }

        public void SimStep(long tick, double dt) => _step(tick, dt);
    }
}
