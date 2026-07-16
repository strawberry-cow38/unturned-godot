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
        public bool Usable => Owner != null && GodotObject.IsInstanceValid(Owner) && !Owner.OnFire;   // a burning/wrecked deployable's ports can't start or accept a wire

        MeshInstance3D _cube;
        StandardMaterial3D _mat;

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
            return cp;
        }

        // Info line for the wire-tool look-at HUD -- reflects the LIVE power flowing through this port.
        public string InfoLine() => Kind switch
        {
            DeployableDef.PortKind.Output => $"{ProviderName} — {Live:0}w output",
            DeployableDef.PortKind.Consumer => $"{ProviderName} — {Watts:0}w consumer ({(Powered ? $"powered, {Live:0}w in" : "unpowered")})",
            DeployableDef.PortKind.Passthrough => $"{ProviderName} — {Live:0}w passthrough",
            _ => ProviderName,
        };

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
