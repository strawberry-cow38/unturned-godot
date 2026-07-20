using Godot;

namespace UnturnedGodot
{
    // A device the power net wires together: a placed Deployable (generator/spotlight/splitter/combiner) OR a powered
    // world fixture (gas pump). PowerNet reads these off the "deployables" group; a ConnectionPort's Owner is one.
    public interface IPowerDevice
    {
        bool PowerProducing { get; }   // a running generator; false for a pure consumer (spotlight/gas pump)
        bool PowerOnFire { get; }      // a burning/wrecked device stops conducting
        bool PowerConducting => true;  // default: conducts (relay/passthrough works). A SWITCH toggled OFF returns false -> its passthrough dies.
        float PowerScale => 1f;        // output-capacity multiplier: a GENERATOR ramps this 0..1 with its engine spin-up/cooldown (master); everything else = full
        uint PowerNetId { get; }       // MP replica id (0 = SP / local / world fixture)
        System.Collections.Generic.IReadOnlyList<ConnectionPort> PowerPorts { get; }
    }

    // A power connection point on a placed deployable: a small cube the wire tool can look at + wire to.
    // Output (produces watts while the source is on), Consumer (draws watts), or Passthrough (re-exports the
    // leftover). Lives on its own collision layer so the wire look-ray finds it without hitting anything else.
    public partial class ConnectionPort : StaticBody3D
    {
        public const uint PortLayer = 1u << 8;   // wire look-ray raycasts this layer only
        const float CubeSize = 0.13f;

        public IPowerDevice Owner;   // the deployable or fixture this port sits on (was Deployable; now any IPowerDevice, e.g. a gas pump)
        public DeployableDef.PortKind Kind;
        public DeployableDef.SwitchRole Role;   // a switch trigger port (TurnOn/TurnOff), else None
        public float Watts;         // output: produced; consumer: drawn; passthrough: unused
        public string ProviderName;
        public float Live;          // live power (recomputed by PowerNet): output = produced now, consumer = received, passthrough = exported now
        public bool Powered;        // consumer: is it getting at least its usage?
        public float Draw;          // output only: total wattage actually drawn by the powered consumers down its chain (the load)
        public bool Occupied;       // a wire is attached here -> the I/O cube shades darker (set each solve by PowerNet)
        public bool Usable => Owner is GodotObject go && GodotObject.IsInstanceValid(go) && !Owner.PowerOnFire;   // a burning/wrecked owner's ports can't start or accept a wire

        MeshInstance3D _cube;
        Node3D _arrow;
        StandardMaterial3D _mat, _arrowMat;
        public static readonly Color ArrowBlue = new Color(0.30f, 0.62f, 1f);   // blueprint-ghost blue (available)
        public static readonly Color ArrowRed = new Color(1f, 0.28f, 0.28f);    // blueprint-ghost red (wired / unusable)
        // I/O port cube fill (master): translucent grey, light when no wire is attached, darker when one is (Occupied).
        static readonly Color IoFree = new Color(0.80f, 0.80f, 0.84f, 0.48f);
        static readonly Color IoUsed = new Color(0.30f, 0.30f, 0.34f, 0.74f);

        static Color BaseColor(DeployableDef.PortKind k, DeployableDef.SwitchRole role) => role switch
        {
            DeployableDef.SwitchRole.TurnOn => new Color(0.30f, 0.90f, 0.40f),     // green: the "turn ON" trigger input
            DeployableDef.SwitchRole.TurnOff => new Color(0.95f, 0.30f, 0.30f),    // red: the "turn OFF" trigger input
            _ => k switch
            {
                DeployableDef.PortKind.Output => new Color(0.25f, 0.85f, 0.30f),        // green: produces power
                DeployableDef.PortKind.Consumer => new Color(0.95f, 0.55f, 0.15f),      // orange: draws power
                DeployableDef.PortKind.Passthrough => new Color(0.30f, 0.75f, 0.95f),   // cyan: re-exports leftover
                _ => Colors.White,
            },
        };

