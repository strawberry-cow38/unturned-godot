using System;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public sealed partial class ServerCombat
    {
        /// <summary>Optional host collision adapter for the firing mount's own
        /// vehicle. Captured identity survives operator exit or seat changes;
        /// ordinary rounds and hosts without this adapter retain their old ray.</summary>
        public CombatMountedWorldRay MountedRayDetailed;
        public Func<uint, Vector3, Vector3, float, long, bool> DamageMountedBarricadeAlong;
        /// <summary>Trusted host launch for a vehicle's own validated belt and muzzle.
        /// No command handler calls this directly: the mounted adapter resolves the
        /// accepted occupant, source mount, cadence and ammunition first. It never
        /// changes the operator's held gun, reload, inventory, or shot cadence.</summary>
        public bool ServerLaunchMounted(ushort owner, ushort sequence, Vector3 origin, Vector3 direction,
            ServerGunProfile profile, object context = null, bool npc = false, uint firingVehicle = 0)
        {
            if (profile == null || !MountedWire.Finite(origin) || !MountedWire.Finite(direction)
                || direction.sqrMagnitude < .000001f || !float.IsFinite(profile.MuzzleVelocity)
                || profile.MuzzleVelocity <= 0 || profile.BallisticSteps <= 0 || profile.BallisticSteps > 100000
                || !float.IsFinite(profile.GravityMultiplier) || !DamageValid(profile.PlayerDamage)
                || !DamageValid(profile.ZombieDamage) || !DamageValid(profile.AnimalDamage)
                || !DamageValid(profile.VehicleDamage) || !DamageValid(profile.ObjectDamage)
                || !DamageValid(profile.ResourceDamage)) return false;
            if (npc ? owner != 0 : owner == 0 || !_state.IsAlive(owner) || !_players.TryGetByOwner(owner, out _)) return false;
            _bullets.Add(new Bullet {
                Pos = origin, Origin = origin, Vel = direction.normalized * profile.MuzzleVelocity,
                StepsLeft = profile.BallisticSteps, Gravity = -9.81f * profile.GravityMultiplier,
                Shooter = owner, Seq = sequence, Gun = profile.Copy(), AnimalDamage = profile.AnimalDamage,
                HostContext = context, Npc = npc, Mounted = true, FiringVehicle = firingVehicle,
            });
            Diag.ShotsAccepted++;
            return true;
        }
        static bool DamageValid(float value) => float.IsFinite(value) && value >= 0;
    }
}
