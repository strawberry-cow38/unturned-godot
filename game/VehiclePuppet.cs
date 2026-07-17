using Godot;

namespace UnturnedGodot
{
    // Client-side vehicle replica visual (MP_PLAN §3.6): a plain Node3D carrying the ripped body/parts/
    // wheel meshes -- NO VehicleBody3D, no wheels physics, no collision, no audio. VehicleReplicaView
    // dead-reckons its transform from snapshots; DressWheels applies the replicated steer angle plus a
    // spin derived from the replicated forward speed (rolling wheels need no spin on the wire).
    // Built by Vehicle.BuildPuppetByName so puppet and server node share the same Spec data.
    public partial class VehiclePuppet : Node3D, IPuppetFocusable
    {
        public sealed class WheelDress
        {
            public Node3D Pivot;
            public bool Steer;
            public float Radius;
            public float Spin;   // accumulated roll angle (radians)
        }

        public string SpecKey = "jeep";
        public WheelDress[] Wheels = System.Array.Empty<WheelDress>();

        // look-at focus (client-only): the same screen-space outline the real Vehicle draws -- add every mesh to
        // OutlineOverlay's layer so the offscreen mask cam picks them up as ONE silhouette. Detection is the bit-5
        // box collider added in Vehicle.BuildPuppetByName; PlayerController.UpdateLookFocus drives this on/off.
        public Color OutlineColor = new Color(0.82f, 0.83f, 0.90f);   // default vehicle tint; BuildPuppetByName overrides from spec rarity
        bool _lookFocused;
        System.Collections.Generic.List<MeshInstance3D> _outlineMeshes;

        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (on || _outlineMeshes == null)   // (re)collect on FOCUS so newly-dressed wheel meshes are covered
            {
                _outlineMeshes = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, _outlineMeshes);
            }
            foreach (var mi in _outlineMeshes)
                if (IsInstanceValid(mi))
                    mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = OutlineColor;   // OutlineOverlay tints the rim with this
        }

        static void CollectMeshes(Node n, System.Collections.Generic.List<MeshInstance3D> list)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi) list.Add(mi);   // body + wheels -> one combined silhouette
                CollectMeshes(c, list);
            }
        }

        // C6 ride mode (PEI_CLIENT_PLAN §3 C6): the puppet is the shell's ENTER TARGET and drive-cam anchor.
        public uint NetId;                    // the replicated vehicle entity id (set by VehicleReplicaView at spawn) -- what SendEnterVehicle takes
        public Vector3 DriverEyeLocal = new Vector3(-0.4f, 1.85f, 0.4f);   // FP ride-cam eye; same default + per-spec override as Vehicle
        public Vector3 SeatOffset;            // driver seat (prefab Seat_0) for the 3rd-person seated body pose

        float _meshSize;
        /// <summary>Bounding diagonal for the chase-cam auto-zoom (the Vehicle.WorldMeshAabb analogue).
        /// The Body mesh alone -- it spans the vehicle's footprint, and the cam distance clamps anyway.</summary>
        public float MeshSize
        {
            get
            {
                if (_meshSize <= 0f)
                    _meshSize = GetNodeOrNull<MeshInstance3D>("Body")?.GetAabb().Size.Length() ?? 6f;
                return _meshSize;
            }
        }

        /// <summary>Wheel dressing: front wheels yaw to the replicated steer, every wheel rolls at the
        /// rolling-contact rate for the replicated forward speed (forward = -Z -> negative roll about X,
        /// the VehicleWheel3D convention).</summary>
        public void DressWheels(float steerDegrees, float forwardSpeed, float dt)
        {
            float steerRad = Mathf.DegToRad(steerDegrees);
            foreach (var wd in Wheels)
            {
                if (wd.Pivot == null || !IsInstanceValid(wd.Pivot)) continue;
                wd.Spin -= (forwardSpeed / Mathf.Max(0.05f, wd.Radius)) * dt;
                var basis = new Basis(Vector3.Right, wd.Spin);
                if (wd.Steer) basis = new Basis(Vector3.Up, steerRad) * basis;
                wd.Pivot.Basis = basis;
            }
        }
    }
}
