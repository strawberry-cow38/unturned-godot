using Godot;

namespace UnturnedGodot
{
    // A placed deployable in the world (the result of planting a held barricade). Mesh + a box collider
    // hugging it + health/fuel, in group "deployables". Look-at gets the same screen-space outline + info
    // billboard (name / HP / fuel gauge) as vehicles. First pass: inert -- no power/light behaviour yet
    // (that's the next pass; src runtime = InteractableGenerator / InteractableSpot).
    public partial class Deployable : StaticBody3D
    {
        public DeployableDef Def;
        public float Health, HealthMax;
        public float Fuel, FuelMax;   // src InteractableGenerator: fuel drawn from Capacity; a fresh build starts FULL here (matches how vehicles spawn: Fuel = FuelMax) until the refuel/power pass

        bool _lookFocused;
        System.Collections.Generic.List<MeshInstance3D> _outlineMeshes;
        Label3D _infoLabel;
        static readonly Color OutlineColor = new Color(0.82f, 0.83f, 0.90f);   // same neutral tint as vehicles (no per-deployable rarity yet)
        const float InfoH = 1.6f;   // billboard height above the base (generators are short)

        // Build the mesh + material for a def, returning the MeshInstance and its local AABB (in the flat
        // authored frame, before the -90 X stand-up). Shared by the placed object and the placement ghost.
        public static MeshInstance3D BuildMesh(DeployableDef def, out Aabb localAabb)
        {
            var mesh = def.LoadMesh();
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = def.MakeMaterial() };
            localAabb = mesh != null ? mesh.GetAabb() : new Aabb();
            return mi;
        }

        // `surface` = the ground contact point (the raycast hit); the model is lifted so its base sits there.
        public static Deployable Spawn(Node parent, DeployableDef def, Vector3 surface, float yawDeg)
        {
            var d = new Deployable { Def = def, Health = def.Health, HealthMax = def.Health, Fuel = def.Fuel, FuelMax = def.Fuel };
            var mi = BuildMesh(def, out Aabb ab);
            d.AddChild(mi);
            // collider hugs the real mesh (in the same flat frame as the mesh, so it stands up with the node)
            d.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = ab.Size == Vector3.Zero ? def.Size : ab.Size },
                Position = ab.GetCenter(),
            });
            d.Position = surface + Vector3.Up * DeployableDef.GroundLift(ab);   // base sits on the surface
            d.Basis = DeployableDef.StandBasis(yawDeg);   // yaw + the stand-up
            d.AddToGroup("deployables");
            // look-at info billboard (name / HP / fuel), TopLevel so it floats in world space above the object
            d._infoLabel = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, TopLevel = true, Visible = false,
                Modulate = OutlineColor, PixelSize = 0.0055f, NoDepthTest = true, FontSize = 52, OutlineSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            d.AddChild(d._infoLabel);
            parent.AddChild(d);
            return d;
        }

        static void CollectMeshes(Node n, System.Collections.Generic.List<MeshInstance3D> list)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi) list.Add(mi);
                CollectMeshes(c, list);
            }
        }

        // Look-at focus (same system as vehicles/items): put the mesh silhouette on OutlineLayer so the
        // OutlineOverlay draws a rim, and show/hide the info billboard.
        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (_outlineMeshes == null)
            {
                _outlineMeshes = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, _outlineMeshes);
            }
            foreach (var mi in _outlineMeshes)
                if (IsInstanceValid(mi))
                    mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = OutlineColor;   // OutlineOverlay tints the rim with this
            if (_infoLabel != null) _infoLabel.Visible = on;
        }

        public void TakeDamage(float amount) => Health = Mathf.Max(0f, Health - amount);

        public override void _Process(double delta)
        {
            if (!_lookFocused || _infoLabel == null) return;   // only the focused one keeps its billboard live
            _infoLabel.GlobalPosition = GlobalPosition + Vector3.Up * InfoH;
            string fuelLine = FuelMax > 0f ? $"\nFuel {Fuel:0}/{FuelMax:0}" : "";
            _infoLabel.Text = $"{Def?.Name}\nHP {Health:0}/{HealthMax:0}{fuelLine}";
        }
    }
}
