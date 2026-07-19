using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A gas-station pump (the Gas_Pump_0 map object) as a POWERED FLUID TANK (master): a 750w consumer port + a
    // FluidTank of fuel. While powered you can right-click it with a gas can in hand to pull fuel into the can
    // (min of the can's free space and the pump's remaining). Once drained it stays at 0 -- no respawn. Look-at gets a
    // screen-space outline (a glow duplicate of the pump mesh on the overlay layer) + an info tooltip (fuel / state).
    // It's a world FIXTURE (no HP/pickup); the world builder still draws the pump mesh + collider.
    public partial class GasPump : Node3D, IPowerDevice
    {
        public const float Watts = 750f;
        // where the orange input cube sits in the pump's LOCAL (flat-authored) frame: raw Z is the pump's height, so
        // Z=1.2 is ~waist up when the map stands it upright; Y=-0.4 puts it just off one of the thin faces.
        public static readonly Vector3 PortLocal = new Vector3(0f, -0.4f, 1.2f);

        public int StationId;   // pumps sharing a stationId share one underground tank (map-editor field, or auto from position)
        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _input;
        public FluidTank Fluid => StationFuel.Tank(StationId);   // the shared station tank -- drained by extraction, never respawns

        // IPowerDevice
        public bool PowerProducing => false;   // a pure consumer, never a source
        public bool PowerOnFire => false;      // a map fixture doesn't burn
        public uint PowerNetId => 0;           // not replicated -> SP/local wiring only
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;
        public bool IsPowered => _input != null && GodotObject.IsInstanceValid(_input) && _input.Powered;   // on/off flag: getting its 750w

        // look-at outline (glow duplicate on the overlay layer) + info tooltip
        MeshInstance3D _glow;
        InfoBillboard _info;
        bool _focused;
        static readonly Color PumpColor = new Color(0.95f, 0.75f, 0.30f);   // warm amber for the fuel-tank outline / name

        public static GasPump Attach(Node parent, Vector3 pos, Basis basis, Vector3 portLocal, Mesh pumpMesh = null, int stationId = -1)
        {
            var gp = new GasPump { Transform = new Transform3D(basis, pos), StationId = stationId >= 0 ? stationId : StationFuel.StationIdFor(pos) };   // -1 -> auto-group by position
            parent.AddChild(gp);
            var port = ConnectionPort.Create(gp, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = portLocal, Watts = Watts }, "Gas Pump");
            gp.AddChild(port);
            gp._ports.Add(port);
            gp._input = port;
            gp.AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice now)
            if (pumpMesh != null)   // outline glow: a duplicate of the pump mesh on the overlay layer, hidden until looked at (matches the pump transform since gp shares it)
                gp.AddChild(gp._glow = new MeshInstance3D
                {
                    Mesh = pumpMesh, Visible = false, Layers = OutlineOverlay.OutlineLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                });
            gp.AddChild(gp._info = new InfoBillboard { TopLevel = true });
            if (gp.GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)   // one PowerManager ticks the whole net (else a placed generator makes it)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            PowerNet.MarkDirty();
            return gp;
        }

        // Right-click extract (master): fill the can as much as possible = min(can free space, our remaining fuel).
        // Only draws while powered. Returns the fuel actually moved. Once we hit 0 we stay there (no respawn).
        public float Extract(float canSpace) => IsPowered ? Fluid.Drain(canSpace) : 0f;

        public void SetLookFocused(bool on)
        {
            if (_focused == on) return;
            _focused = on;
            if (_glow != null) _glow.Visible = on;
            if (on) WorldItem.FocusColor = PumpColor;   // OutlineOverlay tints the rim with this
            _info?.SetActive(on);
        }

        public override void _Process(double delta)
        {
            if (!_focused || _info == null) return;   // only the looked-at pump keeps its tooltip live
            _info.GlobalPosition = GlobalPosition + Vector3.Up * 2.6f;   // float above the ~2.4m pump
            _info.SetName("Gas Pump", PumpColor);
            _info.SetBar(0, Fluid.Capacity > 0f ? Mathf.Clamp(Fluid.Amount / Fluid.Capacity, 0f, 1f) : 0f, PumpColor);
            string state = Fluid.IsEmpty ? "empty" : (IsPowered ? "[RMB] with a gas can to fill" : "no power");
            _info.SetPrompt($"{Fluid.Amount:0} / {Fluid.Capacity:0} fuel · {state}", PumpColor);
        }
    }
}
