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
        // where the input cube sits in the pump's LOCAL (flat-authored) frame: raw Z is the pump's height (small Z = low,
        // below the band), authored X = the horizontal side (+X = right). Master: right side, bottom, below the band.
        public static readonly Vector3 PortLocal = new Vector3(0.45f, -0.3f, 0.25f);   // UG_GPP tunes it in UG_DEVIO

        public int StationId;   // pumps sharing a stationId share one underground tank (map-editor field, or auto from position)
        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _input;
        public FluidTank Fluid => StationFuel.Tank(StationId);   // the shared station tank -- drained by extraction, never respawns (DIRECT SP only)

        // A2 (SP/MP-unify): the replicated server entity this node mirrors (0 = a direct SP/local pump from Attach).
        // A joined client / consuming loopback materializes the pump via Materialize and stamps the server NetId, so
        // an RMB extract routes over the wire (PlayerController.TryExtractFuel) instead of draining a local tank.
        public uint NetId;

        // A2: on a client replica (Materialize) the fuel bar is driven by the REPLICATED 0..100 percent of the
        // shared station tank (the absolute litres stay server-side), set each tick by DeployableReplicaView from
        // entity.Fuel. The replica owns NO FluidTank -- Extract is server-routed, never a local Drain.
        public float FillPercent;

        // IPowerDevice
        public bool PowerProducing => false;   // a pure consumer, never a source
        public bool PowerOnFire => false;      // a map fixture doesn't burn
        public uint PowerNetId => NetId;       // review H1: 0 for a direct SP/local pump (Attach), the server NetId for a replica (Materialize) so an interactive wire routes over the wire -- mirrors GridPowerSource; was hardcoded 0 => replica pumps could never be powered server-side (extract dead)
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

        // A2 (SP/MP-unify): materialize a gas pump from its REPLICATED entity (DeployableReplicaView) -- a
        // self-contained node stamped with the server NetId, its one 750 W Consumer port hung off PortLocal, in
        // the "deployables" group the local PowerNet reads (so a wired source lights _input.Powered exactly as in
        // SP, from replicated INPUTS). Owns NO FluidTank; the fuel bar rides FillPercent (set from entity.Fuel).
        // Adds its OWN gaspump-meta interaction collider (the world mesh's collider is no longer tagged under the
        // consume paradigm). Positioned at the quantized entity pos with the standard flat->upright pump basis.
        public static GasPump Materialize(Node parent, Vector3 pos, float yawDegrees, uint netId)
        {
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(yawDegrees)) * new Basis(Vector3.Right, Mathf.DegToRad(-90f));   // stand the flat-authored pump upright (same as SpawnEditorGasPump)
            var gp = new GasPump { Transform = new Transform3D(basis, pos), NetId = netId, StationId = -2 };   // StationId -2: a replica NEVER touches StationFuel (server owns the tank)
            parent.AddChild(gp);
            var port = ConnectionPort.Create(gp, new DeployableDef.Port { Kind = DeployableDef.PortKind.Consumer, Pos = PortLocal, Watts = Watts }, "Gas Pump");
            gp.AddChild(port);
            gp._ports.Add(port);
            gp._input = port;
            gp.AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice)
            gp.AddChild(gp._info = new InfoBillboard { TopLevel = true });
            gp.AddInteractionCollider();
            if (gp.GetTree() is SceneTree t && t.GetNodesInGroup("powermgr").Count == 0)
            { var pm = new PowerManager(); pm.AddToGroup("powermgr"); parent.AddChild(pm); }
            PowerNet.MarkDirty();
            return gp;
        }

        // A2: a self-contained gaspump-meta collider so the look-ray focuses the pump (outline + tooltip +
        // RMB-extract) without relying on WorldBuilder tagging the world mesh's collider (which it no longer
        // does under the consume paradigm). A world-space box wrapping the standing pump, on the small-prop
        // look layer (1<<6, in PlayerController's look-ray mask); slightly oversized so it wins the coincident
        // world collider along the ray. Used by Materialize (replica) and the pure-direct SpawnFixturesDirect.
        StaticBody3D _hitBody;
        public void AddInteractionCollider()
        {
            _hitBody = new StaticBody3D { TopLevel = true, Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * 1.2f), CollisionLayer = 1u << 6, CollisionMask = 0 };
            _hitBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.1f, 2.6f, 1.1f) } });
            _hitBody.SetMeta("gaspump", this);   // look-ray hits this -> resolve the GasPump (PlayerController:175)
            AddChild(_hitBody);
        }

        // Right-click extract (master): fill the can as much as possible = min(can free space, our remaining fuel).
        // Only draws while powered. Returns the fuel actually moved. Once we hit 0 we stay there (no respawn).
        // DIRECT SP only -- a replica (NetId != 0) NEVER calls this; its extract is server-routed (TryExtractFuel).
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
            if (NetId != 0)   // A2 replica: the bar rides the REPLICATED station-fill percent (no local FluidTank)
            {
                float frac = Mathf.Clamp(FillPercent / 100f, 0f, 1f);
                _info.SetBar(0, frac, PumpColor);
                string rstate = FillPercent <= 0.1f ? "empty" : (IsPowered ? "[RMB] with a gas can to fill" : "no power");
                _info.SetPrompt($"{FillPercent:0}% station fuel · {rstate}", PumpColor);
                return;
            }
            _info.SetBar(0, Fluid.Capacity > 0f ? Mathf.Clamp(Fluid.Amount / Fluid.Capacity, 0f, 1f) : 0f, PumpColor);
            string state = Fluid.IsEmpty ? "empty" : (IsPowered ? "[RMB] with a gas can to fill" : "no power");
            _info.SetPrompt($"{Fluid.Amount:0} / {Fluid.Capacity:0} fuel · {state}", PumpColor);
        }
    }
}
