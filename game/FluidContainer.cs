using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Source = has fluid + a Source port; Storage = accumulates via a Consumer port; Consumer = deletes via a Consumer
    // port (a transformer's OUTPUT fluid is F5). Splitter/Combiner = tankless FITTINGS (mirror power's splitter/combiner):
    // a Splitter is a 0-rate Consumer relay + N Passthrough outputs; a Combiner is N Consumer relays + 1 Passthrough.
    public enum FluidRole { Source, Storage, Consumer, Splitter, Combiner }

    // A fluid device on the hose graph (the fluid analog of a power deployable). A tanked container (Source/Storage/
    // Consumer) holds a FluidTank + one port + a fill bar; a tankless FITTING (Splitter/Combiner) is a pure relay with
    // several ports and no bar. Each port gets a physical HosePort cube the hose tool can connect.
    public partial class FluidContainer : Node3D
    {
        public FluidTank Tank;             // null for a fitting (splitter/combiner)
        public FluidRole Role;
        public bool Blocked;               // a clogged/closed-valve container stops conducting (F5)
        public float FlowRate = 50f;       // base supply (source) / intake (storage/consumer), units/s
        public int Ways = 2;               // splitter outputs / combiner inputs
        public readonly System.Collections.Generic.List<FluidPortNode> Ports = new();
        public readonly System.Collections.Generic.List<HosePort> PortNodes = new();   // the physical hose-tool cubes for each Port
        public Vector3 PortLocalPos = new Vector3(0f, 0.7f, 0.55f);   // where a single-port tank's cube sits (front face); placement sets it per-face
        public float LastFlow;             // debug / fill-bar readout
        InfoBillboard _info;

        public bool IsFitting => Role == FluidRole.Splitter || Role == FluidRole.Combiner;

        public static FluidContainer Make(FluidRole role, FluidTank tank, float flowRate = 50f)
            => new FluidContainer { Role = role, Tank = tank, FlowRate = flowRate };

        public static FluidContainer MakeFitting(FluidRole role, int ways)
            => new FluidContainer { Role = role, Tank = null, Ways = Mathf.Max(2, ways) };

        public override void _Ready()
        {
            AddToGroup("fluid_devices");
            BuildPorts();
            BuildVisuals();
        }

        void BuildPorts()
        {
            switch (Role)
            {
                case FluidRole.Source:
                    AddPort(FluidPortKind.Source, FlowRate, PortLocalPos); break;
                case FluidRole.Storage:
                case FluidRole.Consumer:
                    AddPort(FluidPortKind.Consumer, FlowRate, PortLocalPos); break;
                case FluidRole.Splitter:   // 0-rate relay input (left) + N passthrough outputs (right)
                    AddPort(FluidPortKind.Consumer, 0f, new Vector3(-0.5f, 0.6f, 0f));
                    for (int i = 0; i < Ways; i++) AddPort(FluidPortKind.Passthrough, 0f, new Vector3(0.5f, 0.6f, Fan(i, Ways)));
                    break;
                case FluidRole.Combiner:   // N relay inputs (left) + 1 passthrough output (right)
                    for (int i = 0; i < Ways; i++) AddPort(FluidPortKind.Consumer, 0f, new Vector3(-0.5f, 0.6f, Fan(i, Ways)));
                    AddPort(FluidPortKind.Passthrough, 0f, new Vector3(0.5f, 0.6f, 0f));
                    break;
            }
        }

        void AddPort(FluidPortKind kind, float rate, Vector3 local)
        {
            var node = new FluidPortNode { Kind = kind, Rate = rate, Owner = this };
            Ports.Add(node);
            var fp = HosePort.Create(this, node, local);
            PortNodes.Add(fp); AddChild(fp);
        }

        static float Fan(int i, int n) => n <= 1 ? 0f : Mathf.Lerp(-0.32f, 0.32f, i / (float)(n - 1));   // spread ports across a face

        void BuildVisuals()
        {
            if (IsFitting)   // a small metal box, no fill bar (no tank)
            {
                var fcol = Role == FluidRole.Splitter ? new Color(0.56f, 0.60f, 0.68f) : new Color(0.62f, 0.56f, 0.66f);
                AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 1.05f, 0.9f) }, Position = new Vector3(0, 0.55f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = fcol, Metallic = 0.35f, Roughness = 0.45f } });
                return;
            }
            // tank body — a cylinder tinted by role (green source / blue storage / orange consumer)
            var roleCol = Role switch { FluidRole.Source => new Color(0.35f, 0.70f, 0.42f), FluidRole.Storage => new Color(0.42f, 0.55f, 0.78f), _ => new Color(0.78f, 0.46f, 0.30f) };
            AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 1.4f }, Position = new Vector3(0, 0.7f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = roleCol, Metallic = 0.25f, Roughness = 0.5f } });
            // the fill bar + name — reuse the deployable InfoBillboard (name line + a value bar + a prompt line).
            // Skip it under --headless (the viewport-backed billboard is pointless there; the log-check stays clean).
            if (DisplayServer.GetName() != "headless")
            {
                _info = new InfoBillboard { TopLevel = true };
                AddChild(_info);
                _info.SetActive(true);
            }
        }

        public override void _Process(double delta)
        {
            if (Tank == null || _info == null) return;   // fittings have no tank/bar
            _info.GlobalPosition = GlobalPosition + new Vector3(0, 2.2f, 0);   // hover the bar above the tank (TopLevel node)
            float frac = Tank.Capacity > 0f ? Mathf.Clamp(Tank.Amount / Tank.Capacity, 0f, 1f) : 0f;
            var col = FluidDef.Color(Tank.Type);
            _info.SetName($"{Role} — {FluidDef.Name(Tank.Type)}", col);
            _info.SetBar(0, frac, col);
            _info.SetPrompt($"{Tank.Amount:0} / {Tank.Capacity:0}", new Color(0.9f, 0.92f, 0.95f));
        }
    }
}
