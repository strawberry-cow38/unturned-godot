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
            Velocity.x = dir.x * speed;
            Velocity.z = dir.y * speed;

            // Vertical: rest on ground, jump off it, otherwise integrate gravity to terminal velocity.
            if (grounded)
            {
                if (Velocity.y < 0f) Velocity.y = 0f;
                if (wantJump) Velocity.y = PlayerMovementDef.JUMP;
            }
            else
            {
                Velocity.y -= PlayerMovementDef.GRAVITY * dt;
                if (Velocity.y < PlayerMovementDef.TERMINAL_VELOCITY)
                    Velocity.y = PlayerMovementDef.TERMINAL_VELOCITY;
            }
            return Velocity;
        }
    }
}
