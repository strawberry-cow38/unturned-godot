# Skills — the planned set (NAMES ONLY, not implemented)

Master, 2026-10-04/05: *"kill all previous skill stuff. this is our own. add Cooking, Mechanics,
Electrical"* … then *"i didnt actually **want** implementation yet, just wanted to name the planned
skills"* … then *"revert skills work (keeping the skill names)"*.

So this file is the decision, and the code is deliberately **not** written. The implementation that
briefly existed (flat `ESkill`, wire v55, save format change) was reverted in `83cfcff2`; the retail
22-skill port is back in place and untouched until someone asks for this.

## The 14

| | |
|---|---|
| **Make & maintain** | Metalworking · Carpentry · Gunsmithing · Electrical · Mechanics · Science · Cooking |
| **Gather** | Plants · Animals · Fishing · Mining |
| **Body** | Medical · Shooting · Melee |

## ⭐⭐ The design rule, which is the actually important part

Master on vanilla: *"i dont like all the weird things the vanilla has that weirdly buff you wayyy too
much just by spending skill points. (i can sprint for 10x as long after killing a few zombies)"*

That is not an exaggeration, it is arithmetic: retail CARDIO doubles stamina regen while EXERCISE
halves the drain, and because one scales the fill and the other the empty they **compound** — roughly
4× the sprint out of two cheap skills, with SURVIVAL slowing hunger and VITALITY doubling health
regen stacked on top.

**So skills buy ACCESS and EFFICIENCY, not a bigger body.** What you can make, repair and build; how
much you get back from what you gather; how little you waste. Where a skill touches a player stat at
all it should be small and capped — single-digit-to-20% territory, not a multiplier.

## ⚠ Two things worth knowing before anyone implements this

1. **There are already TWO skill systems in the tree.** `PlayerSkills` (levels + XP, retail's 22) and
   `SkillTree.cs` + `SkillsUI` — a separate node graph from 2026-09-09 (6 pillars, 15 nodes, all
   marked "placeholder") whose grants are **Recipe / Ability / Buff / Pillar**, i.e. unlocks. That
   shape fits "skills buy access" far better than a 0–5 level does, and these 14 look like its
   **pillars**. ⭐ OPEN QUESTION, asked and not yet answered: are the 14 flat levelled skills, or the
   pillars of that node tree with nodes hanging off each?
2. **A call-site census of the retail set found 10 of its 22 skills wired to nothing** — Diving,
   Parkour, Toughness, Warmblooded, Healing, Crafting, Outdoors, Cooking, Mechanic, Engineer levelled
   up, charged XP and changed no number anywhere, with a green suite throughout, because every skill
   test asserted that a helper returned the right number and none asserted anything *called* it.
   Whatever gets built here: **no helper gets defined until something calls it**, and publish which
   skills are live (a boot line) so a dead one cannot hide again.
