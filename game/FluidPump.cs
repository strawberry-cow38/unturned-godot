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
        ConnectionPort _powerInput, _onTrigger, _offTrigger;
        bool _remoteOn = true;   // electrical remote enable (default ON); a wired TurnOff trigger disables the pump, TurnOn re-enables it
        bool _hasWork = true;    // AUTO-SHUTOFF: the line has both supply + demand (set each fluid tick by FluidNet); default ON so tick 1 doesn't stall

        // FluidNet's auto-shutoff sets this each tick: does the pump's connected line have both a supplying source AND a
        // demanding sink? If not (target full / source dry), the pump idles -> no lift, 0w draw. Tick-driven (not _Process)
        // so the draw is authoritative even in headless tests, which call FluidNet.Tick directly.
        public void SetHasWork(bool w)
        {
            _hasWork = w;
            float wantWatts = (_remoteOn && _hasWork) ? PumpWatts : 0f;   // draw PumpWatts only while remote-enabled AND with work
            if (_powerInput != null && GodotObject.IsInstanceValid(_powerInput) && _powerInput.Watts != wantWatts) { _powerInput.Watts = wantWatts; PowerNet.MarkDirty(); }
        }

        internal bool DebugHasWork => _hasWork;                                                                     // L1 probe
        internal float DebugInputWatts => _powerInput != null && GodotObject.IsInstanceValid(_powerInput) ? _powerInput.Watts : -1f;   // L1 probe

        public static FluidPump Make(float headLift = 6f)
            => new FluidPump { Role = FluidRole.Pump, Tank = null, HeadLift = headLift };

        protected override void OnReadyExtra()
        {
            // the power CONSUMER side: one input cube drawing PumpWatts on the power net's "deployables" group. A wire
            // tool run from a generator to this cube powers the pump exactly as a gas pump / spotlight is powered.
            _powerInput = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = new Vector3(0f, 1.25f, 0.42f), Watts = PumpWatts }, "Fluid Pump");
            // + electrical remote control (strawberry): a green OPEN/enable trigger + a red CLOSE/disable trigger, 0-watt
            // sense inputs (a >=1w signal flips _remoteOn, drawing nothing) -- the mirror of the generator's remote start/stop.
            _onTrigger = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Role = DeployableDef.SwitchRole.TurnOn, Pos = new Vector3(-0.32f, 1.25f, -0.42f), Watts = 0f }, "Fluid Pump");
            _offTrigger = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Role = DeployableDef.SwitchRole.TurnOff, Pos = new Vector3(0.32f, 1.25f, -0.42f), Watts = 0f }, "Fluid Pump");
            _powerPorts.Add(_powerInput); _powerPorts.Add(_onTrigger); _powerPorts.Add(_offTrigger);
            AddChild(_powerInput); AddChild(_onTrigger); AddChild(_offTrigger);
            AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice)
        }

        public override void _Process(double delta)
        {
            base._Process(delta);   // FluidContainer drives the motor-drum vibration
            // a wired >=1w sense on a trigger remotely enables/disables the pump (mirror of the generator's remote start/stop)
            if (_onTrigger != null && GodotObject.IsInstanceValid(_onTrigger) && _onTrigger.Live >= 1f) _remoteOn = true;
            else if (_offTrigger != null && GodotObject.IsInstanceValid(_offTrigger) && _offTrigger.Live >= 1f) _remoteOn = false;
            // the actual power draw (0w when remote-off / idle) is set tick-driven in SetHasWork, so it holds in headless tests too
        }

        // powered when REMOTE-ENABLED, has WORK to do, and its power input is getting its watts (set by PowerNet each solve),
        // or forced on for a headless test. A remote-off / idle pump provides no head lift (like an unpowered one).
        public bool IsPowered => _remoteOn && _hasWork && (DebugForcePower || (_powerInput != null && GodotObject.IsInstanceValid(_powerInput) && _powerInput.Powered));

        // the motor drum vibrates only when the pump is BOTH powered AND actually moving fluid (strawberry) — a powered
        // pump on a dead/empty line sits still. Flow shows on the passthrough output; Flowing on the consumer relay.
        public override bool DriveActive => IsPowered && Ports.Exists(p => p != null && (p.Flowing || Mathf.Abs(p.Flow) > 0.01f));

        // at-a-glance status (strawberry polish): the pump answers "why isn't my water moving?" without the hose tool.
        // off (remote-disabled) → no power (wire it) → idle (line has no supply/sink) → pumping.
        public override (string text, Color color) StatusLine()
        {
            if (!_remoteOn) return ("off", StatusOff);   // an electrical TurnOff trigger disabled it
            bool wired = DebugForcePower || (_powerInput != null && GodotObject.IsInstanceValid(_powerInput) && _powerInput.Powered);
            if (!wired) return ("no power", StatusWarn);   // needs a wire from a generator
            if (!_hasWork) return ("idle — no supply", StatusIdle);   // powered but the line has no source to draw / nowhere to push
            return ("pumping", StatusGo);
        }

        // picked up -> also free any wire plugged into the input or a trigger cube (the base frees its hoses), then re-solve
        protected override void OnPickup()
        {
            foreach (var n in GetTree().GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w) && _powerPorts.Exists(pp => w.Source == pp || w.Consumer == pp))
                { w.RemoveFromGroup("wires"); w.QueueFree(); }
            PowerNet.MarkDirty();
        }

        // IPowerDevice — a pure consumer (mirror of GasPump)
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;
        public IReadOnlyList<ConnectionPort> PowerPorts => _powerPorts;
    }
}
