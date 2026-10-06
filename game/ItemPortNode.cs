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
            ip._arrow = ConnectionPort.MakeArrow(new DeployableDef.Port { Kind = pk, Pos = p.Pos }, ip._arrowMat, Vector3.Zero);
            ip._arrow.Visible = false;
            ip.AddChild(ip._arrow);
            ip.SetHighlight(PortHi.None);
            ip.AddToGroup("item_ports");
            return ip;
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

        /// <summary>The owner died: retire the cube and drop it off the pipe ray's layer.</summary>
        public void Deactivate() { Visible = false; CollisionLayer = 0; }
    }
}
