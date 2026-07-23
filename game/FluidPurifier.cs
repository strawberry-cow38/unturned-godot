using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // The water PURIFIER (strawberry 2026-07-23): tainted/dirty water + power in -> CLEAN water out. It's a Water->Water
    // TRANSFORMER (so all the transformer machinery — delete input, produce output, 1-tick lag — works), whose output
    // resolves to Clean by default (a transformer isn't a flow relay, so FluidNet.ResolveWaterQuality returns Clean for
    // it; only the sluice's DirtiesWater flag overrides that to Dirty). What makes it a PURIFIER and not a free relay is
    // that it ALSO draws power: a Consumer ConnectionPort on the power net (mirror of FluidPump), and its transform is
    // gated on being powered (TransformEnabled => IsPowered). Unpowered = inert: FluidNet zeroes its port rates, so it
    // neither consumes the tainted water nor fabricates clean water. Wire a generator to it to run it.
    public partial class FluidPurifier : FluidContainer, IPowerDevice
    {
        public const float PurifierWatts = 750f;   // drawn off the power net while wired (a hungry appliance)
        public uint NetId;                          // MP replica id (0 = SP/local)
        public bool DebugForcePower;                // headless tests: pretend it's wired + powered

        readonly List<ConnectionPort> _powerPorts = new();
        ConnectionPort _powerInput;

        // a Water->Water transformer at garden-hose rate; the base FluidContainer builds the Consumer input + Source output.
        public static FluidPurifier Make()
            => new FluidPurifier { Role = FluidRole.Transformer, Tank = null, FlowRate = 125f, TransformIn = FluidType.Water, TransformOut = FluidType.Water };

        protected override void OnReadyExtra()
        {
            // the power CONSUMER side: one input cube drawing PurifierWatts on the power net's "deployables" group -- wire a
            // generator to it exactly as you'd power a pump / gas pump / spotlight.
            _powerInput = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = new Vector3(0f, 1.25f, 0.42f), Watts = PurifierWatts }, "Fluid Purifier");
            _powerPorts.Add(_powerInput);
            AddChild(_powerInput);
            AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice)
        }

        // powered when the power net is delivering its watts to the input (or forced for a headless test)
        public bool IsPowered => DebugForcePower || (_powerInput != null && GodotObject.IsInstanceValid(_powerInput) && _powerInput.Powered);

        // the transform runs ONLY while powered -> an unpowered purifier is inert (no consume, no produce)
        public override bool TransformEnabled => IsPowered;

        // picked up -> free any wire plugged into the power input (the base frees its hoses), then re-solve the power net
        protected override void OnPickup()
        {
            foreach (var n in GetTree().GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w) && (w.Source == _powerInput || w.Consumer == _powerInput))
                { w.RemoveFromGroup("wires"); w.QueueFree(); }
            PowerNet.MarkDirty();
        }

        // IPowerDevice -- a pure consumer (mirror of FluidPump)
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;
        public IReadOnlyList<ConnectionPort> PowerPorts => _powerPorts;
    }
}
