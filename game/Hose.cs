using Godot;

namespace UnturnedGodot
{
    // A hose connecting two fluid ports (mirror of Wire): a Source port -> a Consumer port. FluidNet reads Source/
    // Consumer to build the flow graph. Type-lock ("cannot mix fluids") is enforced when a hose is CREATED via the
    // tool (F3); a programmatic/demo hose trusts the caller. The visual polyline + the interactive hose tool land in F3.
    public partial class Hose : Node3D
    {
        public FluidPortNode Source, Consumer;
        public override void _Ready() => AddToGroup("hoses");
    }
}
