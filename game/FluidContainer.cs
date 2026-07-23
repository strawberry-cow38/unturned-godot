using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Source = has fluid + a Source port; Storage = accumulates via a Consumer port; Consumer = deletes via a Consumer
    // port (a transformer's OUTPUT fluid is F5). Splitter/Combiner = tankless FITTINGS (mirror power's splitter/combiner):
    // a Splitter is a 0-rate Consumer relay + N Passthrough outputs; a Combiner is N Consumer relays + 1 Passthrough.
    // Pump = a tankless inline fitting (1 relay + 1 passthrough) that ALSO draws power (FluidPump) and, when powered,
    // provides head lift overriding the gravity gate. Transformer (refinery oil->gas, sluice water->dirty) = a tankless
    // device that DELETES its input fluid and PRODUCES a different output fluid (a Consumer input + a Source output).
    // Valve = a tankless inline fitting that's just a SWITCH for a hose: open = passes flow, closed (Blocked) = stops it.
    public enum FluidRole { Source, Storage, Consumer, Splitter, Combiner, Pump, Transformer, Valve }

    // A fluid device on the hose graph (the fluid analog of a power deployable). A tanked container (Source/Storage/
    // Consumer) holds a FluidTank + one port + a fill bar; a tankless FITTING (Splitter/Combiner) is a pure relay with
    // several ports and no bar. Each port gets a physical HosePort cube the hose tool can connect.
    // A StaticBody3D (like Deployable) so the device body is SOLID — a world collider on layer 1 the player can't walk
    // through and the interaction/pickup ray can hit (mirrors a power deployable; the port cubes stay on PortLayer).
    public partial class FluidContainer : StaticBody3D
    {
        public FluidTank Tank;             // null for a fitting (splitter/combiner)
        public FluidRole Role;
        public DeployableDef Def;          // the item def this was placed from -> hold-F pickup returns that item (set by FluidDeploy)
        public bool Blocked;               // a clogged/closed-valve container stops conducting (F5)
        public bool Infinite;              // a submersible INLET: an infinite source (never depletes)
        public bool NoHead;                // no head pressure: its output won't flow passively (gravity) — needs a PUMP to draw from it
        public float FlowRate = 50f;       // base supply (source) / intake (storage/consumer), units/s
        public int Ways = 2;               // splitter outputs / combiner inputs
        public FluidType TransformIn = FluidType.None, TransformOut = FluidType.None;   // transformer: input fluid -> output fluid
        public float TransformRatio = 1f; // transformer: output units produced per input unit consumed
        public bool TransformActive;       // transformer: did its input flow last tick? (gates this tick's output supply, 1-tick lag)
        public readonly System.Collections.Generic.List<FluidPortNode> Ports = new();
        public readonly System.Collections.Generic.List<HosePort> PortNodes = new();   // the physical hose-tool cubes for each Port
        public Vector3 PortLocalPos = new Vector3(0f, 0.7f, 0.55f);   // where a single-port tank's cube sits (front face); placement sets it per-face
        public float LastFlow;             // debug / fill-bar readout
        InfoBillboard _info;
        StandardMaterial3D _valveHandleMat;   // valve: the handle wheel material (green open / red closed)
        MeshInstance3D _pumpDrum; Vector3 _pumpDrumBase; float _vibePhase;   // pump motor drum: vibrates when the pump is DRIVING (powered + fluid flowing)

        // True when the device is actively working and should animate (a powered pump with fluid moving through it).
        // Base = never; FluidPump overrides it with IsPowered && a port is flowing. Drives the motor-drum shake.
        public virtual bool DriveActive => false;

        public bool IsFitting => Role == FluidRole.Splitter || Role == FluidRole.Combiner || Role == FluidRole.Pump || Role == FluidRole.Transformer || Role == FluidRole.Valve;

        // A relay fitting conducts a powered pump's pressure CEILING THROUGH itself, so a pump can lift a whole chain of
        // fittings (splitter/combiner/pump/OPEN valve) up to its head. A TANK (source/storage/consumer) or a TRANSFORMER
        // (a fluid boundary) stops it — "up to a source/consumer, not through it" (strawberry). Blocked/CLOSED = doesn't conduct.
        public bool IsFlowRelay => !Blocked && (Role == FluidRole.Splitter || Role == FluidRole.Combiner || Role == FluidRole.Pump || Role == FluidRole.Valve);

        public static FluidContainer Make(FluidRole role, FluidTank tank, float flowRate = 50f)
            => new FluidContainer { Role = role, Tank = tank, FlowRate = flowRate };

        public static FluidContainer MakeFitting(FluidRole role, int ways)
            => new FluidContainer { Role = role, Tank = null, Ways = Mathf.Max(2, ways) };

        // A transformer (refinery/sluice): deletes `inp`, produces `outp` at `flowRate` (input) * `ratio` (output/input).
        public static FluidContainer MakeTransformer(FluidType inp, FluidType outp, float flowRate = 50f, float ratio = 1f)
            => new FluidContainer { Role = FluidRole.Transformer, Tank = null, FlowRate = flowRate, TransformIn = inp, TransformOut = outp, TransformRatio = ratio };

        // A valve — an inline switch for a hose. Starts OPEN; ToggleValve() closes it (Blocked -> stops flow + pump lift).
        public static FluidContainer MakeValve() => new FluidContainer { Role = FluidRole.Valve, Tank = null };

        public override void _Ready()
        {
            AddToGroup("fluid_devices");
            BuildPorts();
            BuildVisuals();
            OnReadyExtra();   // subclass hook (a FluidPump adds its power ConnectionPort here)
        }

        protected virtual void OnReadyExtra() { }

        void BuildPorts()
        {
            switch (Role)
            {
                case FluidRole.Source:
                    AddPort(FluidPortKind.Source, FlowRate, PortLocalPos); break;
                case FluidRole.Storage:    // a buffer TANK: a Consumer INPUT (fill, left face, Ports[0]) + a Source OUTPUT (draw, right face)
                    AddPort(FluidPortKind.Consumer, FlowRate, new Vector3(-0.5f, 0.7f, 0f));
                    AddPort(FluidPortKind.Source, FlowRate, new Vector3(0.5f, 0.7f, 0f));
                    break;
                case FluidRole.Consumer:   // a pure sink: one Consumer input (deletes)
                    AddPort(FluidPortKind.Consumer, FlowRate, PortLocalPos); break;
                case FluidRole.Splitter:   // 0-rate relay input (left) + N passthrough outputs (right)
                    AddPort(FluidPortKind.Consumer, 0f, new Vector3(-0.5f, 0.6f, 0f));
                    for (int i = 0; i < Ways; i++) AddPort(FluidPortKind.Passthrough, 0f, new Vector3(0.5f, 0.6f, Fan(i, Ways)));
                    break;
                case FluidRole.Combiner:   // N relay inputs (left) + 1 passthrough output (right)
                    for (int i = 0; i < Ways; i++) AddPort(FluidPortKind.Consumer, 0f, new Vector3(-0.5f, 0.6f, Fan(i, Ways)));
                    AddPort(FluidPortKind.Passthrough, 0f, new Vector3(0.5f, 0.6f, 0f));
                    break;
                case FluidRole.Pump:       // inline: a 0-rate relay input (left) + one passthrough output (right)
                    AddPort(FluidPortKind.Consumer, 0f, new Vector3(-0.5f, 0.6f, 0f));
                    AddPort(FluidPortKind.Passthrough, 0f, new Vector3(0.5f, 0.6f, 0f));
                    break;
                case FluidRole.Transformer:   // a Consumer INPUT (deletes TransformIn) + a Source OUTPUT (produces TransformOut)
                    AddPort(FluidPortKind.Consumer, FlowRate, new Vector3(-0.5f, 0.6f, 0f), TransformIn);
                    AddPort(FluidPortKind.Source, FlowRate * TransformRatio, new Vector3(0.5f, 0.6f, 0f), TransformOut);
                    break;
                case FluidRole.Valve:      // inline switch: a 0-rate relay input + one passthrough output (dead when Blocked/closed)
                    AddPort(FluidPortKind.Consumer, 0f, new Vector3(-0.5f, 0.6f, 0f));
                    AddPort(FluidPortKind.Passthrough, 0f, new Vector3(0.5f, 0.6f, 0f));
                    break;
            }
        }

        void AddPort(FluidPortKind kind, float rate, Vector3 local, FluidType typeOverride = FluidType.None)
        {
            var node = new FluidPortNode { Kind = kind, Rate = rate, Owner = this };
            Ports.Add(node);
            var fp = HosePort.Create(this, node, local);
            fp.TypeOverride = typeOverride;
            PortNodes.Add(fp); AddChild(fp);
        }

        static float Fan(int i, int n) => n <= 1 ? 0f : Mathf.Lerp(-0.32f, 0.32f, i / (float)(n - 1));   // spread ports across a face

        void BuildVisuals()
        {
            if (IsFitting)   // a small metal box, no fill bar (no tank)
            {
                var fcol = Role switch { FluidRole.Splitter => new Color(0.56f, 0.60f, 0.68f), FluidRole.Combiner => new Color(0.62f, 0.56f, 0.66f), FluidRole.Transformer => new Color(0.60f, 0.42f, 0.28f), FluidRole.Valve => new Color(0.48f, 0.52f, 0.58f), _ => new Color(0.30f, 0.42f, 0.62f) };   // pump = electric blue / transformer = copper / valve = steel
                AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 1.05f, 0.9f) }, Position = new Vector3(0, 0.55f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = fcol, Metallic = 0.35f, Roughness = 0.45f } });
                AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.9f, 1.05f, 0.9f) }, Position = new Vector3(0, 0.55f, 0) });   // solid body (layer 1) — no walk-through, ray-hittable
                if (Role == FluidRole.Pump)   // a little motor drum on top so a pump reads distinct from a splitter box
                {
                    _pumpDrumBase = new Vector3(0, 1.25f, 0);
                    _pumpDrum = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.28f, Height = 0.4f }, Position = _pumpDrumBase, RotationDegrees = new Vector3(90, 0, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.72f, 0.20f), Metallic = 0.4f, Roughness = 0.4f } };
                    AddChild(_pumpDrum);
                }
                if (Role == FluidRole.Transformer)   // a chimney stack so a refinery reads distinct
                    AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.14f, BottomRadius = 0.16f, Height = 0.7f }, Position = new Vector3(0.2f, 1.4f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.3f, 0.32f), Metallic = 0.3f, Roughness = 0.6f } });
                if (Role == FluidRole.Valve)   // a handle wheel on top — GREEN open / RED closed (Blocked)
                {
                    _valveHandleMat = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.85f, 0.4f), Metallic = 0.3f, Roughness = 0.5f };
                    AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.12f }, Position = new Vector3(0, 1.2f, 0), MaterialOverride = _valveHandleMat });
                    RefreshValveVisual();
                }
                return;
            }
            // tank body — a cylinder tinted by role (green source / blue storage / orange consumer)
            var roleCol = Role switch { FluidRole.Source => new Color(0.35f, 0.70f, 0.42f), FluidRole.Storage => new Color(0.42f, 0.55f, 0.78f), _ => new Color(0.78f, 0.46f, 0.30f) };
            AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 1.4f }, Position = new Vector3(0, 0.7f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = roleCol, Metallic = 0.25f, Roughness = 0.5f } });
            AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.5f, Height = 1.4f }, Position = new Vector3(0, 0.7f, 0) });   // solid body (layer 1) — no walk-through, ray-hittable
            // the fill bar + name — reuse the deployable InfoBillboard (name line + a value bar + a prompt line).
            // Skip it under --headless (the viewport-backed billboard is pointless there; the log-check stays clean).
            if (DisplayServer.GetName() != "headless")
            {
                _info = new InfoBillboard { TopLevel = true };
                AddChild(_info);
                _info.SetActive(true);
            }
        }

        // Hold-F pickup (mirror of Deployable.Pickup): free every hose plugged into any of my ports, run the subclass hook
        // (a pump frees its power wire too), then despawn. The caller grants the item back into the bag.
        public void Pickup()
        {
            foreach (var n in GetTree().GetNodesInGroup("hoses"))
                if (n is Hose h && GodotObject.IsInstanceValid(h))
                {
                    bool mine = false;
                    foreach (var hp in PortNodes) if (h.Source == hp.Node || h.Consumer == hp.Node) { mine = true; break; }
                    if (mine) { h.RemoveFromGroup("hoses"); h.QueueFree(); }
                }
            OnPickup();   // subclass hook (FluidPump frees its power wire + marks the power net dirty)
            QueueFree();
        }
        protected virtual void OnPickup() { }

        // Flip a valve open/closed (Blocked). Closed = stops flow through it + stops a pump's lift propagating past it.
        public void ToggleValve()
        {
            if (Role != FluidRole.Valve) return;
            Blocked = !Blocked;
            RefreshValveVisual();
        }
        void RefreshValveVisual()
        {
            if (_valveHandleMat != null) _valveHandleMat.AlbedoColor = Blocked ? new Color(0.9f, 0.2f, 0.2f) : new Color(0.3f, 0.85f, 0.4f);   // red closed / green open
        }

        // A clean, "Fluid"-prefixed label (strawberry: everything fluid reads "Fluid ..."). Used by the tank billboard and
        // the hose-tool port HUD so the in-world name matches the inventory item.
        public string RoleLabel() => Role switch
        {
            FluidRole.Source => "Fluid Source",
            FluidRole.Storage => "Fluid Tank",
            FluidRole.Consumer => "Fluid Drain",
            _ => $"Fluid {Role}",
        };

        public override void _Process(double delta)
        {
            // a powered pump with fluid moving through it VIBRATES its motor drum (strawberry: powered AND flowing, not
            // just powered). Idle / unpowered / dry -> the drum sits still at its base position.
            if (_pumpDrum != null && GodotObject.IsInstanceValid(_pumpDrum))
            {
                if (DriveActive)
                {
                    _vibePhase += (float)delta * 42f;
                    _pumpDrum.Position = _pumpDrumBase + new Vector3(Mathf.Sin(_vibePhase * 1.3f), Mathf.Sin(_vibePhase), Mathf.Sin(_vibePhase * 0.7f)) * 0.01f;
                }
                else if (_pumpDrum.Position != _pumpDrumBase) _pumpDrum.Position = _pumpDrumBase;
            }
            if (Tank == null || _info == null) return;   // fittings have no tank/bar
            _info.GlobalPosition = GlobalPosition + new Vector3(0, 2.2f, 0);   // hover the bar above the tank (TopLevel node)
            float frac = Tank.Capacity > 0f ? Mathf.Clamp(Tank.Amount / Tank.Capacity, 0f, 1f) : 0f;
            var col = FluidDef.Color(Tank.Type);
            // an un-adopted tank has FluidType.None -> reads "(empty)" (a STATE), not "— Empty" as if Empty were a fluid
            // (strawberry: "20L of EMPTY"). Amounts show in L, not raw mL.
            string label = Tank.Type == FluidType.None ? $"{RoleLabel()} (empty)" : $"{RoleLabel()} — {FluidDef.Name(Tank.Type)}";
            _info.SetName(label, col);
            _info.SetBar(0, frac, col);
            _info.SetPrompt($"{FluidDef.Litres(Tank.Amount)} / {FluidDef.Litres(Tank.Capacity)}", new Color(0.9f, 0.92f, 0.95f));
        }
    }
}
