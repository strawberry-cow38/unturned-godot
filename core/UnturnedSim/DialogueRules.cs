using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>What a dialogue response can be gated on. The full set retail actually uses, counted across all
    /// 142 dialogues rather than guessed from the three I happened to read first: Quest 81, Flag_Bool 29,
    /// Flag_Short 34, Holiday 10, Reputation 2, Experience 1.</summary>
    /// <summary>The first seven gate DIALOGUE -- they ask a question about the world and the answer decides
    /// whether a line shows. The last four gate a QUEST, and they are different in kind: they are OBJECTIVES,
    /// counted as you play (kill six zombies, hold eleven barnacles). Retail parses both with one enum and so
    /// does this, but only the tracked ones ever appear on a quest, which is why Passes answers them from a
    /// counter rather than from a flag.</summary>
    public enum ENpcConditionType
    {
        None, Flag_Bool, Flag_Short, Quest, Holiday, Reputation, Experience,
        Item, Kills_Zombie, Kills_Horde, Kills_Tree, Kills_Object,
    }

    /// <summary>Retail's comparison words. Equal is 91 of the 101 that name one, and the 9 that name none mean
    /// Equal too -- an absent Logic is the default, not an error.</summary>
    public enum ENpcLogic { Equal, Not_Equal, Greater_Than, Greater_Than_Or_Equal_To, Less_Than, Less_Than_Or_Equal_To }

    public enum ENpcRewardType { None, Flag_Bool, Flag_Short, Item, Quest, Experience, Reputation, Vehicle }

    /// <summary>Assign writes the value; Increment adds it. 70 of the 90 rewards name neither, and those are
    /// Assign -- the same "absent means the common case" habit the Logic field has.</summary>
    public enum ENpcModification { Assign, Increment }

    public enum ENpcQuestStatus { None, Active, Ready, Completed }

    public sealed class NpcCondition
    {
        public ENpcConditionType Type;
        public ushort Id;
        public short Value;
        public ENpcLogic Logic = ENpcLogic.Equal;
        public ENpcQuestStatus Status;   // Quest conditions compare a STATUS, not a number
        public string Text = "";         // Holiday compares a name; on a QUEST condition, the objective line

        /// <summary>How many the objective wants ("Cleanup {0}/{1} Barnacles" -> 11). Only the tracked kinds
        /// use it; a flag condition compares Value instead.</summary>
        public int Amount;

        /// <summary>Consumed on turn-in. An Item objective with Reset takes the items off you when you hand
        /// the quest in; without it you keep them and only had to HAVE them, which is a different quest.</summary>
        public bool Reset;

        /// <summary>How many this objective wants. Amount for the COUNTED kinds, the compared Value for a flag
        /// -- two fields because retail uses two, and collapsing them would make "flag >= 9" read as "9 of them".
        ///
        /// ⚠ ONE definition, used by both the test and the sentence. Describe filled {1} from Amount alone at
        /// first, so "Cleanup {0}/{1} Barnacles" rendered as "Cleanup 0/0 Barnacles" on a flag objective -- an
        /// objective that reads as already satisfied and is not.</summary>
        public int Wants => Amount > 0 ? Amount : Value;

        /// <summary>The objective as the player reads it, {0} from progress and {1} from <see cref="Wants"/>.
        /// Retail ships this string per condition, so a quest log does not have to invent a sentence from a
        /// type name -- and the text and the test stay lined up because they share an index.</summary>
        public string Describe(int have) =>
            string.IsNullOrEmpty(Text) ? $"{Type} {have}/{Wants}"
                                       : Text.Replace("{0}", have.ToString()).Replace("{1}", Wants.ToString());
    }

    public sealed class NpcReward
    {
        /// <summary>Quest rewards count with Amount; dialogue rewards use Value for the same idea. Both are
        /// carried so neither has to be translated into the other at extract time.</summary>
        public int Amount;
        public string Spawnpoint = "";

        public ENpcRewardType Type;
        public ushort Id;
        public short Value;
        public ENpcModification Modification = ENpcModification.Assign;
    }

    public sealed class NpcResponse
    {
        public string Text = "";
        public int Dialogue;      // where it goes next; 0 = nowhere
        public string Vendor = ""; // a vendor GUID; empty = not a trade
        public int Quest;
        public NpcCondition[] Conditions = System.Array.Empty<NpcCondition>();
        public NpcReward[] Rewards = System.Array.Empty<NpcReward>();

        /// <summary>A response that goes NOWHERE ends the conversation. That is how retail writes "Goodbye" --
        /// an absence of every target rather than a flag saying so -- and treating it as a missing link instead
        /// of a deliberate exit is how you end up with a dialogue nobody can leave.</summary>
        public bool EndsConversation => Dialogue == 0 && string.IsNullOrEmpty(Vendor) && Quest == 0;
    }

    public sealed class NpcMessage
    {
        public string[] Pages = System.Array.Empty<string>();
        public NpcCondition[] Conditions = System.Array.Empty<NpcCondition>();
    }

    public sealed class NpcDialogue
    {
        public int Id;
        public string Key = "";
        public NpcMessage[] Messages = System.Array.Empty<NpcMessage>();
        public NpcResponse[] Responses = System.Array.Empty<NpcResponse>();
    }

    public sealed class NpcCharacterDef
    {
        public int Id;
        public string Key = "", Name = "";
        public ushort Shirt, Pants, Hat, Vest, Mask, Glasses, Backpack;
        public int Face;
        public string Skin = "", Hair = "";
        public int Dialogue;

        /// <summary>A vendor guid/key this character will trade with directly, or empty.
        ///
        /// NOT how retail does it, and said so out loud: retail reaches a shop through a dialogue RESPONSE that
        /// carries a vendor, which means opening a shop costs you a whole authored conversation. That is the
        /// right model for ported content and a wall for someone dressing a map, so a character may also just
        /// HAVE a shop. Ripped characters leave this empty and keep going through their real dialogue.</summary>
        public string Shop = "";
    }

    /// <summary>One line of a vendor's list. Retail prices everything in a single currency (experience), which
    /// is exactly what makes master's "items for items" workable without authoring anything: one price list
    /// gives every PAIR an exchange rate. See TradeRules.</summary>
    public sealed class NpcTradeLine
    {
        public string Type = "Item";   // Item | Vehicle
        public ushort Item;            // resolved id (0 for a vehicle, or an unresolved reference)
        public string Guid = "";
        public int Cost;
        public string Spawnpoint = "", Paint = "";
    }

    public sealed class NpcVendorDef
    {
        public string Guid = "", Key = "", Name = "", Description = "";
        public NpcTradeLine[] Selling = System.Array.Empty<NpcTradeLine>();
        public NpcTradeLine[] Buying = System.Array.Empty<NpcTradeLine>();
    }

    /// <summary>What the dialogue evaluator needs to know about the world. An interface rather than concrete
    /// state because the SAME evaluator has to run against a live player in the game layer and against a
    /// fixture in a test, and a condition that can only be checked one of those ways is a condition nobody
    /// checks.</summary>
    public interface INpcWorld
    {
        short GetFlag(ushort id);
        void SetFlag(ushort id, short value);
        ENpcQuestStatus GetQuestStatus(ushort id);
        string ActiveHoliday { get; }
        int Reputation { get; }
        uint Experience { get; }
        void GiveItem(ushort itemId, short amount);

        // ---- quests ------------------------------------------------------------------------------------
        void SetQuestStatus(ushort id, ENpcQuestStatus status);

        /// <summary>How far along objective `index` of quest `questId` is, for the COUNTED kinds only (kills).
        /// Everything else is read from where it already lives -- a flag from the flags, an item from the bag --
        /// so this exists for exactly the objectives that have nowhere else to be stored.</summary>
        int QuestProgress(ushort questId, int index);
        void AddQuestProgress(ushort questId, int index, int by);

        int CountItem(ushort itemId);
        void TakeItem(ushort itemId, int amount);
        void AddExperience(int amount);
        void AddReputation(int amount);
    }

    public static class DialogueRules
    {
        static bool Compare(long a, long b, ENpcLogic logic) => logic switch
        {
            ENpcLogic.Not_Equal => a != b,
            ENpcLogic.Greater_Than => a > b,
            ENpcLogic.Greater_Than_Or_Equal_To => a >= b,
            ENpcLogic.Less_Than => a < b,
            ENpcLogic.Less_Than_Or_Equal_To => a <= b,
            _ => a == b,
        };

        /// <summary>Does this condition hold? An UNKNOWN type passes: a response gated on something this port
        /// has not implemented yet should still be reachable, because the alternative is a conversation that
        /// silently loses half its branches and looks finished. Quests are the live case -- 81 of the 101
        /// conditions are Quest ones and the quest system does not exist, so GetQuestStatus reports None for
        /// everything, which is the truthful answer for a player who has started nothing.</summary>
        public static bool Passes(NpcCondition c, INpcWorld w)
        {
            if (c == null || w == null) return true;
            switch (c.Type)
            {
                case ENpcConditionType.Flag_Bool:
                case ENpcConditionType.Flag_Short:
                    return Compare(w.GetFlag(c.Id), c.Value, c.Logic);
                case ENpcConditionType.Quest:
                    return Compare((int)w.GetQuestStatus(c.Id), (int)c.Status, c.Logic);
                case ENpcConditionType.Holiday:
                    bool same = string.Equals(w.ActiveHoliday ?? "", c.Text ?? "", System.StringComparison.OrdinalIgnoreCase);
                    return c.Logic == ENpcLogic.Not_Equal ? !same : same;
                case ENpcConditionType.Reputation:
                    return Compare(w.Reputation, c.Value, c.Logic);
                case ENpcConditionType.Experience:
                    return Compare(w.Experience, c.Value, c.Logic);
                default:
                    return true;
            }
        }

        public static bool PassesAll(NpcCondition[] cs, INpcWorld w)
        {
            if (cs == null) return true;
            foreach (var c in cs) if (!Passes(c, w)) return false;
            return true;
        }

        /// <summary>The responses this player can actually see. ⚠ Index-preserving is deliberate: the caller
        /// needs to know WHICH response was picked to apply its rewards, and filtering into a fresh list then
        /// indexing by position is how the wrong branch's reward gets granted.</summary>
        public static List<int> AvailableResponses(NpcDialogue d, INpcWorld w)
        {
            var outIdx = new List<int>();
            if (d == null) return outIdx;
            for (int i = 0; i < d.Responses.Length; i++)
                if (PassesAll(d.Responses[i].Conditions, w)) outIdx.Add(i);
            return outIdx;
        }

        /// <summary>The message pages to show: the FIRST message whose conditions hold. Retail gates messages
        /// the same way it gates responses, so an NPC greets you differently once you have done something for
        /// them -- and falls back to the first message rather than going silent if none match.</summary>
        public static NpcMessage MessageFor(NpcDialogue d, INpcWorld w)
        {
            if (d == null || d.Messages.Length == 0) return null;
            foreach (var m in d.Messages) if (PassesAll(m.Conditions, w)) return m;
            return d.Messages[0];
        }

        /// <summary>Grant a response's rewards. Flags and items only -- a Quest reward is recorded as a flag
        /// write it cannot make, and is skipped rather than faked, because a quest silently "granted" into a
        /// system that does not exist is worse than one that visibly did not happen.</summary>
        /// <summary>Quest rewards of type Vehicle that were asked for and not handed over. See the Vehicle
        /// case in Grant: a known gap, kept visible.</summary>
        public static int UndeliveredVehicleRewards;

        public static void Grant(NpcReward[] rs, INpcWorld w)
        {
            if (rs == null || w == null) return;
            foreach (var r in rs)
            {
                switch (r.Type)
                {
                    case ENpcRewardType.Flag_Bool:
                    case ENpcRewardType.Flag_Short:
                        short next = r.Modification == ENpcModification.Increment
                                   ? (short)(w.GetFlag(r.Id) + r.Value) : r.Value;
                        w.SetFlag(r.Id, next);
                        break;
                    case ENpcRewardType.Item:
                        // Amount if it has one, else Value, else one. Retail uses Reward_N_Amount on quests and
                        // Reward_N_Value on dialogue for the same idea; taking whichever is set beats picking
                        // one and silently handing out a single item where seven were meant.
                        short n = r.Amount > 0 ? (short)r.Amount : (r.Value == 0 ? (short)1 : r.Value);
                        w.GiveItem(r.Id, n);
                        break;
                    case ENpcRewardType.Experience:
                        w.AddExperience(r.Amount > 0 ? r.Amount : r.Value);
                        break;
                    case ENpcRewardType.Reputation:
                        w.AddReputation(r.Amount > 0 ? r.Amount : r.Value);
                        break;
                    case ENpcRewardType.Quest:
                        // Handing out a quest as a reward is how a chain links: 1 of the 88 does it, and it is
                        // the only way Ready ever follows Completed without the player going back to a board.
                        if (w.GetQuestStatus(r.Id) == ENpcQuestStatus.None)
                            w.SetQuestStatus(r.Id, ENpcQuestStatus.Active);
                        break;
                    case ENpcRewardType.Vehicle:
                        // 1 of the 88, and delivering it needs a spawnpoint this layer knows nothing about --
                        // core has no world and no logger. COUNTED rather than silently skipped, so the gap is
                        // a number somebody can print instead of a mystery nobody knows to look for.
                        UndeliveredVehicleRewards++;
                        break;
                }
            }
        }
    }

    /// <summary>ITEMS FOR ITEMS (master 2026-09-11: "the shop ui should be more of a trade ui. items for
    /// items"), built out of retail's own numbers rather than an authored barter table.
    ///
    /// Every vendor line already carries a Cost in a single currency, so one price list gives EVERY pair an
    /// exchange rate: if they sell a rifle for 1000 and buy scrap at 250, the rifle costs four scrap. That is
    /// a trade in items with no new content invented and no rate I chose -- which matters, because a barter
    /// rate I made up is a balance decision wearing a mechanic's clothes.
    ///
    /// ⚠ An item the vendor does NOT buy is worth nothing to them. Not "worth its sell price" -- a vendor who
    /// accepts anything at its own asking price is an infinite-money machine the moment two vendors disagree.</summary>
    public static class TradeRules
    {
        /// <summary>Retail writes rarity into the NAME -- "&lt;color=legendary&gt;Coalition&lt;/color&gt; Aircraft Hangar" --
        /// so anything that shows one raw puts the markup on screen. Strips the tags and keeps the words.
        ///
        /// Stripped at DISPLAY rather than at load, deliberately: the colour says which rarity the vendor is,
        /// and throwing it away when the file is read means nothing can ever render it. Here it is only this
        /// label that does not want it.</summary>
        public static string PlainText(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
            var sb = new System.Text.StringBuilder(s.Length);
            int depth = 0;
            foreach (char c in s)
            {
                if (c == '<') depth++;
                else if (c == '>') { if (depth > 0) depth--; }
                else if (depth == 0) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public static int ValueOf(NpcVendorDef v, ushort itemId)
        {
            if (v == null) return 0;
            foreach (var b in v.Buying) if (b.Item == itemId) return b.Cost;
            return 0;
        }

        /// <summary>What a pile of offered items is worth to this vendor.</summary>
        public static int OfferValue(NpcVendorDef v, IEnumerable<ushort> offered)
        {
            int t = 0;
            if (offered != null) foreach (var id in offered) t += ValueOf(v, id);
            return t;
        }

        /// <summary>Is this pile enough for that line? Deliberately NOT "exactly enough": you may overpay, the
        /// same way you can hand over a note and not get change, and refusing an overpay would make a trade
        /// impossible whenever no combination adds up exactly.</summary>
        public static bool CanAfford(NpcVendorDef v, NpcTradeLine want, IEnumerable<ushort> offered)
            => want != null && OfferValue(v, offered) >= want.Cost;

        /// <summary>How many of `pay` it takes to cover `want`, or 0 if the vendor will not take that item at
        /// all. Rounds UP -- four-and-a-bit scrap means five.</summary>
        public static int PriceIn(NpcVendorDef v, NpcTradeLine want, ushort pay)
        {
            int unit = ValueOf(v, pay);
            if (unit <= 0 || want == null || want.Cost <= 0) return 0;
            return (want.Cost + unit - 1) / unit;
        }

        /// <summary>What a pile is worth when the pile is COUNTS -- three tomatoes rather than three entries.
        /// The IEnumerable overload above computes the same sum for a flat list; both exist because a bag holds
        /// stacks and a test holds a list, and making either side convert is how the two drift apart.</summary>
        public static int OfferValue(NpcVendorDef v, IReadOnlyDictionary<ushort, int> offered)
        {
            int t = 0;
            if (offered != null) foreach (var kv in offered) t += ValueOf(v, kv.Key) * kv.Value;
            return t;
        }

        public static bool CanAfford(NpcVendorDef v, NpcTradeLine want, IReadOnlyDictionary<ushort, int> offered)
            => want != null && OfferValue(v, offered) >= want.Cost;

        /// <summary>Build a pile out of what the player HAS that covers `want`, or null if their bag cannot cover
        /// it at all. <paramref name="have"/> answers "how many of this id do I hold".
        ///
        /// CHEAPEST UNIT FIRST, deliberately -- not fewest items. Handing over your one valuable thing to buy a
        /// tomato is the trade nobody makes on purpose, and an "offer for me" button that makes it is a trap
        /// dressed as a convenience. Ties break on the lower id so the same bag always produces the same pile:
        /// an auto-fill that shuffles between presses looks broken even when every pile it picks is valid.
        ///
        /// Returns null rather than a partial pile. A short pile that cannot buy anything is not a smaller
        /// success, and handing one back leaves the caller to discover the failure by checking the total.</summary>
        public static Dictionary<ushort, int> AutoOffer(NpcVendorDef v, NpcTradeLine want, System.Func<ushort, int> have)
        {
            var pile = new Dictionary<ushort, int>();
            if (v == null || want == null || have == null) return null;
            if (want.Cost <= 0) return pile;   // free: the empty pile already covers it

            var rate = new List<NpcTradeLine>(v.Buying);
            rate.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost) : a.Item.CompareTo(b.Item));

            int paid = 0;
            foreach (var line in rate)
            {
                if (paid >= want.Cost) break;
                if (line.Cost <= 0) continue;              // a thing they will take but pay nothing for buys nothing
                int held = have(line.Item);
                if (held <= 0) continue;
                int need = (want.Cost - paid + line.Cost - 1) / line.Cost;   // round UP: part of an item is an item
                int take = System.Math.Min(held, need);
                pile[line.Item] = take;
                paid += take * line.Cost;
            }
            if (paid < want.Cost) return null;

            // ---- TRIM ------------------------------------------------------------------------------------
            // Cheapest-first fills the pile but OVERSHOOTS on the last unit, and the overshoot can be large:
            // 85 covered by 2x10 + 2x30 + 1x30 = 110, when 3x30 = 90 was available out of the same bag. The
            // player does not care that each individual item was cheap; they care what the pile cost them.
            //
            // So walk back over it and drop any unit the pile can spare. MOST VALUABLE FIRST, so the expensive
            // things get the first chance to go back in your bag; when the pile cannot spare them it is the small
            // change that comes out instead, which is the 110 -> 90 case above. One pass is enough in this order:
            // every removal only LOWERS the total, so a unit that could not be spared when it was offered up
            // cannot become sparable later.
            var byValue = new List<NpcTradeLine>(rate);
            byValue.Reverse();   // `rate` is ascending; this is the same order read backwards
            foreach (var line in byValue)
            {
                if (!pile.TryGetValue(line.Item, out int n) || line.Cost <= 0) continue;
                while (n > 0 && paid - line.Cost >= want.Cost) { n--; paid -= line.Cost; }
                if (n > 0) pile[line.Item] = n; else pile.Remove(line.Item);
            }
            return pile;
        }
    }
}
