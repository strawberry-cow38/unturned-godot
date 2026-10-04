using UnityEngine;   // Mathf shim (SDG.Compat) -- engine-free for MP_PLAN §3.2 (same math)

namespace SDG.Unturned
{
    /// ⭐⭐ OUR SKILL SET, NOT RETAIL'S. Master 2026-10-04: "kill all previous skill stuff. this is our own."
    ///
    /// Retail's 22 skills across 3 specialities are GONE. They were ported faithfully and they worked, but the
    /// design was the problem, not the implementation -- master: "i dont like all the weird things the vanilla has
    /// that weirdly buff you wayyy too much just by spending skill points. (i can sprint for 10x as long after
    /// killing a few zombies)". That is not an exaggeration: vanilla CARDIO doubled stamina regen while EXERCISE
    /// halved the drain, and the two compound.
    ///
    /// ⭐ SO THE RULE FOR THIS SET IS: **SKILLS BUY ACCESS AND EFFICIENCY, NOT A BIGGER BODY.** What you can make,
    /// repair and build; how much you get back from what you gather; how little you waste. Where a skill does move
    /// a player stat at all it moves it a little and it is capped. Nothing here should ever multiply a survival
    /// number by 4x, and if a future effect wants to, that is the signal it belongs in a different system.
    ///
    /// FLAT, no specialities. Retail's 3x(7/7/8) grouping existed to shape its own skill tree; ours is a list of
    /// trades and the UI can group them for display without the data model carrying it.
    public enum ESkill : byte
    {
        // make & maintain
        Metalworking, Carpentry, Gunsmithing, Electrical, Mechanics, Science, Cooking,
        // gather
        Plants, Animals, Fishing, Mining,
        // body
        Medical, Shooting, Melee,
    }

    /// One skill's level + its XP upgrade cost. ⭐ KEPT FROM THE RETAIL PORT DELIBERATELY: master's objection was to
    /// what the skills DID, not to the XP economy, and this cost curve (linear, cost = base + level*increase) is
    /// sound and already tested. Replacing a working thing nobody complained about is how a rework overruns.
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

