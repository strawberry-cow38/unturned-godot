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

        [Test]
        public void APriceListIsAnExchangeRate()
        {
            var v = Shop();
            Assert.That(TradeRules.PriceIn(v, v.Selling[0], 70), Is.EqualTo(4), "1000 / 250");
            Assert.That(TradeRules.PriceIn(v, v.Selling[0], 71), Is.EqualTo(10), "1000 / 100");
        }

        // ⭐ AN ITEM THE VENDOR DOES NOT BUY IS WORTH NOTHING TO THEM. Not "worth its sell price" -- a vendor
        // who takes anything at its own asking price is an infinite-item machine the moment two vendors
        // disagree on what something is worth.
        [Test]
        public void AnItemTheyDoNotBuyIsWorthNothing()
        {
            var v = Shop();
            Assert.That(TradeRules.ValueOf(v, 4), Is.EqualTo(0), "they SELL the rifle; that does not mean they buy it");
            Assert.That(TradeRules.PriceIn(v, v.Selling[0], 4), Is.EqualTo(0));
            Assert.That(TradeRules.CanAfford(v, v.Selling[0], new ushort[] { 4, 4, 4, 4, 4 }), Is.False);
        }

        [Test]
        public void OverpayingIsAllowedAndUnderpayingIsNot()
        {
            var v = Shop();
            Assert.That(TradeRules.CanAfford(v, v.Selling[0], new ushort[] { 70, 70, 70 }), Is.False, "750 of 1000");
            Assert.That(TradeRules.CanAfford(v, v.Selling[0], new ushort[] { 70, 70, 70, 70 }), Is.True, "exactly 1000");
            // ...and over is fine: refusing it would make a trade impossible whenever nothing adds up exactly.
            Assert.That(TradeRules.CanAfford(v, v.Selling[0], new ushort[] { 70, 70, 70, 70, 71 }), Is.True);
        }

        [Test]
        public void RoundingIsUp_BecauseYouCannotHandOverAFractionOfAnItem()
        {
            var v = new NpcVendorDef
            {
                Selling = new[] { new NpcTradeLine { Item = 4, Cost = 1000 } },
                Buying = new[] { new NpcTradeLine { Item = 70, Cost = 300 } },
            };
            Assert.That(TradeRules.PriceIn(v, v.Selling[0], 70), Is.EqualTo(4), "3.33 -> 4, and 4 of them is an overpay");
            Assert.That(TradeRules.CanAfford(v, v.Selling[0], new ushort[] { 70, 70, 70 }), Is.False, "900 is not 1000");
        }
    }
}
