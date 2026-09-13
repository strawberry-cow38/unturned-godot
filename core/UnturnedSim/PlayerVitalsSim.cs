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

        /// <summary>Seconds left before passive health regen may resume after taking damage. Armed by
        /// <see cref="NotifyDamaged"/>, counted down in Step. A FIELD rather than a Step parameter because the
        /// damage that arms it does not arrive on the vitals tick -- it arrives from combat, whenever combat
        /// happens, and a per-tick "was I hit" flag would have to be cleared by someone who might forget.</summary>
        public float RegenLockDelay;

        /// <summary>Call when the player takes damage from ANY source, to block passive regen for
        /// <see cref="RegenDamageLockSeconds"/>. Idempotent -- a burst of hits just re-arms the same timer.</summary>
        public void NotifyDamaged() => RegenLockDelay = RegenDamageLockSeconds;

        /// <summary>A full 0 -> MaxHealth heal in HealthHealDays of game time. Derived from MaxHealth so a
        /// raised ceiling heals proportionally rather than taking twice as long.</summary>
        public float HealthRegenPerSecond => MaxHealth / (HealthHealDays * GameDaySeconds);

        /// <summary>Seconds of air from a full breath, and seconds to refill it at the surface.</summary>
        public const float OxygenSeconds = 30f, OxygenRefillSeconds = 4f;

        // NO DROWNING DAMAGE. The bar bottoms out at zero and stays there; running out of air costs you
        // nothing but the reading. Master asked for a bar that "depletes when underwater" and nothing more --
        // I added damage on top because a bar that empties and does nothing looked unfinished to me, flagged
        // it as unasked-for, and they said to pull it (2026-09-07). Their call, not mine to infer twice.
        //
        // If it ever comes back: it belongs HERE, reported apart from the health delta, because the server
        // gates that delta behind SurvivalDrain and breath must not ride that switch with it.

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
        // SURVIVAL DRAIN, EXPRESSED IN GAME DAYS (strawberry 2026-09-13: food 100->0 over two days, water over one).
        //
        // A game day is DayNightCycle.DefaultDayLength = 24 real minutes = 1440 s, so the rates are written as
        // 1/(days x 1440) rather than as tuned decimals -- the intent stays readable and re-deriving them if the
        // day length ever moves is arithmetic instead of archaeology.
        //
        // They were 0.0050 and 0.0070, i.e. a full bar in 200 s and 143 s. That is not a survival curve, it is a
        // timer: you starve in the time it takes to loot one house.
        public const float FoodDaysToEmpty = 2f, WaterDaysToEmpty = 1f;
        public const float GameDaySeconds = 24f * 60f;   // mirrors DayNightCycle.DefaultDayLength (core cannot reference the game assembly)
        public const float FoodDrainPerSecond  = 1f / (FoodDaysToEmpty  * GameDaySeconds);   // ~0.000347 -> 2880 s
        public const float WaterDrainPerSecond = 1f / (WaterDaysToEmpty * GameDaySeconds);   // ~0.000694 -> 1440 s

        // SPRINT DRAIN. strawberry asked for double the stamina MAXIMUM and a lower sprint decay on top.
        //
        // ⚠ The maximum stays 1.0 ON PURPOSE: Stamina crosses the wire as
        // WriteUnsignedNormalizedFloat(Clamp01(Stamina)) (PlayerVitalsReplication), so a value above 1 is silently
        // clamped in MP -- the client would read a full bar while the server held 2.0. Raising the literal ceiling
        // is a WIRE change, not a constant change. Stamina therefore stays "fraction of capacity" and the capacity
        // is doubled where it is actually observable: how long a sprint lasts.
        //
        //   0.22  -> 4.5 s of continuous sprint   (was)
        //   0.11  -> 9.1 s                        ("double the maximum")
        //   0.075 -> 13.3 s                       (+ the lower decay on top)
        public const float SprintDrainPerSecond = 0.075f;
        public const float BleedHealthPerSecond = 0.75f;

        // INFECTION SELF-CLEAR (strawberry 2026-09-13: "have infection heal over a full day, and never heal
        // while taking infection damage").
        //
        // The gate USED to be "below 50%" and the rate a flat 0.01/s -- a full bar in 100 s, which is not a
        // sickness, it is a cooldown. It now clears a full bar over one game day at the same 1/(days x
        // GameDaySeconds) form as food and water, so the three survival curves are read the same way.
        //
        // ⚠ The 50% threshold is GONE, replaced by the condition strawberry named: no self-clear while the
        // virus is actually costing you health. That keeps the point of the old rule -- a bad bite is a
        // problem you treat, not one you outlast -- because InfectionSickAbove is where the -2 HP/s starts,
        // so anything bad enough to hurt you still holds until antibiotics bring it under that line.
        public const float InfectionDaysToClear = 1f;
        public const float InfectionClearPerSecond = 1f / (InfectionDaysToClear * GameDaySeconds);
        /// <summary>Above this the virus costs health (-2 HP/s) -- and, since 2026-09-13, refuses to self-clear.</summary>
        public const float InfectionSickAbove = 0.75f;
        // ...and at a full 100 it kills, rather than merely sitting at the -2 HP/s sick drain.
        public const float InfectionFatal = 1.0f;

        // PASSIVE HEALTH REGEN (strawberry 2026-09-13: "have health slowly regen, never when taking damage,
        // or having a bleeding or broken leg effect").
        //
        // It was 2 HP/s -- a full 0->100 in 50 s, which makes taking a hit free as long as you break contact.
        // "Slowly" is expressed as a full heal over a QUARTER of a game day (~6 min) so it reads in the same
        // unit as the other curves; retune HealthHealDays, not a decimal.
        //
        // The damage lock is the new part. Bleeding, sickness, exposure and starvation already blocked regen,
        // but a bullet did not -- you could be shot and start healing on the same tick. RegenLockDelay is armed
        // by NotifyDamaged() and has to run out before regen resumes, which is what makes disengaging a
        // decision rather than a formality.
        public const float HealthHealDays = 0.25f;
        public const float RegenDamageLockSeconds = 10f;

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
            => Step(sprinting, submerged, survivalDrain, bleeding, false, PlayerTemperatureSim.Band.Comfortable, dt, m);

        public bool Step(bool sprinting, bool submerged, bool survivalDrain, bool bleeding,
                         PlayerTemperatureSim.Band band, float dt, in Multipliers m)
            => Step(sprinting, submerged, survivalDrain, bleeding, false, band, dt, m);

        public bool Step(bool sprinting, bool submerged, bool survivalDrain, bool bleeding, bool broken,
                         float dt, in Multipliers m)
            => Step(sprinting, submerged, survivalDrain, bleeding, broken, PlayerTemperatureSim.Band.Comfortable, dt, m);

        /// <summary>broken = broken legs (PlayerLife.isBroken). Blocks passive health regen, per strawberry
        /// 2026-09-13 -- you do not walk a fracture off while it is still a fracture.</summary>
        public bool Step(bool sprinting, bool submerged, bool survivalDrain, bool bleeding, bool broken,
                         PlayerTemperatureSim.Band band, float dt, in Multipliers m)
        {
            RegenLockDelay = MathF.Max(0f, RegenLockDelay - dt);   // count down the post-damage regen lock
            if (sprinting) { Stamina = MathF.Max(0f, Stamina - SprintDrainPerSecond * dt * m.ExerciseStaminaDrain); StaminaRegenDelay = 1f; }   // hold regen 1s after releasing sprint
            else { StaminaRegenDelay = MathF.Max(0f, StaminaRegenDelay - dt); if (StaminaRegenDelay <= 0f) Stamina = MathF.Min(1f, Stamina + 0.33f * dt * m.CardioStaminaRegen); }
            bool cold = band == PlayerTemperatureSim.Band.Cold || band == PlayerTemperatureSim.Band.Freezing;
            bool hot = band == PlayerTemperatureSim.Band.Hot || band == PlayerTemperatureSim.Band.Boiling;
            if (survivalDrain)   // hunger/thirst ON by default (strawberry 2026-09-13); F1 console `survival` toggles it
            {
                // Temperature MULTIPLIES the existing drain rather than adding its own, so it rides the same
                // survival toggle. Turning survival off and still starving from the cold would be a surprise.
                float foodMul = cold ? ColdDrainMultiplier : 1f;
                float waterMul = cold ? ColdDrainMultiplier : hot ? HotWaterMultiplier : 1f;
                Food  = MathF.Max(0f, Food  - FoodDrainPerSecond  * dt * m.SurvivalDrain * foodMul);
                Water = MathF.Max(0f, Water - WaterDrainPerSecond * dt * m.SurvivalDrain * waterMul);
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

            // ⚠ `sick` is decided BEFORE the self-clear, not after, because it is now the self-clear's own gate
            // ("never heal while taking infection damage"). Computing it afterwards would let a tick both take
            // the damage and count as healed, and at the boundary the two readings disagree.
            bool sick = Infection > InfectionSickAbove;              // heavy infection makes you ill (loses health)
            if (!sick) Infection = MathF.Max(0f, Infection - InfectionClearPerSecond * dt);   // a full bar over one game day
            if (Infection >= InfectionFatal) { Health = 0f; return true; }   // 100% virus kills outright
            // BLEEDING costs health and blocks regen -- it is no longer a HUD decoration. It does not clear on
            // a timer either; a wound stays open until it is dressed (ItemAsset.useStopsBleeding).
            if (bleeding) Health = MathF.Max(0f, Health - BleedHealthPerSecond * dt);
            // EXPOSURE is deliberately NOT behind the survival toggle: hunger is a mode, weather is a hazard,
            // and a map that can kill you with cold should still do it with hunger switched off.
            bool exposed = band == PlayerTemperatureSim.Band.Freezing || band == PlayerTemperatureSim.Band.Boiling;
            if (exposed) Health = MathF.Max(0f, Health - ExposureHealthPerSecond * dt);
            // PASSIVE REGEN, and every clause is a way of saying "nothing is currently hurting you": fed, hydrated,
            // not sick, not bleeding, not exposed, legs intact, and nothing has hit you for RegenDamageLockSeconds.
            // The last two are strawberry 2026-09-13; the rest were already here.
            if (Food > 0.30f && Water > 0.30f && Health < MaxHealth && !sick && !bleeding && !exposed
                && !broken && RegenLockDelay <= 0f)
                Health = MathF.Min(MaxHealth, Health + HealthRegenPerSecond * dt * m.VitalityRegen);
            else if (Food <= 0f || Water <= 0f || sick)
                Health = MathF.Max(0f, Health - (sick ? 2f : 1.5f) * dt);   // starve / dehydrate / infection sickness
            return Health <= 0f;
        }
    }
}
