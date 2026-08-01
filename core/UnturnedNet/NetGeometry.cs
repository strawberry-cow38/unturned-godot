using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Yaw-frame geometry shared by the authority and the Godot layer. PUBLIC (unlike ReplicationUtil,
    /// which is replication bookkeeping) precisely because the point is that both sides use one copy:
    /// every duplicate of these was a place server and client could silently disagree about geometry.
    /// </summary>
    public static class NetGeometry
    {
        /// <summary>
        /// Horizontal forward for a yaw in DEGREES, in the Godot frame a body at yaw 0 faces: <c>-Z</c>.
        /// So this is <c>(-sin, 0, -cos)</c> — and the sign is the whole point of it existing.
        ///
        /// This lived inline in three places (the pickup facing cone, the melee cone, the item toss). One of
        /// the three shipped as <c>(+sin, +cos)</c>, which is 180° inverted: melee swings landed BEHIND the
        /// attacker. Three copies of a four-character-typo-away expression is exactly the shape that produces
        /// (DUPLICATE_AUDIT 2.11).
        ///
        /// Uses <c>Mathf.Deg2Rad</c> rather than a hand-written <c>Mathf.PI / 180f</c>. Those are
        /// bit-identical here — same float32 constant (0x3C8EFA35), and the products agree on every one of
        /// 72001 yaw values from -360 to +360 in 0.01° steps — so this is a pure rename, not a numeric change.
        /// </summary>
        public static Vector3 ForwardFromYaw(float yawDegrees)
        {
            float r = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(-Mathf.Sin(r), 0f, -Mathf.Cos(r));
        }

        /// <summary>Horizontal RIGHT (Godot basis.X) for a yaw in degrees: <c>(cos, 0, -sin)</c>.
        /// Not the same convention as <see cref="ForwardFromYaw"/> — kept as its own named thing so the two
        /// sign patterns cannot be confused for typos of each other.</summary>
        public static Vector3 RightFromYaw(float yawDegrees)
        {
            float r = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(r), 0f, -Mathf.Sin(r));
        }

        // Where a driver is put down when they leave a vehicle: beside the driver door, lifted clear of the
        // hull. Retail's spot (U3 InteractableVehicle) and the numbers are part of the rule, not decoration.
        public const float ExitSideOffset = 2.4f, ExitUpOffset = 1.0f;

        /// <summary>
        /// The vehicle exit spot. This formula existed twice — server-side in ServerVehicles.ServerExit and
        /// again client-side as the fallback in ClientWorldSession — and the two drifting apart against a
        /// frozen replica is the documented root cause in docs/EXIT_POSITION_ROOTCAUSE.md (which is why
        /// VehicleExitedEvent carries the authoritative spot at all). The client copy stays as the
        /// no-spot fallback; sharing the formula means the fallback can no longer disagree about geometry
        /// (DUPLICATE_AUDIT 2.13).
        /// </summary>
        public static Vector3 ExitSpotBeside(Vector3 vehiclePos, float yawDegrees)
            => vehiclePos + RightFromYaw(yawDegrees) * ExitSideOffset + new Vector3(0f, ExitUpOffset, 0f);
    }
}
