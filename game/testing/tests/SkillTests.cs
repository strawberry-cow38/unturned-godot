using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --skilltest: the PlayerSkills grid sizes, the source XP cost formula, upgrade/mastery, and every
    // skill-effect multiplier at its extremes (data-model self-test).
    public class SkillsGridXpMastery : GameTest
    {
        public override string Name => "skills.grid_xp_mastery";
        public override IEnumerable<Step> Run()
        {
            var sk = new PlayerSkills();
            T.Check("OFFENSE has 7", sk.skills[(int)EPlayerSpeciality.OFFENSE].Length == 7);
            T.Check("DEFENSE has 7", sk.skills[(int)EPlayerSpeciality.DEFENSE].Length == 7);
            T.Check("SUPPORT has 8", sk.skills[(int)EPlayerSpeciality.SUPPORT].Length == 8);

            // cost: AGRICULTURE (max7,base10,diff1.0) L0=10,L1=20 ; CRAFTING (max3,base20,diff1.5) L0=20,L1=50
            var ag = sk.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.AGRICULTURE);
            T.Check("AGRICULTURE max 7", ag.max == 7);
            T.Check("AGRICULTURE L0 cost 10", ag.Cost == 10);
            var cr = sk.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.CRAFTING);
            T.Check("CRAFTING max 3", cr.max == 3);
            T.Check("CRAFTING L0 cost 20", cr.Cost == 20);

            // award 30 XP -> upgrade AGRICULTURE twice (10+20) -> level 2, 0 XP left, 3rd blocked
            sk.AwardExperience(30);
            bool u1 = sk.TryUpgrade(EPlayerSupport.AGRICULTURE);
            bool u2 = sk.TryUpgrade(EPlayerSupport.AGRICULTURE);
            bool u3 = sk.TryUpgrade(EPlayerSupport.AGRICULTURE);
            T.Check("upgrade x2 ok + 3rd blocked", u1 && u2 && !u3);
            T.Check("AGRICULTURE level 2", sk.Level(EPlayerSupport.AGRICULTURE) == 2);
            T.Check("XP spent to 0", sk.experience == 0);
            T.Check("mastery 2/7", Mathf.Abs(ag.Mastery - 2f / 7f) < 0.001f);

            // SHARPSHOOTER recoil/spread multiplier = 1 - mastery*0.4 (lvl0 = 1.0, max7 = 0.6)
            var ss = sk.GetSkill((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.SHARPSHOOTER);
            ss.level = 0; T.Check("sharpshooter mult 1.0 at lvl0", Mathf.Abs(sk.SharpshooterRecoilMultiplier() - 1.0f) < 0.001f);
            ss.level = 7; T.Check("sharpshooter mult 0.6 at max", Mathf.Abs(sk.SharpshooterRecoilMultiplier() - 0.6f) < 0.001f);

            // STRENGTH fall-damage multiplier = 1 - mastery*0.75 (max STRENGTH lvl 5 -> 0.25)
            var st = sk.GetSkill((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.STRENGTH);
            st.level = 0; T.Check("strength fall mult 1.0 at lvl0", Mathf.Abs(sk.StrengthFallMultiplier() - 1.0f) < 0.001f);
            st.level = 5; T.Check("strength fall mult 0.25 at max", Mathf.Abs(sk.StrengthFallMultiplier() - 0.25f) < 0.001f);

            // survival-sim multipliers at max level
            sk.GetSkill((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.VITALITY).level = 5;
            T.Check("vitality regen 2.0x at max", Mathf.Abs(sk.VitalityRegenMultiplier() - 2.0f) < 0.001f);
            sk.GetSkill((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.SURVIVAL).level = 5;
            T.Check("survival drain 0.8x at max", Mathf.Abs(sk.SurvivalDrainMultiplier() - 0.8f) < 0.001f);
            sk.GetSkill((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.CARDIO).level = 5;
            T.Check("cardio regen 2.0x at max", Mathf.Abs(sk.CardioStaminaRegenMultiplier() - 2.0f) < 0.001f);
            sk.GetSkill((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.EXERCISE).level = 5;
            T.Check("exercise drain 0.5x at max", Mathf.Abs(sk.ExerciseStaminaDrainMultiplier() - 0.5f) < 0.001f);
            sk.GetSkill((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.OVERKILL).level = 7;
            T.Check("overkill melee 1.5x at max", Mathf.Abs(sk.OverkillMeleeMultiplier() - 1.5f) < 0.001f);
            sk.GetSkill((int)EPlayerSpeciality.OFFENSE, (int)EPlayerOffense.DEXTERITY).level = 5;
            T.Check("dexterity reload 1.5x at max", Mathf.Abs(sk.DexterityReloadSpeed() - 1.5f) < 0.001f);
            sk.GetSkill((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.IMMUNITY).level = 5;
            T.Check("immunity infection 0.5x at max", Mathf.Abs(sk.ImmunityInfectionMultiplier() - 0.5f) < 0.001f);
            sk.GetSkill((int)EPlayerSpeciality.DEFENSE, (int)EPlayerDefense.SNEAKYBEAKY).level = 7;
            T.Check("sneakybeaky noise 0.25x at max", Mathf.Abs(sk.SneakyBeakyNoiseMultiplier() - 0.25f) < 0.001f);
            yield break;
        }
    }
}