        public static ConnectionPort Create(IPowerDevice owner, DeployableDef.Port p, string providerName)
        {
            var cp = new ConnectionPort
            {
                Owner = owner, Kind = p.Kind, Role = p.Role, Watts = p.Watts, ProviderName = providerName,
                Position = p.Pos, CollisionLayer = PortLayer, CollisionMask = 0,   // detectable, but doesn't collide with anything
            };
            cp._mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Metallic = 0f, Roughness = 0.6f };
            if (p.Role == DeployableDef.SwitchRole.None) cp._mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;   // I/O ports translucent (master); triggers stay solid
            cp.UpdateCubeColor();   // grey-by-occupancy for I/O ports, green/red for switch triggers
            cp._cube = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * CubeSize }, MaterialOverride = cp._mat };
            cp.AddChild(cp._cube);
            cp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * CubeSize } });
            cp.BuildArrow(p.Kind);
            cp.AddToGroup("ports");   // PlayerController toggles all ports' arrows when the wire tool is out
            return cp;
        }

        // A small in/out arrow that reads where the port is + which way power flows: a cone that points OUT of the
        // deployable for a producer (output/passthrough) and IN toward it for a consumer. Local frame, so it stands up
        // with the model. Hidden until the wire tool is out (or on a placement ghost). Blueprint blue / red.
        void BuildArrow(DeployableDef.PortKind kind)
        {
            _arrowMat = ArrowMaterial(ArrowBlue);
            _arrow = MakeArrow(new DeployableDef.Port { Kind = kind, Pos = Position }, _arrowMat, Vector3.Zero);   // child of the port -> local origin is the port
            _arrow.Visible = false;
            AddChild(_arrow);
        }

        public static StandardMaterial3D ArrowMaterial(Color c) => new()
        {
            AlbedoColor = c, AlbedoTexture = ArrowTexture(), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        // A flat arrow glyph drawn once into an alpha texture: opaque white arrowhead + shaft on transparent, pointing
        // +Y (up) in texture space. White so the material's AlbedoColor tints it (blue available / red wired). Two
        // crossed quads wear this in MakeArrow -> a grass-billboard "X" that reads as an arrow from any angle.
        static Texture2D _arrowTex;
        static Texture2D ArrowTexture()
        {
            if (_arrowTex != null) return _arrowTex;
            const int N = 64;
            var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
            var clear = new Color(1f, 1f, 1f, 0f);
            for (int r = 0; r < N; r++)
                for (int c = 0; c < N; c++)
                {
                    bool inside;
                    if (r >= 3 && r <= 30) inside = Mathf.Abs(c - 32) <= (r - 3);        // head: apex at top, widening down
                    else if (r > 30 && r <= 58) inside = Mathf.Abs(c - 32) <= 9;         // shaft
                    else inside = false;
                    img.SetPixel(c, r, inside ? Colors.White : clear);
                }
            _arrowTex = ImageTexture.CreateFromImage(img);
            return _arrowTex;
        }

        // A flat in/out arrow for a port, in the flat authored frame: a grass-billboard "X" (two crossed textured quads)
        // whose arrow points OUT for a producer / IN for a consumer, sitting just outside the port. `basePos` = the port
        // position when the arrow parents the deployable (ghost), or Vector3.Zero when it parents the port cube itself.
        // Returns a Node3D root (holds the two crossed quads). Shared by placed ports + the placement ghost.
        public static Node3D MakeArrow(DeployableDef.Port p, StandardMaterial3D mat, Vector3 basePos)
        {
            // perpendicular to the port's OUTWARD FACE (master): ports sit on VERTICAL faces (authored Z is the up axis
            // after the stand-up), so the outward normal is the dominant HORIZONTAL axis (X or Y). Ignoring Z means a port
            // lowered to the feet still points sideways out of the cube, never diagonally or down.
            Vector3 outDir = Vector3.Up;
            if (Mathf.Abs(p.Pos.X) > 1e-3f || Mathf.Abs(p.Pos.Y) > 1e-3f)
                outDir = Mathf.Abs(p.Pos.X) >= Mathf.Abs(p.Pos.Y)
                       ? new Vector3(Mathf.Sign(p.Pos.X), 0f, 0f)
                       : new Vector3(0f, Mathf.Sign(p.Pos.Y), 0f);
            Vector3 flow = p.Kind == DeployableDef.PortKind.Consumer ? -outDir : outDir;         // consumer draws IN; producer pushes OUT

            // two crossed quads sharing the flow axis (grass "X"): the flat arrow reads from any viewing angle. The quad
            // texture points +Y in quad-local; RotateYTo aims that +Y along `flow`. Double-sided (material CullMode off).
            const float W = 0.17f, L = 0.24f;
            var root = new Node3D { Position = basePos + outDir * 0.20f, Basis = RotateYTo(flow) };
            root.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(W, L) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            root.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(W, L) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Basis = new Basis(Vector3.Up, Mathf.Pi / 2f) });
            return root;
        }

        public bool DebugArrowVisible => _arrow != null && _arrow.Visible;   // L1 test probe (deploy.port_arrows)

        // Show/hide the in/out arrow; its colour is synced to the current cube state (grey occupancy, green/red wire
        // feedback, or brighter focus) via ApplyHi. The placement-blueprint ghost keeps its own blue/red arrows via
        // DeployablePlacer (a separate _arrowMat), so this only affects placed, wire-tool-out ports.
        public void SetArrowState(bool show, bool available)
        {
            if (_arrow == null) return;
            if (_arrow.Visible != show) _arrow.Visible = show;
            if (show) ApplyHi();
        }

        // rotation mapping the cone's +Y (apex) onto unit direction u (axis-angle)
        static Basis RotateYTo(Vector3 u)
        {
            float d = Vector3.Up.Dot(u);
            if (d > 0.9999f) return Basis.Identity;
            if (d < -0.9999f) return new Basis(Vector3.Right, Mathf.Pi);
            return new Basis(Vector3.Up.Cross(u).Normalized(), Mathf.Acos(Mathf.Clamp(d, -1f, 1f)));
        }

        // Info line for the wire-tool look-at HUD -- reflects the LIVE power flowing through this port.
        public string InfoLine() => Kind switch
        {
            DeployableDef.PortKind.Output => $"{ProviderName} — {Watts:0}w output · {Draw:0}w drawn",
            DeployableDef.PortKind.Consumer => Watts > 0f
                ? $"{ProviderName} — {Watts:0}w consumer ({(Powered ? $"powered, {Live:0}w in" : "unpowered")})"
                : $"{ProviderName} — input ({(Powered ? $"{Live:0}w in" : "no power")})",   // a 0-watt consumer = a splitter's relay input
            DeployableDef.PortKind.Passthrough => $"{ProviderName} — {Live:0}w passthrough",
            _ => ProviderName,
        };

        // the owning deployable was destroyed -> retire this cube: hide it + drop off the wire look-ray layer
        public void Deactivate() { Visible = false; CollisionLayer = 0; }

        // The port cube's BASE fill colour. I/O ports (Role None) are grey, shading light (free) -> dark (Occupied) as a
        // wire attaches; switch trigger ports keep their green/red semantic.
        Color CubeColor() => Role != DeployableDef.SwitchRole.None ? BaseColor(Kind, Role) : (Occupied ? IoUsed : IoFree);

        // Wire-tool highlight state (driven by PlayerController): None = base grey; Focus = a little brighter on look-at
        // (master); WireOk/WireBad = green/red cube + arrow while routing a wire onto this port (valid vs occupied/
        // incompatible target, master). Stored so PowerNet's per-solve UpdateCubeColor re-applies it instead of wiping it.
        public enum PortHi { None, Focus, WireOk, WireBad }
        static readonly Color FeedGreen = new Color(0.30f, 0.90f, 0.42f);   // green: a valid wire target
        static readonly Color FeedRed   = new Color(0.95f, 0.28f, 0.28f);   // red: an occupied / invalid target
        PortHi _hi = PortHi.None;

        public void SetHighlight(PortHi state) { _hi = state; ApplyHi(); }
        public void UpdateCubeColor() { ApplyHi(); }   // PowerNet calls this after a solve -> re-applies base fill + current highlight

        // paint the cube (+ arrow) for the current occupancy + highlight state
        void ApplyHi()
        {
            if (_mat == null) return;
            switch (_hi)
            {
                case PortHi.Focus:
                    _mat.AlbedoColor = CubeColor();
                    _mat.EmissionEnabled = true; _mat.Emission = CubeColor().Lightened(0.55f); _mat.EmissionEnergyMultiplier = 1.15f;
                    TintArrow(CubeColor());
                    break;
                case PortHi.WireOk:  Feedback(FeedGreen); break;
                case PortHi.WireBad: Feedback(FeedRed);   break;
                default:
                    _mat.AlbedoColor = CubeColor();
                    _mat.EmissionEnabled = false; _mat.EmissionEnergyMultiplier = 0f;
                    TintArrow(CubeColor());
                    break;
            }
        }
        void Feedback(Color c)   // solid green/red cube + matching glow + arrow
        {
            var solid = c; solid.A = 0.92f;
            _mat.AlbedoColor = solid;
            _mat.EmissionEnabled = true; _mat.Emission = c; _mat.EmissionEnergyMultiplier = 0.55f;
            TintArrow(c);
        }
        void TintArrow(Color c) { if (_arrowMat != null) { c.A = 0.95f; _arrowMat.AlbedoColor = c; } }
    }
}
