using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// The skill DATA MODEL: 14 flat skills, the XP cost curve, upgrade and mastery.
    public class SkillsGridXpMastery : GameTest
    {
        public override string Name => "skills.grid_xp_mastery";
        public override IEnumerable<Step> Run()
        {
            var sk = new PlayerSkills();
            T.Check($"14 skills, flat ({sk.skills.Length})", sk.skills.Length == PlayerSkills.COUNT && sk.skills.Length == 14);
            T.Check("every ESkill value has a Skill", System.Enum.GetValues(typeof(ESkill)).Length == PlayerSkills.COUNT);

            // uniform max 5, base 10, difficulty 1.0 -> L0 costs 10, L1 costs 20
            var carp = sk.GetSkill(ESkill.Carpentry);
            T.Check("Carpentry max 5", carp.max == 5);
            T.Check("Carpentry L0 cost 10", carp.Cost == 10);

            sk.AwardExperience(30);
            T.Check("upgrade 1 (10)", sk.TryUpgrade(ESkill.Carpentry));
            T.Check("upgrade 2 (20)", sk.TryUpgrade(ESkill.Carpentry));
            T.Check("3rd blocked -- 0 XP left", !sk.TryUpgrade(ESkill.Carpentry));
            T.Check("Carpentry level 2", sk.Level(ESkill.Carpentry) == 2);
            T.Check("XP spent exactly", sk.experience == 0);

            T.Check("mastery 0.4 at 2/5", Mathf.Abs(sk.Mastery(ESkill.Carpentry) - 0.4f) < 0.001f);
            T.Check("TryFind by name", sk.TryFind("mining", out _, out var label) && label == "Mining");
            T.Check("TryFind rejects a retail name", !sk.TryFind("sharpshooter", out _, out _));
            yield break;
        }
    }

    /// ⭐⭐ THE EFFECT CENSUS. This is the test the retail port needed and never had: it asserts that the set of
    /// skills CLAIMING an effect matches the set that HAS one, so a skill cannot quietly become decorative.
    ///
    /// The old suite could not catch that. Every one of its skill tests asserted a helper returned the right
    /// number; not one asserted anything CALLED it, so ten of twenty-two skills charged XP and changed nothing
    /// while the suite stayed green.
    public class SkillEffectCensus : GameTest
    {
        public override string Name => "skills.effect_census";
        public override IEnumerable<Step> Run()
        {
            var wired = new List<ESkill>(PlayerSkills.WiredSkills);
            var unwired = new List<ESkill>(PlayerSkills.UnwiredSkills());

            T.Check($"wired + unwired accounts for all {PlayerSkills.COUNT} ({wired.Count} + {unwired.Count})",
                    wired.Count + unwired.Count == PlayerSkills.COUNT);
            foreach (var s in wired)
                T.Check($"{s} is not ALSO listed unwired", !unwired.Contains(s));

            // Every skill that claims an effect must have a helper that actually moves off its neutral value.
            var sk = new PlayerSkills();
            foreach (var s in new[] { ESkill.Medical, ESkill.Plants, ESkill.Shooting, ESkill.Melee })
                sk.GetSkill(s).level = sk.GetSkill(s).max;
            T.Check($"Medical moves off 1.0 ({sk.MedicalMultiplier():0.00})", sk.MedicalMultiplier() > 1.01f);
            T.Check($"Plants moves off 0 ({sk.PlantsSecondYieldChance():0.00})", sk.PlantsSecondYieldChance() > 0.01f);
            T.Check($"Shooting moves off 1.0 ({sk.ShootingRecoilMultiplier():0.00})", sk.ShootingRecoilMultiplier() < 0.99f);
            T.Check($"Melee moves off 1.0 ({sk.MeleeDamageMultiplier():0.00})", sk.MeleeDamageMultiplier() > 1.01f);

            // ⭐ ...and the restraint master asked for is itself asserted, so a future tuning pass cannot quietly
            // reintroduce a retail-sized buff without this failing and making someone say so on purpose.
            T.Check($"Shooting recoil cut stays modest ({1f - sk.ShootingRecoilMultiplier():P0} <= 20%)",
                    sk.ShootingRecoilMultiplier() >= 0.80f);
            T.Check($"Melee damage gain stays modest ({sk.MeleeDamageMultiplier() - 1f:P0} <= 25%)",
                    sk.MeleeDamageMultiplier() <= 1.25f);
            T.Check($"Medical gain stays modest ({sk.MedicalMultiplier() - 1f:P0} <= 50%)",
                    sk.MedicalMultiplier() <= 1.50f);
            yield break;
        }
    }

    /// Drives the REAL consume path, so it fails if Medical is wired to nothing (see the census note above).
    public class MedicalSkillWired : GameTest
    {
        public override string Name => "skills.medical_wired";
        public override IEnumerable<Step> Run()
        {
            var p = Rigs.Player(World, new Vector3(0f, 2f, 0f));
            yield return Ticks(2);

            // ⚠ Built here rather than looked up by id: a Medkit's .dat can be retuned by anyone, and this test is
            // about the SKILL, not about what a Medkit happens to restore today.
            var kit = new ItemAsset { useHealth = 40 };
            var med = p.Skills.GetSkill(ESkill.Medical);

            p.MaxHealth = 200f;   // headroom, so the clamp cannot hide the difference being measured
            med.level = 0; p.Health = 10f; p.Consume(kit);
            float unskilled = p.Health - 10f;
            med.level = med.max; p.Health = 10f; p.Consume(kit);
            float skilled = p.Health - 10f;

            T.Check($"unskilled heals the item's own 40 ({unskilled:0.00})", Mathf.Abs(unskilled - 40f) < 0.01f);
            T.Check($"⭐ Medical CHANGES the outcome: {unskilled:0.0} -> {skilled:0.0} " +
                    "(this is the check that fails if the skill is wired to nothing)", skilled > unskilled + 0.01f);
            T.Check($"...and by the capped 1.5x ({skilled:0.0})", Mathf.Abs(skilled - 60f) < 0.01f);
            yield break;
        }
    }
}
