using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The Pump Jack: a nodding-donkey derrick that lifts CRUDE OIL out of the ground while it has
    /// power (master 2026-09-10: "wire the pumpjack deployable. takes power input to work, has a fluid output
    /// for crude oil when powered").
    ///
    /// It is the first fluid SOURCE the port has that MAKES its fluid rather than merely holding some. The
    /// water reservoir is a filled tank hosed to whatever you like; a hydrant is the mains; both only ever
    /// hand out what already exists. This one pumps: while powered it fills its own tank at PumpRateMlPerSec
    /// and hoses draw from that, and while unpowered it produces nothing AND supplies nothing -- an
    /// oil derrick with the power cut is a lump of metal, not a tank with a tap.
    ///
    /// Built on the two seams the fluid system already has for exactly this rather than on new machinery:
    ///   SupplyEnabled  -- a source that is off must go dead IN THE SOLVER, or it keeps advertising supply
    ///                     that never arrives and every pump downstream stays awake waiting for it. That is
    ///                     the reason that seam exists (the municipal water shutoff uses it) and it is the
    ///                     reason this uses it instead of just refusing to produce.
    ///   OnPostTick     -- tick-driven, not _Process, so the production is authoritative headlessly too.
    /// The power half is FluidPurifier's, unchanged: one Consumer port on the "deployables" group that a
    /// generator wires to like any other appliance.</summary>
    public partial class PumpJack : FluidContainer, IPowerDevice
    {
        /// <summary>Drawn off the net while wired. Heavier than the purifier's 750 W: this is a beam pump
        /// hauling a rod string, not a filter.</summary>
        public const float PumpJackWatts = 1500f;

        /// <summary>Crude lifted per second while powered (mL/s). Deliberately SLOWER than the 125 mL/s
        /// garden-hose flow the fittings move: the derrick should be the bottleneck, so a tank downstream
        /// fills at the rate the well produces rather than at the rate the pipe can carry. A well you can
        /// drain faster than it fills is the interesting one.</summary>
        public const float PumpRateMlPerSec = 40f;

        /// <summary>What it holds at the wellhead before a hose takes it away (mL).</summary>
        public const float WellheadCapacityMl = 20000f;

        public uint NetId;              // MP replica id (0 = SP/local)
        public bool DebugForcePower;    // headless tests: pretend it is wired + powered

        readonly List<ConnectionPort> _powerPorts = new();
        ConnectionPort _powerInput;

        /// <summary>An OIL source with a wellhead tank that starts EMPTY -- it has pumped nothing yet. The
        /// base FluidContainer builds the source port a hose connects to.</summary>
        public static PumpJack Make()
            => new PumpJack { Role = FluidRole.Source, Tank = new FluidTank(FluidType.Oil, WellheadCapacityMl, 0f), FlowRate = 125f };

        protected override void OnReadyExtra()
        {
            _powerInput = ConnectionPort.Create(this, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.Consumer,
                Pos = FluidElectricalPanel.Anchor(DeployableDef.PumpJackId),
                Watts = PumpJackWatts,
            }, "Pump Jack");
            _powerPorts.Add(_powerInput);
            AddChild(_powerInput);
            AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice)
        }

        public bool IsPowered => DebugForcePower
                              || (_powerInput != null && GodotObject.IsInstanceValid(_powerInput) && _powerInput.Powered);

        /// <summary>Dead in the SOLVER without power, not merely unproductive -- see the class note.</summary>
        public override bool SupplyEnabled => IsPowered;

        /// <summary>Lift crude while powered. Capped at the wellhead's capacity: a derrick nobody has hosed up
        /// fills its tank and then idles, exactly like a real one waiting on a truck.</summary>
        public override void OnPostTick(float dt)
        {
            if (!IsPowered || Tank == null || dt <= 0f) return;
            Tank.Fill(PumpRateMlPerSec * dt);   // Fill clamps to the remaining Space and reports what went in
        }

        /// <summary>No power (wire it) -> pumping -> full and waiting on a hose. Same three-state shape the
        /// purifier uses, because the questions a player asks at a dead machine are the same ones.</summary>
        public override (string text, Color color) StatusLine()
        {
            if (!IsPowered) return ("No power", StatusWarn);
            if (Tank != null && Tank.Space <= 0.001f) return ("Wellhead full", StatusIdle);
            return ("Pumping crude", StatusGo);
        }

        // picked up -> free any wire plugged into the power input (the base frees the hoses), then re-solve
        protected override void OnPickup()
        {
            foreach (var n in GetTree().GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w) && (w.Source == _powerInput || w.Consumer == _powerInput))
                { w.RemoveFromGroup("wires"); w.QueueFree(); }
            PowerNet.MarkDirty();
        }

        // IPowerDevice -- a pure consumer, like the purifier and the fluid pump
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;
        public IReadOnlyList<ConnectionPort> PowerPorts => _powerPorts;
    }
}
