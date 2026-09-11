using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The point of moving conversations server-side is that a CLIENT CANNOT ASK FOR SOMETHING IT WAS NOT
    // OFFERED. So these are written as the things a bad client would actually send -- answering a conversation
    // it is not in, picking a gated response, paying with items it does not hold -- and each asserts that
    // nothing moved, not merely that the call returned false. A refusal that half-applies is worse than none.
    [TestFixture]
    public class ServerNpcsTests
    {
        const ushort Tomato = 70, Steak = 71, Syrup = 1159;

        static NpcVendorDef Shop() => new NpcVendorDef
        {
            Guid = "abc", Key = "Market", Name = "Market",
            Selling = new[] { new NpcTradeLine { Item = Syrup, Cost = 60 } },
            Buying = new[] { new NpcTradeLine { Item = Tomato, Cost = 30 } },
        };

        static NpcDialogue Talk() => new NpcDialogue
        {
            Id = 59,
            Messages = new[] { new NpcMessage { Pages = new[] { "Hello!" } } },
            Responses = new[]
            {
                new NpcResponse { Text = "open", Vendor = "abc" },
                new NpcResponse
                {
                    Text = "gated", Dialogue = 60,
                    Conditions = new[] { new NpcCondition { Type = ENpcConditionType.Flag_Bool, Id = 61, Value = 1 } },
                },
                new NpcResponse { Text = "bye" },
            },
        };

        /// <summary>⚠ THE TRADE ITEMS NEED ASSETS OR NOTHING CAN BE STOCKED. `tryAddItem` places by the item's
        /// SIZE, which comes off its ItemAsset -- with no asset registered the item has no dimensions and every
        /// add silently returns false. That is what broke the four trade tests: the bag was empty in all of
        /// them, so the one valid trade was refused for lack of goods and the three refusals then failed on
        /// their "and the goods are untouched" counts. Measured before fixing: tryAddItem false 3/3, count 0.
        ///
        /// ADDITIVE, not Assets.clear() + add like TransactionalFixtures does -- this runs per-test and must
        /// not yank the catalog out from under a suite that shares the process.</summary>
        [SetUp]
        public void RegisterTradeAssets()
        {
            if (Assets.find(Tomato) == null) Assets.add(new ItemAsset { id = Tomato, itemName = "Tomato", size_x = 1, size_y = 1 });
            if (Assets.find(Steak) == null)  Assets.add(new ItemAsset { id = Steak,  itemName = "Steak",  size_x = 1, size_y = 1 });
            if (Assets.find(Syrup) == null)  Assets.add(new ItemAsset { id = Syrup,  itemName = "Syrup",  size_x = 1, size_y = 1 });
        }

        static (ServerNpcs npcs, InventoryReplication inv, PlayerInventory bag) Rig(int tomatoes = 0)
        {
            var inv = new InventoryReplication();
            inv.ServerAdd(1, 0L);
            inv.TryGet(1, out var e);
            // ASSERTED, because an unasserted fixture is how this shipped: the adds were failing and every
            // downstream assertion just reported the consequence.
            for (int i = 0; i < tomatoes; i++)
                Assert.That(e.Inventory.tryAddItem(new Item(Tomato)), Is.True, $"fixture: tomato {i + 1} went into the bag");
            if (tomatoes > 0)
                Assert.That(e.Inventory.getItemCount(Tomato), Is.EqualTo(tomatoes), "fixture: the bag really holds them");
            var dialogues = new Dictionary<int, NpcDialogue> { [59] = Talk(), [60] = new NpcDialogue { Id = 60 } };
            var vendor = Shop();
            var npcs = new ServerNpcs
            {
                Inventories = inv,
                DialogueOf = id => dialogues.TryGetValue(id, out var d) ? d : null,
                QuestOfId = _ => null,
                VendorOf = g => g == "abc" ? vendor : null,
                ActiveHoliday = () => "",
            };
            return (npcs, inv, e.Inventory);
        }

        // ⭐ ACCEPTING A REQUEST AND TELLING NOBODY IS THE SAME AS REFUSING IT. The client sends Talk and then
        // WAITS for the state that says which dialogue it is in -- it is not allowed to decide that itself any
        // more. Open set the field and never fired Changed, so the server quietly agreed and the panel never
        // opened. Nothing threw; the conversation simply did not happen.
        [Test]
        public void OpeningAndClosingAConversationPublishesIt()
        {
            var (npcs, _, _) = Rig();
            int pushes = 0;
            npcs.Changed = _ => pushes++;

            Assert.That(npcs.Open(1, 59), Is.True);
            Assert.That(pushes, Is.EqualTo(1), "Open must publish, or the client never learns it worked");

            npcs.Close(1);
            Assert.That(pushes, Is.EqualTo(2), "and so must Close");

            npcs.Open(1, 4242);
            Assert.That(pushes, Is.EqualTo(2), "a REFUSED open publishes nothing");
        }

        // ⭐⭐ THE TEST THAT REJECTS "A CALL SITE FORGOT THE ASSIGNMENT".
        //
        // Rig() hands ServerNpcs its catalog -- which is precisely the input a forgotten call site does NOT
        // provide, so every test above would pass just as happily against a server that was never wired. That
        // is the shape tinyclaw's radiation test had (2026-09-11): it constructed the loopback with the volumes
        // already assigned, passed against the broken code, and the feature did nothing on the mode the game
        // actually boots. So this one builds a ServerNpcs with NOTHING assigned.
        //
        // Revert the `?? DefaultDialogueOf` fallback and the second half of this fails.
        [Test]
        public void AServerNobodyWiredStillResolves_AndSaysSoIfItCannot()
        {
            var saveD = ServerNpcs.DefaultDialogueOf;
            var saveQ = ServerNpcs.DefaultQuestOf;
            var saveV = ServerNpcs.DefaultVendorOf;
            try
            {
                ServerNpcs.DefaultDialogueOf = null;
                ServerNpcs.DefaultQuestOf = null;
                ServerNpcs.DefaultVendorOf = null;

                // Nothing anywhere: refuses, and the refusal is COUNTED as unconfigured rather than looking
                // like "that dialogue does not exist" -- which is the whole difference between a broken server
                // and a bad request, and they were indistinguishable before.
                var bare = new ServerNpcs();
                Assert.That(bare.IsConfigured, Is.False);
                Assert.That(bare.Open(1, 59), Is.False);
                Assert.That(bare.Trade(1, "abc", 0, new[] { ((ushort)70, 2) }), Is.False);
                Assert.That(bare.UnconfiguredRefusals, Is.EqualTo(2), "counted, not silent");

                // Only the STATIC registered -- no per-instance assignment at all, which is what a server built
                // by a call site that knows nothing about NPCs looks like. It must work anyway.
                var dialogues = new Dictionary<int, NpcDialogue> { [59] = Talk() };
                ServerNpcs.DefaultDialogueOf = id => dialogues.TryGetValue(id, out var d) ? d : null;
                ServerNpcs.DefaultQuestOf = _ => null;
                ServerNpcs.DefaultVendorOf = _ => null;

                var unwired = new ServerNpcs();
                Assert.That(unwired.IsConfigured, Is.True);
                Assert.That(unwired.Open(1, 59), Is.True, "a server nobody wired still finds the catalog");
                Assert.That(unwired.UnconfiguredRefusals, Is.EqualTo(0));
            }
            finally
            {
                ServerNpcs.DefaultDialogueOf = saveD;
                ServerNpcs.DefaultQuestOf = saveQ;
                ServerNpcs.DefaultVendorOf = saveV;
            }
        }

        // ONE COMMAND, ONE PUSH. Touch fires on every individual mutation, so a response that grants three
        // flags and moves the conversation pushed the player's whole state four times -- and a quest granting
        // ten would push ten. Nothing breaks; it is just the same payload sent again and again.
        [Test]
        public void AWholeCommandProducesExactlyOneStatePush()
        {
            var (npcs, _, _) = Rig();
            npcs.Open(1, 59);
            int pushes = 0;
            npcs.Changed = _ => pushes++;

            npcs.Batch(() =>
            {
                var p = npcs.For(1);
                p.SetFlag(10, 1);
                p.SetFlag(11, 1);
                p.SetFlag(12, 1);
                npcs.Choose(1, 59, 2, out _, out _);
            });
            Assert.That(pushes, Is.EqualTo(1), "three flags and a dialogue move are still one state");

            // Outside a batch nothing has to remember to flush -- it sends immediately.
            npcs.For(1).SetFlag(13, 1);
            Assert.That(pushes, Is.EqualTo(2));
        }

        // ⭐ THE CORE ONE. Without the open-dialogue check a client can answer any conversation in the game from
        // anywhere -- which is how a quest gets handed in by somebody who never met the person holding it.
        [Test]
        public void AResponseIsRefusedUnlessTheServerHasYouInThatDialogue()
        {
            var (npcs, _, _) = Rig();
            Assert.That(npcs.Choose(1, 59, 2, out _, out _), Is.False, "never opened it");

            Assert.That(npcs.Open(1, 59), Is.True);
            Assert.That(npcs.Choose(1, 60, 0, out _, out _), Is.False, "in 59, answering 60");
            Assert.That(npcs.Choose(1, 59, 2, out _, out _), Is.True);

            // "bye" has no target, so the server records the conversation as over -- and a late answer to it
            // is refused rather than replayed.
            Assert.That(npcs.For(1).OpenDialogue, Is.EqualTo(0));
            Assert.That(npcs.Choose(1, 59, 0, out _, out _), Is.False, "the conversation ended");
        }

        [Test]
        public void AGatedResponseIsRefusedByTheSamePredicateThatHidIt()
        {
            var (npcs, _, _) = Rig();
            npcs.Open(1, 59);
            Assert.That(npcs.Choose(1, 59, 1, out _, out _), Is.False, "flag 61 is not set");
            Assert.That(npcs.For(1).OpenDialogue, Is.EqualTo(59), "and it did not move us");

            npcs.For(1).SetFlag(61, 1);
            Assert.That(npcs.Choose(1, 59, 1, out int next, out _), Is.True);
            Assert.That(next, Is.EqualTo(60));
            Assert.That(npcs.For(1).OpenDialogue, Is.EqualTo(60));
        }

        [Test]
        public void AnOutOfRangeResponseIsRefused()
        {
            var (npcs, _, _) = Rig();
            npcs.Open(1, 59);
            Assert.That(npcs.Choose(1, 59, 99, out _, out _), Is.False);
            Assert.That(npcs.Choose(1, 59, 255, out _, out _), Is.False);
            Assert.That(npcs.Open(1, 4242), Is.False, "a dialogue that does not exist");
        }

        // ⭐ PAYING WITH ITEMS YOU DO NOT HAVE. The affordability maths says 2x30 covers 60 and it is right --
        // about a pile that does not exist. Without the holdings check the first ones are removed, the rest are
        // silently ignored, and you are paid anyway.
        [Test]
        public void ATradeYouCannotStockIsRefusedAndTakesNothing()
        {
            var (npcs, _, bag) = Rig(tomatoes: 1);
            var offer = new[] { (Tomato, 2) };
            Assert.That(npcs.Trade(1, "abc", 0, offer), Is.False, "claims two, holds one");
            Assert.That(bag.getItemCount(Tomato), Is.EqualTo(1), "and the one it DOES hold is untouched");
            Assert.That(bag.getItemCount(Syrup), Is.EqualTo(0), "and nothing was paid out");
        }

        [Test]
        public void ATradeThatIsShortIsRefused()
        {
            var (npcs, _, bag) = Rig(tomatoes: 1);
            Assert.That(npcs.Trade(1, "abc", 0, new[] { (Tomato, 1) }), Is.False, "30 of 60");
            Assert.That(bag.getItemCount(Tomato), Is.EqualTo(1));
            Assert.That(bag.getItemCount(Syrup), Is.EqualTo(0));
        }

        [Test]
        public void AGoodTradeMovesTheGoodsOnce()
        {
            var (npcs, _, bag) = Rig(tomatoes: 3);
            Assert.That(npcs.Trade(1, "abc", 0, new[] { (Tomato, 2) }), Is.True);
            Assert.That(bag.getItemCount(Tomato), Is.EqualTo(1), "two handed over, one kept");
            Assert.That(bag.getItemCount(Syrup), Is.EqualTo(1));
        }

        // An "offer" of a negative or zero count is an attempt to be PAID for handing over nothing.
        [Test]
        public void ANonPositiveOfferIsRefused()
        {
            var (npcs, _, bag) = Rig(tomatoes: 3);
            Assert.That(npcs.Trade(1, "abc", 0, new[] { (Tomato, 0) }), Is.False);
            Assert.That(npcs.Trade(1, "abc", 0, new[] { (Tomato, -5) }), Is.False);
            Assert.That(bag.getItemCount(Syrup), Is.EqualTo(0));
        }

        // An item the vendor does not buy is worth nothing, and offering a pile of them must not coincidentally
        // pass by arriving with enough entries.
        [Test]
        public void OfferingSomethingTheyDoNotBuyIsRefused()
        {
            var (npcs, _, bag) = Rig();
            npcs.For(1).GiveItem(Steak, 10);
            Assert.That(npcs.Trade(1, "abc", 0, new[] { (Steak, 10) }), Is.False);
            Assert.That(bag.getItemCount(Steak), Is.EqualTo(10), "untouched");
        }

        [Test]
        public void AnUnknownVendorOrLineIsRefused()
        {
            var (npcs, _, _) = Rig(tomatoes: 4);
            Assert.That(npcs.Trade(1, "nope", 0, new[] { (Tomato, 2) }), Is.False);
            Assert.That(npcs.Trade(1, "abc", 7, new[] { (Tomato, 2) }), Is.False);
        }

        // Opening a shop is a one-shot: parked for the next state push and cleared by it. Left on the player it
        // would ride every later change and reopen the window each time a flag moved.
        [Test]
        public void AVendorOpensOnceAndDoesNotStick()
        {
            var (npcs, _, _) = Rig();
            npcs.Open(1, 59);
            Assert.That(npcs.Choose(1, 59, 0, out _, out string vendor), Is.True);
            Assert.That(vendor, Is.EqualTo("abc"));
            Assert.That(npcs.For(1).PendingVendor, Is.Empty, "cleared by the push it was parked for");
            Assert.That(npcs.For(1).OpenDialogue, Is.EqualTo(59), "and the conversation is still up behind it");
        }

        // The state is PER PLAYER. Two people in the same conversation must not share a flag or a position in it.
        [Test]
        public void StateIsPerPlayer()
        {
            var (npcs, inv, _) = Rig();
            inv.ServerAdd(2, 0L);
            npcs.Open(1, 59);
            npcs.For(1).SetFlag(61, 1);
            Assert.That(npcs.For(2).GetFlag(61), Is.EqualTo((short)0));
            Assert.That(npcs.For(2).OpenDialogue, Is.EqualTo(0));
            Assert.That(npcs.Choose(2, 59, 1, out _, out _), Is.False, "player 2 is in no conversation");
        }
    }
}
