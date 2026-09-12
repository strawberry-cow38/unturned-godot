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

        /// <summary>The stand-in used until per-zone values come from map data.
        ///
        /// ⚠ These are now DOSE rates, not infection rates, and the difference is why they moved. Infection is
        /// no longer added directly: it is scarring proportional to the dose you are CARRYING
        /// (PlayerVitalsSim.InfectionPerRadiationSecond), so the same number means something else than it did
        /// and the old "~40 s from clean to dead" is not a claim these values still make.
        ///
        /// Re-derived rather than swept, measured against the old lethality at full intensity, unprotected:
        /// sprint and jump go at ~30 s (MajorRadiation), death at ~52 s -- close to the 40 s the health-based
        /// version had, with a warning stage in front of it that it did not have. A sealed suit reaches those
        /// at ~73 s / ~95 s, and the FILTER is the real clock: at MaskFilterLossPerSecond it runs out at 50 s
        /// and you finish the zone unprotected. A trip under ~30 s leaves nothing permanent (the dose washes
        /// out and the infection it caused is below the self-clear line); ~45 s leaves ~0.64 infection that
        /// never clears, which is the "your infection stays" half of the mechanic.
        ///
        /// The 1:6.7 protected-to-unprotected ratio is close to the old 1:8 and means the same thing, because
        /// the washout no longer runs while you are inside -- under a background decay the honest comparison
        /// was the NET rate, and a suit slower than the decay would have been total immunity rather than a
        /// delay.</summary>
        public static DeadzoneDef Default(DeadzoneKind kind = DeadzoneKind.Radiation) => new DeadzoneDef
        {
            Kind = kind,
            ProtectedRadiationPerSecond = 0.003f,
            UnprotectedRadiationPerSecond = 0.020f,
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
            => Step(zone, gear, dt, 1f);

        /// <summary>As above, scaled by where in the volume you are standing: 1 deep inside, less near the
        /// boundary (DeadzoneVolumeDef.Intensity). The grace and the filter burn are NOT scaled -- a filter
        /// works just as hard at the edge, and a grace period that stretched near the boundary would make
        /// the tamest part of the zone also the slowest to start counting.</summary>
        public DeadzoneTickResult Step(in DeadzoneDef zone, in RadiationGear gear, float dt, float intensity)
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
                result.Radiation = zone.ProtectedRadiationPerSecond * dt;   // scaled by intensity below, with the unprotected case

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
            result.Radiation *= MathF.Max(0f, intensity);
            return result;
        }
    }

    /// <summary>An axis-aligned contaminated box. Kept as plain data so the level layer can hand the sim
    /// a list without either side knowing about the other's types.</summary>
    /// <summary>A volume's shape. Retail authors both, and PEI's only deadzone is a SPHERE -- approximating
    /// it with its bounding box would make the corners hot, which at r=16 is ground you can stand on 27 m
    /// from the centre and still take a dose.</summary>
    public enum DeadzoneShape { Box = 0, Sphere = 1 }

    public struct DeadzoneVolumeDef
    {
        public Vector3 Center;
        /// <summary>Box: the half-size on each axis. Sphere: the radius is <c>HalfExtent.x</c> and all three
        /// are set to it, so the field stays a valid bounding half-extent either way.</summary>
        public Vector3 HalfExtent;
        public DeadzoneDef Zone;
        /// <summary>Defaults to Box, so every existing caller and test keeps its old meaning.</summary>
        public DeadzoneShape Shape;

        /// <summary>Distance from the centre, squared. Local rather than leaning on the Vector3 compat
        /// shim's operators, which differ between the two Vector3 types in play here.</summary>
        float DistSq(Vector3 p)
        {
            float dx = p.x - Center.x, dy = p.y - Center.y, dz = p.z - Center.z;
            return dx * dx + dy * dy + dz * dz;
        }

        public bool Contains(Vector3 p) => Shape == DeadzoneShape.Sphere
            ? DistSq(p) <= HalfExtent.x * HalfExtent.x
            : MathF.Abs(p.x - Center.x) <= HalfExtent.x &&
              MathF.Abs(p.y - Center.y) <= HalfExtent.y &&
              MathF.Abs(p.z - Center.z) <= HalfExtent.z;

        /// <summary>Fraction of the zone's full rate at this point: ~0 at the boundary, 1 deep inside
        /// (strawberry 2026-09-11: "rads at the edge of the zone are tamer, they still build but not as fast.
        /// a warning to turn around").
        ///
        /// STILL BUILDS AT THE EDGE, never zero inside the volume -- that is the difference between a warning
        /// and a safe place to stand. A falloff that reached 0 would make the boundary a free perch to loot
        /// from, which is the opposite of turning you around.
        ///
        /// Measured on the WORST axis rather than by distance to the centre: a box's danger is how deep you
        /// are past its nearest face, and a Euclidean falloff would call the middle of a long thin zone "deep"
        /// while you stand a metre from its side.</summary>
        public float Intensity(Vector3 p)
        {
            if (!Contains(p)) return 0f;
            if (Shape == DeadzoneShape.Sphere)
            {
                // For a sphere the nearest surface IS radially outward, so distance-to-centre is the correct
                // measure here -- the worst-axis rule below exists because a BOX's nearest face is not.
                float depthR = Depth(MathF.Sqrt(DistSq(p)), HalfExtent.x);
                return EdgeFloor + (1f - EdgeFloor) * MathF.Min(1f, depthR / EdgeBand);
            }
            float dx = Depth(MathF.Abs(p.x - Center.x), HalfExtent.x);
            float dy = Depth(MathF.Abs(p.y - Center.y), HalfExtent.y);
            float dz = Depth(MathF.Abs(p.z - Center.z), HalfExtent.z);
            float depth = MathF.Min(dx, MathF.Min(dy, dz));   // the face you are closest to decides
            // EdgeFloor keeps the boundary live; the ramp reaches full over the outer EdgeBand of the extent.
            return EdgeFloor + (1f - EdgeFloor) * MathF.Min(1f, depth / EdgeBand);
        }

        /// <summary>0 at the face, 1 at the centre line of that axis.</summary>
        static float Depth(float dist, float half) => half <= 0f ? 1f : 1f - dist / half;

        /// <summary>Rate right at the boundary, as a fraction of full. Low enough to be a warning, high enough
        /// that lingering there still costs you.</summary>
        public const float EdgeFloor = 0.15f;

        /// <summary>How far in (as a fraction of the half-extent) the rate takes to reach full.</summary>
        public const float EdgeBand = 0.35f;
    }
}
