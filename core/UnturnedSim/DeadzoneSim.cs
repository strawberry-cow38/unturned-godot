using System;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>How much of a suit a deadzone demands. A plain radiation zone is survivable behind a
    /// filtered mask; the harsher kind wants the whole outfit sealed.</summary>
    public enum DeadzoneKind : byte
    {
        Radiation = 0,
        FullSuitRadiation = 1,
    }

    /// <summary>A contaminated volume. Rates are per second so a caller can step at any rate.
    ///
    /// ⚠ CONTAMINATION IS INFECTION, NOT HEALTH (strawberry 2026-09-11: "wire deadzones to deal infection
    /// damage instead of hp"). This used to take health directly and add a little virus on the side. It now
    /// takes ONLY infection, and the health cost -- if any -- arrives through PlayerVitalsSim, which already
    /// kills at Infection 1.0 and already drains infection back down below 0.5 on its own.
    ///
    /// That second half is why the rates are what they are rather than a straight port of the old numbers.
    /// Self-clearing below 0.5 means a short exposure genuinely heals off, so a dose has to be big enough to
    /// push PAST the halfway mark to be a threat at all -- brief trips are survivable by design, and standing
    /// in one is what kills you.</summary>
    public struct DeadzoneDef
    {
        public DeadzoneKind Kind;
        public float ProtectedRadiationPerSecond;    // attrition even in a good suit -- a sealed suit buys time, not immunity
        public float UnprotectedRadiationPerSecond;  // infection accrued with no protection
        public float MaskFilterLossPerSecond;        // filter quality burned while the mask is doing its job

        /// <summary>The stand-in used until per-zone values come from map data. Infection runs 0..1 and is
        /// fatal at 1.0, so these are chosen against that scale rather than against a health pool:
        /// unprotected is ~40 s from clean to dead, a sealed suit ~5 minutes. The old 1:8 protected-to-
        /// unprotected ratio is kept -- the suit was worth wearing at that ratio and still is.</summary>
        public static DeadzoneDef Default(DeadzoneKind kind = DeadzoneKind.Radiation) => new DeadzoneDef
        {
            Kind = kind,
            ProtectedRadiationPerSecond = 0.003f,
            UnprotectedRadiationPerSecond = 0.025f,
            MaskFilterLossPerSecond = 2f,
        };
    }

    /// <summary>What the player is wearing, as far as a deadzone cares.</summary>
    public struct RadiationGear
    {
        public bool MaskProofs;      // mask has Proof_Radiation
        public int MaskQuality;      // 0..100; a spent filter protects nothing
        public bool ShirtProofs;
        public bool PantsProofs;
    }

    /// <summary>What a single step of standing in a deadzone did.
    ///
    /// No Damage field any more, and its absence is the point: a deadzone has exactly one way to hurt you
    /// now, so there is no path by which a zone quietly takes health without the infection meter -- the thing
    /// the player is actually watching -- moving first.</summary>
    public struct DeadzoneTickResult
    {
        public float Radiation;       // infection to add
        public int MaskQualityLost;   // whole points of filter burned this step
        public bool Protected;        // was the suit holding?
    }

    /// <summary>
    /// Standing in contaminated ground.
    ///
    /// This exists partly to finish a seam that was already half-built: <c>ClothingDef.proofRadiation</c>
    /// has been parsed from item data all along, and nothing in the game ever produced radiation for it
    /// to protect against. A flag with no hazard is a flag nobody can tell is broken.
    ///
    /// Engine-free on purpose -- the whole of it is arithmetic over gear and time, so the awkward parts
    /// (a filter running out mid-zone, a grace window on entry, a full-suit zone defeating a mask-only
    /// loadout) are ordinary tests instead of something you verify by standing in a swamp.
    /// </summary>
    public sealed class DeadzoneSim
    {
        /// <summary>Seconds inside before anything is applied, so clipping a corner -- or respawning near
        /// one -- is not instantly punishing.
        ///
        /// Honest provenance: the source has a guard of this KIND but not this shape. It counts
        /// simulation FRAMES (damage waits until you have been inside for more than two of them) and also
        /// resets the counter on respawn. 0.5 s is my own value, chosen because a frame-count threshold
        /// does not port to a sim that callers may step at any rate; the two-frame original would be
        /// ~0.04 s here, which is not a grace period so much as a rounding error.</summary>
        public const float EntryGrace = 0.5f;

        float _inside;                 // continuous seconds in the current zone
        float _pendingFilterLoss;      // fractional filter wear, carried between steps

        public bool IsInside { get; private set; }
        public float SecondsInside => _inside;

        /// <summary>Leaving resets the grace and drops fractional wear, so re-entry starts clean rather
        /// than resuming a half-spent tick.</summary>
        public void Exit()
        {
            IsInside = false;
            _inside = 0f;
            _pendingFilterLoss = 0f;
        }

        /// <summary>Does this loadout hold against this zone? A mask only counts while it has filter
        /// left; the full-suit kind additionally wants shirt and trousers sealed.</summary>
        public static bool IsProtected(in DeadzoneDef zone, in RadiationGear gear)
        {
            bool ok = gear.MaskProofs && gear.MaskQuality > 0;
            if (zone.Kind == DeadzoneKind.FullSuitRadiation)
                ok = ok && gear.ShirtProofs && gear.PantsProofs;
            return ok;
        }

        /// <summary>One step of standing in <paramref name="zone"/>. Returns what to apply; the caller
        /// owns health, infection and the mask item, because those live in different systems.</summary>
        public DeadzoneTickResult Step(in DeadzoneDef zone, in RadiationGear gear, float dt)
        {
            var result = new DeadzoneTickResult();
            if (dt <= 0f) return result;

            IsInside = true;
            _inside += dt;
            if (_inside < EntryGrace) return result;   // just clipped the edge -- nothing yet

            result.Protected = IsProtected(zone, gear);
            if (result.Protected)
            {
                // A sealed suit slows the dose, it does not stop it -- same claim the old health-based
                // version made, expressed on the axis that now carries the whole hazard.
                result.Radiation = zone.ProtectedRadiationPerSecond * dt;

                // The filter is what is actually being consumed; when it runs out the next step is
                // unprotected, which is the failure mode worth feeling.
                _pendingFilterLoss += zone.MaskFilterLossPerSecond * dt;
                int whole = (int)MathF.Floor(_pendingFilterLoss);
                if (whole > 0)
                {
                    _pendingFilterLoss -= whole;
                    result.MaskQualityLost = Math.Min(whole, Math.Max(0, gear.MaskQuality));
                }
            }
            else
            {
                result.Radiation = zone.UnprotectedRadiationPerSecond * dt;
            }
            return result;
        }
    }

    /// <summary>An axis-aligned contaminated box. Kept as plain data so the level layer can hand the sim
    /// a list without either side knowing about the other's types.</summary>
    public struct DeadzoneVolumeDef
    {
        public Vector3 Center;
        public Vector3 HalfExtent;
        public DeadzoneDef Zone;

        public bool Contains(Vector3 p) =>
            MathF.Abs(p.x - Center.x) <= HalfExtent.x &&
            MathF.Abs(p.y - Center.y) <= HalfExtent.y &&
            MathF.Abs(p.z - Center.z) <= HalfExtent.z;
    }
}
