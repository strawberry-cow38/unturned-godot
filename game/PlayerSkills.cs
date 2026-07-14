using Godot;

namespace SDG.Unturned
{
    // Source SDG.Unturned skill enums. 3 specialities, each with its skills (AGRICULTURE = farming, CRAFTING gates blueprints).
    public enum EPlayerSpeciality : byte { OFFENSE, DEFENSE, SUPPORT }
    public enum EPlayerOffense : byte { OVERKILL, SHARPSHOOTER, DEXTERITY, CARDIO, EXERCISE, DIVING, PARKOUR }
    public enum EPlayerDefense : byte { SNEAKYBEAKY, VITALITY, IMMUNITY, TOUGHNESS, STRENGTH, WARMBLOODED, SURVIVAL }
    public enum EPlayerSupport : byte { HEALING, CRAFTING, OUTDOORS, COOKING, FISHING, AGRICULTURE, MECHANIC, ENGINEER }

    // Source SDG.Unturned.Skill: one skill's level + its XP upgrade cost. Nelson-2025-09-11 LINEAR cost:
    //   cost = round((baseCost + level * perLevelCostIncrease) * costMultiplier),  perLevelCostIncrease = round(baseCost * difficulty)
    public class Skill
    {
        public byte level, max;
        public int baseCost, perLevelCostIncrease;
        public float costMultiplier = 1f;

        public Skill(byte newLevel, byte newMax, uint newCost, float newDifficulty)
        {
            level = newLevel; max = newMax;
            baseCost = (int)newCost;
            perLevelCostIncrease = Mathf.RoundToInt(baseCost * newDifficulty);
        }

        // XP to raise this skill from its current level to the next.
        public uint Cost => (uint)Mathf.Max(0, Mathf.RoundToInt((baseCost + level * perLevelCostIncrease) * costMultiplier));
        // 0..1 fraction of max level (source Skill.mastery / NormalizeLevel).
        public float Mastery => max == 0 ? 0f : Mathf.Clamp((float)level / max, 0f, 1f);
    }

    // Source SDG.Unturned.PlayerSkills: OFFENSE[7]/DEFENSE[7]/SUPPORT[8] skill grid + a shared XP pool (experience)
    // spent to level skills. Maxes/costs/difficulties are the source values (PlayerSkills ctor). Increment 1 = the
    // data model + XP award/upgrade; effects (agriculture 2nd-yield, craft/skill gating) wire in as follow-ups.
    public class PlayerSkills
    {
        public const int SPECIALITIES = 3;
        readonly Skill[][] _skills;
        public Skill[][] skills => _skills;
        uint _experience;
        public uint experience => _experience;

        public PlayerSkills()
        {
            _skills = new Skill[SPECIALITIES][];
            _skills[(int)EPlayerSpeciality.OFFENSE] = new[] {
                new Skill(0, 7, 10, 1f),   // OVERKILL
                new Skill(0, 7, 10, 1f),   // SHARPSHOOTER
                new Skill(0, 5, 10, 0.5f), // DEXTERITY
                new Skill(0, 5, 10, 0.5f), // CARDIO
                new Skill(0, 5, 10, 0.5f), // EXERCISE
                new Skill(0, 5, 10, 0.5f), // DIVING
                new Skill(0, 5, 20, 0.5f), // PARKOUR
            };
            _skills[(int)EPlayerSpeciality.DEFENSE] = new[] {
                new Skill(0, 7, 10, 1f),   // SNEAKYBEAKY
                new Skill(0, 5, 10, 0.5f), // VITALITY
                new Skill(0, 5, 10, 0.5f), // IMMUNITY
                new Skill(0, 5, 10, 0.5f), // TOUGHNESS
                new Skill(0, 5, 10, 0.5f), // STRENGTH
                new Skill(0, 5, 10, 0.5f), // WARMBLOODED
                new Skill(0, 5, 10, 0.5f), // SURVIVAL
            };
            _skills[(int)EPlayerSpeciality.SUPPORT] = new[] {
                new Skill(0, 7, 10, 1f),    // HEALING
                new Skill(0, 3, 20, 1.5f),  // CRAFTING
                new Skill(0, 5, 10, 0.5f),  // OUTDOORS
                new Skill(0, 3, 20, 1.5f),  // COOKING
                new Skill(0, 5, 10, 0.5f),  // FISHING
                new Skill(0, 7, 10, 1f),    // AGRICULTURE
                new Skill(0, 5, 10, 0.5f),  // MECHANIC
                new Skill(0, 3, 20, 1.5f),  // ENGINEER
            };
        }

