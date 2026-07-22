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
    }
}
