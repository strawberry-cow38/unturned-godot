using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // An ITEM connection point on a placed deployable (v56 industrial pipes, strawberry 2026-10-06) -- the pipe
    // tool's target, the way a ConnectionPort is the wire tool's and a HosePort the hose tool's.
    //
    // ⚠ ITS OWN COLLISION LAYER, AND THAT IS THE WHOLE "WIRE TOOL MUST NOT CONNECT ITEM PORTS" RULE. The wire
    // look-ray masks ConnectionPort.PortLayer (bit 8), the hose ray HosePort.PortLayer (bit 11), the pipe ray this
    // (bit 21) -- so no tool can even SEE another tool's sockets, and nothing has to remember to check a kind.
    // Bit 21 was free at the time of writing: 0-15 and 20 are taken as physics layers (wheel debris 2, player 3,
    // elevator/editor 10, hose + shelf items 11, editor 12, chassis 13, remote players 14, hit mesh 15, resources
    // 20), and 16-19 are spoken for as render layers.
    //
    // COLOURS: amber for Out (items leave), violet for In (items arrive) -- deliberately neither the power palette
    // (grey I/O, green/orange triggers, cyan data) nor the fluid one (green/orange), so a glance tells you which
    // tool a socket wants.
    public partial class ItemPortNode : StaticBody3D
    {
        public const uint PortLayer = 1u << 21;
        const float CubeSize = 0.15f;

        public new Deployable Owner;   // `new`: the domain owner, not Node.Owner (same convention as ConnectionPort/HosePort)
        public byte Index;             // the pipe sub-address: this port's index in DeployableDef.ItemPorts
        public ItemPortDir Dir;
        public string ProviderName;
        public bool Usable => GodotObject.IsInstanceValid(Owner) && !Owner.OnFire;
        public uint OwnerNetId => GodotObject.IsInstanceValid(Owner) ? Owner.NetId : 0;

        static readonly Color OutColor = new Color(0.98f, 0.66f, 0.14f);   // amber: items leave here
        static readonly Color InColor = new Color(0.62f, 0.38f, 0.95f);    // violet: items arrive here
        static readonly Color FeedGreen = new Color(0.30f, 0.90f, 0.42f);
        static readonly Color FeedRed = new Color(0.95f, 0.28f, 0.28f);

        MeshInstance3D _cube;
        StandardMaterial3D _mat;
        Node3D _arrow; StandardMaterial3D _arrowMat;

        public static ItemPortNode Create(Deployable owner, DeployableDef.ItemPort p, byte index, string providerName)
        {
            var ip = new ItemPortNode
            {
                Owner = owner, Index = index, Dir = p.Dir, ProviderName = providerName, Position = p.Pos,
                CollisionLayer = PortLayer, CollisionMask = 0,   // detectable by the pipe ray, collides with nothing
                Visible = false,                                 // shown only while the pipe tool is out, like the hose's sockets
            };
            ip._mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Metallic = 0.2f, Roughness = 0.5f };
            ip._cube = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * CubeSize }, MaterialOverride = ip._mat };
            ip.AddChild(ip._cube);
            ip.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * CubeSize } });
            // the in/out arrow, reusing the power port's glyph: OUT points away from the box, IN points into it
            ip._arrowMat = ConnectionPort.ArrowMaterial(ConnectionPort.ArrowBlue);
            var pk = p.Dir == ItemPortDir.In ? DeployableDef.PortKind.Consumer : DeployableDef.PortKind.Output;
            ip._arrow = ConnectionPort.MakeArrow(new DeployableDef.Port { Kind = pk, Pos = p.Pos }, ip._arrowMat, Vector3.Zero,
                                                 FaceNormal(p.Pos, owner?.Def?.Size ?? Vector3.Zero));
            ip._arrow.Visible = false;
            ip.AddChild(ip._arrow);
            ip.SetHighlight(PortHi.None);
            ip.AddToGroup("item_ports");
            return ip;
        }

        /// <summary>The outward normal of the box face a socket sits on, in the flat authored frame.
        ///
        /// MakeArrow's own rule takes whichever of X/Y is BIGGER in the raw position, which assumes the box is as
        /// wide as it is deep. The splitter and combiner are 0.80 wide and 0.36 deep, so their OUTER sockets
        /// (x = +-0.26 on the +-0.18 face) won on X and drew their arrows SIDEWAYS along the face -- seen in the
        /// first render of the chain, where the splitter's outer outputs pointed at each other. Measured against the
        /// box's own half-extents instead, a socket on a face is at 100% of that axis and nowhere near it on the
        /// other. Zero (no size) falls back to MakeArrow's rule.</summary>
        public static Vector3 FaceNormal(Vector3 pos, Vector3 size)
        {
            if (size.X <= 0f || size.Y <= 0f) return Vector3.Zero;
            float rx = Mathf.Abs(pos.X) / (size.X * 0.5f), ry = Mathf.Abs(pos.Y) / (size.Y * 0.5f);
            if (rx < 1e-3f && ry < 1e-3f) return Vector3.Zero;
            return rx >= ry ? new Vector3(Mathf.Sign(pos.X), 0f, 0f) : new Vector3(0f, Mathf.Sign(pos.Y), 0f);
        }

        public enum PortHi { None, Focus, PipeOk, PipeBad }

        public void SetHighlight(PortHi state)
        {
            if (_mat == null) return;
            Color c = Dir == ItemPortDir.Out ? OutColor : InColor;
            switch (state)
            {
                case PortHi.PipeOk: Feedback(FeedGreen); return;
                case PortHi.PipeBad: Feedback(FeedRed); return;
                case PortHi.Focus:
                    _mat.AlbedoColor = c;
                    _mat.EmissionEnabled = true; _mat.Emission = c.Lightened(0.5f); _mat.EmissionEnergyMultiplier = 1.1f;
                    return;
                default:
                    _mat.AlbedoColor = c;
                    _mat.EmissionEnabled = false; _mat.EmissionEnergyMultiplier = 0f;
                    return;
            }
        }

        void Feedback(Color c)
        {
            _mat.AlbedoColor = c;
            _mat.EmissionEnabled = true; _mat.Emission = c; _mat.EmissionEnergyMultiplier = 0.55f;
        }

        /// <summary>Show/hide the flow arrow; blue where a pipe can start/finish, red where the socket is taken.</summary>
        public void SetArrowState(bool show, bool available)
        {
            if (_arrow == null) return;
            if (_arrow.Visible != show) _arrow.Visible = show;
            if (show && _arrowMat != null) _arrowMat.AlbedoColor = available ? ConnectionPort.ArrowBlue : ConnectionPort.ArrowRed;
        }

        public string InfoLine(bool piped) => Dir == ItemPortDir.Out
            ? $"{ProviderName} -- item OUT{(piped ? " (piped)" : "")}"
            : $"{ProviderName} -- item IN{(piped ? " (piped)" : "")}";

        public bool DebugArrowVisible => _arrow != null && _arrow.Visible;
        /// <summary>L1 probe: the arrow's flow direction in the owner's frame (the glyph's +Y; the port cube is unrotated).</summary>
        public Vector3 DebugArrowFlow => _arrow != null ? _arrow.Basis.Y.Normalized() : Vector3.Zero;

        /// <summary>The owner died: retire the cube and drop it off the pipe ray's layer.</summary>
        public void Deactivate() { Visible = false; CollisionLayer = 0; }
    }
}
