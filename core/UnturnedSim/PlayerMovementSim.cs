using UnityEngine; // SDG.Compat Vector2/Vector3 (namespace UnityEngine), same types the ported game code uses

namespace SDG.Unturned
{
    // Engine-agnostic reproduction of PlayerMovement's velocity model, using constants faithful to
    // PlayerMovementDef (from the source). The Godot controller feeds local-space input + the grounded
    // state each fixed 50 Hz tick and applies the returned velocity via CharacterBody3D. Kept engine-free
    // so it is unit-testable (movement-trace determinism) and identical on client + dedicated server.
    //
    // NOTE (honest fidelity): the CONSTANTS are exact; the trajectory is semi-implicit-Euler and applied
    // through Godot/Jolt collision, so it is "recognizably Unturned + tunable", not a bit-identical trace
    // of Unity's CharacterController. Cross-engine physics can't be byte-equal -- see the plan's risk note.
    public sealed class PlayerMovementSim
    {
        public Vector3 Velocity;
        public EPlayerStance Stance = EPlayerStance.STAND;

        // inputDir: local-space (x = strafe, y = forward), each component in [-1,1].
        // grounded: whether the body was on the floor after the previous move.
        // Returns the velocity to hand to the character body this tick.
        public Vector3 Step(Vector2 inputDir, bool wantJump, bool grounded, float dt)
        {
            // Horizontal: direction clamped to the unit disc so diagonals don't exceed stance speed.
            float speed = PlayerMovementDef.SpeedForStance(Stance);
            Vector2 dir = inputDir;
            float m2 = dir.x * dir.x + dir.y * dir.y;
            if (m2 > 1f)
            {
                float inv = 1f / Mathf.Sqrt(m2);
                dir.x *= inv; dir.y *= inv;
            }
            float wantX = dir.x * speed, wantZ = dir.y * speed;

            if (grounded)
            {
                // On the ground the controller still assigns outright. Retail accelerates here too, but
                // that governs the whole feel of walking and is not what was reported; changing it is a
                // separate decision from fixing the air.
                Velocity.x = wantX;
                Velocity.z = wantZ;
                if (Velocity.y < 0f) Velocity.y = 0f;
                if (wantJump) Velocity.y = PlayerMovementDef.JUMP;
            }
            else
            {
                // AIRBORNE: accelerate toward the desired velocity, do not assign it (PlayerMovement.cs
                // :1283-1301). Assigning gave the capsule no inertia at all -- measured before this fix, a
                // full mid-air reversal completed in ONE 50 Hz tick, -7.00 to +7.00 m/s, and a sprint jump
                // took off at the full 7 m/s instantly rather than carrying momentum. VoX reported the
                // second symptom ("I jump super fast"); the first is the same defect seen from the side.
                //
                // Note what does NOT change: the jump impulse. Measured walk vs sprint jumps at identical
                // height (0.903 m vs 0.902) and identical airtime (0.44 s both) -- the impulse never had a
                // stance term, so the difference was always horizontal carry.
                float wantSpeed = Mathf.Sqrt(wantX * wantX + wantZ * wantZ);
                float curSpeed = Mathf.Sqrt(Velocity.x * Velocity.x + Velocity.z * Velocity.z);

                // Already faster than you are asking for (a sprint takeoff, a rocket jump): bleed off
                // gradually rather than snapping down, so momentum survives letting go of the stick.
                float maxSpeed = curSpeed > wantSpeed
                    ? Mathf.Max(wantSpeed, curSpeed - PlayerMovementDef.AIR_DECELERATION * dt)
                    : wantSpeed;

                float nx = Velocity.x + wantX * PlayerMovementDef.AIR_ACCELERATION * dt;
                float nz = Velocity.z + wantZ * PlayerMovementDef.AIR_ACCELERATION * dt;
                float nSpeed = Mathf.Sqrt(nx * nx + nz * nz);
                if (nSpeed > maxSpeed && nSpeed > 0f)
                {
                    float k = maxSpeed / nSpeed;
                    nx *= k; nz *= k;
                }
                Velocity.x = nx;
                Velocity.z = nz;

                Velocity.y -= PlayerMovementDef.GRAVITY * dt;
                if (Velocity.y < PlayerMovementDef.TERMINAL_VELOCITY)
                    Velocity.y = PlayerMovementDef.TERMINAL_VELOCITY;
            }
            return Velocity;
        }
    }
}
