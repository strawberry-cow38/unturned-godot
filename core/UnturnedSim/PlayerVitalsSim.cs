using System;

namespace SDG.Unturned
{
    // The survival-vitals stepping extracted VERBATIM from PlayerController.UpdateVitals (MP_PLAN §3.4:
    // vitals belong to the player sim-core -- server-authoritative per player, owner-only on the wire).
    // The mechanism is source-accurate (PlayerLife: stamina burns while sprinting + regens otherwise;
    // health regenerates only while fed AND hydrated; you take damage when food or water bottoms out);
    // the RATES are the same stand-ins the controller carried. Skill multipliers arrive as plain floats
    // so the core stays engine- and game-layer-free (PlayerSkills lives in game/).
    public sealed class PlayerVitalsSim
    {
        public float Health = 100f;
        public float MaxHealth = 100f;
        // survival vitals (0..1)
        public float Stamina = 1f, Food = 1f, Water = 1f;
        public float Infection;            // 0..1 virus
        public float Oxygen = 1f;          // 0..1 breath: drains with the head under water, refills above it

        /// <summary>0..1 absorbed dose. HIDDEN -- no bar, deliberately (strawberry 2026-09-11: "note radiation
        /// is a separate hidden thing different from infection"). You read it off the geiger counter and the
        /// grain, not off the HUD.
        ///
        /// SEPARATE FROM INFECTION because the two behave oppositely, and that difference is the mechanic:
        /// radiation DECAYS once you are out of the zone -- the dose washes out, sprint comes back, the screen
        /// clears -- while the infection it caused STAYS. Leaving saves you; it does not undo what you already
        /// took. One stat could not do both.</summary>
        public float Radiation;
        public float StaminaRegenDelay;    // seconds to wait after releasing sprint before stamina regenerates

        /// <summary>Seconds of air from a full breath, and seconds to refill it at the surface.</summary>
        public const float OxygenSeconds = 30f, OxygenRefillSeconds = 4f;

        // NO DROWNING DAMAGE. The bar bottoms out at zero and stays there; running out of air costs you
        // nothing but the reading. Master asked for a bar that "depletes when underwater" and nothing more --
        // I added damage on top because a bar that empties and does nothing looked unfinished to me, flagged
        // it as unasked-for, and they said to pull it (2026-09-07). Their call, not mine to infer twice.
        //
        // If it ever comes back: it belongs HERE, reported apart from the health delta, because the server
        // gates that delta behind SurvivalDrain (hunger ships off) and breath must not ship off with it.

        public struct Multipliers
        {
            public float ExerciseStaminaDrain;   // EXERCISE slows the drain
            public float CardioStaminaRegen;     // CARDIO speeds the regen
            public float SurvivalDrain;          // SURVIVAL slows hunger/thirst
            public float VitalityRegen;          // VITALITY speeds health regen

            public static Multipliers None => new Multipliers
            { ExerciseStaminaDrain = 1f, CardioStaminaRegen = 1f, SurvivalDrain = 1f, VitalityRegen = 1f };
        }

        /// <summary>One vitals step. Returns true if health reached zero THIS step -- the caller (shell or
        /// server) owns what death means (corpse, respawn, events). Callers must not step a dead player.</summary>
        /// <summary>Take a dose. The ONLY way radiation goes up, and the only place it turns into infection.
        ///
        /// Both happen here together so they cannot get out of step: every unit of dose absorbed is scarring
        /// you at the same moment, rather than infection being a second effect some other caller might forget
        /// to apply. `ratePerSecond` is the zone's own rate, already scaled for the suit and for how far into
        /// the volume you are standing.</summary>
        public void Irradiate(float ratePerSecond, float dt) => AbsorbDose(ratePerSecond * dt, dt);

        /// <summary>The same thing, given the dose ALREADY scaled by dt -- which is the shape DeadzoneSim
        /// returns, so neither caller has to divide by dt only for this to multiply it straight back. That
        /// round trip is not merely ugly: it is a division by a small float on a path where the server and the
        /// client must agree bit-for-bit, and the two sides step at different dt.
        ///
        /// dt is still taken, because the SCAR needs it independently of the dose: the dose is a quantity, the
        /// infection it causes is a rate applied over time.</summary>
        public void AbsorbDose(float dose, float dt)
        {
            if (dt <= 0f || dose <= 0f) return;
            _doseHold = DoseHoldSeconds;   // you are IN it; nothing washes out until you leave
            Radiation = MathF.Min(1f, Radiation + dose);
            // Scarring scales with the dose you are CARRYING, not merely with the zone's rate: deep in a hot
            // zone with a full body burden you take infection far faster than someone who just stepped in.
            Infection = MathF.Min(1f, Infection + Radiation * InfectionPerRadiationSecond * dt);
        }

