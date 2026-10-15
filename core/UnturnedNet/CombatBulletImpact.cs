using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>A round that the server accepted and collided. Payload is the host's launch-time
    /// value, not the currently selected weapon. Normal is absent for legacy approximate actor hits.</summary>
    public readonly struct CombatBulletImpact
    {
        public readonly ushort Shooter, Sequence;
        public readonly int Pellet;
        public readonly Vector3 Point, Velocity;
        public readonly Vector3? Normal;
        public readonly object Context;
        public readonly long Tick;
        public readonly bool Thin, Water;
        public readonly int Pierced;
        public CombatBulletImpact(ushort shooter, ushort sequence, int pellet, Vector3 point,
            Vector3? normal, Vector3 velocity, object context, long tick, bool thin = false, bool water = false, int pierced = 0)
        { Shooter = shooter; Sequence = sequence; Pellet = pellet; Point = point; Normal = normal;
          Velocity = velocity; Context = context; Tick = tick; Thin = thin; Water = water; Pierced = pierced; }
    }

    /// <summary>Trusted host surface response. Null response stops a round as before.
    /// Continuing spends one ballistic step, without a free segment or gravity update.</summary>
    public readonly struct CombatBulletContinuation
    {
        public readonly float VelocityKeep, DamageKeep, ExitDistance;
        public CombatBulletContinuation(float velocityKeep, float damageKeep, float exitDistance)
        { VelocityKeep = velocityKeep; DamageKeep = damageKeep; ExitDistance = exitDistance; }
    }
}
