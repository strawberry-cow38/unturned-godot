using Godot;

namespace UnturnedGodot
{
    // Client-side vehicle replica visual (MP_PLAN §3.6): a plain Node3D carrying the ripped body/parts/
    // wheel meshes -- NO VehicleBody3D, no wheels physics, no collision, no audio. VehicleReplicaView
    // dead-reckons its transform from snapshots; DressWheels applies the replicated steer angle plus a
    // spin derived from the replicated forward speed (rolling wheels need no spin on the wire).
    // Built by Vehicle.BuildPuppetByName so puppet and server node share the same Spec data.
    public partial class VehiclePuppet : Node3D
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