        /// <summary>Dose high enough to cost you your legs -- sprint and jump, same as a broken bone.</summary>
        public bool MajorlyIrradiated => Radiation >= MajorRadiation;

        public bool Step(bool sprinting, bool survivalDrain, float dt, in Multipliers m)
            => Step(sprinting, false, survivalDrain, false, dt, m);

        public bool Step(bool sprinting, bool submerged, bool survivalDrain, float dt, in Multipliers m)
            => Step(sprinting, submerged, survivalDrain, false, dt, m);

        // HP per second lost while BLEEDING. Slow on purpose (strawberry: "bleeding should slowly drain hp"):
        // ~2.2 minutes from full, so an unbandaged wound is a clock you have to answer, not a death sentence.
        public const float BleedHealthPerSecond = 0.75f;
        // Infection only clears ITSELF below this. Above it the virus has the upper hand and antibiotics are
        // the only way down (strawberry: "below 50% infection drains slowly on its own"). It used to decay
        // unconditionally, which made a bite something you could always walk off.
        public const float InfectionSelfClearBelow = 0.50f;
        // ...and at a full 100 it kills, rather than merely sitting at the -2 HP/s sick drain.
        public const float InfectionFatal = 1.0f;

        // RADIATION (strawberry 2026-09-11). A hidden 0..1 dose that builds in contaminated ground and washes
        // out once you leave -- the opposite of infection, which is what it leaves behind.
        //
        // MajorRadiation is where the dose stops being a warning and starts costing you: sprint and jump go,
        // exactly as they do on a broken leg, because the failure mode is the same one (your legs will not
        // answer) and the player already knows what that feels like.
        //
        // The decay is deliberately slower than the accrual: walking out is relief, not a reset, and standing
        // at the boundary hopping in and out must not be free. ~66 s to wash a full dose out.
        public const float MajorRadiation = 0.60f;
        public const float RadiationDecayPerSecond = 0.015f;

        /// <summary>How long a dose suppresses the washout. Strawberry asked for radiation that "gradually
        /// dissipates WHEN LEAVING a deadzone" -- so decay is what leaving does, not a constant background
        /// drain, and it must not run while you are still standing in the ground.
        ///
        /// ⚠ THIS IS WHAT MAKES THE ZONE EDGE BUILD AT ALL, and getting it wrong is silent. A flat decay that
        /// ran everywhere would cancel any accrual slower than itself: at the boundary the rate is scaled by
        /// EdgeFloor, so the tamest part of every zone would have been not merely tame but completely inert --
        /// a safe perch to loot from, which is the exact thing DeadzoneVolumeDef.Intensity's own comment
        /// claims the EdgeFloor prevents. The falloff test passed the whole time: it asserted the intensity
        /// was non-zero, which it was, and never that a player standing there accumulated anything.
        ///
        /// Longer than the 0.25 s deadzone poll on BOTH sides (DeadzoneField.PollSeconds and
        /// ServerDeadzones.PollSeconds), with room to spare, so an ordinary gap between polls cannot flicker
        /// a player who has not moved into washing out.</summary>
        public const float DoseHoldSeconds = 0.6f;

        float _doseHold;

        /// <summary>Is the dose currently being topped up (i.e. standing in contaminated ground)? Exposed for
        /// tests and for the HUD-free feedback, which wants "getting worse" to read differently from
        /// "recovering".</summary>
        public bool AbsorbingDose => _doseHold > 0f;

        /// <summary>Infection accrued per second per unit of radiation, while the dose is being taken. This is
        /// the ONE-WAY door between the two: radiation drives infection, infection never drives radiation, so
        /// the dose washing out cannot also un-infect you.</summary>
        public const float InfectionPerRadiationSecond = 0.05f;

        // TEMPERATURE EFFECTS (strawberry 2026-09-10: "being cold drains food and water faster, being
        // freezing hurts you. being hot drains water faster, being boiling hurts you"). Cold drains BOTH
        // because shivering burns fuel as well as water; hot drains water only, which is what makes a
        // desert and a blizzard feel like different problems rather than one problem at two speeds.
        public const float ColdDrainMultiplier = 1.6f;
        public const float HotWaterMultiplier = 2.0f;
        public const float ExposureHealthPerSecond = 1.0f;

