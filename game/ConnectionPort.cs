using Godot;

namespace UnturnedGodot
{
    // A power connection point on a placed deployable: a small cube the wire tool can look at + wire to.
    // Output (produces watts while the source is on), Consumer (draws watts), or Passthrough (re-exports the
    // leftover). Lives on its own collision layer so the wire look-ray finds it without hitting anything else.
    public partial class ConnectionPort : StaticBody3D
    {
        public const uint PortLayer = 1u << 8;   // wire look-ray raycasts this layer only
        const float CubeSize = 0.13f;

        public Deployable Owner;
        public DeployableDef.PortKind Kind;
        public float Watts;         // output: produced; consumer: drawn; passthrough: unused
        public string ProviderName;
        public float Live;          // live power (recomputed by PowerNet): output = produced now, consumer = received, passthrough = exported now
        public bool Powered;        // consumer: is it getting at least its usage?
        public float Draw;          // output only: total wattage actually drawn by the powered consumers down its chain (the load)
        public bool Usable => Owner != null && GodotObject.IsInstanceValid(Owner) && !Owner.OnFire;   // a burning/wrecked deployable's ports can't start or accept a wire

        MeshInstance3D _cube, _arrow;
        StandardMaterial3D _mat, _arrowMat;
        public static readonly Color ArrowBlue = new Color(0.30f, 0.62f, 1f);   // blueprint-ghost blue (available)
        public static readonly Color ArrowRed = new Color(1f, 0.28f, 0.28f);    // blueprint-ghost red (wired / unusable)

        static Color BaseColor(DeployableDef.PortKind k) => k switch
        {
            DeployableDef.PortKind.Output => new Color(0.25f, 0.85f, 0.30f),        // green: produces power
            DeployableDef.PortKind.Consumer => new Color(0.95f, 0.55f, 0.15f),      // orange: draws power
            DeployableDef.PortKind.Passthrough => new Color(0.30f, 0.75f, 0.95f),   // cyan: re-exports leftover
            _ => Colors.White,
        };

        public static ConnectionPort Create(Deployable owner, DeployableDef.Port p, string providerName)
        {
            var cp = new ConnectionPort
            {
                Owner = owner, Kind = p.Kind, Watts = p.Watts, ProviderName = providerName,
                Position = p.Pos, CollisionLayer = PortLayer, CollisionMask = 0,   // detectable, but doesn't collide with anything
            };
            cp._mat = new StandardMaterial3D { AlbedoColor = BaseColor(p.Kind), ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Metallic = 0f, Roughness = 0.6f };
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
            AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        // A small in/out dart for a port, in the flat authored frame: cone apex points OUT for a producer / IN for a
        // consumer, sitting just outside the port. `basePos` = the port position when the arrow parents the deployable
        // (ghost), or Vector3.Zero when it parents the port cube itself. Shared by placed ports + the placement ghost.
        public static MeshInstance3D MakeArrow(DeployableDef.Port p, StandardMaterial3D mat, Vector3 basePos)
        {
            Vector3 outDir = p.Pos.LengthSquared() > 1e-4f ? p.Pos.Normalized() : Vector3.Up;   // deployable-center -> port ≈ outward
            Vector3 flow = p.Kind == DeployableDef.PortKind.Consumer ? -outDir : outDir;         // consumer draws IN; producer pushes OUT
            return new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = 0.22f, RadialSegments = 8 },   // top-radius 0 = cone (dart); apex on +Y
                MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = basePos + outDir * 0.22f, Basis = RotateYTo(flow),
            };
        }

        // Show/hide the in/out arrow + colour it available (blue) or unavailable (red).
        public void SetArrowState(bool show, bool available)
        {
            if (_arrow == null) return;
            if (_arrow.Visible != show) _arrow.Visible = show;
            if (show && _arrowMat != null) { var c = available ? ArrowBlue : ArrowRed; c.A = 0.92f; _arrowMat.AlbedoColor = c; }
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
            DeployableDef.PortKind.Consumer => $"{ProviderName} — {Watts:0}w consumer ({(Powered ? $"powered, {Live:0}w in" : "unpowered")})",
            DeployableDef.PortKind.Passthrough => $"{ProviderName} — {Live:0}w passthrough",
            _ => ProviderName,
        };

        // the owning deployable was destroyed -> retire this cube: hide it + drop off the wire look-ray layer
        public void Deactivate() { Visible = false; CollisionLayer = 0; }

        // look-at highlight / selection feedback (brighten + emit)
        public void SetHighlighted(bool on)
        {
            if (_mat == null) return;
            _mat.EmissionEnabled = on;
            _mat.Emission = BaseColor(Kind);
            _mat.EmissionEnergyMultiplier = on ? 0.9f : 0f;
        }
    }
}
