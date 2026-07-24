using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A fluid connection point on a container — a small cube the hose tool can look at + hose to (the fluid analog of
    // ConnectionPort; named HosePort to avoid clashing with the solver's SDG.Unturned.FluidPort record). Source (pushes
    // supply) or Consumer (draws intake). Lives on its OWN collision layer so the hose look-ray finds fluid ports
    // without hitting power ports (and the wire ray never hits these). Leaner than ConnectionPort: no switch triggers,
    // no per-solve occupancy shading — a fluid port is just source-or-consumer.
    public partial class HosePort : StaticBody3D
    {
        public const uint PortLayer = 1u << 11;   // hose look-ray raycasts this layer only (distinct from ConnectionPort's 1<<8)
        const float CubeSize = 0.14f;

        public FluidContainer Owner;      // the container this port sits on
        public FluidPortNode Node;        // the data port the FluidSolver/FluidNet drive (Flow/Flowing/Load written here)
        public FluidPortKind Kind;
        public FluidType TypeOverride = FluidType.None;   // a TRANSFORMER's ports carry a fixed fluid (in!=out) independent of the (null) tank
        public bool Usable => GodotObject.IsInstanceValid(Owner) && !Owner.Blocked;   // a clogged/valve-off container can't start or accept a hose

        // the fluid this port carries for the type-lock: an explicit override (transformer in/out) else the tank's fluid
        public FluidType EffectiveType => TypeOverride != FluidType.None ? TypeOverride : (Owner != null && Owner.Tank != null ? Owner.Tank.Type : FluidType.None);

        MeshInstance3D _cube;
        StandardMaterial3D _mat;
        Node3D _arrow; StandardMaterial3D _arrowMat;   // in/out flow arrow (mirror of ConnectionPort), shown while the hose tool is out

        // source = green (pushes out), consumer = orange (draws in), passthrough = cyan (a fitting relays it) — power palette
        static Color BaseColor(FluidPortKind k) => k switch
        {
            FluidPortKind.Source => new Color(0.25f, 0.85f, 0.30f),
            FluidPortKind.Passthrough => new Color(0.30f, 0.75f, 0.95f),
            _ => new Color(0.95f, 0.55f, 0.15f),
        };

        public static HosePort Create(FluidContainer owner, FluidPortNode node, Vector3 localPos)
        {
            var fp = new HosePort
            {
                Owner = owner, Node = node, Kind = node.Kind, Position = localPos,
                CollisionLayer = PortLayer, CollisionMask = 0,   // detectable, but collides with nothing
                Visible = false,   // fluid IO only shows while the hose tool is out (strawberry); the collider stays live for the look-ray
            };
            fp._mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Metallic = 0f, Roughness = 0.55f };
            fp._cube = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * CubeSize }, MaterialOverride = fp._mat };
            fp.AddChild(fp._cube);
            fp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * CubeSize } });
            fp.BuildArrow();
            fp.SetHighlight(PortHi.None);
            fp.AddToGroup("fluid_ports");   // PlayerController can toggle/scan every fluid port when the hose tool is out
            return fp;
        }

        // An in/out flow arrow on the port (mirror of ConnectionPort's) — points OUT of the device for a source/passthrough,
        // IN for a consumer. Hidden until the hose tool is out; blue where you can hose, red where the port is occupied.
        void BuildArrow()
        {
            var pk = Kind == FluidPortKind.Consumer ? DeployableDef.PortKind.Consumer
                   : Kind == FluidPortKind.Passthrough ? DeployableDef.PortKind.Passthrough : DeployableDef.PortKind.Output;
            // fluid ports live in Godot Y-up (not power's flat stand-up frame), so the outward normal is the HORIZONTAL
            // direction from the device centre to the port (its X,Z). Passing it explicitly keeps the arrow perpendicular
            // to the cube face instead of pointing at the sky (strawberry).
            Vector3 horiz = new Vector3(Position.X, 0f, Position.Z);
            Vector3 outDir = horiz.LengthSquared() > 1e-6f ? horiz.Normalized() : Vector3.Forward;
            _arrowMat = ConnectionPort.ArrowMaterial(ConnectionPort.ArrowBlue);
            _arrow = ConnectionPort.MakeArrow(new DeployableDef.Port { Kind = pk, Pos = Position }, _arrowMat, Vector3.Zero, outDir);
            _arrow.Visible = false;
            AddChild(_arrow);
        }

        // Show/hide the arrow (the hose tool toggles it every frame it's out); blue = free/usable, red = occupied/unusable.
        public void SetArrowState(bool show, bool available)
        {
            if (_arrow == null) return;
            if (_arrow.Visible != show) _arrow.Visible = show;
            if (show && _arrowMat != null) _arrowMat.AlbedoColor = available ? ConnectionPort.ArrowBlue : ConnectionPort.ArrowRed;
        }

        // Look-at HUD line — reflects the live flow through this port + the fluid it carries. `hosed` = a hose is attached
        // (the tool passes it): an OUT node (source/passthrough) with no hose AND no flow shows just its name, no flow
        // numbers (strawberry: "out nodes dont need flow info when they arent connected and dont have fluid flowing").
        public string InfoLine(bool hosed = false)
        {
            string name = Owner != null ? Owner.RoleLabel() : "Fluid";
            string fluid = FluidDef.Name(EffectiveType);
            // the meaningful throughput: a SOURCE reports what's actually DRAWN off it (Load); a passthrough/consumer
            // reports its Flow. Plain "flowing N mL/s" wording instead of the "N/s out · N/s drawn" telemetry (strawberry).
            float flow = Node == null ? 0f : Mathf.Abs(Kind == FluidPortKind.Source ? Node.Load : Node.Flow);
            bool flowing = flow > 0.01f || (Node != null && Node.Flowing);
            switch (Kind)
            {
                case FluidPortKind.Source:
                    // an OUT node with no hose AND no flow shows just its name — no flow numbers when idle + unconnected
                    if (!hosed && !flowing) return $"{name} ({fluid})";
                    return flowing ? $"{name} ({fluid}) — flowing {flow:0} mL/s" : $"{name} ({fluid}) — not flowing";
                case FluidPortKind.Passthrough:
                    if (!hosed && !flowing) return name;
                    return flowing ? $"{name} — flowing {flow:0} mL/s" : $"{name} — not flowing";
                default:   // Consumer (an IN node)
                    return flowing ? $"{name} ({fluid}) — taking {flow:0} mL/s" : $"{name} ({fluid}) — idle";
            }
        }

        // Hose-tool highlight: None = base green/orange; Focus = brighter on look-at; HoseOk/HoseBad = green/red while
        // routing a hose onto this port (valid vs occupied/incompatible target, e.g. a fluid-type mismatch); HoseWarn =
        // ORANGE — a legal target that WON'T flow without a pump (uphill / no-head source; strawberry). Still connectable.
        public enum PortHi { None, Focus, HoseOk, HoseBad, HoseWarn }
        static readonly Color FeedGreen = new Color(0.30f, 0.90f, 0.42f);
        static readonly Color FeedRed = new Color(0.95f, 0.28f, 0.28f);
        static readonly Color FeedOrange = new Color(0.98f, 0.62f, 0.12f);   // needs-a-pump warn

        public void SetHighlight(PortHi state)
        {
            if (_mat == null) return;
            switch (state)
            {
                case PortHi.HoseOk: Feedback(FeedGreen); break;
                case PortHi.HoseBad: Feedback(FeedRed); break;
                case PortHi.HoseWarn: Feedback(FeedOrange); break;
                case PortHi.Focus:
                    _mat.AlbedoColor = BaseColor(Kind);
                    _mat.EmissionEnabled = true; _mat.Emission = BaseColor(Kind).Lightened(0.55f); _mat.EmissionEnergyMultiplier = 1.1f;
                    break;
                default:
                    _mat.AlbedoColor = BaseColor(Kind);
                    _mat.EmissionEnabled = false; _mat.EmissionEnergyMultiplier = 0f;
                    break;
            }
        }

        void Feedback(Color c)
        {
            _mat.AlbedoColor = c;
            _mat.EmissionEnabled = true; _mat.Emission = c; _mat.EmissionEnergyMultiplier = 0.55f;
        }

        // the owning container was destroyed / picked up -> retire the cube + drop off the hose look-ray layer
        public void Deactivate() { Visible = false; CollisionLayer = 0; }
    }
}