        /// <summary>submerged = the player's HEAD is under water (not merely their feet, and not merely "is
        /// swimming" -- treading water at the surface has your face in the air and must not cost you a breath).</summary>
        public bool Step(bool sprinting, bool submerged, bool survivalDrain, bool bleeding, float dt, in Multipliers m)
            => Step(sprinting, submerged, survivalDrain, bleeding, PlayerTemperatureSim.Band.Comfortable, dt, m);

        public bool Step(bool sprinting, bool submerged, bool survivalDrain, bool bleeding,
                         PlayerTemperatureSim.Band band, float dt, in Multipliers m)
        {
            if (sprinting) { Stamina = MathF.Max(0f, Stamina - 0.22f * dt * m.ExerciseStaminaDrain); StaminaRegenDelay = 1f; }   // hold regen 1s after releasing sprint
            else { StaminaRegenDelay = MathF.Max(0f, StaminaRegenDelay - dt); if (StaminaRegenDelay <= 0f) Stamina = MathF.Min(1f, Stamina + 0.33f * dt * m.CardioStaminaRegen); }
            bool cold = band == PlayerTemperatureSim.Band.Cold || band == PlayerTemperatureSim.Band.Freezing;
            bool hot = band == PlayerTemperatureSim.Band.Hot || band == PlayerTemperatureSim.Band.Boiling;
            if (survivalDrain)   // hunger/thirst OFF by default (strawberry); F1 console `survival` toggles it
            {
                // Temperature MULTIPLIES the existing drain rather than adding its own, so it rides the same
                // survival toggle. Turning survival off and still starving from the cold would be a surprise.
                float foodMul = cold ? ColdDrainMultiplier : 1f;
                float waterMul = cold ? ColdDrainMultiplier : hot ? HotWaterMultiplier : 1f;
                Food  = MathF.Max(0f, Food  - 0.0050f * dt * m.SurvivalDrain * foodMul);
                Water = MathF.Max(0f, Water - 0.0070f * dt * m.SurvivalDrain * waterMul);
            }
            // BREATH. Drains only with the head under and refills far faster than it empties -- a surfacing
            // player gets their air back in a gulp, not over half a minute. Purely a readout: see the note by
            // OxygenSeconds for why running out costs nothing.
            if (submerged) Oxygen = MathF.Max(0f, Oxygen - dt / OxygenSeconds);
            else Oxygen = MathF.Min(1f, Oxygen + dt / OxygenRefillSeconds);

            // RADIATION WASHES OUT, unconditionally and from any level (strawberry 2026-09-11: "radiation
            // gradually dissipates when leaving a deadzone"). Unlike infection there is no threshold above
            // which it holds -- the whole point of the pair is that the dose is survivable and temporary while
            // what it did to you is not. DeadzoneField re-raises it every poll, so this only wins once you are
            // actually out.
            if (_doseHold > 0f) _doseHold = MathF.Max(0f, _doseHold - dt);
            else if (Radiation > 0f) Radiation = MathF.Max(0f, Radiation - RadiationDecayPerSecond * dt);

            // The virus clears on its own ONLY below InfectionSelfClearBelow. Past that it holds, so a bad bite
            // is a problem you have to treat rather than one you outlast.
            if (Infection < InfectionSelfClearBelow) Infection = MathF.Max(0f, Infection - 0.01f * dt);
            if (Infection >= InfectionFatal) { Health = 0f; return true; }   // 100% virus kills outright
            bool sick = Infection > 0.75f;                           // heavy infection makes you ill (loses health)
            // BLEEDING costs health and blocks regen -- it is no longer a HUD decoration. It does not clear on
            // a timer either; a wound stays open until it is dressed (ItemAsset.useStopsBleeding).
            if (bleeding) Health = MathF.Max(0f, Health - BleedHealthPerSecond * dt);
            // EXPOSURE is deliberately NOT behind the survival toggle: hunger is a mode, weather is a hazard,
            // and a map that can kill you with cold should still do it with hunger switched off.
            bool exposed = band == PlayerTemperatureSim.Band.Freezing || band == PlayerTemperatureSim.Band.Boiling;
            if (exposed) Health = MathF.Max(0f, Health - ExposureHealthPerSecond * dt);
            if (Food > 0.30f && Water > 0.30f && Health < MaxHealth && !sick && !bleeding && !exposed)
                Health = MathF.Min(MaxHealth, Health + 2f * dt * m.VitalityRegen);     // regen while fed + hydrated (blocked while sick or bleeding)
            else if (Food <= 0f || Water <= 0f || sick)
                Health = MathF.Max(0f, Health - (sick ? 2f : 1.5f) * dt);   // starve / dehydrate / infection sickness
            return Health <= 0f;
        }
    }
}