        /// XP to raise this skill from its current level to the next.
        public uint Cost => (uint)Mathf.Max(0, Mathf.RoundToInt((baseCost + level * perLevelCostIncrease) * costMultiplier));
        /// 0..1 fraction of max level.
        public float Mastery => max == 0 ? 0f : Mathf.Clamp((float)level / max, 0f, 1f);
    }

    public class PlayerSkills
    {
        public const int COUNT = 14;
        public static readonly int Count = COUNT;

        readonly Skill[] _skills = new Skill[COUNT];
        public Skill[] skills => _skills;
        uint _experience;
        public uint experience => _experience;

        /// ⚠ UNIFORM max 5 and cost 10/difficulty 1.0 for every skill -- 10+20+30+40+50 = 150 XP to master one.
        /// Deliberately flat for now: the per-skill effect table goes to master before any of these numbers get
        /// tuned, and a set of invented-looking curves would only make that conversation about the curves.
        public PlayerSkills()
        {
            for (int i = 0; i < COUNT; i++) _skills[i] = new Skill(0, 5, 10, 1f);
        }

        public Skill GetSkill(ESkill s) => _skills[(int)s];
        public Skill GetSkill(int index) => _skills[index];
        public byte Level(ESkill s) => _skills[(int)s].level;
        public float Mastery(ESkill s) => _skills[(int)s].Mastery;

        public void AwardExperience(uint amount) => _experience += amount;
        public void NetSetExperience(uint total) => _experience = total;
        public bool TrySpend(uint amount)
        {
            if (_experience < amount) return false;
            _experience -= amount; return true;
        }

        /// Spend XP to raise a skill one level. Returns true if it leveled up.
        public bool TryUpgrade(ESkill s)
        {
            var sk = _skills[(int)s];
            uint c = sk.Cost;
            if (sk.level >= sk.max || _experience < c) return false;
            _experience -= c;
            sk.level++;
            return true;
        }
        public bool TryUpgrade(int index) => index >= 0 && index < COUNT && TryUpgrade((ESkill)index);

        /// Find a skill by name ("carpentry", "mining"...) -- for the dev console.
        public bool TryFind(string name, out Skill skill, out string label)
        {
            if (System.Enum.TryParse<ESkill>(name, true, out var s) && System.Enum.IsDefined(typeof(ESkill), s))
            { skill = _skills[(int)s]; label = s.ToString(); return true; }
            skill = null; label = null; return false;
        }

        // ------------------------------------------------------------------------------------------------
        // EFFECTS
        //
        // ⚠⚠ A HELPER IS ONLY DEFINED HERE ONCE SOMETHING CALLS IT. That rule is the whole point of this
        // section, and it comes from what the retail port turned into: TEN of its twenty-two skills levelled
        // up, charged XP and changed no number anywhere, because the helpers existed and the wires never got
        // written. The suite stayed green the entire time -- every skill test asserted that the helper returned
        // the right number, and not one asserted that anything CALLED it.
        //
        // ⭐ So: no speculative helpers, and `WiredSkills` below is published at boot so a dead skill cannot
        // hide again. If a skill is not in that list, it does nothing, and the game says so out loud.
        // ------------------------------------------------------------------------------------------------

        /// MEDICAL: medical items restore more. ⚠ Capped at +50% at max, and it scales the AMOUNT only -- stopping
        /// a bleed or setting a bone is a yes/no, not something a skill makes you better at.
        public float MedicalMultiplier() => 1f + Mastery(ESkill.Medical) * 0.5f;

        /// PLANTS: chance of a second yield when harvesting a crop (0 -> 1.0 at max). Access/efficiency, not a stat.
        public float PlantsSecondYieldChance() => Mastery(ESkill.Plants);

        /// SHOOTING: recoil/spread. ⚠ DELIBERATELY SMALL -- 15% at max, where retail SHARPSHOOTER gave 40%.
        /// This is the knob master's complaint was aimed at, so it stays something you notice and not something
        /// that turns a different gun into your gun.
        public float ShootingRecoilMultiplier() => 1f - Mastery(ESkill.Shooting) * 0.15f;

        /// MELEE: melee damage. ⚠ Same restraint -- 20% at max against retail OVERKILL's 50%.
        public float MeleeDamageMultiplier() => 1f + Mastery(ESkill.Melee) * 0.2f;

        /// ⭐ WHICH SKILLS ACTUALLY DO SOMETHING. Published at boot (see the [skills] line) so "this skill is
        /// wired to nothing" is a fact anyone can read instead of something only a call-site census finds.
        /// ⚠ Keep this honest: adding a name here without a call site is worse than leaving it out.
        public static readonly ESkill[] WiredSkills =
        {
            ESkill.Medical, ESkill.Plants, ESkill.Fishing, ESkill.Cooking,
            ESkill.Metalworking, ESkill.Carpentry, ESkill.Gunsmithing, ESkill.Mechanics,
            ESkill.Shooting, ESkill.Melee,
        };

        /// ⭐⭐ One line naming which skills DO something and which are decorative, printed at boot. The retail set
        /// hid ten dead skills for weeks because nothing ever said so out loud and the suite only checked the
        /// arithmetic. A running game that states its own wiring cannot do that again -- same trick as the
        /// [water] line, and for the same reason: "is this actually on?" should be answerable by the person
        /// asking, not by me running a call-site census.
        public static string CensusLine()
        {
            var wired = string.Join(" ", WiredSkills);
            var dead = new System.Collections.Generic.List<string>();
            foreach (var s in UnwiredSkills()) dead.Add(s.ToString());
            return $"[skills] {COUNT} skills, flat; {WiredSkills.Length} wired: {wired}"
                 + (dead.Count > 0 ? $"   \u26a0 NO EFFECT YET ({dead.Count}): {string.Join(" ", dead)}" : "");
        }

        /// The ones with no effect yet -- Animals, Science, Mining, Electrical. Derived, never hand-listed, so it
        /// cannot drift from the line above.
        public static System.Collections.Generic.IEnumerable<ESkill> UnwiredSkills()
        {
            for (int i = 0; i < COUNT; i++)
            {
                var s = (ESkill)i;
                if (System.Array.IndexOf(WiredSkills, s) < 0) yield return s;
            }
        }
    }
}
