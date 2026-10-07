using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // The dialogue evaluator and the barter maths. Both are places where being quietly wrong produces a
    // conversation that still "works" -- a branch that never shows, a reward granted to the wrong response, a
    // trade that hands over more than it should -- so each of these is aimed at a specific way of being wrong.
    [TestFixture]
    public class DialogueRulesTests
    {
        sealed class World : INpcWorld
        {
            public readonly Dictionary<ushort, short> Flags = new();
            public readonly Dictionary<ushort, ENpcQuestStatus> Quests = new();
            public readonly List<(ushort id, short n)> Given = new();
            public string ActiveHoliday { get; set; } = "";
            public int Reputation { get; set; }
            public uint Experience { get; set; }
            public short GetFlag(ushort id) => Flags.TryGetValue(id, out var v) ? v : (short)0;
            public void SetFlag(ushort id, short v) => Flags[id] = v;
            public ENpcQuestStatus GetQuestStatus(ushort id) => Quests.TryGetValue(id, out var s) ? s : ENpcQuestStatus.None;
            public void GiveItem(ushort id, short n) => Given.Add((id, n));

            // ---- quests. A FIXTURE, so every one of these is a real little store rather than a stub that
            // returns 0 -- a world that cannot remember a quest status makes every quest test vacuous.
            public readonly Dictionary<ushort, int> Items = new();
            public readonly Dictionary<(ushort, int), int> Counters = new();
            public readonly List<(ushort id, int amount)> Taken = new();
            public int Xp, Rep;
            public void SetQuestStatus(ushort id, ENpcQuestStatus s) => Quests[id] = s;
            public int QuestProgress(ushort q, int i) => Counters.TryGetValue((q, i), out var n) ? n : 0;
            public void AddQuestProgress(ushort q, int i, int by) => Counters[(q, i)] = QuestProgress(q, i) + by;
            public int CountItem(ushort id) => Items.TryGetValue(id, out var n) ? n : 0;
            public void TakeItem(ushort id, int amount)
            {
                Taken.Add((id, amount));
                int left = CountItem(id) - amount;
                if (left > 0) Items[id] = left; else Items.Remove(id);
            }
            public void AddExperience(int n) { Xp += n; Experience = (uint)System.Math.Max(0, (int)Experience + n); }
            public void AddReputation(int n) { Rep += n; Reputation += n; }
        }

        static NpcCondition Flag(ushort id, short v, ENpcLogic l = ENpcLogic.Equal)
            => new NpcCondition { Type = ENpcConditionType.Flag_Bool, Id = id, Value = v, Logic = l };

        [Test]
        public void AFlagConditionReadsTheFlag()
        {
            var w = new World();
            Assert.That(DialogueRules.Passes(Flag(61, 1), w), Is.False, "unset flag is 0, not 1");
            w.SetFlag(61, 1);
            Assert.That(DialogueRules.Passes(Flag(61, 1), w), Is.True);
            Assert.That(DialogueRules.Passes(Flag(61, 1, ENpcLogic.Not_Equal), w), Is.False);
        }

        [TestCase(ENpcLogic.Greater_Than, (short)4, true)]
        [TestCase(ENpcLogic.Greater_Than, (short)5, false)]
        [TestCase(ENpcLogic.Greater_Than_Or_Equal_To, (short)5, true)]
        [TestCase(ENpcLogic.Less_Than, (short)6, true)]
        [TestCase(ENpcLogic.Less_Than_Or_Equal_To, (short)5, true)]
        public void EveryComparisonRetailUsesWorks(ENpcLogic logic, short against, bool expected)
        {
            var w = new World();
            w.SetFlag(7, 5);
            Assert.That(DialogueRules.Passes(
                new NpcCondition { Type = ENpcConditionType.Flag_Short, Id = 7, Value = against, Logic = logic }, w),
                Is.EqualTo(expected));
        }

        // ⭐ 81 of the 101 conditions in the shipped data are QUEST conditions and the quest system does not
        // exist. If an unimplemented condition FAILED, most gated branches would silently vanish and the
        // dialogue would look finished while being half missing. Unknown passes; Quest reports None, which is
        // the truthful answer for a player who has started nothing.
        [Test]
        public void AnUnstartedQuestReadsAsNoneRatherThanBlockingEverything()
        {
            var w = new World();
            var c = new NpcCondition { Type = ENpcConditionType.Quest, Id = 230, Status = ENpcQuestStatus.None };
            Assert.That(DialogueRules.Passes(c, w), Is.True, "None == None");
            c.Logic = ENpcLogic.Not_Equal;
            Assert.That(DialogueRules.Passes(c, w), Is.False, "...and Not_Equal on an unstarted quest is false");
            w.Quests[230] = ENpcQuestStatus.Completed;
            Assert.That(DialogueRules.Passes(c, w), Is.True, "once it IS something else, Not_Equal holds");
        }

        [Test]
        public void AnUnimplementedConditionTypeDoesNotHideTheBranch()
        {
            Assert.That(DialogueRules.Passes(new NpcCondition { Type = ENpcConditionType.None }, new World()), Is.True);
        }

        // ⭐ THE ONE THAT MATTERS FOR REWARDS. AvailableResponses returns INDICES into the original array. If it
        // returned a filtered list instead, the caller would index by position and grant the wrong branch's
        // reward the moment any earlier response was gated out -- and it would look right whenever nothing was.
        [Test]
        public void AvailableResponsesKeepsTheOriginalIndices()
        {
            var w = new World();
            var d = new NpcDialogue
            {
                Responses = new[]
                {
                    new NpcResponse { Text = "gated", Conditions = new[] { Flag(1, 1) } },
                    new NpcResponse { Text = "open" },
                    new NpcResponse { Text = "also open" },
                }
            };
            var idx = DialogueRules.AvailableResponses(d, w);
            Assert.That(idx, Is.EqualTo(new List<int> { 1, 2 }), "the gated one is gone and the rest keep their numbers");
            Assert.That(d.Responses[idx[0]].Text, Is.EqualTo("open"));
            w.SetFlag(1, 1);
            Assert.That(DialogueRules.AvailableResponses(d, w), Is.EqualTo(new List<int> { 0, 1, 2 }));
        }

        [Test]
        public void AResponseWithNoTargetEndsTheConversation()
        {
            Assert.That(new NpcResponse { Text = "Goodbye" }.EndsConversation, Is.True);
            Assert.That(new NpcResponse { Dialogue = 60 }.EndsConversation, Is.False);
            Assert.That(new NpcResponse { Vendor = "abc" }.EndsConversation, Is.False);
        }

        [Test]
        public void MessagesArePickedByCondition_AndFallBackRatherThanGoingSilent()
        {
            var w = new World();
            var d = new NpcDialogue
            {
                Messages = new[]
                {
                    new NpcMessage { Pages = new[] { "you helped me" }, Conditions = new[] { Flag(9, 1) } },
                    new NpcMessage { Pages = new[] { "hello stranger" } },
                }
            };
            Assert.That(DialogueRules.MessageFor(d, w).Pages[0], Is.EqualTo("hello stranger"));
            w.SetFlag(9, 1);
            Assert.That(DialogueRules.MessageFor(d, w).Pages[0], Is.EqualTo("you helped me"));
            // ...and if NOTHING matches, the first message still shows. An NPC with nothing to say is a bug
            // that reads as a broken model rather than as a missing condition.
            var none = new NpcDialogue { Messages = new[] { new NpcMessage { Pages = new[] { "x" }, Conditions = new[] { Flag(99, 1) } } } };
            Assert.That(DialogueRules.MessageFor(none, w).Pages[0], Is.EqualTo("x"));
        }

        [Test]
        public void RewardsAssignAndIncrement()
        {
            var w = new World();
            w.SetFlag(5, 3);
            DialogueRules.Grant(new[]
            {
                new NpcReward { Type = ENpcRewardType.Flag_Short, Id = 5, Value = 2, Modification = ENpcModification.Increment },
                new NpcReward { Type = ENpcRewardType.Flag_Bool, Id = 6, Value = 1 },
                new NpcReward { Type = ENpcRewardType.Item, Id = 17 },
            }, w);
            Assert.That(w.GetFlag(5), Is.EqualTo(5), "3 + 2");
            Assert.That(w.GetFlag(6), Is.EqualTo(1));
            Assert.That(w.Given, Is.EqualTo(new List<(ushort, short)> { (17, 1) }), "an itemless amount means one");
        }

        // ---- ITEMS FOR ITEMS ----

        static NpcVendorDef Shop() => new NpcVendorDef
        {
            Selling = new[] { new NpcTradeLine { Item = 4, Cost = 1000 } },     // a rifle
            Buying = new[] { new NpcTradeLine { Item = 70, Cost = 250 },       // scrap, 250 each
                             new NpcTradeLine { Item = 71, Cost = 100 } },
        };

        // Chef Leonard's real shelf, as shipped: Maple Syrup at 85, paid for out of tinned food. The numbers
        // are the actual ones from content/npcs.json so a change to the extractor that mangles the rate shows
        // up here rather than as a vendor nobody can afford.
        static NpcVendorDef Chef() => new NpcVendorDef
        {
            Name = "<color=legendary>Leonard</color>'s Fresh Food Market",
            Selling = new[] { new NpcTradeLine { Item = 1159, Cost = 85 } },   // maple syrup
            Buying = new[]
            {
                new NpcTradeLine { Item = 1952, Cost = 10 },   // sardines
                new NpcTradeLine { Item = 1954, Cost = 30 },   // beans
                new NpcTradeLine { Item = 1956, Cost = 30 },   // pasta
                new NpcTradeLine { Item = 1959, Cost = 50 },   // MRE
            },
        };

        // Retail writes rarity into the name as markup. Anything that shows one raw puts <color=legendary> on
        // screen, and the vendor window is the one place a vendor's name is displayed.
        [Test]
        public void PlainText_StripsTheRarityMarkupAndKeepsTheWords()
        {
            Assert.That(TradeRules.PlainText(Chef().Name), Is.EqualTo("Leonard's Fresh Food Market"));
            Assert.That(TradeRules.PlainText("no markup here"), Is.EqualTo("no markup here"));
            Assert.That(TradeRules.PlainText(""), Is.EqualTo(""));
            Assert.That(TradeRules.PlainText(null), Is.EqualTo(""));
        }

        // ---- DOLLARS: a line's Cost is its price; their buying list is what they pay you ---------------------
        [Test]
        public void CanBuyIsAWalletAgainstTheAskingPrice()
        {
            var line = new NpcTradeLine { Item = 4, Cost = 1000 };
            Assert.That(TradeRules.CanBuy(line, 999), Is.False);
            Assert.That(TradeRules.CanBuy(line, 1000), Is.True, "exactly enough is enough");
            Assert.That(TradeRules.CanBuy(new NpcTradeLine { Item = 0, Cost = 10 }, 500), Is.False, "a vehicle line is not deliverable here");
            Assert.That(TradeRules.CanBuy(null, 500), Is.False);
        }

        [Test]
        public void AffordableIsWholeItemsOnly()
        {
            var line = new NpcTradeLine { Item = 4, Cost = 60 };
            Assert.That(TradeRules.Affordable(line, 179, 100), Is.EqualTo(2), "$179 is two, not two-and-change");
            Assert.That(TradeRules.Affordable(line, 59, 100), Is.EqualTo(0));
            Assert.That(TradeRules.Affordable(new NpcTradeLine { Item = 4, Cost = 0 }, 0, 7), Is.EqualTo(7), "free is capped, not infinite");
        }

        // ⭐ AN ITEM THE VENDOR DOES NOT BUY IS WORTH NOTHING TO THEM. Not "worth its sell price" -- a vendor who
        // takes anything at its own asking price is an infinite-money machine the moment two vendors disagree.
        [Test]
        public void AnItemTheyDoNotBuyIsWorthNothing()
        {
            var v = Shop();
            Assert.That(TradeRules.ValueOf(v, 4), Is.EqualTo(0), "they SELL the rifle; that does not mean they buy it");
            Assert.That(TradeRules.ValueOf(v, 70), Is.EqualTo(250), "and what they DO buy pays its line's price");
        }
    }
}
