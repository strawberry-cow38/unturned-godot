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
        public bool Usable => GodotObject.IsInstanceValid(Owner) && !Owner.Blocked;   // a clogged/valve-off container can't start or accept a hose

        MeshInstance3D _cube;
        StandardMaterial3D _mat;

        // source = green (pushes fluid out), consumer/storage = orange (draws fluid in) — mirrors the power port palette
        static Color BaseColor(FluidPortKind k) => k == FluidPortKind.Source
            ? new Color(0.25f, 0.85f, 0.30f)
            : new Color(0.95f, 0.55f, 0.15f);

        public static HosePort Create(FluidContainer owner, FluidPortNode node, Vector3 localPos)
        {
            var fp = new HosePort
            {
                Owner = owner, Node = node, Kind = node.Kind, Position = localPos,
                CollisionLayer = PortLayer, CollisionMask = 0,   // detectable, but collides with nothing
            };
            fp._mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Metallic = 0f, Roughness = 0.55f };
            fp._cube = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * CubeSize }, MaterialOverride = fp._mat };
            fp.AddChild(fp._cube);
            fp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * CubeSize } });
            fp.SetHighlight(PortHi.None);
            fp.AddToGroup("fluid_ports");   // PlayerController can toggle/scan every fluid port when the hose tool is out
            return fp;
        }

        // Look-at HUD line — reflects the live flow through this port + the fluid it carries.
        public string InfoLine()
        {
            string fluid = FluidDef.Name(Owner != null && Owner.Tank != null ? Owner.Tank.Type : FluidType.None);
            return Kind == FluidPortKind.Source
                ? $"{Owner?.Role} ({fluid}) — {Node.Rate:0}/s out · {Node.Load:0}/s drawn"
                : $"{Owner?.Role} ({fluid}) — intake {(Node.Flowing ? $"{Node.Flow:0}/s in" : "idle")}";
        }

        // Hose-tool highlight: None = base green/orange; Focus = brighter on look-at; HoseOk/HoseBad = green/red while
        // routing a hose onto this port (valid vs occupied/incompatible target, e.g. a fluid-type mismatch).
        public enum PortHi { None, Focus, HoseOk, HoseBad }
        static readonly Color FeedGreen = new Color(0.30f, 0.90f, 0.42f);
        static readonly Color FeedRed = new Color(0.95f, 0.28f, 0.28f);

        public void SetHighlight(PortHi state)
        {
            if (_mat == null) return;
            switch (state)
            {
                case PortHi.HoseOk: Feedback(FeedGreen); break;
                case PortHi.HoseBad: Feedback(FeedRed); break;
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
