using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    public enum FluidRole { Source, Storage, Consumer }

    // A fluid container deployable — the fluid analog of a power deployable. Holds a FluidTank (fluidID + amount +
    // capacity) and exposes fluid ports to the hose graph. Source = has fluid + a Source port; Storage = accumulates
    // via a Consumer port; Consumer = deletes via a Consumer port (a transformer's OUTPUT fluid is F5). Fill bars +
    // interactive port cubes + the hose tool + in-game placement land in F3+; F2 is the data + the flow.
    public partial class FluidContainer : Node3D
    {
        public FluidTank Tank;
        public FluidRole Role;
        public bool Blocked;              // a clogged/closed-valve container stops conducting (F5)
        public float FlowRate = 50f;      // base supply (source) / intake (storage/consumer), units/s
        public readonly System.Collections.Generic.List<FluidPortNode> Ports = new();
        public float LastFlow;            // debug / fill-bar readout

        public static FluidContainer Make(FluidRole role, FluidTank tank, float flowRate = 50f)
            => new FluidContainer { Role = role, Tank = tank, FlowRate = flowRate };

        public override void _Ready()
        {
            AddToGroup("fluid_devices");
            var kind = Role == FluidRole.Source ? FluidPortKind.Source : FluidPortKind.Consumer;
            Ports.Add(new FluidPortNode { Kind = kind, Rate = FlowRate, Owner = this });
        }
    }
}
