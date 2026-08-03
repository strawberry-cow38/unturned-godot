using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Power tap bolted onto a Street_Light_0 / Traffic_Light_0 world prop (master 2026-08-03). INTACT, the fixture's
    // surviving base carries ONE wire-able INPUT; SMASHED, it retires that input and reveals a wire-able OUTPUT that
    // taps the residual mains -- scavenge power out of wrecked infrastructure. Input and output NEVER coexist, so a
    // wire can't loop the output back into its own input (a solver cycle -- tinyclaw). The output is gated exactly
    // like the fixture it hangs off: a STREETLIGHT's output lives only while PowerNet.GlobalPower is up (a municipal
    // main -- dead in a blackout); a TRAFFIC signal's rides TrafficLight.BatteryBackup (the cabinet BBS keeps it alive
    // through an outage). A world FIXTURE like GasPump / GridPowerSource: an IPowerDevice on the "deployables" group,
    // with no HP / pickup / fire of its own -- the prop's DestructibleField owns the smash and drives SetBroken.
    public partial class LightTap : Node3D, IPowerDevice
    {
        public enum LightKind { Streetlight, Traffic }
        // A wrecked lamp taps the MUNICIPAL MAIN, not just its own ~200W HPS bulb, so the stub is worth more than the
        // fixture drew -- enough to run a real consumer (Spotlight 250W) with margin. Traffic = the cabinet feed.
        public const float StreetlightWatts = 500f;
        public const float TrafficWatts     = 300f;   // clears the 250W Spotlight (the smallest consumer in DeployableDef) with headroom

        LightKind _kind;
        float _outWatts;
        Vector3 _inLocal, _outLocal;
        string _label;
        bool _broken;
        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _input, _output;

        // IPowerDevice: produces ONLY once smashed AND the residual feed is live (streetlight: mains; traffic: BBS).
        public bool PowerProducing => _broken && OutputLive;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;   // SP-local map fixture (an MP replica id would go here, like GasPump / GridSource)
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;

        bool OutputLive => _kind == LightKind.Traffic ? TrafficLight.BatteryBackup : PowerNet.GlobalPower;

        /// <summary>Attach a tap at a light prop. pos/basis = the placement transform (WorldBuilder.PlaceObject);
        /// inLocal / outLocal are port positions in the prop's LOCAL frame (the flat authored frame the basis stands
        /// up, so +Z runs up the pole).</summary>
        public static LightTap Attach(Node parent, Vector3 pos, Basis basis, LightKind kind, Vector3 inLocal, Vector3 outLocal)
        {
            var t = new LightTap
            {
                _kind = kind, _outWatts = kind == LightKind.Traffic ? TrafficWatts : StreetlightWatts,
                _inLocal = inLocal, _outLocal = outLocal, _label = kind == LightKind.Traffic ? "Traffic Signal" : "Streetlight",
                Transform = new Transform3D(basis, pos),
            };
            parent.AddChild(t);
            t.BuildInput();                // intact: the base carries a wire-able INPUT
            t.AddToGroup("deployables");   // PowerNet gathers this group by IPowerDevice
            if (t.GetTree() is SceneTree tr && tr.GetNodesInGroup("powermgr").Count == 0)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            PowerNet.MarkDirty();
            return t;
        }

        /// <summary>Intact &lt;-&gt; smashed. INTACT exposes the base INPUT; SMASHED retires it and reveals the tappable
        /// OUTPUT (a live stub of the wrecked feed) -- never both, so a wire can't loop the output back into its own
        /// input. The base survives the break either way (master: "stays after they get destroyed"), it just swaps role.
        /// A port-set change marks the power net dirty so the solver re-runs (it's dirty-gated, not per-frame).</summary>
        public void SetBroken(bool broken)
        {
            if (_broken == broken) return;
            _broken = broken;
            if (broken) { FreePort(ref _input); BuildOutput(); }
            else        { FreePort(ref _output); BuildInput(); }
            PowerNet.MarkDirty();
        }

        void BuildInput()
        {
            if (_input != null) return;
            _input = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = _inLocal, Watts = 0f }, _label);
            AddChild(_input); _ports.Add(_input);
        }
        void BuildOutput()
        {
            if (_output != null) return;
            _output = ConnectionPort.Create(this, new DeployableDef.Port { Kind = DeployableDef.PortKind.Output, Pos = _outLocal, Watts = _outWatts }, _label);
            AddChild(_output); _ports.Add(_output);
        }
        void FreePort(ref ConnectionPort p)
        {
            if (p == null) return;
            _ports.Remove(p); p.QueueFree(); p = null;
        }

        // --- test seams ---
        public bool BrokenForTest => _broken;
        public bool ProducingForTest => PowerProducing;
        public bool HasOutputForTest => _output != null;
        public bool HasInputForTest => _input != null;
    }
}
