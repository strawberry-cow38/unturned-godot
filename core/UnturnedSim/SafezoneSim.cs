using System;
using System.Collections.Generic;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>A protected volume. Retail's safezone is a sphere that only counts while its generator
    /// is wired AND powered, so the zone is a consequence of the power grid rather than a fixed map
    /// property -- cut the power and the protection goes with it. Radius is horizontal-and-vertical
    /// (a true sphere), which is what makes standing on a roof inside the bubble still safe.</summary>
    public struct SafezoneDef
    {
        public Vector3 Center;
        public float Radius;
        /// <summary>False while unwired or unpowered. An inactive zone protects nobody, but it still
        /// EXISTS -- the distinction matters because the view has to keep rendering a dead bubble.</summary>
        public bool Active;

        public bool Contains(Vector3 p)
        {
            if (!Active) return false;
            return (p - Center).sqrMagnitude <= Radius * Radius;
        }
    }

    /// <summary>
    /// The safezone rules, engine-free.
    ///
    /// Everything here is a pure query over a small set of spheres, because that is all the gameplay
    /// consumers need: "is this point protected?" Retail's own call sites are exactly that shape --
    /// a point test asked by damage, by building placement and by sentry targeting -- so the sim owns
    /// the zones and answers questions, rather than reaching into players or zombies itself.
    ///
    /// Deliberately NOT a Godot Area3D: a zone must exist identically on a dedicated server with no
    /// rendering and no physics stepping, and the whole point of this codebase's split is that rules
    /// like this stay testable without an engine.
    /// </summary>
    public sealed class SafezoneSim
    {
        readonly List<SafezoneDef> _zones = new();

        public int Count => _zones.Count;
        public SafezoneDef ZoneAt(int i) => _zones[i];

        /// <summary>Register a zone; returns its index so the owner can update Active later.</summary>
        public int Add(Vector3 center, float radius, bool active = true)
        {
            _zones.Add(new SafezoneDef { Center = center, Radius = radius, Active = active });
            return _zones.Count - 1;
        }

        public void SetActive(int index, bool active)
        {
            if (index < 0 || index >= _zones.Count) return;
            var z = _zones[index];
            z.Active = active;
            _zones[index] = z;
        }

        public void SetCenter(int index, Vector3 center)
        {
            if (index < 0 || index >= _zones.Count) return;
            var z = _zones[index];
            z.Center = center;
            _zones[index] = z;
        }

        public void Clear() => _zones.Clear();

        /// <summary>Is this point inside any ACTIVE zone? The one question every consumer asks.</summary>
        public bool Contains(Vector3 p)
        {
            for (int i = 0; i < _zones.Count; i++)
                if (_zones[i].Contains(p)) return true;
            return false;
        }

        /// <summary>Index of the containing zone, or -1. Lets a caller distinguish which zone it is in
        /// (a view wants that; a damage test does not).</summary>
        public int IndexOf(Vector3 p)
        {
            for (int i = 0; i < _zones.Count; i++)
                if (_zones[i].Contains(p)) return i;
            return -1;
        }

        // --- the gameplay rules, as pure functions -----------------------------------------------
        //
        // Named for what they DECIDE, not for what they check, so a call site reads as the rule it is
        // enforcing. Each is a one-liner today; they exist because "safezone" means three different
        // restrictions and collapsing them into one `Contains` at the call sites is how a rule quietly
        // gets applied in two places and forgotten in a third.

        /// <summary>Player-vs-player and environmental damage is refused inside a live zone. Retail's
        /// safezone is a truce, which is the whole reason to build one.</summary>
        public bool BlocksDamageAt(Vector3 victim) => Contains(victim);

        /// <summary>Zombies do not enter. Enforced as a movement/targeting rule rather than by killing
        /// them at the boundary, so a horde piles up at the edge instead of evaporating.</summary>
        public bool RepelsZombiesAt(Vector3 p) => Contains(p);

        /// <summary>No building inside someone else's bubble -- retail refuses barricade and structure
        /// placement in a safezone, which is what stops a zone being griefed shut.</summary>
        public bool BlocksBuildingAt(Vector3 p) => Contains(p);

        /// <summary>Nearest active zone centre to a point, for pushing a repelled zombie outward.
        /// Returns false when the point is not in any zone.</summary>
        public bool TryGetContaining(Vector3 p, out Vector3 center, out float radius)
        {
            int i = IndexOf(p);
            if (i < 0) { center = default; radius = 0f; return false; }
            center = _zones[i].Center;
            radius = _zones[i].Radius;
            return true;
        }

        /// <summary>Where a zombie at `p` should be pushed to leave the zone it is standing in: the
        /// nearest point just outside the boundary, along the outward radial. Horizontal only -- a
        /// zombie shoved vertically out of a bubble would be launched into the air or the ground.</summary>
        public Vector3 EjectionTarget(Vector3 p, float margin = 0.5f)
        {
            if (!TryGetContaining(p, out var c, out float r)) return p;
            Vector3 flat = new Vector3(p.x - c.x, 0f, p.z - c.z);
            float d = flat.magnitude;
            // Dead centre has no outward direction; pick one deterministically rather than NaN.
            Vector3 dir = d > 1e-4f ? flat / d : new Vector3(1f, 0f, 0f);
            return new Vector3(c.x + dir.x * (r + margin), p.y, c.z + dir.z * (r + margin));
        }
    }
}
