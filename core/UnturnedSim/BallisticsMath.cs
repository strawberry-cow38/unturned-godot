using UnityEngine;

namespace SDG.Unturned
{
    /// <summary>
    /// The ONE ballistic integration model (MP_PLAN §3.4 "server steps ballistics ... the same gravity-drop
    /// model"): Unturned's BulletInfo step (UseableGun.cs:1539-1542) -- each 0.02 s tick the bullet's segment
    /// runs from Pos to NextPos(Pos, Vel); on no hit the bullet adopts that position and its velocity drops by
    /// StepVel(Vel, gravity). PlayerController.StepBullets (the SP/client path) and ServerCombat (the server
    /// path, MP Phase 5) both call THIS, so a server-stepped bullet flies the exact trajectory a single-player
    /// bullet does -- identical IEEE ops, not similar-looking code. gravity = -9.81 * the gun's GravityMultiplier.
    /// </summary>
    public static class BallisticsMath
    {
        public const float StepSeconds = 0.02f;   // TOCK_PER_SECOND = 50

        /// <summary>Where the bullet reaches this tick (the segment end to hit-test against).</summary>
        public static Vector3 NextPos(Vector3 pos, Vector3 vel) => pos + vel * StepSeconds;

        /// <summary>The post-step velocity: gravity drop only (the source model has no drag).</summary>
        public static Vector3 StepVel(Vector3 vel, float gravity) => new Vector3(vel.x, vel.y + gravity * StepSeconds, vel.z);
    }
}
