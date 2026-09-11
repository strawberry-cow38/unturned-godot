using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Quests are the half of the dialogue data that was never evaluated: 81 of the 157 shipped conditions ask
    // about one. A quest system that is quietly wrong does not crash -- it shows you a conversation that looks
    // whole and is missing most of its branches -- so each of these aims at a specific way of being wrong.
    [TestFixture]
    public class QuestRulesTests
    {
        sealed class World : INpcWorld
        {
            public readonly Dictionary<ushort, short> Flags = new();
            public readonly Dictionary<ushort, ENpcQuestStatus> Quests = new();
            public readonly Dictionary<ushort, int> Items = new();
            public readonly Dictionary<(ushort, int), int> Counters = new();
            public readonly List<(ushort id, short n)> Given = new();
            public readonly List<(ushort id, int amount)> Taken = new();
            public string ActiveHoliday { get; set; } = "";
            public int Reputation { get; set; }
            public uint Experience { get; set; }
            public short GetFlag(ushort id) => Flags.TryGetValue(id, out var v) ? v : (short)0;
            public void SetFlag(ushort id, short v) => Flags[id] = v;
            public ENpcQuestStatus GetQuestStatus(ushort id) => Quests.TryGetValue(id, out var s) ? s : ENpcQuestStatus.None;
            public void SetQuestStatus(ushort id, ENpcQuestStatus s) => Quests[id] = s;
            public void GiveItem(ushort id, short n) => Given.Add((id, n));
            public int QuestProgress(ushort q, int i) => Counters.TryGetValue((q, i), out var n) ? n : 0;
            public void AddQuestProgress(ushort q, int i, int by) => Counters[(q, i)] = QuestProgress(q, i) + by;
            public int CountItem(ushort id) => Items.TryGetValue(id, out var n) ? n : 0;
            public void TakeItem(ushort id, int amount)
            {
                Taken.Add((id, amount));
                int left = CountItem(id) - amount;
                if (left > 0) Items[id] = left; else Items.Remove(id);
            }
            public void AddExperience(int n) => Experience = (uint)System.Math.Max(0, (int)Experience + n);
            public void AddReputation(int n) => Reputation += n;
        }

        // Barnacle_Cleanup, verbatim from the shipped asset: one Flag_Short >= 9 objective, and three rewards
        // of three different kinds -- one of which INCREMENTS. Real numbers so an extractor change that mangles
        // them fails here rather than as a quest nobody can finish.
        static NpcQuestDef Barnacles() => new NpcQuestDef
        {
            Id = 157, Key = "Barnacle_Cleanup", Name = "Nautical Nuisance",
            Conditions = new[]
            {
                new NpcCondition { Type = ENpcConditionType.Flag_Short, Id = 156, Value = 9,
                                   Logic = ENpcLogic.Greater_Than_Or_Equal_To, Text = "Cleanup {0}/{1} Barnacles" },
            },
            Rewards = new[]
            {
                new NpcReward { Type = ENpcRewardType.Experience, Value = 50 },
                new NpcReward { Type = ENpcRewardType.Reputation, Value = 5 },
                new NpcReward { Type = ENpcRewardType.Flag_Short, Id = 13, Value = 1, Modification = ENpcModification.Increment },
            },
        };

        [Test]
        public void TheWholeLifecycle_TakeProgressHandIn()
        {
            var w = new World();
            var q = Barnacles();
            Assert.That(w.GetQuestStatus(157), Is.EqualTo(ENpcQuestStatus.None));

            QuestRules.Give(q, w);
            Assert.That(QuestRules.StatusOf(q, w), Is.EqualTo(ENpcQuestStatus.Active));
            Assert.That(QuestRules.TurnIn(q, w), Is.False, "not ready: 0 of 9");
            Assert.That(w.Experience, Is.EqualTo(0u), "a refused turn-in pays NOTHING");

            w.SetFlag(156, 9);
            Assert.That(QuestRules.StatusOf(q, w), Is.EqualTo(ENpcQuestStatus.Ready));
            Assert.That(QuestRules.TurnIn(q, w), Is.True);

            Assert.That(w.Experience, Is.EqualTo(50u));
            Assert.That(w.Reputation, Is.EqualTo(5));
            Assert.That(w.GetFlag(13), Is.EqualTo((short)1), "Increment, from 0");
            Assert.That(w.GetQuestStatus(157), Is.EqualTo(ENpcQuestStatus.Completed));
            Assert.That(QuestRules.TurnIn(q, w), Is.False, "and it cannot be handed in twice");
            Assert.That(w.Experience, Is.EqualTo(50u), "so it cannot be farmed");
        }

        // ⭐ THE SENTENCE AND THE TEST MUST WANT THE SAME NUMBER. Describe filled {1} from Amount alone, which is
        // 0 on a flag objective, so "Cleanup {0}/{1} Barnacles" rendered "Cleanup 0/0 Barnacles" -- an objective
        // that reads as already satisfied while ObjectiveMet correctly said no. A caught render, not a crash.
        [Test]
        public void ObjectiveText_ShowsWhatTheTestActuallyWants()
        {
            var w = new World();
            var q = Barnacles();
            var o = QuestRules.Objectives(q, w);
            Assert.That(o, Has.Count.EqualTo(1));
            Assert.That(o[0].Text, Is.EqualTo("Cleanup 0/9 Barnacles"), "NOT 0/0");
            Assert.That(o[0].Done, Is.False);

            w.SetFlag(156, 9);
            Assert.That(QuestRules.Objectives(q, w)[0].Text, Is.EqualTo("Cleanup 9/9 Barnacles"));
            Assert.That(QuestRules.Objectives(q, w)[0].Done, Is.True);

            // Overshoot reads as complete, not as 14/9 -- clamped for display, unclamped for the test.
            w.SetFlag(156, 14);
            Assert.That(QuestRules.Objectives(q, w)[0].Text, Is.EqualTo("Cleanup 9/9 Barnacles"));
            Assert.That(QuestRules.Objectives(q, w)[0].Done, Is.True);
        }

        // A flag objective carries its own comparison. Treating one as a count ("have >= wants") happens to
        // agree for >=, and is wrong for every other logic -- so this uses Less_Than, where they disagree.
        [Test]
        public void AFlagObjectiveKeepsItsOwnComparison()
        {
            var w = new World();
            var q = new NpcQuestDef
            {
                Id = 900,
                Conditions = new[] { new NpcCondition { Type = ENpcConditionType.Flag_Short, Id = 5, Value = 3, Logic = ENpcLogic.Less_Than } },
            };
            w.SetFlag(5, 1);
            Assert.That(QuestRules.ObjectiveMet(q, 0, w), Is.True, "1 < 3");
            w.SetFlag(5, 9);
            Assert.That(QuestRules.ObjectiveMet(q, 0, w), Is.False, "9 is not < 3 -- a count test would have PASSED this");
        }

        // Reset is the difference between "bring me eleven" and "show me eleven". Getting it backwards either
        // steals the player's items or lets one stack complete every fetch quest in the game.
        [Test]
        public void OnlyAResetObjectiveConsumesWhatYouHandOver()
        {
            var w = new World();
            w.Items[70] = 11;
            w.Items[71] = 4;
            var q = new NpcQuestDef
            {
                Id = 901,
                Conditions = new[]
                {
                    new NpcCondition { Type = ENpcConditionType.Item, Id = 70, Amount = 11, Reset = true },
                    new NpcCondition { Type = ENpcConditionType.Item, Id = 71, Amount = 4, Reset = false },
                },
            };
            QuestRules.Give(q, w);
            Assert.That(QuestRules.CanTurnIn(q, w), Is.True);
            Assert.That(QuestRules.TurnIn(q, w), Is.True);
            Assert.That(w.CountItem(70), Is.EqualTo(0), "Reset: handed over");
            Assert.That(w.CountItem(71), Is.EqualTo(4), "no Reset: shown, and kept");
            Assert.That(w.Taken, Has.Count.EqualTo(1));
        }

        // Kill counters are the only objectives with nowhere else to live, and they are keyed by (quest, index)
        // -- two quests that both want six zombies must not share one counter.
        [Test]
        public void KillCountersAreScopedToTheirOwnQuestAndIndex()
        {
            var w = new World();
            NpcQuestDef Kills(int id) => new NpcQuestDef
            {
                Id = id,
                Conditions = new[] { new NpcCondition { Type = ENpcConditionType.Kills_Zombie, Amount = 6, Text = "Kill {0}/{1}" } },
            };
            var a = Kills(910);
            var b = Kills(911);
            for (int i = 0; i < 6; i++) w.AddQuestProgress(910, 0, 1);
            Assert.That(QuestRules.CanTurnIn(a, w), Is.True);
            Assert.That(QuestRules.CanTurnIn(b, w), Is.False, "the other quest's kills are not yours");
            Assert.That(QuestRules.Objectives(b, w)[0].Text, Is.EqualTo("Kill 0/6"));
        }

        // A quest handed out as a REWARD is how a chain links. One of the 88 shipped rewards does it, so if this
        // path is dead exactly one quest in the game silently never appears.
        [Test]
        public void AQuestRewardStartsTheNextQuest()
        {
            var w = new World();
            var q = new NpcQuestDef
            {
                Id = 920,
                Rewards = new[] { new NpcReward { Type = ENpcRewardType.Quest, Id = 921 } },
            };
            QuestRules.Give(q, w);
            Assert.That(QuestRules.TurnIn(q, w), Is.True, "no objectives = ready at once");
            Assert.That(w.GetQuestStatus(921), Is.EqualTo(ENpcQuestStatus.Active));
        }

        // Ready is DERIVED, never stored. Nothing watches your inventory, so a quest whose last item arrived
        // while you were walking must become ready by being asked, not by having been told.
        [Test]
        public void ReadyIsDerivedFromTheObjectives_NotStored()
        {
            var w = new World();
            var q = new NpcQuestDef
            {
                Id = 930,
                Conditions = new[] { new NpcCondition { Type = ENpcConditionType.Item, Id = 70, Amount = 2 } },
            };
            QuestRules.Give(q, w);
            Assert.That(w.GetQuestStatus(930), Is.EqualTo(ENpcQuestStatus.Active), "what is STORED stays Active");
            w.Items[70] = 2;
            Assert.That(QuestRules.StatusOf(q, w), Is.EqualTo(ENpcQuestStatus.Ready), "what is ASKED is Ready");
            Assert.That(w.GetQuestStatus(930), Is.EqualTo(ENpcQuestStatus.Active), "and nothing wrote it down");
        }
    }
}
