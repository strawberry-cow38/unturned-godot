using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A fluid VALVE with electrical remote control (strawberry): besides the hose-tool-port RMB and the tap-F toggle, two
    // wired trigger ports let a power signal OPEN (TurnOn) or CLOSE (TurnOff) it -- exactly like a generator's remote
    // start/stop. Still a tankless inline switch (FluidRole.Valve); it just ALSO exposes 0-watt sense ports on the power net.
    public partial class FluidValve : FluidContainer, IPowerDevice
    {
        public uint NetId;   // MP replica id (0 = SP/local)
        readonly List<ConnectionPort> _powerPorts = new();
        ConnectionPort _onTrigger, _offTrigger;

        public static FluidValve Make() => new FluidValve { Role = FluidRole.Valve, Tank = null };

        protected override void OnReadyExtra()
        {
            // green OPEN trigger (left) + red CLOSE trigger (right), on top by the handle wheel. 0-watt sense inputs (a
            // >=1w signal flips the valve, drawing nothing) -- the exact mirror of the generator's TurnOn/TurnOff ports.
            _onTrigger = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Role = DeployableDef.SwitchRole.TurnOn, Pos = new Vector3(-0.32f, 1.2f, 0f), Watts = 0f }, "Fluid Valve");
            _offTrigger = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Role = DeployableDef.SwitchRole.TurnOff, Pos = new Vector3(0.32f, 1.2f, 0f), Watts = 0f }, "Fluid Valve");
            _powerPorts.Add(_onTrigger); _powerPorts.Add(_offTrigger);
            AddChild(_onTrigger); AddChild(_offTrigger);
            AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice) for the trigger ports
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            // a wired >=1w sense on a trigger opens/closes the valve (SetValveOpen no-ops if already in that state)
            if (_onTrigger != null && GodotObject.IsInstanceValid(_onTrigger) && _onTrigger.Live >= 1f) SetValveOpen(true);
            else if (_offTrigger != null && GodotObject.IsInstanceValid(_offTrigger) && _offTrigger.Live >= 1f) SetValveOpen(false);
        }

        // picked up -> free any wires plugged into either trigger, then re-solve the net (base frees its hoses)
        protected override void OnPickup()
        {
            foreach (var n in GetTree().GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w) && (w.Source == _onTrigger || w.Consumer == _onTrigger || w.Source == _offTrigger || w.Consumer == _offTrigger))
                { w.RemoveFromGroup("wires"); w.QueueFree(); }
            PowerNet.MarkDirty();
        }

        // IPowerDevice -- a pure trigger host (0-watt sense ports; produces/consumes nothing)
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;
        public IReadOnlyList<ConnectionPort> PowerPorts => _powerPorts;
    }
}
