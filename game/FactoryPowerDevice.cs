using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // An Asset Factory prop that joins the power grid (the "power in/outs" behaviour). AssetBundleLoader
    // swaps a prop's root to this when the bundle declares a power_kind, bolts on a ConnectionPort, and
    // adds it to the "deployables" group PowerNet scans — so a composed generator / powered device is a
    // first-class member of the existing electrical net (ConnectionPort / Wire / PowerNet / PowerSolver).
    public partial class FactoryPowerDevice : StaticBody3D, IPowerDevice
    {
        readonly List<ConnectionPort> _ports = new();
        public bool IsSource;   // an Output device produces power (a generator); consumer/passthrough don't

        public bool PowerProducing => IsSource;
        public bool PowerOnFire => false;               // factory props don't wreck-and-stop-conducting (yet)
        public uint PowerNetId => 0;                    // SP / local / world fixture
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;
        // PowerConducting (=> true) + PowerScale (=> 1) come from IPowerDevice's default members.

        public void AddPort(ConnectionPort p) { _ports.Add(p); AddChild(p); }

        // powered-flag behaviour: a light that only shines while a consumer port on this device has power.
        OmniLight3D _light;
        public void AddPoweredLight(float energy, Color color, float range)
        {
            _light = new OmniLight3D { LightEnergy = energy, LightColor = color, OmniRange = range, Position = new Vector3(0f, 1f, 0f), Visible = false };
            AddChild(_light);
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            if (_light == null) return;
            bool powered = false;
            foreach (var p in _ports) if (p.Powered) { powered = true; break; }
            _light.Visible = powered;
        }
    }
}
