using Godot;

namespace UnturnedGodot
{
    // Climbable ladders, ported from PlayerStance.simulate + InteractableLadder.
    //
    // The rules are retail's, and each one is there because of a specific exploit or annoyance:
    //   - a 0.75 m forward probe from 0.5 m above the feet, so you grab a ladder you are standing at rather
    //     than one across the room;
    //   - the probe must hit the ladder's FRONT OR BACK face, not its edge, or you can mount a ladder by
    //     brushing its side and end up inside geometry;
    //   - the ladder must be UPRIGHT -- retail's "prevent climbing angled ladders. Only 'mostly' up/down";
    //   - the climb point snaps to the ladder's own centre line, half a metre out along the face, so you are
    //     always centred on the rungs regardless of where on the face you looked.
    //
    // ON THE AXIS -- I talked myself into a bug here and the test caught it. Reasoning that the ex=270
    // stand-up sends mesh Y to world -Z, I "corrected" retail's `transform.up` to `GlobalBasis.Z`, which comes
    // back VERTICAL and refuses every ladder in the map. The error was conflating a world DIRECTION with a
    // basis COLUMN: `GlobalBasis.Y` already means "where this node's local Y points", whatever rotation is on
    // it. The rip's thin axis is mesh Y and retail's ladder prefab also faces along its local up, so the naive
    // port was right all along and my correction was the defect.
    public static class Ladder
    {
        /// <summary>Meta set on a ladder prop's StaticBody3D, so a probe hit resolves back to the prop.</summary>
        public const string Meta = "ladder";

        public const float ProbeDist = 0.75f;    // PlayerStance: Physics.Raycast(ray, out ladder, 0.75f, LADDER_INTERACT)
        public const float ProbeHeight = 0.5f;   // ray starts at transform.position + up * 0.5
        /// <summary>Probe origin while ALREADY attached. Lower than the entry probe on purpose: the entry
        /// height doubles as the grab radius, but it also caps how high you can ride (feet stop 0.5 m below
        /// the ladder top), which strands you below a roof flush with the ladder. Holding lower lets the climb
        /// finish at the top without widening the grab. See PlayerController.StepLadder.</summary>
        public const float HoldProbeHeight = 0.1f;
        public const float FaceDot = 0.9f;       // |dot(hitNormal, faceAxis)| > 0.9  -> front/back, not the edge
        public const float SlopeDot = 0.1f;      // |dot(worldUp, faceAxis)| <= 0.1   -> upright ladders only
        public const float OutOffset = 0.5f;     // climb point pushed out along the hit normal
        public const float DropBelowHit = 0.5f;  // ...and half a metre below where the probe landed

        /// <summary>Every ladder prop in the catalogue. Both are 1.16 x 0.15 x 6.76 with the thin axis on
        /// mesh Y; there are 76 of them placed across the map.</summary>
        public static bool IsLadderProp(string meshName) =>
            meshName != null && meshName.StartsWith("Ladder_", System.StringComparison.Ordinal);

        /// <summary>The ladder's outward face normal in world space -- the world direction of its local Y,
        /// which is what a basis column gives you regardless of how the placement rotated it.</summary>
        public static Vector3 FaceAxis(Node3D body) => body.GlobalBasis.Y.Normalized();

        /// <summary>Retail's two gates, together: did the probe hit the climbable face, and is this ladder
        /// upright? Pure, so it tests without a scene.</summary>
        public static bool IsClimbable(Vector3 hitNormal, Vector3 faceAxis)
        {
            if (hitNormal.LengthSquared() < 1e-6f || faceAxis.LengthSquared() < 1e-6f) return false;
            hitNormal = hitNormal.Normalized(); faceAxis = faceAxis.Normalized();
            if (Mathf.Abs(hitNormal.Dot(faceAxis)) <= FaceDot) return false;        // hit the edge, not the face
            return Mathf.Abs(Vector3.Up.Dot(faceAxis)) <= SlopeDot;                 // sloped ladder -> refuse
        }

        /// <summary>Where the player stands once attached: the ladder's OWN x/z (so you centre on the rungs no
        /// matter where you looked), just below the probe hit, pushed clear of the face.</summary>
        public static Vector3 ClimbPoint(Vector3 ladderOrigin, Vector3 hitPoint, Vector3 hitNormal) =>
            new Vector3(ladderOrigin.X, hitPoint.Y - DropBelowHit, ladderOrigin.Z) + hitNormal.Normalized() * OutOffset;

        /// <summary>Vertical climb speed for a forward-input value. PlayerMovement applies
        /// `velocity = (0, move.z * speed * 0.5, 0)` with speed = SPEED_CLIMB, so full forward is 2.25 m/s --
        /// the 0.5 is not a guess, it is in the movement code and halves the 4.5 constant.</summary>
        public static float ClimbVelocity(float forwardInput) =>
            forwardInput * SDG.Unturned.PlayerMovementDef.SPEED_CLIMB * 0.5f;
    }
}
