using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Contaminated ground, server side.
    ///
    /// Singleplayer runs deadzones through DeadzoneField, which walks PlayerController nodes. A dedicated
    /// server has no such nodes -- its players are entities in PlayerReplication -- so this is the same
    /// hazard driven off replicated positions instead. The arithmetic is not re-implemented: every step
    /// goes through the identical DeadzoneSim singleplayer uses, one instance per player, so entry grace,
    /// suit resolution and filter burn-down behave the same in both modes by construction rather than by
    /// two implementations agreeing.
    ///
    /// The four sinks are how the result leaves: damage into the same external-damage queue every other
    /// server-derived hazard uses (death-capable), infection into the authoritative vitals, filter burn
    /// onto the server's own copy of the mask. Any sink left null simply drops that effect, which is how a
    /// harness can exercise the geometry without standing up an inventory.
    /// </summary>
    public sealed class ServerDeadzones
    {
        /// <summary>Matches DeadzoneField.PollSeconds. A deadzone is a slow hazard; re-evaluating every
        /// player every tick would be per-player work nobody could feel the difference of -- and stepping
        /// at the SAME cadence as singleplayer keeps the two damage curves comparable.</summary>
        public const float PollSeconds = 0.25f;

        readonly List<DeadzoneVolumeDef> _volumes = new List<DeadzoneVolumeDef>();
        readonly Dictionary<ushort, DeadzoneSim> _inside = new Dictionary<ushort, DeadzoneSim>();
        readonly List<ushort> _stale = new List<ushort>();
        float _acc;

        /// <summary>What this player is wearing, from the server's authoritative inventory. Null = nobody
        /// is protected, which is only right for a harness with no inventories at all.</summary>
        public Func<ushort, RadiationGear> GearOf;

        /// <summary>Health to remove. Wire this to the same sink the other server-derived damage uses, so
        /// a deadzone can kill and the death runs the normal path.</summary>
        public Action<ushort, float> DamageSink;

        /// <summary>Virus accrued while unprotected.</summary>
        public Action<ushort, float> InfectionSink;

        /// <summary>Whole points of mask filter burned this step.</summary>
        public Action<ushort, int> MaskBurnSink;

        public int VolumeCount => _volumes.Count;

        public void AddVolume(Vector3 center, Vector3 halfExtent, DeadzoneKind kind = DeadzoneKind.Radiation)
            => AddVolume(center, halfExtent, DeadzoneDef.Default(kind));

        public void AddVolume(Vector3 center, Vector3 halfExtent, DeadzoneDef zone)
            => _volumes.Add(new DeadzoneVolumeDef { Center = center, HalfExtent = halfExtent, Zone = zone });

        public void Clear() { _volumes.Clear(); _inside.Clear(); }

        public bool TryGetVolume(Vector3 p, out DeadzoneVolumeDef found)
        {
            foreach (var v in _volumes)
                if (v.Contains(p)) { found = v; return true; }
            found = default;
            return false;
        }

        public bool IsInside(Vector3 p) => TryGetVolume(p, out _);

        /// <summary>Seconds this player has stood in their current zone; 0 if they are not in one.</summary>
        public float SecondsInside(ushort playerId)
            => _inside.TryGetValue(playerId, out var s) ? s.SecondsInside : 0f;

        /// <summary>Called every tick; does the real work only once per <see cref="PollSeconds"/>. A player
        /// missing from <paramref name="players"/> when a poll runs is forgotten, so someone who disconnected
        /// does not keep their accrued exposure waiting for a reconnect. <paramref name="isAlive"/> is
        /// optional; without it a corpse keeps accruing, which is only acceptable in a harness.</summary>
        public void Step(float dt, IEnumerable<PlayerReplication.PlayerEntity> players, Func<ushort, bool> isAlive = null)
        {
            _acc += dt;
            if (_acc < PollSeconds) return;
            float step = _acc;
            _acc = 0f;

            _present.Clear();
            foreach (var e in players)
            {
                if (e == null) continue;
                _present.Add(e.OwnerPlayerId);
                // A corpse stops accruing. Without this the death timer keeps ticking damage into a body
                // that is already dead, and the respawn arrives pre-damaged.
                if (isAlive != null && !isAlive(e.OwnerPlayerId)) { _inside.Remove(e.OwnerPlayerId); continue; }
                Apply(e.OwnerPlayerId, e.Pos, step);
            }

            _stale.Clear();
            foreach (var kv in _inside) if (!_present.Contains(kv.Key)) _stale.Add(kv.Key);
            foreach (var k in _stale) _inside.Remove(k);
        }

        readonly HashSet<ushort> _present = new HashSet<ushort>();

        /// <summary>One player, one step. Public so a test can drive it deterministically instead of
        /// accumulating wall time -- the same reason DeadzoneField.Apply is public.</summary>
        public void Apply(ushort playerId, Vector3 pos, float dt)
        {
            if (!TryGetVolume(pos, out var volume))
            {
                // Leaving resets the grace window: DeadzoneSim.Exit is what makes stepping out and back in
                // cost you the entry grace again rather than resuming mid-burn.
                if (_inside.TryGetValue(playerId, out var left)) { left.Exit(); _inside.Remove(playerId); }
                return;
            }

            if (!_inside.TryGetValue(playerId, out var sim))
            {
                sim = new DeadzoneSim();
                _inside[playerId] = sim;
            }

            var gear = GearOf != null ? GearOf(playerId) : default;
            var r = sim.Step(volume.Zone, gear, dt);

            if (r.Damage > 0f) DamageSink?.Invoke(playerId, r.Damage);
            if (r.Radiation > 0f) InfectionSink?.Invoke(playerId, r.Radiation);
            if (r.MaskQualityLost > 0) MaskBurnSink?.Invoke(playerId, r.MaskQualityLost);
        }

        /// <summary>Forget a player outright (disconnect). Their exposure does not survive them.</summary>
        public void OnPlayerLeft(ushort playerId) => _inside.Remove(playerId);
    }
}
