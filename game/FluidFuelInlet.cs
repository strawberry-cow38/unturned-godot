using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A FUEL hose input bolted onto a generator (strawberry: "hose IO input for fuel into generators"). A small fuel-locked
    // Storage buffer with ONE input HosePort that the fluid net fills; each frame it empties into the owner generator's Fuel
    // tank. Its tank Type is Fuel, so the hose type-lock REFUSES (and warns on) water/oil/etc. Bridges fluid -> the power/fuel
    // economy: plumb a fuel line to your gens instead of hand-carrying cans. SP-local (device replication = fast-follow).
    public partial class FluidFuelInlet : FluidContainer
    {
        Deployable _gen;   // the generator whose Fuel this feeds

        public static FluidFuelInlet Make(Deployable gen) => new FluidFuelInlet
        {
            Role = FluidRole.Storage, _gen = gen, FlowRate = 125f,   // garden-hose fuel intake (625 through a powered pump)
            Tank = new FluidTank(FluidType.Fuel, 5000f, 0f),   // a 5L in-transit buffer, fuel-locked (Type=Fuel -> won't adopt, demands Fuel)
        };

        // ONE fuel input port, low on the gen's front face -- NO Source output (you can't siphon the gen's fuel back out here)
        protected override void BuildPorts() => AddPort(FluidPortKind.Consumer, FlowRate, new Vector3(0f, 0.15f, 0.55f));

        // no body of its own -- it rides the generator's mesh/collider; just the port cube (built in BuildPorts)
        protected override void BuildVisuals() { }

        static readonly Vector3 Offset = new Vector3(0f, 0.35f, 0f);   // sits just above the generator

        public override void _Ready()
        {
            base._Ready();
            TopLevel = true;   // the generator sits in a flat stand-up basis; keep the inlet UPRIGHT so its port cube is Y-up like every other fluid port
            if (_gen != null && GodotObject.IsInstanceValid(_gen)) GlobalPosition = _gen.GlobalPosition + Offset;   // initial upright placement
            // a generator may be placed with no other fluid device around -> make sure a FluidManager ticks the net
            if (GetTree() != null && GetTree().GetNodesInGroup("fluid_managers").Count == 0) GetParent()?.AddChild(new FluidManager());
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (_gen != null && GodotObject.IsInstanceValid(_gen)) GlobalPosition = _gen.GlobalPosition + Offset;   // track the gen (TopLevel -> world Y-up)
        }

        // tick-driven (so it fuels in headless tests too): empty the in-transit buffer into the generator's fuel tank; its
        // Space clamps intake once the gen is full (-> the feeding pump's auto-shutoff then idles).
        public override void OnPostTick(float dt)
        {
            if (_gen == null || !GodotObject.IsInstanceValid(_gen) || Tank == null) return;
            float move = Mathf.Min(Tank.Amount, _gen.FuelMax - _gen.Fuel);
            if (move > 0.001f) { Tank.Drain(move); _gen.Fuel += move; PowerNet.MarkDirty(); }   // a dry gen just got fuel -> re-solve the power net
        }
    }
}
