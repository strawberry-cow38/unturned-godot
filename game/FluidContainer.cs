using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    public enum FluidRole { Source, Storage, Consumer }

    // A fluid container deployable — the fluid analog of a power deployable. Holds a FluidTank (fluidID + amount +
    // capacity) and exposes fluid ports to the hose graph. Source = has fluid + a Source port; Storage = accumulates
    // via a Consumer port; Consumer = deletes via a Consumer port (a transformer's OUTPUT fluid is F5). F3 adds the
    // visual tank mesh + a fill bar (reusing the deployable InfoBillboard); in-game placement + the hose tool are next.
    public partial class FluidContainer : Node3D
    {
        public FluidTank Tank;
        public FluidRole Role;
        public bool Blocked;              // a clogged/closed-valve container stops conducting (F5)
        public float FlowRate = 50f;      // base supply (source) / intake (storage/consumer), units/s
        public readonly System.Collections.Generic.List<FluidPortNode> Ports = new();
        public float LastFlow;            // debug / fill-bar readout
        InfoBillboard _info;

        public static FluidContainer Make(FluidRole role, FluidTank tank, float flowRate = 50f)
            => new FluidContainer { Role = role, Tank = tank, FlowRate = flowRate };

        public override void _Ready()
        {
            AddToGroup("fluid_devices");
            var kind = Role == FluidRole.Source ? FluidPortKind.Source : FluidPortKind.Consumer;
            Ports.Add(new FluidPortNode { Kind = kind, Rate = FlowRate, Owner = this });
            BuildVisuals();
        }

        void BuildVisuals()
        {
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
            if (Tank == null || _info == null) return;
            _info.GlobalPosition = GlobalPosition + new Vector3(0, 2.2f, 0);   // hover the bar above the tank (TopLevel node)
            float frac = Tank.Capacity > 0f ? Mathf.Clamp(Tank.Amount / Tank.Capacity, 0f, 1f) : 0f;
            var col = FluidDef.Color(Tank.Type);
            _info.SetName($"{Role} — {FluidDef.Name(Tank.Type)}", col);
            _info.SetBar(0, frac, col);
            _info.SetPrompt($"{Tank.Amount:0} / {Tank.Capacity:0}", new Color(0.9f, 0.92f, 0.95f));
        }
    }
}
