using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>Host-only flight data. The original projectile wire stays kind+position;
    /// an integrated renderer/save adapter can inspect the same authoritative record.</summary>
    public readonly struct ProjectileFlight
    {
        public readonly uint NetId;
        public readonly ushort OwnerPlayerId, ItemId;
        public readonly Vector3 Position, Velocity;
        public readonly long SpawnTick, RetireTick;
        public ProjectileFlight(uint id, ushort owner, ushort item, Vector3 position, Vector3 velocity, long spawnTick, long retireTick)
        { NetId = id; OwnerPlayerId = owner; ItemId = item; Position = position; Velocity = velocity; SpawnTick = spawnTick; RetireTick = retireTick; }
    }

    public sealed partial class ServerCombat
    {
        static long ProjectileLifetimeTicks(ThrowableDef def) => Throwables.FuseTicks
            + (def.Kind == EThrowableKind.Flare ? (long)(def.EffectSeconds * 50f) : 0L);
        static ProjectileFlight Flight(GrenadeEntity g) => new ProjectileFlight(g.NetIdValue, g.Owner, g.ItemId, g.Pos, g.Vel, g.SpawnTick, g.ExplodeTick);
        public IEnumerable<ProjectileFlight> ProjectileFlights
        { get { foreach (var g in _grenades) yield return Flight(g); } }
        public bool TryGetProjectileFlight(uint netId, out ProjectileFlight flight)
        {
            foreach (var g in _grenades) if (g.NetIdValue == netId) { flight = Flight(g); return true; }
            flight = default; return false;
        }

        /// <summary>Restore an already-spent throwable's flight; no throw command, inventory spend or
        /// accepted-throw event is replayed. Timers are relative to the new host clock. Original hosts
        /// that do not persist transient flights never call this and retain their existing behavior.</summary>
        public bool ServerRestoreProjectile(NetId id, ushort owner, ushort itemId, Vector3 position, Vector3 velocity,
            long tick, long remainingTicks, long elapsedTicks)
        {
            var def = Throwables.Find(itemId);
            if (def == null || remainingTicks <= 0 || elapsedTicks < 0 || _projectiles.TryGet(id, out _)) return false;
            if (!ItemPipeRules.Finite(position) || !ItemPipeRules.Finite(velocity)) return false;
            long life = ProjectileLifetimeTicks(def);
            remainingTicks = Math.Min(remainingTicks, life); elapsedTicks = Math.Min(elapsedTicks, life);
            _grenades.Add(new GrenadeEntity { NetIdValue = id.Value, Owner = owner, ItemId = itemId, Def = def,
                Pos = position, Vel = velocity, SpawnTick = tick - elapsedTicks, ExplodeTick = tick + remainingTicks });
            _projectiles.ServerSpawn(id, KindOf(def), position, tick + 1);
            return true;
        }
    }
}
