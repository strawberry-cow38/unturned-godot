using System.Collections.Generic;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>What the world is doing to you where you are standing. Retail's EPlayerTemperature,
    /// same members in the same order -- the numeric values end up on the wire.</summary>
    public enum PlayerTemperature : byte
    {
        Freezing = 0,
        Cold = 1,
        Warm = 2,
        Burning = 3,
        None = 4,
        Covered = 5,
        Acid = 6,
    }

    /// <summary>
    /// The temperature field: a set of spheres that say "it is hot here".
    ///
    /// Retail models heat as bubbles rather than as a property of the thing that emits it, and that is
    /// the useful part -- a campfire, a burning barricade and a puddle of acid all just register a
    /// sphere, so the player only ever asks "what temperature is this point" and never has to know what
    /// kinds of heat source exist. Adding a new one is a Register call, not a change here.
    ///
    /// Engine-free so a dedicated server and a client resolve the same point identically. Burning does
    /// damage, so this cannot be a client-side effect that happens to look right.
    /// </summary>
    public sealed class TemperatureSim
    {
        public struct Bubble
        {
            public int Id;
            public Vector3 Origin;
            public float SqrRadius;
            public PlayerTemperature Temperature;
        }

        readonly List<Bubble> _bubbles = new();
        int _nextId = 1;

        public int Count => _bubbles.Count;
        public IReadOnlyList<Bubble> Bubbles => _bubbles;

        /// <summary>Add a heat source. Returns a handle -- a bubble outlives the frame that made it and
        /// something has to be able to take it away again when the fire goes out.</summary>
        public int Register(Vector3 origin, float radius, PlayerTemperature temperature)
        {
            int id = _nextId++;
            _bubbles.Add(new Bubble { Id = id, Origin = origin, SqrRadius = radius * radius, Temperature = temperature });
            return id;
        }

        /// <summary>Bubbles move: retail's TemperatureTrigger reads its transform every check, so a fire
        /// on a moving vehicle carries its heat with it.</summary>
        public bool Move(int id, Vector3 origin)
        {
            for (int i = 0; i < _bubbles.Count; i++)
                if (_bubbles[i].Id == id)
                {
                    var b = _bubbles[i]; b.Origin = origin; _bubbles[i] = b; return true;
                }
            return false;
        }

        public bool Deregister(int id)
        {
            for (int i = 0; i < _bubbles.Count; i++)
                if (_bubbles[i].Id == id) { _bubbles.RemoveAt(i); return true; }
            return false;
        }

        public void Clear() => _bubbles.Clear();

        /// <summary>
        /// What a point is subject to, given every bubble covering it.
        ///
        /// The precedence is retail's, and it is not a simple "hottest wins":
        ///   * ACID returns immediately -- nothing outranks it, and it is the only early exit.
        ///   * BURNING is sticky: once seen it survives any later non-acid bubble.
        ///   * everything else is LAST-WINS among itself, so a WARM bubble registered after a COLD one
        ///     replaces it.
        /// That last rule makes the answer depend on registration order, which is worth knowing before
        /// anyone "tidies" this into a max(). Reordering the list changes what the player feels.
        ///
        /// fireproof skips BURNING bubbles entirely rather than surviving them: retail checks the suit
        /// before the radius, so a fireproofed player standing in a fire that is ALSO inside a warm
        /// bubble comes out WARM, not merely undamaged.
        /// </summary>
        public PlayerTemperature Resolve(Vector3 point, bool fireproof)
        {
            var result = PlayerTemperature.None;
            for (int i = 0; i < _bubbles.Count; i++)
            {
                var b = _bubbles[i];
                if (fireproof && b.Temperature == PlayerTemperature.Burning) continue;
                float dx = b.Origin.x - point.x, dy = b.Origin.y - point.y, dz = b.Origin.z - point.z;
                if (dx * dx + dy * dy + dz * dz >= b.SqrRadius) continue;
                if (b.Temperature == PlayerTemperature.Acid) return PlayerTemperature.Acid;
                if (b.Temperature == PlayerTemperature.Burning) result = PlayerTemperature.Burning;
                else if (result != PlayerTemperature.Burning) result = b.Temperature;
            }
            return result;
        }
    }

    /// <summary>
    /// One player's side of it: what they currently feel, and what that costs them.
    ///
    /// Split from TemperatureSim because the field is shared and this is per-player -- and because the
    /// damage cadence is the part that has to be identical on both machines. Retail gates burning on
    /// `simulation - lastBurn > 10` where simulation ticks at PlayerInput.RATE (0.08 s), i.e. every
    /// 0.8 s; freezing is > 25 ticks, i.e. 2 s. Those are expressed in seconds here so the cadence does
    /// not silently change if this port ever picks a different tick rate.
    /// </summary>
    public sealed class PlayerTemperatureSim
    {
        public const float BurnIntervalSeconds = 0.8f;      // retail: 10 ticks at 12.5 Hz
        public const float FreezeIntervalSeconds = 2.0f;    // retail: 25 ticks
        public const float BurnDamage = 10f;
        public const float AcidDamage = 10f;

        public PlayerTemperature Temperature { get; private set; } = PlayerTemperature.None;

        /// <summary>Carried warmth, in seconds. Overrides a cold ambient while it lasts -- retail burns
        /// it down one unit per tick and treats any remaining warmth as WARM.</summary>
        public float Warmth;

        /// <summary>Damage this step owes, 0 most steps. Reported rather than applied because the sim
        /// does not own health: the server decides what damage means (armour, death, a kill feed), and
        /// a sim that reached in and subtracted would have to know all of that.</summary>
        public float Damage { get; private set; }

        /// <summary>True on the step the state changed, so a HUD can react without polling for a diff.</summary>
        public bool JustChanged { get; private set; }

        float _sinceHurt;

        /// <summary>Advance one step against the field's answer for wherever this player is.</summary>
        public void Step(float dt, PlayerTemperature area)
        {
            Damage = 0f;
            JustChanged = false;
            if (Warmth > 0f) Warmth = System.MathF.Max(0f, Warmth - dt);

            var next = area switch
            {
                PlayerTemperature.Acid => PlayerTemperature.Acid,
                PlayerTemperature.Burning => PlayerTemperature.Burning,
                PlayerTemperature.Warm => PlayerTemperature.Warm,
                _ => Warmth > 0f ? PlayerTemperature.Warm : area,
            };

            bool hurts = next == PlayerTemperature.Burning || next == PlayerTemperature.Acid
                      || next == PlayerTemperature.Freezing;
            if (!hurts)
            {
                // Reset rather than let it accumulate: stepping in and out of a fire repeatedly must not
                // bank progress toward a tick you never stood still for.
                _sinceHurt = 0f;
            }
            else
            {
                _sinceHurt += dt;
                float interval = next == PlayerTemperature.Freezing ? FreezeIntervalSeconds : BurnIntervalSeconds;
                if (_sinceHurt >= interval)
                {
                    _sinceHurt -= interval;
                    Damage = next == PlayerTemperature.Freezing ? 8f
                           : next == PlayerTemperature.Acid ? AcidDamage : BurnDamage;
                }
            }

            if (next != Temperature) { Temperature = next; JustChanged = true; }
        }

        /// <summary>Top up carried warmth, e.g. from a heat-giving item.</summary>
        public void AddWarmth(float seconds) => Warmth = System.MathF.Max(Warmth, seconds);

        public void Reset()
        {
            Temperature = PlayerTemperature.None;
            Warmth = 0f; Damage = 0f; JustChanged = false; _sinceHurt = 0f;
        }
    }
}