        public Skill GetSkill(int speciality, int index) => _skills[speciality][index];
        public byte Level(EPlayerOffense s) => _skills[(int)EPlayerSpeciality.OFFENSE][(int)s].level;
        public byte Level(EPlayerDefense s) => _skills[(int)EPlayerSpeciality.DEFENSE][(int)s].level;
        public byte Level(EPlayerSupport s) => _skills[(int)EPlayerSpeciality.SUPPORT][(int)s].level;

        // Earn XP (source ServerModifyExperience/askAward). Kills/harvests/crafts feed this in the follow-up.
        public void AwardExperience(uint xp) { _experience += xp; }

        // Source GetSharpshooterRecoilMultiplier: recoil + spread scale by 1 - mastery*0.4 (up to 40% less at max SHARPSHOOTER lvl 7).
        public float SharpshooterRecoilMultiplier() => 1f - GetSkill((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.SHARPSHOOTER).Mastery * 0.4f;

        // Source PlayerLife:2428: STRENGTH cuts fall damage by up to 75% at max level.
        public float StrengthFallMultiplier() => 1f - GetSkill((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.STRENGTH).Mastery * 0.75f;

        // Source skills.mastery(spec, index) = level/max fraction.
        public float Mastery(int speciality, int index) => _skills[speciality][index].Mastery;

        // Source PlayerEquipment:2274: OVERKILL boosts melee damage by up to 50% at max level.
        public float OverkillMeleeMultiplier() => 1f + Mastery((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.OVERKILL) * 0.5f;

        // Source UseableGun:2979/3046: DEXTERITY speeds the reload animation (speed += mastery*0.5), up to 1.5x at max. Reload TIME = duration / this.
        public float DexterityReloadSpeed() => 1f + Mastery((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.DEXTERITY) * 0.5f;

        // Survival-sim skill multipliers. VITALITY/SURVIVAL are the source-exact interval->rate inversions (magnitudes
        // 0.5/0.25 from PlayerLife:2030/1953/1975); CARDIO/EXERCISE are source-INFORMED for the port's simplified
        // continuous stamina (the source is tick-based -- PlayerLife:1797/1806-1810). Applied to the port's stand-in rates.
        public float VitalityRegenMultiplier() => 1f / (1f - Mastery((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.VITALITY) * 0.5f);          // faster health regen (up to 2x)
        public float SurvivalDrainMultiplier() => 1f / (1f + Mastery((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.SURVIVAL) * 0.25f);          // slower food/water drain (down to 0.8x)
        public float CardioStaminaRegenMultiplier() => 1f + Mastery((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.CARDIO);                       // faster stamina regen (up to 2x)
        public float ExerciseStaminaDrainMultiplier() => 1f - Mastery((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.EXERCISE) * 0.5f;            // slower stamina drain (down to 0.5x)

        // Spend XP to raise a skill one level (source askUpgrade). Returns true if it leveled up.
        public bool TryUpgrade(int speciality, int index)
        {
            var sk = _skills[speciality][index];
            uint c = sk.Cost;
            if (sk.level >= sk.max || _experience < c) return false;
            _experience -= c;
            sk.level++;
            return true;
        }
        public bool TryUpgrade(EPlayerSupport s) => TryUpgrade((int)EPlayerSpeciality.SUPPORT, (int)s);

        // Find a skill by its enum name ("crafting", "agriculture", "sharpshooter"...) across all specialities. For the dev console.
        public bool TryFind(string name, out Skill skill, out string label)
        {
            if (System.Enum.TryParse<EPlayerOffense>(name, true, out var o) && System.Enum.IsDefined(typeof(EPlayerOffense), o))
            { skill = _skills[(int)EPlayerSpeciality.OFFENSE][(int)o]; label = o.ToString(); return true; }
            if (System.Enum.TryParse<EPlayerDefense>(name, true, out var d) && System.Enum.IsDefined(typeof(EPlayerDefense), d))
            { skill = _skills[(int)EPlayerSpeciality.DEFENSE][(int)d]; label = d.ToString(); return true; }
            if (System.Enum.TryParse<EPlayerSupport>(name, true, out var s) && System.Enum.IsDefined(typeof(EPlayerSupport), s))
            { skill = _skills[(int)EPlayerSpeciality.SUPPORT][(int)s]; label = s.ToString(); return true; }
            skill = null; label = null; return false;
        }
    }
}
