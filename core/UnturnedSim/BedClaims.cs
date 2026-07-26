using System;
using System.Collections.Generic;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>
    /// Who owns which bed, and therefore where each player wakes up.
    ///
    /// The rule that makes this more than a dictionary: a player has exactly ONE bed. Claiming a new one
    /// releases the old, so a spawn point is a thing you move rather than a thing you accumulate --
    /// otherwise a player scatters beds across the map and is unkillable in any meaningful sense.
    ///
    /// Engine-free: beds are ids and positions, so respawn selection is L0-testable rather than something
    /// you can only check by dying in-game.
    /// </summary>
    public sealed class BedClaims
    {
        /// <summary>Settle time before a bed can change hands again, mirroring the door's re-toggle
        /// window -- it stops a contested bed being claim-flipped every frame by two players.</summary>
        public const float ClaimCooldown = 0.75f;

        public struct Bed
        {
            public int Id;
            public Vector3 Position;
            public float Yaw;
            public ulong Owner;        // 0 = unclaimed
            public double LastClaimed;
        }

        readonly Dictionary<int, Bed> _beds = new Dictionary<int, Bed>();
        readonly Dictionary<ulong, int> _byOwner = new Dictionary<ulong, int>();

        public int Count => _beds.Count;

        public void Register(int id, Vector3 position, float yaw = 0f)
        {
            if (_beds.ContainsKey(id)) throw new ArgumentException($"bed {id} already registered", nameof(id));
            // Never-claimed means no settle window. Leaving this at 0 put a bed placed at sim time 0
            // inside its own cooldown, so it could not be claimed at all for the first 0.75 s -- which is
            // exactly when a freshly placed bed gets claimed.
            _beds[id] = new Bed { Id = id, Position = position, Yaw = yaw, LastClaimed = double.NegativeInfinity };
        }

        /// <summary>Remove a bed (salvaged, destroyed). Its owner loses their spawn point, which is the
        /// point of blowing one up.</summary>
        public bool Remove(int id)
        {
            if (!_beds.TryGetValue(id, out var bed)) return false;
            if (bed.Owner != 0UL && _byOwner.TryGetValue(bed.Owner, out int owned) && owned == id)
                _byOwner.Remove(bed.Owner);
            _beds.Remove(id);
            return true;
        }

        public bool TryGet(int id, out Bed bed) => _beds.TryGetValue(id, out bed);
        public bool IsClaimed(int id) => _beds.TryGetValue(id, out var b) && b.Owner != 0UL;
        public ulong OwnerOf(int id) => _beds.TryGetValue(id, out var b) ? b.Owner : 0UL;

        /// <summary>Can <paramref name="player"/> claim this bed now? Someone else's bed is not
        /// claimable -- you have to destroy it first, which is what makes a base worth defending.</summary>
        public bool CanClaim(int id, ulong player, double now)
        {
            if (player == 0UL) return false;
            if (!_beds.TryGetValue(id, out var bed)) return false;
            if (now - bed.LastClaimed < ClaimCooldown) return false;
            return bed.Owner == 0UL || bed.Owner == player;
        }

        /// <summary>Claim a bed, releasing whichever one this player held before. Returns false if the
        /// bed is not claimable.</summary>
        public bool Claim(int id, ulong player, double now)
        {
            if (!CanClaim(id, player, now)) return false;

            if (_byOwner.TryGetValue(player, out int previous) && previous != id && _beds.TryGetValue(previous, out var old))
            {
                old.Owner = 0UL;
                old.LastClaimed = now;
                _beds[previous] = old;   // one bed per player: the old one goes back on the market
            }

            var bed = _beds[id];
            bed.Owner = player;
            bed.LastClaimed = now;
            _beds[id] = bed;
            _byOwner[player] = id;
            return true;
        }

        /// <summary>Give up a bed without claiming another.</summary>
        public bool Unclaim(ulong player, double now)
        {
            if (!_byOwner.TryGetValue(player, out int id)) return false;
            if (_beds.TryGetValue(id, out var bed)) { bed.Owner = 0UL; bed.LastClaimed = now; _beds[id] = bed; }
            _byOwner.Remove(player);
            return true;
        }

        /// <summary>Where this player respawns, or false if they have no bed and should go to the
        /// map's default spawn.</summary>
        public bool TryGetSpawn(ulong player, out Vector3 position, out float yaw)
        {
            position = Vector3.zero; yaw = 0f;
            if (!_byOwner.TryGetValue(player, out int id)) return false;
            if (!_beds.TryGetValue(id, out var bed)) { _byOwner.Remove(player); return false; }   // bed was destroyed
            position = bed.Position; yaw = bed.Yaw;
            return true;
        }

        public bool TryGetOwnedBedId(ulong player, out int id) => _byOwner.TryGetValue(player, out id);
    }
}
