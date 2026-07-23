using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // An electric fluid PUMP — the power<->fluid BRIDGE. It's a tankless inline fitting (a 0-rate relay input + a
    // passthrough output, via FluidRole.Pump) that ALSO draws power off the power net: a Consumer ConnectionPort in
    // group "deployables" (mirror of GasPump), wired from a generator. When POWERED it provides HEAD LIFT — FluidNet's
    // gravity gate lets a hose adjacent to a powered pump run UPHILL up to HeadLift metres (unpowered = a passive relay,
    // gravity-gated like any fitting). Flow-rate boost is a fast-follow; head lift is the essential "or has a pump".
    public partial class FluidPump : FluidContainer, IPowerDevice
    {
        public const float PumpWatts = 500f;   // drawn off the power net while wired + running
        public float HeadLift = 6f;            // metres of rise a powered pump can push fluid up (overrides gravity)
        public uint NetId;                     // MP replica id (0 = SP/local)
        public bool DebugForcePower;           // headless tests: pretend the pump is wired + powered

        readonly List<ConnectionPort> _powerPorts = new();
        ConnectionPort _powerInput;

        public static FluidPump Make(float headLift = 6f)
            => new FluidPump { Role = FluidRole.Pump, Tank = null, HeadLift = headLift };

        protected override void OnReadyExtra()
        {
            // the power CONSUMER side: one input cube drawing PumpWatts on the power net's "deployables" group. A wire
            // tool run from a generator to this cube powers the pump exactly as a gas pump / spotlight is powered.
            _powerInput = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = new Vector3(0f, 1.25f, 0.42f), Watts = PumpWatts }, "Fluid Pump");
            _powerPorts.Add(_powerInput);
            AddChild(_powerInput);
            AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice)
        }

        // powered when its power input is getting its watts (set by PowerNet each solve), or forced on for a headless test
        public bool IsPowered => DebugForcePower || (_powerInput != null && GodotObject.IsInstanceValid(_powerInput) && _powerInput.Powered);

        // the motor drum vibrates only when the pump is BOTH powered AND actually moving fluid (strawberry) — a powered
        // pump on a dead/empty line sits still. Flow shows on the passthrough output; Flowing on the consumer relay.
        public override bool DriveActive => IsPowered && Ports.Exists(p => p != null && (p.Flowing || Mathf.Abs(p.Flow) > 0.01f));

        // IPowerDevice — a pure consumer (mirror of GasPump)
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;
        public IReadOnlyList<ConnectionPort> PowerPorts => _powerPorts;
    }
}
