using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// An opt-in piece authored in CLOSED vehicle/body space (not centred on its hinge).
    /// PanelIndex is a unique bit INDEX, 0..31; SeatIndex is 0=driver, 1..=passenger,
    /// -1=hood, -2=trunk. Axis and Pivot are in Godot body space; Degrees is signed.
    /// GlassLabel uses Vehicle.GlassPaneLabels, e.g. "l_front", without "Glass_".
    /// </summary>
    public readonly struct AuthoredPanelDef
    {
        public readonly string MeshPath;
        public readonly int PanelIndex, SeatIndex;
        public readonly Vector3 Pivot, Axis;
        public readonly float Degrees;
        public readonly string GlassLabel;
        public uint PanelBit => 1u << PanelIndex;

        public AuthoredPanelDef(string meshPath, int panelIndex, int seatIndex,
            Vector3 pivot, Vector3 axis, float degrees, string glassLabel = null)
        {
            MeshPath = meshPath;
            PanelIndex = panelIndex;
            SeatIndex = seatIndex;
            Pivot = pivot;
            Axis = axis;
            Degrees = degrees;
            GlassLabel = glassLabel;
        }
    }

    public partial class Vehicle
    {
        // Deliberately NOT a processing node. Existing physics/replica loops drive it explicitly.
        public VehiclePanelRig AuthoredPanelRig { get; private set; }

        /// <summary>
        /// Call after AddGlassOverlay (including its pane colliders). Uses the existing mesh-hitbox
        /// switch, and never attaches moving shapes to this VehicleBody3D. Idempotent at build time.
        /// Spec fields/hooks live in Vehicle.cs, not in this opt-in implementation.
        /// </summary>
        public void BuildAuthoredPanels(AuthoredPanelDef[] defs, Material sharedPaint)
        {
            if (AuthoredPanelRig != null) return;
            AuthoredPanelRig = BuildAuthoredPanelRig(this, defs, sharedPaint, MeshHitbox);
        }

        /// <summary>Shared real/puppet builder; null/empty definitions leave the old fleet untouched.</summary>
        public static VehiclePanelRig BuildAuthoredPanelRig(Node3D owner, AuthoredPanelDef[] defs,
            Material sharedPaint, bool queryColliders)
            => VehiclePanelRig.BuildAuthoredPanelRig(owner, defs, sharedPaint, queryColliders);

        /// <summary>Puppet seam: store the returned rig and tick it from VehicleReplicaView.</summary>
        public static VehiclePanelRig CreateAuthoredPanelRig(Node3D owner, AuthoredPanelDef[] defs,
            Material sharedPaint, bool queryColliders = false)
            => BuildAuthoredPanelRig(owner, defs, sharedPaint, queryColliders);
    }
}
