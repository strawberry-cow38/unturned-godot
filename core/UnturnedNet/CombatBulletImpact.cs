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
        public CombatBulletImpact(ushort shooter, ushort sequence, int pellet, Vector3 point,
            Vector3? normal, Vector3 velocity, object context, long tick)
        { Shooter = shooter; Sequence = sequence; Pellet = pellet; Point = point; Normal = normal;
          Velocity = velocity; Context = context; Tick = tick; }
    }
}
